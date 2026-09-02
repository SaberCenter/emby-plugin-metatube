using System.ComponentModel;

namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>
///     How much reasoning the model is allowed to spend before answering.
///     Reasoning cannot be disabled on grok-4.6, so low is the cheapest setting.
///     xAI also offers high and xhigh, but measured against this catalogue they only
///     multiply the cost and the refusal risk without translating any better.
/// </summary>
public enum GrokReasoningEffort
{
    [Description("Low (default, fastest and cheapest)")]
    Low,

    [Description("Medium")]
    Medium
}
