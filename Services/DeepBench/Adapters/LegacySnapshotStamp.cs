using System.Runtime.CompilerServices;

namespace XinSpect;

/// <summary>
/// 記錄 legacy 服務最後一次被 Deep Bench adapter 實際執行的場次與時間窗。
/// adapter 只能重用「同場」產生的快照；跨場的舊資料必須重跑，不得重蓋時間戳當新場次證據。
/// </summary>
internal static class LegacySnapshotStamp
{
    private sealed record Stamp(Guid SessionId, DateTime StartedUtc, DateTime CompletedUtc);

    private static readonly ConditionalWeakTable<object, Stamp> Stamps = new();

    public static void Mark(object service, Guid sessionId, DateTime startedUtc, DateTime completedUtc)
    {
        Stamps.Remove(service);
        Stamps.Add(service, new Stamp(sessionId, startedUtc, completedUtc));
    }

    public static bool IsFreshFor(object service, Guid sessionId, out DateTime startedUtc, out DateTime completedUtc)
    {
        if (Stamps.TryGetValue(service, out Stamp? stamp) && stamp.SessionId == sessionId)
        {
            startedUtc = stamp.StartedUtc;
            completedUtc = stamp.CompletedUtc;
            return true;
        }
        startedUtc = default;
        completedUtc = default;
        return false;
    }
}
