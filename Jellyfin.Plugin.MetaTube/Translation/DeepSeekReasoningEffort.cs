using System.ComponentModel;

namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>
///     How much reasoning the model is allowed to spend before answering.
///     Only takes effect when thinking mode is enabled.
/// </summary>
public enum DeepSeekReasoningEffort
{
    [Description("Low (fastest, cheapest)")]
    Low,

    [Description("High (default)")]
    High,

    [Description("Max (slowest, most expensive)")]
    Max
}
