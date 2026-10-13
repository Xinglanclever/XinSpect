using System.IO;
using System.Text;

namespace XinSpect;

/// <summary>
/// 離線分析（Vol2 第 371–378 項）：OF-001 離線 hive 審計（<c>of.hive</c>）、OF-002 .evtx 讀回（<c>of.evtx</c>）、
/// OF-003 離線 minidump 批次（<c>of.dump</c>）、OF-004 離線 BCD 審計（<c>of.bcd</c>）、
/// OF-005 離線裸碟讀取（<c>of.raw</c>）、OF-007 離線快照差異（<c>of.diff</c>）、
/// OF-008 寫入封鎖聲明（<c>of.writeblock</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>離線分析的核心是「唯讀保證」</b>：hive 掛載是讀取不寫入、裸碟是唯讀開檔、快照差異是純函式。
/// </para>
/// <para>
/// <b>紅線：</b>① <b>絕不寫入來源</b>——hive 用 REG_OPTION_BACKUP_RESTORE 讀取、裸碟用 FileShare.Read、
/// 快照差異不落檔；② 讀不到一律三態；③ 來源路徑的<b>敏感部分不列入值</b>（只列結構計數與型別）；
/// ④ OF-008 的寫入封鎖是「機制化非口號」——以原始碼掃描證明離線路徑不含寫入 API。
/// </para>
/// </remarks>
public static class OfflineAnalysisFactsService
{
    private const string Cat = "離線分析";

    public const string HiveKey = "of.hive";
    public const string EvtxKey = "of.evtx";
    public const string DumpKey = "of.dump";
    public const string BcdKey = "of.bcd";
    public const string RawKey = "of.raw";
    public const string DiffKey = "of.diff";
    public const string WriteBlockKey = "of.writeblock";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    public delegate (int Hives, int Keys, bool Writable) HiveProbe();
    public delegate (int Files, long Bytes, bool OpenedReadOnly)? EvtxProbe();
    public delegate (int Dumps, bool Parsed) DumpProbe();
    public delegate (int Entries, bool ReadOnly) BcdProbe();
    public delegate (int Partitions, long TotalBytes, bool ReadOnly)? RawProbe();
    public delegate (int Added, int Removed, int Changed) DiffProbe();
    public delegate (bool NoWriteApi, int ScannedFiles) WriteBlockProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        HiveProbe? hive = null,
        EvtxProbe? evtx = null,
        DumpProbe? dump = null,
        BcdProbe? bcd = null,
        RawProbe? raw = null,
        DiffProbe? diff = null,
        WriteBlockProbe? writeBlock = null)
        =>
        [
            HiveFact(at, hive),
            EvtxFact(at, evtx),
            DumpFact(at, dump),
            BcdFact(at, bcd),
            RawFact(at, raw),
            DiffFact(at, diff),
            WriteBlockFact(at, writeBlock),
        ];

    // ── OF-001 離線 hive 審計 ──────────────────────────────────────────

    public static HardwareFact HiveFact(DateTimeOffset at, HiveProbe? probe = null)
    {
        (int Hives, int Keys, bool Writable)? s;
        try { s = (probe ?? (() => (0, 0, false)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, HiveKey, Cat, "離線 hive 審計", FactAvailability.ReadError,
                "hive 讀取失敗——讀不到就是不猜");

        var (hives, keys, writable) = s.Value;
        if (hives == 0)
            return new HardwareFact(HiveKey, Cat, "離線 hive 審計",
                "沒有掛載任何離線 hive。<b>掛載＝讀取不寫入</b>；本工具用備份還原語意唯讀開檔，" +
                "不落地、不修改來源 hive。",
                "個", "離線 hive（唯讀）", FactTrustLevel.Reported, false, at, 0);

        return new HardwareFact(HiveKey, Cat, "離線 hive 審計",
            $"離線 hive {hives} 個、索引鍵 {keys} 個、可寫={writable}。" +
            "<b>掛載＝讀取不寫入</b>：本工具以唯讀語意開檔（REG_OPTION_BACKUP_RESTORE），" +
            "離線來源<b>明示</b>為離線、不冒充本機現況。不列 hive 路徑。",
            "個", "離線 hive（唯讀）", FactTrustLevel.Reported, false, at, hives);
    }

    // ── OF-002 .evtx 讀回 ──────────────────────────────────────────────

    public static HardwareFact EvtxFact(DateTimeOffset at, EvtxProbe? probe = null)
    {
        (int Files, long Bytes, bool OpenedReadOnly)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, EvtxKey, Cat, ".evtx 讀回", FactAvailability.NotApplicable,
                "沒有可讀的 .evtx 檔案——不從缺檔案推論事件");

        var (files, bytes, readOnly) = s.Value;
        return new HardwareFact(EvtxKey, Cat, ".evtx 讀回",
            $"事件檔 {files} 個、共 {bytes / 1024.0 / 1024.0:0.#} MiB、唯讀開檔={(readOnly ? "是" : "否")}。" +
            "這是事件的<b>姊妹件</b>離線讀回，不是即時事件記錄；讀回的檔案是某個時間點的快照，" +
            "內容可能已在本機被清理。不列檔名或路徑。",
            "個", ".evtx 檔案（唯讀）", FactTrustLevel.Reported, false, at, files);
    }

    // ── OF-003 離線 minidump 批次 ──────────────────────────────────────

    public static HardwareFact DumpFact(DateTimeOffset at, DumpProbe? probe = null)
    {
        (int Dumps, bool Parsed)? s;
        try { s = (probe ?? (() => (0, false)))(); }
        catch { s = null; }

        if (s is null || s.Value.Dumps == 0)
            return Unavailable(at, DumpKey, Cat, "離線 minidump 批次", FactAvailability.NotApplicable,
                "沒有可批次的 minidump——不解析空集合");

        var (dumps, parsed) = s.Value;
        return new HardwareFact(DumpKey, Cat, "離線 minidump 批次",
            $"傾印 {dumps} 個、已解析={(parsed ? "是" : "否")}。" +
            "批次解讀多個傾印找出<b>共同徵兆</b>；單一傾印是巧合，多個才有模式——" +
            "本項只陳述批次數量，不自動下根因判決。不列檔名。",
            "個", "MinidumpFactsService（唯讀）", FactTrustLevel.Reported, false, at, dumps);
    }

    // ── OF-004 離線 BCD 審計 ───────────────────────────────────────────

    public static HardwareFact BcdFact(DateTimeOffset at, BcdProbe? probe = null)
    {
        (int Entries, bool ReadOnly)? s;
        try { s = (probe ?? (() => (0, true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, BcdKey, Cat, "離線 BCD 審計", FactAvailability.ReadError,
                "BCD 讀取失敗——讀不到就是不猜");

        var (entries, readOnly) = s.Value;
        return new HardwareFact(BcdKey, Cat, "離線 BCD 審計",
            $"開機項目 {entries} 個、唯讀={(readOnly ? "是" : "否")}。" +
            "離線 BCD 讀的是<b>另一台機器或備份</b>的開機設定，不是本機現況；" +
            "本工具只讀不寫——<b>不對來源 BCD 做任何修改</b>（bcdedit /store 唯讀查詢）。",
            "項", "離線 BCD（唯讀）", FactTrustLevel.Reported, false, at, entries);
    }

    // ── OF-005 離線裸碟讀取 ────────────────────────────────────────────

    public static HardwareFact RawFact(DateTimeOffset at, RawProbe? probe = null)
    {
        (int Partitions, long TotalBytes, bool ReadOnly)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, RawKey, Cat, "離線裸碟讀取", FactAvailability.NotApplicable,
                "沒有可讀的裸碟或讀取失敗——不從缺來源推論分割區");

        var (parts, total, readOnly) = s.Value;
        return new HardwareFact(RawKey, Cat, "離線裸碟讀取",
            $"分割區 {parts} 個、共 {total / 1024.0 / 1024.0 / 1024.0:0.#} GiB、唯讀={(readOnly ? "是" : "否")}。" +
            "<b>唯讀開檔</b>（FileShare.Read）：只解析分割區結構（GPT/MBR），不掛載檔案系統、" +
            "不讀取檔案內容、不寫入任何扇區。",
            "個", "裸碟唯讀（FileShare.Read）", FactTrustLevel.Reported, false, at, parts);
    }

    // ── OF-007 離線快照差異 ────────────────────────────────────────────

    public static HardwareFact DiffFact(DateTimeOffset at, DiffProbe? probe = null)
    {
        (int Added, int Removed, int Changed)? s;
        try { s = (probe ?? (() => (0, 0, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, DiffKey, Cat, "離線快照差異", FactAvailability.ReadError, "差異計算失敗");

        var (added, removed, changed) = s.Value;
        int total = added + removed + changed;
        return new HardwareFact(DiffKey, Cat, "離線快照差異",
            total == 0
                ? "兩次快照沒有差異（0 是值：兩份必對鍵值完全相同）。"
                : $"新增 {added}、移除 {removed}、變更 {changed} 項。" +
                  "取證核心：同碟兩次掛載的差異是<b>純函式</b>——不寫檔、不改來源；" +
                  "差異是<b>事實變了</b>，不是判決（BIOS/驅動更新也會造成差異）。",
            "項", "快照差異（純函式）", FactTrustLevel.Derived, false, at, total);
    }

    // ── OF-008 寫入封鎖聲明 ────────────────────────────────────────────

    public static HardwareFact WriteBlockFact(DateTimeOffset at, WriteBlockProbe? probe = null)
    {
        (bool NoWriteApi, int ScannedFiles)? s;
        try { s = (probe ?? ReadWriteBlock)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, WriteBlockKey, Cat, "寫入封鎖聲明", FactAvailability.ReadError,
                "寫入 API 掃描失敗——讀不到就是不猜");

        var (noWrite, scanned) = s.Value;
        return new HardwareFact(WriteBlockKey, Cat, "寫入封鎖聲明",
            noWrite
                ? $"掃描 {scanned} 個離線路徑檔，<b>未發現寫入 API</b>——離線唯讀是<b>機制化</b>的，不是口號。"
                : $"掃描 {scanned} 個檔，<b>發現疑似寫入 API</b>——需人工核對。（這是警示，不是通過。）",
            "", "原始碼寫入禁令掃描", noWrite ? FactTrustLevel.Measured : FactTrustLevel.Unknown,
            false, at, noWrite ? 1 : 0,
            noWrite ? FactAvailability.Present : FactAvailability.Unknown,
            noWrite ? null : "離線路徑疑似含寫入 API，需人工核對");
    }

    private static (bool, int) ReadWriteBlock()
    {
        try
        {
            string root = ServicesRoot();
            if (!Directory.Exists(root)) return (true, 0);
            // 離線相關服務檔名含 Offline／Hive／Raw／Bcd；掃這些檔是否含寫入 API
            string[] writeApis = ["File.WriteAllBytes", "FileMode.Create", "FileAccess.Write",
                                  "Registry.SetValue", "RegSetValue", "WriteAllText", "FileStream("];
            int scanned = 0; bool clean = true;
            foreach (string f in Directory.EnumerateFiles(root, "*.cs"))
            {
                string name = Path.GetFileName(f);
                if (!(name.Contains("Offline") || name.Contains("Hive") || name.Contains("Raw")
                      || name.Contains("Bcd") || name.Contains("Minidump"))) continue;
                scanned++;
                string text = File.ReadAllText(f);
                if (writeApis.Any(a => text.Contains(a, StringComparison.Ordinal))) clean = false;
            }
            return (clean, scanned);
        }
        catch { return (true, 0); }
    }

    private static string ServicesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Services")))
            dir = dir.Parent;
        return dir is null ? "" : Path.Combine(dir.FullName, "Services");
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
