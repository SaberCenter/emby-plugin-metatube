using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MetaTube.Configuration;

namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>
///     A lightweight client that talks to the xAI (Grok) Responses API directly from
///     the plugin, so the translation prompt can be fully controlled here instead of
///     being fixed on the MetaTube backend.
///     The OpenAI-compatible /v1/chat/completions endpoint is deliberately not used:
///     xAI marks it as legacy and only honours reasoning_effort there for grok-4.3,
///     so the effort setting below would be silently ignored on grok-4.6.
/// </summary>
public static class GrokClient
{
    private const string DefaultApiUrl = "https://api.x.ai/v1";
    private const string ResponsesPath = "/responses";

    // grok-4.6 always reasons: the effort can be lowered but never turned off.
    private const string ModelId = "grok-4.6";

    private static PluginConfiguration Configuration => Plugin.Instance.Configuration;

    /// <summary>
    ///     Translate a single piece of text into the target language using the
    ///     caller-supplied prompt template.
    /// </summary>
    /// <param name="text">The source (Japanese) text to translate.</param>
    /// <param name="to">The target language code (e.g. zh, en).</param>
    /// <param name="prompt">The prompt template to use for this field.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The translated text.</returns>
    public static async Task<string> TranslateAsync(string text, string to, string prompt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Configuration.GrokApiKey))
            throw new ArgumentException("Grok api key is not set");

        var payload = new Dictionary<string, object>
        {
            ["model"] = ModelId,
            ["input"] = LlmTranslationHelper.BuildMessages(text, to, prompt),
            ["stream"] = false,
            // The Responses API keeps every request on the xAI servers for 30 days
            // unless this is turned off, and nothing here is worth storing.
            ["store"] = false,
            ["reasoning"] = new { effort = GetReasoningEffort() }
        };

        var json = JsonSerializer.Serialize(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpointUrl())
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("User-Agent", DefaultUserAgent);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Configuration.GrokApiKey);

        var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;

            // xAI answers a malformed request, an unknown model and even a bad api key
            // alike with 400, so every one of those needs a change on our side and
            // resending it is pointless. Rate limiting (429) and the server-side
            // failures are treated as transient.
            var isTransient = statusCode is not (400 or 401 or 403 or 404 or 422);
            throw new GrokException(
                $"Grok API request error: {statusCode} ({LlmTranslationHelper.Truncate(responseText)})", isTransient);
        }

        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;

        if (root.TryGetProperty("error", out var error) &&
            error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new GrokException(
                $"Grok API returned an error: {LlmTranslationHelper.Truncate(error.ToString())}", false);

        // A successful HTTP status does not mean the answer is complete: the response
        // carries its own status, and a truncated or aborted result may not be written
        // to the metadata as if the translation had succeeded.
        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;

        switch (status)
        {
            case null:
            case "":
            case "completed":
                break;
            case "incomplete":
                var reason = root.TryGetProperty("incomplete_details", out var details) &&
                             details.ValueKind == JsonValueKind.Object &&
                             details.TryGetProperty("reason", out var reasonElement)
                    ? reasonElement.GetString()
                    : "unknown";
                throw new GrokException(
                    $"Grok API returned an incomplete result (reason: {reason})", false);
            case "failed":
                throw new GrokException(
                    $"Grok API failed to generate a response: {LlmTranslationHelper.Truncate(responseText)}", true);
            default:
                throw new GrokException(
                    $"Grok API returned an unexpected status: {status}", false);
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            throw new GrokException(
                $"Grok API returned no output: {LlmTranslationHelper.Truncate(responseText)}", true);

        // The output array interleaves the reasoning summary with the answer, so only
        // the assistant message is collected and everything else is dropped.
        var builder = new StringBuilder();

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var itemType) || itemType.GetString() != "message")
                continue;

            if (!item.TryGetProperty("content", out var contents) || contents.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var content in contents.EnumerateArray())
            {
                var contentType = content.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString()
                    : null;

                // Grok does moralise about this catalogue: a refusal is a deliberate
                // answer rather than a hiccup, and resending it changes nothing.
                if (contentType == "refusal")
                    throw new GrokException(
                        "Grok API refused to translate the text: " +
                        LlmTranslationHelper.Truncate(content.TryGetProperty("refusal", out var refusal)
                            ? refusal.GetString()
                            : null), false);

                if (contentType == "output_text" && content.TryGetProperty("text", out var textElement))
                    builder.Append(textElement.GetString());
            }
        }

        var translated = builder.ToString();

        if (string.IsNullOrWhiteSpace(translated))
            throw new GrokException("Grok API returned empty content", true);

        return translated.Trim();
    }

    private static string GetReasoningEffort()
    {
        return Configuration.GrokReasoningEffort switch
        {
            GrokReasoningEffort.Medium => "medium",
            _ => "low"
        };
    }

    private static string BuildEndpointUrl()
    {
        var baseUrl = Configuration.GrokApiUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = DefaultApiUrl;

        baseUrl = baseUrl.TrimEnd('/');

        // Allow the user to provide either the base url or the full endpoint.
        return baseUrl.EndsWith(ResponsesPath, StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + ResponsesPath;
    }

    #region Http

    private static readonly HttpClient HttpClient;
    private static string DefaultUserAgent => $"{Plugin.ProviderName}/{Plugin.Instance.Version}";

    static GrokClient()
    {
        HttpClient = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90)
        })
        {
            // A non-streaming request takes about a minute at the highest effort, so
            // the timeout only has to leave room for a slow queue on the xAI side.
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    #endregion
}
