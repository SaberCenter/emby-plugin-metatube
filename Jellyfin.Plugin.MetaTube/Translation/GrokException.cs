namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>
///     An error reported by the direct Grok (xAI) client.
/// </summary>
public class GrokException : TranslationApiException
{
    public GrokException(string message, bool isTransient) : base(message, isTransient)
    {
    }
}
