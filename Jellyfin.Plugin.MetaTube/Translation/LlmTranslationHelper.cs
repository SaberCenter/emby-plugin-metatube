namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>
///     The pieces shared by the translation clients that call an LLM provider
///     directly (DeepSeek, Grok) instead of going through the MetaTube backend.
/// </summary>
public static class LlmTranslationHelper
{
    /// <summary>
    ///     Build the chat messages for a single translation request.
    ///     If the prompt contains the {text} placeholder, the caller-supplied prompt
    ///     controls the whole message; otherwise the prompt is used as a system
    ///     instruction and the text is sent separately. An empty prompt falls back to
    ///     sending the raw text.
    /// </summary>
    /// <param name="text">The source (Japanese) text to translate.</param>
    /// <param name="to">The target language code (e.g. zh, en).</param>
    /// <param name="prompt">The prompt template to use for this field.</param>
    public static List<object> BuildMessages(string text, string to, string prompt)
    {
        var language = ToLanguageName(to);

        var messages = new List<object>();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            messages.Add(new { role = "user", content = text });
        }
        else if (prompt.Contains("{text}"))
        {
            var content = prompt.Replace("{lang}", language).Replace("{text}", text);
            messages.Add(new { role = "user", content });
        }
        else
        {
            messages.Add(new { role = "system", content = prompt.Replace("{lang}", language) });
            messages.Add(new { role = "user", content = text });
        }

        return messages;
    }

    public static string Truncate(string s, int maxLength = 512)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;

        return s.Length <= maxLength ? s : s[..maxLength] + "...";
    }

    public static string ToLanguageName(string code)
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
}
