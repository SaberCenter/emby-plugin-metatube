using System.Collections.Specialized;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Metadata;

namespace Jellyfin.Plugin.MetaTube.Translation;

public static class TranslationHelper
{
    private const string AutoLanguageCode = "auto";
    private const string JapaneseLanguageCode = "ja";

    private static readonly SemaphoreSlim Semaphore = new(1);

    private static PluginConfiguration Configuration => Plugin.Instance.Configuration;

    private static async Task<string> TranslateAsync(string q, string from, string to,
        string prompt, CancellationToken cancellationToken)
    {
        int millisecondsDelay;
        var nv = new NameValueCollection();
        switch (Configuration.TranslationEngine)
        {
            case TranslationEngine.Baidu:
                millisecondsDelay = 1000; // Limit Baidu API request rate to 1 rps.
                nv.Add(new NameValueCollection
                {
                    { "baidu-app-id", Configuration.BaiduAppId },
                    { "baidu-app-key", Configuration.BaiduAppKey }
                });
                break;
            case TranslationEngine.Google:
                millisecondsDelay = 100; // Limit Google API request rate to 10 rps.
                nv.Add(new NameValueCollection
                {
                    { "google-api-key", Configuration.GoogleApiKey },
                    { "google-api-url", Configuration.GoogleApiUrl }
                });
                break;
            case TranslationEngine.GoogleFree:
                millisecondsDelay = 100;
                nv.Add(new NameValueCollection());
                break;
            case TranslationEngine.DeepL:
                millisecondsDelay = 100;
                nv.Add(new NameValueCollection
                {
                    { "deepl-api-key", Configuration.DeepLApiKey },
                    { "deepl-api-url", Configuration.DeepLApiUrl }
                });
                break;
            case TranslationEngine.OpenAi:
                millisecondsDelay = 1000;
                nv.Add(new NameValueCollection
                {
                    { "openai-api-key", Configuration.OpenAiApiKey },
                    { "openai-api-url", Configuration.OpenAiApiUrl },
                    { "openai-model", Configuration.OpenAiModel }
                });
                break;
            case TranslationEngine.DeepSeek:
            case TranslationEngine.Grok:
                // Both are called directly from the plugin (see TranslateWithDelay),
                // so no backend parameters are required here.
                millisecondsDelay = 200;
                break;
            default:
                throw new ArgumentException($"Invalid translation engine: {Configuration.TranslationEngine}");
        }

        await Semaphore.WaitAsync(cancellationToken);

        try
        {
            async Task<string> TranslateWithDelay()
            {
                await Task.Delay(millisecondsDelay, cancellationToken);

                // DeepSeek and Grok bypass the MetaTube backend and call the API
                // directly, allowing the prompt to be configured from the plugin UI.
                switch (Configuration.TranslationEngine)
                {
                    case TranslationEngine.DeepSeek:
                        return await DeepSeekClient.TranslateAsync(q, to, prompt, cancellationToken)
                            .ConfigureAwait(false);
                    case TranslationEngine.Grok:
                        return await GrokClient.TranslateAsync(q, to, prompt, cancellationToken)
                            .ConfigureAwait(false);
                }

                return (await ApiClient
                    .TranslateAsync(q, from, to, Configuration.TranslationEngine.ToString(), nv, cancellationToken)
                    .ConfigureAwait(false)).TranslatedText;
            }

            return await RetryAsync(TranslateWithDelay, 5, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public static async Task TranslateAsync(MovieInfo m, string to, CancellationToken cancellationToken)
    {
        if (string.Equals(to, JapaneseLanguageCode, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"language not allowed: {to}");

        if (Configuration.TranslationMode.HasFlag(TranslationMode.Title) && !string.IsNullOrWhiteSpace(m.Title))
            m.Title = await TranslateAsync(m.Title, AutoLanguageCode, to, ResolveTitlePrompt(),
                cancellationToken);

        if (Configuration.TranslationMode.HasFlag(TranslationMode.Summary) && !string.IsNullOrWhiteSpace(m.Summary))
            m.Summary = await TranslateAsync(m.Summary, AutoLanguageCode, to, ResolveSummaryPrompt(),
                cancellationToken);
    }

    private static string ResolveTitlePrompt()
    {
        var prompt = Configuration.TranslationEngine == TranslationEngine.Grok
            ? Configuration.GrokTitlePrompt
            : Configuration.DeepSeekTitlePrompt;

        return string.IsNullOrWhiteSpace(prompt) ? PluginConfiguration.DefaultAiTitlePrompt : prompt;
    }

    private static string ResolveSummaryPrompt()
    {
        var prompt = Configuration.TranslationEngine == TranslationEngine.Grok
            ? Configuration.GrokSummaryPrompt
            : Configuration.DeepSeekSummaryPrompt;

        return string.IsNullOrWhiteSpace(prompt) ? PluginConfiguration.DefaultAiSummaryPrompt : prompt;
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> func, int attemptCount,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await func();
            }
            catch (Exception e) when (IsRetryable(e, cancellationToken) && ++attempt < attemptCount)
            {
                // Back off before retrying a transient failure: 1s, 2s, 4s, 8s.
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken);
            }
        }
    }

    private static bool IsRetryable(Exception e, CancellationToken cancellationToken)
    {
        // A cancelled scan or scheduled task must abort right away instead of
        // being retried until the attempts run out.
        if (cancellationToken.IsCancellationRequested)
            return false;

        // Permanent errors from a directly called API (bad request, api key, balance,
        // parameters or a truncated / filtered / refused answer) cannot be fixed by
        // resending the same request. Anything else -- including a client-side timeout,
        // which also surfaces as an OperationCanceledException -- is treated as transient.
        return e is not TranslationApiException { IsTransient: false };
    }
}