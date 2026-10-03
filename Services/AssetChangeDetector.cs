namespace XinSpect;

/// <summary>資產事件的類別。Added＝新資產、Removed＝資產消失、Changed＝資產屬性變更。</summary>
public enum AssetEventKind
{
    Added,
    Removed,
    Changed,
}

/// <summary>一件資產的生命週期事件。AssetClass＝資產類（記憶體／處理器／顯示卡／儲存／主機板）或「狀態」（非資產的變更）。</summary>
public sealed record AssetLifecycleEvent(AssetEventKind Kind, string Key, string Name, string AssetClass, string Description);

/// <summary>
/// WP42 資產生命週期：把兩份快照的差分**自動分類**成資產事件——硬體變更通知的資料層。
/// 只有「資產級」key 前綴（記憶體 mem./spd.、處理器 cpu.、顯示卡 gpu.、儲存 disk./nvme./smart.、
/// 主機板 board.）產生資產事件；其餘（韌體安全、暫存器、感測器讀值…）是**狀態變更**——
/// 照樣入列但標 AssetClass＝狀態，不冒充「硬體變了」。跨機器差分如實拒做。
/// </summary>
public static class AssetChangeDetector
{
    private static readonly (string Prefix, string AssetClass)[] AssetPrefixes =
    [
        ("mem.", "記憶體"),
        ("spd.", "記憶體"),
        ("cpu.", "處理器"),
        ("gpu.", "顯示卡"),
        ("disk.", "儲存"),
        ("nvme.", "儲存"),
        ("smart.", "儲存"),
        ("board.", "主機板"),
    ];

    public static IReadOnlyList<AssetLifecycleEvent> Detect(HardwareSnapshot before, HardwareSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var diff = HardwareSnapshotService.Diff(before, after);
        if (!diff.IsSameMachine)
            throw new InvalidOperationException("兩份快照屬於不同機器——資產生命週期事件跨機器不成立，拒做。");

        return diff.Changes.Where(c => c.Kind != SnapshotChangeKind.Unchanged)
            .Select(c => new AssetLifecycleEvent(
                c.Kind switch
                {
                    SnapshotChangeKind.Added => AssetEventKind.Added,
                    SnapshotChangeKind.Removed => AssetEventKind.Removed,
                    _ => AssetEventKind.Changed,
                },
                c.Key,
                c.Current?.Name ?? c.Previous?.Name ?? c.Key,
                AssetClassOf(c.Key),
                Describe(c)))
            .ToList();
    }

    private static string AssetClassOf(string key) =>
        AssetPrefixes.FirstOrDefault(p => key.StartsWith(p.Prefix, StringComparison.Ordinal)).AssetClass ?? "狀態";

    private static string Describe(HardwareFactChange c) => c.Kind switch
    {
        SnapshotChangeKind.Added => $"新出現：{c.Current?.Value ?? ""}（來源：{c.Current?.Source ?? ""}）",
        SnapshotChangeKind.Removed => $"消失（上次值：{c.Previous?.Value ?? ""}）",
        _ => $"{c.Previous?.Value ?? ""} → {c.Current?.Value ?? ""}",
    };
}
