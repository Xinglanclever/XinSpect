using System.Globalization;
using System.Text.Json.Serialization;

namespace XinSpect;

[JsonConverter(typeof(JsonStringEnumConverter<SensorAlertDirection>))]
public enum SensorAlertDirection { Above, Below }

/// <summary>Explicit local rule. Identity and unit are exact provider values, never parsed UI labels.</summary>
public sealed record SensorAlertRule
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string SensorId { get; init; }
    public required string Unit { get; init; }
    public required SensorAlertDirection Direction { get; init; }
    public required double Threshold { get; init; }
    public required double RecoveryThreshold { get; init; }
    public required double DurationSeconds { get; init; }
    public required double CooldownSeconds { get; init; }
    public bool Enabled { get; init; } = true;

    [JsonIgnore] public string ConditionText => Direction == SensorAlertDirection.Above
        ? $"≥ {Threshold:G} {Unit}；恢復 ≤ {RecoveryThreshold:G} {Unit}"
        : $"≤ {Threshold:G} {Unit}；恢復 ≥ {RecoveryThreshold:G} {Unit}";
    [JsonIgnore] public string EnabledText => Enabled ? "啟用" : "停用";

    public string? Validate()
    {
        static bool TextOk(string? s, int max, bool empty = false) => s is not null
            && (empty || !string.IsNullOrWhiteSpace(s)) && s.Length <= max && !s.Any(char.IsControl);
        if (Id == Guid.Empty) return "規則 ID 不得為空。";
        if (!TextOk(Name, 100)) return "名稱須為 1–100 個字元，不得含控制字元。";
        if (!TextOk(SensorId, 512)) return "感測器 ID 須為 1–512 個字元，不得含控制字元。";
        if (!TextOk(Unit, 32, true)) return "感測器單位不合法。";
        if (!Enum.IsDefined(Direction)) return "請選擇有效的上限或下限。";
        if (!double.IsFinite(Threshold) || !double.IsFinite(RecoveryThreshold)) return "觸發與恢復閾值必須為有限數字。";
        if (Direction == SensorAlertDirection.Above ? RecoveryThreshold >= Threshold : RecoveryThreshold <= Threshold)
            return Direction == SensorAlertDirection.Above ? "上限規則的恢復閾值須低於觸發閾值。" : "下限規則的恢復閾值須高於觸發閾值。";
        if (!double.IsFinite(DurationSeconds) || DurationSeconds < 0 || DurationSeconds > 86400)
            return "持續時間須為 0–86400 秒。";
        if (!double.IsFinite(CooldownSeconds) || CooldownSeconds < 0 || CooldownSeconds > 604800)
            return "通知冷卻須為 0–604800 秒。";
        return null;
    }
}

public enum SensorAlertPhase { Disabled, Normal, Pending, Active, Unknown }
public enum SensorAlertTransitionKind { Triggered, Recovered, Unknown }

public sealed record SensorAlertStatus(SensorAlertRule Rule, SensorAlertPhase Phase, bool IsActive,
    double? Value, double PendingSeconds, string Reason, DateTimeOffset? ObservedAtUtc, string Source)
{
    public Guid Id => Rule.Id;
    public string Name => Rule.Name;
    public string SensorId => Rule.SensorId;
    public string ConditionText => Rule.ConditionText;
    public string DetailText => string.IsNullOrEmpty(Reason) ? Source : Reason;
    /// <summary>Translates only recognized product text; source and provider diagnostics remain exact.</summary>
    public string DisplayDetailText => ProductTextProjection.LocalizeKnown(string.IsNullOrEmpty(Reason) ? Source : Reason);
    public string ValueText => Value is double v ? v.ToString("G6", CultureInfo.CurrentCulture) + " " + Rule.Unit : "—";
    public string PhaseText => Phase switch
    {
        SensorAlertPhase.Disabled => "已停用",
        SensorAlertPhase.Normal => "正常",
        SensorAlertPhase.Pending => $"等待持續 {PendingSeconds:0.0}/{Rule.DurationSeconds:G} 秒",
        SensorAlertPhase.Active => "警示中",
        _ => IsActive ? "資料未知；警示尚未確認恢復" : "資料未知／缺讀"
    };
}

public sealed record SensorAlertTransition(SensorAlertRule Rule, SensorAlertTransitionKind Kind,
    DateTimeOffset AtUtc, string Message, bool Notify, string? ProductMessage = null, string RawMessageSuffix = "",
    string? ProductConditionText = null, string? ProductReason = null);

public sealed record SensorAlertHistoryEntry(DateTimeOffset AtUtc, Guid RuleId, string Name,
    SensorAlertTransitionKind Kind, string Message, string? ProductMessage = null, string RawMessageSuffix = "",
    string? ProductConditionText = null, string? ProductReason = null)
{
    public string TimeText => AtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
    /// <summary>Rule names and provider suffixes are preserved while known event prose follows the active language.</summary>
    public string DisplayMessage => ProductMessage is not null
        ? Name + "：" + ProductTextProjection.LocalizeKnown(ProductMessage) + RawMessageSuffix
        : ProductConditionText is not null && ProductReason is not null
            ? SensorAlertTextProjection.Triggered(Message, Name + "：", ProductConditionText, ProductReason)
        : ProductTextProjection.LocalizeKnown(Message);
}

internal static class SensorAlertTextProjection
{
    internal static string Triggered(string message, string productPrefix, string condition, string reason)
    {
        int conditionIndex = message.IndexOf(condition, productPrefix.Length, StringComparison.Ordinal);
        if (conditionIndex < 0 || !message.EndsWith(reason, StringComparison.Ordinal))
            return ProductTextProjection.LocalizeKnown(message);

        string localizedCondition = ProductTextProjection.LocalizeKnown(condition);
        string withLocalizedCondition = message[..conditionIndex] + localizedCondition
            + message[(conditionIndex + condition.Length)..];
        string localizedBody = ProductTextProjection.LocalizeKnown(withLocalizedCondition[..^reason.Length]);
        string localizedReason = ProductTextProjection.LocalizeKnown(reason);
        return LanguageService.Catify(message, localizedBody + localizedReason);
    }
}

public sealed record SensorAlertEvaluation(IReadOnlyList<SensorAlertStatus> Statuses,
    IReadOnlyList<SensorAlertTransition> Transitions);
