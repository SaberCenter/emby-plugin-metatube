namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>
///     An error reported by the direct DeepSeek client.
///     <see cref="IsTransient" /> tells the retry helper whether re-sending the very
///     same request has any chance of succeeding: request format, api key, balance
///     and parameter errors are permanent, while rate limiting, server overload and
///     resource shortages are worth retrying.
/// </summary>
public class DeepSeekException : Exception
{
    public DeepSeekException(string message, bool isTransient) : base(message)
    {
        IsTransient = isTransient;
    }

    public bool IsTransient { get; }
}
