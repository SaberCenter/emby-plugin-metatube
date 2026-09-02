using System.ComponentModel;

namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>
///     How much reasoning the model is allowed to spend before answering.
///     Only takes effect when thinking mode is enabled. The max tier is not offered:
///     for translation it only multiplies the cost without improving the result.
/// </summary>
public enum DeepSeekReasoningEffort
{
    [Description("Low (default, fastest and cheapest)")]
    Low,

    [Description("High (slower, more expensive)")]
    High
}
