using System.Diagnostics;

namespace XinSpect;

/// <summary>Pure frame consumer: no hardware, wall-clock durations, persistence, network or notification side effects.</summary>
public sealed class SensorAlertEngine
{
    public const int MaxRules = 256;
    public static readonly TimeSpan DefaultMaxFrameAge = TimeSpan.FromSeconds(30);
    private readonly Dictionary<Guid, State> _states = new();
    private sealed class State
    {
        public required SensorAlertRule Rule;
        public SensorAlertPhase Phase = SensorAlertPhase.Unknown;
        public bool Active;
        public long? PendingSince;
        public long? LastSample;
        public long? LastNotification;
        public SensorAlertStatus? Status;
    }
    private long _lastSequence;
    private long _lastCapturedTimestamp;

    public SensorAlertEvaluation Evaluate(IReadOnlyList<SensorAlertRule> rules, SensorFrame frame,
        bool enabled, long nowTimestamp, DateTimeOffset nowUtc, TimeSpan? maxFrameAge = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(frame);
        if (rules.Count > MaxRules || rules.Any(r => r is null || r.Validate() is not null)
            || rules.Select(r => r.Id).Distinct().Count() != rules.Count)
            throw new ArgumentException("Invalid or duplicate sensor alert rules.", nameof(rules));
        var maxAge = maxFrameAge ?? DefaultMaxFrameAge;
        if (maxAge <= TimeSpan.Zero || maxAge > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(maxFrameAge));
        var changes = new List<SensorAlertTransition>();
        var statuses = new List<SensorAlertStatus>(rules.Count);
        var ids = rules.Select(r => r.Id).ToHashSet();
        foreach (var removed in _states.Keys.Where(id => !ids.Contains(id)).ToArray()) _states.Remove(removed);
        bool fresh = frame.IsFresh(nowTimestamp, maxAge);
        bool newer = frame.Sequence > _lastSequence && frame.CapturedTimestamp > _lastCapturedTimestamp;
        // A repeated/out-of-order frame cannot advance a duration or confirm recovery.
        // A stale repeated frame must still invalidate observation continuity.
        var readings = frame.Readings.GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (!_states.TryGetValue(rule.Id, out var state) || state.Rule != rule)
                _states[rule.Id] = state = new State { Rule = rule };
            if (!enabled || !rule.Enabled)
            {
                state.Active = false;
                state.PendingSince = state.LastSample = null;
                state.Phase = SensorAlertPhase.Disabled;
                state.Status = Status(rule, state, null, 0, enabled ? "規則已停用。" : "警示總開關已關閉。", null);
            }
            else if (!fresh)
                Unknown(state, frame, nowUtc, "感測器採樣已過期或尚未取得；不推定恢復。", changes);
            else if (!newer)
            {
                // On resuming, require a frame published after the master switch was observed off.
                if (state.Status is null || state.Phase == SensorAlertPhase.Disabled)
                    Unknown(state, frame, nowUtc, "等待新的感測器採樣。", changes);
            }
            else if (!readings.TryGetValue(rule.SensorId, out var matches))
                Unknown(state, frame, nowUtc, "找不到指定 ID 的感測器；不改配同名裝置。", changes);
            else if (matches.Length != 1)
                Unknown(state, frame, nowUtc, "同一採樣內感測器 ID 重複，無法確認身分。", changes);
            else if (!string.Equals(matches[0].Unit, rule.Unit, StringComparison.Ordinal))
                Unknown(state, frame, nowUtc, "來源單位已改變，請重新確認規則。", changes);
            else if (!matches[0].IsUsable)
            {
                string productReason = $"來源不可採信：{matches[0].Availability}／{matches[0].Trust}。";
                Unknown(state, frame, nowUtc, productReason + matches[0].Reason, changes,
                    productReason, matches[0].Reason);
            }
            else if (matches[0].SourceUpdatedAtUtc is DateTimeOffset updated
                && (updated > frame.ReadAtUtc || frame.ReadAtUtc - updated > maxAge))
                Unknown(state, frame, nowUtc, "來源更新時間過期或晚於採樣，不能以重新讀取時間冒充新值。", changes);
            else
                Observe(state, matches[0], frame, nowTimestamp, nowUtc, maxAge, changes);
            statuses.Add(state.Status!);
        }
        if (newer) { _lastSequence = frame.Sequence; _lastCapturedTimestamp = frame.CapturedTimestamp; }
        return new(statuses, changes);
    }

    private static void Unknown(State s, SensorFrame frame, DateTimeOffset nowUtc, string reason,
        List<SensorAlertTransition> changes, string? productMessage = null, string rawMessageSuffix = "")
    {
        if (s.Phase != SensorAlertPhase.Unknown || s.Status is null)
            changes.Add(new(s.Rule, SensorAlertTransitionKind.Unknown, nowUtc, $"{s.Rule.Name}：{reason}", false,
                productMessage ?? reason, rawMessageSuffix));
        s.Phase = SensorAlertPhase.Unknown;
        s.PendingSince = s.LastSample = null;
        s.Status = Status(s.Rule, s, null, 0, reason, null);
    }

    private static void Observe(State s, SensorReading reading, SensorFrame frame, long now, DateTimeOffset utc,
        TimeSpan maxAge, List<SensorAlertTransition> changes)
    {
        var r = s.Rule;
        double value = reading.Value!.Value;
        bool beyond = r.Direction == SensorAlertDirection.Above ? value >= r.Threshold : value <= r.Threshold;
        bool recovered = r.Direction == SensorAlertDirection.Above ? value <= r.RecoveryThreshold : value >= r.RecoveryThreshold;
        bool continuous = s.LastSample is long last && frame.CapturedTimestamp > last
            && Seconds(frame.CapturedTimestamp, last) <= maxAge.TotalSeconds;
        if (!continuous) s.PendingSince = null;
        s.LastSample = frame.CapturedTimestamp;
        double elapsed = 0;
        string reason = "";
        if (s.Active)
        {
            if (recovered)
            {
                s.Active = false;
                s.PendingSince = null;
                s.Phase = SensorAlertPhase.Normal;
                changes.Add(new(r, SensorAlertTransitionKind.Recovered, utc, $"{r.Name} 已確認恢復：{value:G6} {r.Unit}。", false));
            }
            else s.Phase = SensorAlertPhase.Active;
        }
        else if (beyond)
        {
            s.PendingSince ??= frame.CapturedTimestamp;
            elapsed = Seconds(frame.CapturedTimestamp, s.PendingSince.Value);
            if (elapsed >= r.DurationSeconds)
            {
                s.Active = true;
                s.Phase = SensorAlertPhase.Active;
                bool notify = s.LastNotification is not long lastNotify || (now >= lastNotify && Seconds(now, lastNotify) >= r.CooldownSeconds);
                if (notify) s.LastNotification = now;
                reason = notify ? "已觸發通知。" : "已觸發；仍在通知冷卻期間。";
                changes.Add(new(r, SensorAlertTransitionKind.Triggered, utc,
                    $"{r.Name}：{value:G6} {r.Unit}；{r.ConditionText}，持續 {elapsed:0.0} 秒。{reason}", notify,
                    ProductConditionText: r.ConditionText, ProductReason: reason));
            }
            else s.Phase = SensorAlertPhase.Pending;
        }
        else
        {
            s.PendingSince = null;
            s.Phase = SensorAlertPhase.Normal;
        }
        s.Status = Status(r, s, value, elapsed, reason, reading);
    }

    private static double Seconds(long end, long start) => ((double)end - start) / Stopwatch.Frequency;
    private static SensorAlertStatus Status(SensorAlertRule r, State s, double? value, double elapsed, string reason, SensorReading? reading)
        => new(r, s.Phase, s.Active, value, elapsed, reason, reading?.ReadAtUtc, reading?.Source ?? "");
}
