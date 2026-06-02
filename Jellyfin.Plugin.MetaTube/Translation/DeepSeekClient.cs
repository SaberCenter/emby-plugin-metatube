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
    ///     user-defined prompt template.
    /// </summary>
    /// <param name="text">The source (Japanese) text to translate.</param>
    /// <param name="to">The target language code (e.g. zh, en).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The translated text.</returns>
    public static async Task<string> TranslateAsync(string text, string to, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Configuration.DeepSeekApiKey))
            throw new ArgumentException("DeepSeek api key is not set");

        var prompt = Configuration.DeepSeekPrompt;
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = PluginConfiguration.DefaultDeepSeekPrompt;

        var language = ToLanguageName(to);

        // Build chat messages. If the prompt template contains the {text}
        // placeholder, the user controls the whole message; otherwise we treat
        // the prompt as a system instruction and send the text separately.
        var messages = new List<object>();
        if (prompt.Contains("{text}"))
        {
            var content = prompt.Replace("{lang}", language).Replace("{text}", text);
            messages.Add(new { role = "user", content });
        }
        else
        {
            messages.Add(new { role = "system", content = prompt.Replace("{lang}", language) });
            messages.Add(new { role = "user", content = text });
        }

        var payload = new
        {
            model = GetModelId(),
            messages,
            stream = false,
            temperature = 1.3,
            // Translation does not benefit from chain-of-thought. DeepSeek V4
            // defaults to thinking = enabled, so disable it explicitly for
            // faster and cheaper non-thinking calls.
            thinking = new { type = "disabled" }
        };

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
            throw new Exception($"DeepSeek API request error: {(int)response.StatusCode} ({responseText})");

        using var document = JsonDocument.Parse(responseText);
        var translated = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(translated))
            throw new Exception("DeepSeek API returned empty content");

        return translated.Trim();
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

    private static string ToLanguageName(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "简体中文";

        switch (code.ToLowerInvariant())
        {
            case "zh":
            case "zh-cn":
            case "zh-hans":
                return "简体中文";
            case "zh-tw":
            case "zh-hk":
            case "zh-hant":
                return "繁体中文";
            case "en":
            case "en-us":
                return "English";
            case "ja":
                return "日本語";
            case "ko":
                return "한국어";
            default:
                return code;
        }
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
            // DeepSeek responses (especially for long summaries) can take a while.
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    #endregion
}
