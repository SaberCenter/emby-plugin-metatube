namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>
///     An error reported by the direct DeepSeek client.
/// </summary>
public class DeepSeekException : TranslationApiException
{
    public DeepSeekException(string message, bool isTransient) : base(message, isTransient)
    {
    }
}
