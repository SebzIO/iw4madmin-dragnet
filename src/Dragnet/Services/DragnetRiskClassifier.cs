using Dragnet.Models;

namespace Dragnet.Services;

public enum DragnetRiskScore
{
    Low,
    Medium,
    High,
    NeedsAction
}

public sealed record DragnetRiskAssessment(
    DragnetRiskScore Score,
    string Label,
    string Summary,
    string ColorClass,
    string Icon);

public static class DragnetRiskClassifier
{
    private static readonly string[] NeedsActionTerms =
    [
        "cheat",
        "cheating",
        "hacker",
        "hack",
        "wallhack",
        "wall hack",
        "aimbot",
        "aim bot",
        "esp",
        "silent aim",
        "triggerbot",
        "trigger bot",
        "spinbot",
        "spin bot",
        "exploiting",
        "exploit",
        "mod menu",
        "unlock all",
        "bypass",
        "anti-aim",
        "third party"
    ];

    private static readonly string[] HighTerms =
    [
        "ddos",
        "dox",
        "doxx",
        "threat",
        "evading",
        "ban evade",
        "impersonat",
        "crash",
        "botnet"
    ];

    private static readonly string[] MediumTerms =
    [
        "racism",
        "racist",
        "slur",
        "hate speech",
        "harass",
        "harassment",
        "abuse",
        "toxic",
        "insult",
        "insulting",
        "offensive",
        "language",
        "advertising",
        "spam"
    ];

    private static readonly string[] LowTerms =
    [
        "warn",
        "warning",
        "name",
        "tag",
        "camp",
        "minor",
        "chat",
        "words"
    ];

    public static DragnetRiskAssessment Assess(DragnetEventEnvelope envelope) =>
        envelope.EventType is DragnetEventType.BanLifted
            ? Create(DragnetRiskScore.Low, "Lift event")
            : Assess(envelope.Reason);

    public static DragnetRiskAssessment Assess(string? reason)
    {
        var normalized = (reason ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Create(DragnetRiskScore.Medium, "No reason supplied");
        }

        if (ContainsAny(normalized, NeedsActionTerms))
        {
            return Create(DragnetRiskScore.NeedsAction, "Cheating or exploit indicator");
        }

        if (ContainsAny(normalized, HighTerms))
        {
            return Create(DragnetRiskScore.High, "High-risk security or evasion indicator");
        }

        if (ContainsAny(normalized, MediumTerms))
        {
            return Create(DragnetRiskScore.Medium, "Abuse, racism, chat, or conduct indicator");
        }

        if (ContainsAny(normalized, LowTerms))
        {
            return Create(DragnetRiskScore.Low, "Low-risk conduct indicator");
        }

        return Create(DragnetRiskScore.Medium, "General moderation reason");
    }

    public static bool ShouldMentionAdmins(DragnetNotification notification) =>
        notification.Type is DragnetNotificationType.NewBan &&
        Assess(notification.Reason).Score is DragnetRiskScore.High or DragnetRiskScore.NeedsAction;

    private static DragnetRiskAssessment Create(DragnetRiskScore score, string summary) =>
        score switch
        {
            DragnetRiskScore.Low => new(score, "Low", summary, "text-success", "ph-info"),
            DragnetRiskScore.Medium => new(score, "Medium", summary, "text-warning", "ph-warning"),
            DragnetRiskScore.High => new(score, "High", summary, "text-danger", "ph-warning-circle"),
            DragnetRiskScore.NeedsAction => new(score, "Needs action", summary, "text-danger", "ph-siren"),
            _ => new(score, score.ToString(), summary, "text-muted", "ph-question")
        };

    private static bool ContainsAny(string value, IEnumerable<string> terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
