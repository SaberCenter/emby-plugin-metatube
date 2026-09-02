using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MetaTube.Configuration;

namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>
///     A lightweight client that talks to the DeepSeek (OpenAI-compatible) chat
///     completions API directly from the plugin, so the translation prompt can be
///     fully controlled here instead of being fixed on the MetaTube backend.
/// </summary>
public static class DeepSeekClient
{
    private const string DefaultApiUrl = "https://api.deepseek.com";
    private const string ChatCompletionsPath = "/chat/completions";

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
        if (string.IsNullOrWhiteSpace(Configuration.DeepSeekApiKey))
            throw new ArgumentException("DeepSeek api key is not set");

        var messages = LlmTranslationHelper.BuildMessages(text, to, prompt);

        // DeepSeek V4 defaults to thinking = enabled, so always send the flag
        // explicitly and let the plugin setting decide instead of the server default.
        var enableThinking = Configuration.DeepSeekEnableThinking;

        var payload = new Dictionary<string, object>
        {
            ["model"] = GetModelId(),
            ["messages"] = messages,
            ["stream"] = false,
            ["thinking"] = new { type = enableThinking ? "enabled" : "disabled" }
        };

        if (enableThinking)
            payload["reasoning_effort"] = GetReasoningEffort();
        else
            // Thinking mode ignores temperature (as well as top_p and the penalties),
            // so the recommended translation temperature is only sent when it is off.
            payload["temperature"] = 1.3;

        var json = JsonSerializer.Serialize(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpointUrl())
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("User-Agent", DefaultUserAgent);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Configuration.DeepSeekApiKey);

        var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;

            // 400 (bad request), 401 (bad api key), 402 (insufficient balance) and
            // 422 (invalid parameters) all require a change on our side, so resending
            // the identical request is pointless. Everything else (429, 500, 503,
            // gateway errors) is treated as transient.
            var isTransient = statusCode is not (400 or 401 or 402 or 422);
            throw new DeepSeekException(
                $"DeepSeek API request error: {statusCode} ({LlmTranslationHelper.Truncate(responseText)})", isTransient);
        }

        using var document = JsonDocument.Parse(responseText);

        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new DeepSeekException(
                $"DeepSeek API returned no choices: {LlmTranslationHelper.Truncate(responseText)}", true);

        var choice = choices[0];

        // A successful HTTP status does not mean the answer is complete: the result
        // may be truncated, filtered or aborted, and none of those may be written to
        // the metadata as if the translation had succeeded.
        var finishReason = choice.TryGetProperty("finish_reason", out var reason)
            ? reason.GetString()
            : null;

        switch (finishReason)
        {
            case null:
            case "":
            case "stop":
                break;
            case "length":
                throw new DeepSeekException(
                    "DeepSeek API returned a truncated result (finish_reason: length)", false);
            case "content_filter":
                throw new DeepSeekException(
                    "DeepSeek API omitted the content (finish_reason: content_filter)", false);
            case "insufficient_system_resource":
                throw new DeepSeekException(
                    "DeepSeek API ran out of inference resources (finish_reason: insufficient_system_resource)",
                    true);
            default:
                throw new DeepSeekException(
                    $"DeepSeek API returned an unexpected finish reason: {finishReason}", false);
        }

        // In thinking mode the reasoning text lives in a sibling reasoning_content
        // property; only content is the translation.
        var translated = choice.TryGetProperty("message", out var message) &&
                         message.TryGetProperty("content", out var messageContent)
            ? messageContent.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(translated))
            throw new DeepSeekException("DeepSeek API returned empty content", true);

        return translated.Trim();
    }

    private static string GetReasoningEffort()
    {
        return Configuration.DeepSeekReasoningEffort switch
        {
            DeepSeekReasoningEffort.High => "high",
            _ => "low"
        };
    }

    private static string GetModelId()
    {
        return Configuration.DeepSeekModel switch
        {
            DeepSeekModelType.Pro => "deepseek-v4-pro",
            _ => "deepseek-v4-flash"
        };
    }

    private static string BuildEndpointUrl()
    {
        var baseUrl = Configuration.DeepSeekApiUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = DefaultApiUrl;

        baseUrl = baseUrl.TrimEnd('/');

        // Allow the user to provide either the base url or the full endpoint.
        return baseUrl.EndsWith(ChatCompletionsPath, StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + ChatCompletionsPath;
    }

    #region Http

    private static readonly HttpClient HttpClient;
    private static string DefaultUserAgent => $"{Plugin.ProviderName}/{Plugin.Instance.Version}";

    static DeepSeekClient()
    {
        HttpClient = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90)
        })
        {
            // DeepSeek keeps a queued request alive with empty lines / SSE comments
            // and only closes it server-side after about 10 minutes, so a shorter
            // client timeout just cancels requests that were still waiting to run.
            // Thinking mode makes those waits noticeably more common.
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    #endregion
}
