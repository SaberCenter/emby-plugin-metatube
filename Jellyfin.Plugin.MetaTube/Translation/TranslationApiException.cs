namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>
///     An error reported by a translation API that the plugin calls directly instead
///     of going through the MetaTube backend.
///     <see cref="IsTransient" /> tells the retry helper whether re-sending the very
///     same request has any chance of succeeding: request format, api key, balance
///     and parameter errors are permanent, while rate limiting, server overload and
///     resource shortages are worth retrying.
/// </summary>
public abstract class TranslationApiException : Exception
{
    protected TranslationApiException(string message, bool isTransient) : base(message)
    {
        IsTransient = isTransient;
    }

    public bool IsTransient { get; }
}
