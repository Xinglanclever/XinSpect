using System.Globalization;
using System.Text.Json.Serialization;
namespace XinSpect;

/// <summary>Explicit read-only scan. Budgets include the root entry.</summary>
public sealed record DiskSpaceScanOptions
{
    public int MaxEntries { get; init; } = 200_000;
    public int MaxDepth { get; init; } = 64;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxIssues { get; init; } = 2_000;
    public int MaxBigFiles { get; init; } = 500;
    /// <summary>Conservative retained-model accounting budget; not a hard bound on the CLR/process heap.</summary>
    public long MaxRetainedMetadataBytes { get; init; } = 128L * 1024 * 1024;
    public void Validate()
    {
        if (MaxEntries is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaxEntries));
        if (MaxDepth is < 0 or > 256) throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        if (MaxDuration <= TimeSpan.Zero || MaxDuration > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(MaxDuration));
        if (MaxIssues is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(MaxIssues));
        if (MaxBigFiles is < 1 or > 2_000) throw new ArgumentOutOfRangeException(nameof(MaxBigFiles));
        if (MaxRetainedMetadataBytes is < 1024L * 1024 or > 512L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaxRetainedMetadataBytes));
    }
}
public enum DiskSpaceIssueKind { AccessDenied, ReadError, ReparsePoint, DepthLimit, EntryLimit, TimeLimit, MemoryLimit, Cancelled, IdentityUnavailable, DuplicateDirectory, SizeOverflow }
public enum DiskSpaceScanState { Complete, Partial, Cancelled }
public sealed record DiskSpaceIssue(string Path, DiskSpaceIssueKind Kind, string Reason, int? NativeError = null);
public sealed record DiskSpaceFileIdentity(ulong VolumeSerialNumber, ulong IdLow, ulong IdHigh);
/// <summary>Reported unnamed-stream bytes; null is unavailable, never an assumed zero.</summary>
public sealed record DiskSpaceFileMetadata(bool IsDirectory, bool IsReparsePoint, long? LogicalBytes,
    long? AllocatedBytes, DiskSpaceFileIdentity? Identity, uint LinkCount = 1, string? IdentityError = null);
public sealed class DiskSpaceNode
{
    public DiskSpaceNode(string path, bool isDirectory) { Path = path; IsDirectory = isDirectory; }
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;
    public bool IsDirectory { get; }
    public DiskSpaceNode? Parent { get; internal set; }
    public List<DiskSpaceNode> Children { get; } = [];
    public long LogicalBytes { get; internal set; }
    public long UniqueAllocatedBytes { get; internal set; }
    public long? ReportedAllocatedBytes { get; internal set; }
    public long FileCount { get; internal set; }
    public long UnknownAllocationFiles { get; internal set; }
    public bool IsHardLinkDuplicate { get; internal set; }
    public bool IsPartial { get; internal set; }
    public string? Note { get; internal set; }
    // Only the map's synthetic aggregate supplies a product-owned label. Real paths/names stay raw.
    internal Func<string>? UiLabel { get; set; }
    internal string UiName => UiLabel?.Invoke() ?? Name;
    public string LogicalDisplay => DiskSpaceFormatting.Bytes(LogicalBytes);
    public string AllocatedDisplay => DiskSpaceFormatting.Bytes(UniqueAllocatedBytes) + (UnknownAllocationFiles > 0 ? LanguageService.T(" + 未知") : "");
    public string DisplayText => $"{UiName}  ·  {LogicalDisplay}" + (IsPartial ? LanguageService.T("  [部分]") : "") + (IsHardLinkDuplicate ? LanguageService.T("  [硬連結]") : "");
    public string Detail => DiskSpaceUiText.Format("{0}\n邏輯大小（檔名計數）：{1}；已確認唯一配置：{2}\n檔案：{3:N0}；配置未知：{4:N0}",
        UiLabel?.Invoke() ?? Path, LogicalDisplay, AllocatedDisplay, FileCount, UnknownAllocationFiles) +
        (Note is null ? "" : "\n" + DiskSpaceUiText.Reason(Note));
}
public sealed record DiskSpaceTypeRow(string Extension, long Files, long LogicalBytes, long UniqueAllocatedBytes, long UnknownAllocationFiles)
{
    public string LogicalDisplay => DiskSpaceFormatting.Bytes(LogicalBytes);
    public string AllocatedDisplay => DiskSpaceFormatting.Bytes(UniqueAllocatedBytes) + (UnknownAllocationFiles > 0 ? " + 未知" : "");
    // Keep the existing report projection invariant across UI languages.
    [JsonIgnore] public string UiExtension => Extension == "（無副檔名）" ? LanguageService.T("（無副檔名）") : Extension;
    [JsonIgnore] public string UiAllocatedDisplay => DiskSpaceFormatting.Bytes(UniqueAllocatedBytes) + (UnknownAllocationFiles > 0 ? LanguageService.T(" + 未知") : "");
}
public sealed record DiskSpaceProgress(long EntriesVisited, long Files, long LogicalBytes, TimeSpan Elapsed, string CurrentPath, long EstimatedRetainedMetadataBytes = 0);
public sealed record DiskSpaceScanResult(string RootPath, DateTimeOffset StartedAtUtc, TimeSpan Elapsed,
    DiskSpaceScanState State, DiskSpaceScanOptions Options, DiskSpaceNode Root, long EntriesVisited,
    long HardLinkDuplicates, IReadOnlyList<DiskSpaceTypeRow> FileTypes, IReadOnlyList<DiskSpaceNode> BiggestFiles,
    IReadOnlyList<DiskSpaceIssue> Issues, long TotalIssues, long EstimatedRetainedMetadataBytes = 0)
{
    public const string Method = "System.IO immediate-child enumeration; CreateFileW OPEN_EXISTING + OPEN_REPARSE_POINT; active directory guard access=FILE_LIST_DIRECTORY|FILE_READ_ATTRIBUTES (0x81), share=READ|WRITE, attrs/identity re-read from that same handle; files use metadata-only access. GetFileInformationByHandleEx(FileStandardInfo/FileAttributeTagInfo/FileIdInfo). No MFT or USN scan.";
    public const string MemoryAccounting = "保留模型採保守估算：固定 64 KiB + 最大檔案保留數 × 64 B；每節點 4096 B + 完整路徑字元數 × 4 B（涵蓋節點、路徑/副檔名、身分及容器餘裕）；每條保留診斷另加 256 B + 路徑與說明字元數 × 2 B。說明最多保留 1024 字元。到預算上限即停止；這不是 CLR/process heap 的硬上限，不含 CLR、原生 I/O 緩衝、UI 與匯出序列化的暫時配置。";
    public const string Limits = "非快照；檔案在掃描時仍可改變。只統計一般檔案的未命名資料流；不含 ADS、目錄/檔案系統中繼資料、磁碟保留空間。配置值是檔案系統回報，非可回收空間或實體扇區稽核；跨檔案共用區塊/重複資料刪除不保證可辨識。重解析點（含連結、掛載點及雲端預留位置）不追蹤。邏輯大小按檔名計，配置依 volume + 128-bit file ID 去重並歸於先遇到的路徑；沒有 ID 的檔案不加入唯一配置總數。遍歷中的目錄持有禁止 delete-sharing 的 0x81 handle；不保證抵抗所選根以上祖先改變或惡意檔案系統/過濾驅動。預算/取消只在檔案系統呼叫之間檢查，無法中止正在等待的系統 I/O。保留模型另有估算預算，非 CLR heap 硬上限。";
    public string Summary => DiskSpaceUiText.Format("{0} · {1:N0} 個檔案 · {2:N0} 個項目 · {3:F1} 秒\n邏輯大小 {4}；已確認唯一配置 {5}；硬連結重複 {6:N0}；略過/限制/錯誤 {7:N0}\n保留模型估算 {8} / {9}（非 CLR heap 硬上限）",
        LanguageService.T(State switch { DiskSpaceScanState.Complete => "列舉完成", DiskSpaceScanState.Cancelled => "已取消，保留部分結果", _ => "部分結果" }),
        Root.FileCount, EntriesVisited, Elapsed.TotalSeconds, Root.LogicalDisplay, Root.AllocatedDisplay, HardLinkDuplicates, TotalIssues,
        DiskSpaceFormatting.Bytes(EstimatedRetainedMetadataBytes), DiskSpaceFormatting.Bytes(Options.MaxRetainedMetadataBytes));
}

/// <summary>UI boundary only: translate known producer wording, preserving paths and system/user error payloads.</summary>
internal static class DiskSpaceUiText
{
    internal static string Format(string template, params object[] values)
        => string.Format(CultureInfo.CurrentCulture, LanguageService.T(template), values);

    internal static string Kind(DiskSpaceIssueKind kind) => LanguageService.T(kind switch
    {
        DiskSpaceIssueKind.AccessDenied => "存取遭拒", DiskSpaceIssueKind.ReadError => "讀取錯誤",
        DiskSpaceIssueKind.ReparsePoint => "重解析點", DiskSpaceIssueKind.DepthLimit => "深度上限",
        DiskSpaceIssueKind.EntryLimit => "項目上限", DiskSpaceIssueKind.TimeLimit => "時間上限",
        DiskSpaceIssueKind.MemoryLimit => "模型估算上限", DiskSpaceIssueKind.Cancelled => "使用者取消",
        DiskSpaceIssueKind.IdentityUnavailable => "檔案身分未知", DiskSpaceIssueKind.DuplicateDirectory => "重複目錄",
        DiskSpaceIssueKind.SizeOverflow => "大小溢位", _ => kind.ToString()
    });

    internal static string Reason(string reason)
    {
        const string truncated = " [截斷]";
        if (reason.Length == 1024 && reason.EndsWith(truncated, StringComparison.Ordinal))
            return ReasonCore(reason[..^truncated.Length]) + LanguageService.T(truncated);
        return ReasonCore(reason);
    }

    internal static string ExceptionMessage(Exception error)
    {
        // ArgumentException adds an OS-localized parameter suffix. Translate only our validated message.
        if (error is ArgumentException { ParamName: "directory" })
            foreach (string message in new[] { "請指定完整的目錄路徑。", "請選擇一般磁碟/UNC 目錄；不接受裝置命名空間。" })
                if (error.Message.StartsWith(message, StringComparison.Ordinal))
                    return LanguageService.T(message) + error.Message[message.Length..];
        return Reason(error.Message);
    }

    private static string ReasonCore(string reason)
    {
        // These prefixes are introduced by the producer; the remainder may contain arbitrary user/system text.
        foreach (string prefix in new[] { "權限不足：", "讀取失敗：" })
            if (reason.StartsWith(prefix, StringComparison.Ordinal))
                return LanguageService.T(prefix) + KnownReason(reason[prefix.Length..]);
        return KnownReason(reason);
    }

    private static string KnownReason(string reason)
    {
        const string identityPrefix = "FileIdInfo 失敗（Win32 ";
        if (reason.StartsWith(identityPrefix, StringComparison.Ordinal))
        {
            int separator = reason.IndexOf("）：", identityPrefix.Length, StringComparison.Ordinal);
            if (separator >= 0 && int.TryParse(reason.AsSpan(identityPrefix.Length, separator - identityPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return Format("FileIdInfo 失敗（Win32 {0}）：{1}", reason[identityPrefix.Length..separator], reason[(separator + 2)..]);
        }
        return reason switch
        {
            "達到保留中繼資料的估算預算；已停止讀取/保留後續項目，結果不完整。此預算不是 CLR heap 硬上限。" or
            "使用者要求取消；保留已完成的統計。" or "達到時間預算；未列舉的項目數未知。" or
            "達到項目預算；未列舉的項目數未知。" or "位元組加總超出 Int64；此欄已飽和，不能視為精確值。" or
            "本次預算/取消後未讀取。" or "重解析點未追蹤（包含符號連結、junction、掛載點及雲端預留位置）。" or
            "所選根路徑不是目錄。" or "檔案長度未知或格式無效，未當成 0 納入。" or
            "缺少檔案身分或配置大小；未加入唯一配置總數。" or
            "同一 volume + file ID 已計入另一檔名；本路徑的唯一配置貢獻為 0。" or
            "目錄身分重複，拒絕重複遍歷。" or "已達深度上限，未列舉此目錄內容。" or
            "子項路徑超過本掃描器的 32,767 字元上限。" or "列舉器回報非直接子項目，已拒絕越界遍歷。" or
            "目錄在取得遍歷保護前已改變類型，已停止讀取此路徑。" or "檔案系統回報負數長度，拒絕納入統計。" or
            "磁碟配置空間需要 Windows 檔案 API。" or "請指定完整的目錄路徑。" or
            "請選擇一般磁碟/UNC 目錄；不接受裝置命名空間。" or "目錄路徑超過本掃描器的 32,767 字元上限。" or
            "此矩形合併顯示其餘子項；完整清單見左側目錄樹。" => LanguageService.T(reason),
            _ => reason
        };
    }
}
public static class DiskSpaceFormatting
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        double value = bytes; int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "N0" : "N2", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
public sealed record DiskSpaceTile(DiskSpaceNode Node, double X, double Y, double Width, double Height);
/// <summary>Slice-and-dice layout; areas represent only observed positive bytes.</summary>
public static class DiskSpaceTreemap
{
    public static IReadOnlyList<DiskSpaceTile> Layout(IReadOnlyList<DiskSpaceNode> nodes, double width, double height, bool allocated)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return [];
        var items = nodes.Select(n => (Node: n, Bytes: allocated ? n.UniqueAllocatedBytes : n.LogicalBytes)).Where(x => x.Bytes > 0).OrderByDescending(x => x.Bytes).ToArray();
        var result = new List<DiskSpaceTile>(items.Length);
        void Split(int start, int count, double x, double y, double w, double h)
        {
            if (count == 0) return;
            if (count == 1) { result.Add(new(items[start].Node, x, y, w, h)); return; }
            double total = 0; for (int i = start; i < start + count; i++) total += items[i].Bytes;
            double sum = 0; int left = 0;
            do { sum += items[start + left++].Bytes; } while (left < count - 1 && sum < total / 2);
            double ratio = sum / total;
            if (w >= h) { Split(start, left, x, y, w * ratio, h); Split(start + left, count - left, x + w * ratio, y, w * (1 - ratio), h); }
            else { Split(start, left, x, y, w, h * ratio); Split(start + left, count - left, x, y + h * ratio, w, h * (1 - ratio)); }
        }
        Split(0, items.Length, 0, 0, width, height);
        return result;
    }
}
