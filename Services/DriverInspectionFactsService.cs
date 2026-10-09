using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace XinSpect;

/// <summary>驅動靜態檢視卡片的一列：驅動名、明細（機器／子系統／匯入）、訊號（裝置字串／IOCTL 候選／BYOVD 命中）。</summary>
public sealed record DriverInspectionRow(string Driver, string Detail, string Signals);

/// <summary>
/// 驅動檔的純靜態檢視（DrvEye／DriverSight 那條能力的唯讀版）：對非系統目錄的載入模組
/// 逐顆讀回 .sys 檔案內容、解析 PE 結構與內嵌字串，並與既有 BYOVD 封鎖清單交叉引用。
/// </summary>
/// <remarks>
/// <para>
/// <b>誠實界線：</b>不載入任何驅動、不呼叫任何 IOCTL——檢視的是<b>檔案的靜態形狀</b>，
/// 不是行為驗證。IOCTL 碼是<b>候選</b>（靜態掃描無法證明真的是分派碼）；裝置字串是檔案裡
/// 有的字串，不代表分派了那個裝置。命中封鎖清單是「攻擊面事實」不是「中毒判決」（與
/// ByovdCompareService 同一條界線）。檔案讀不到時如實三態。
/// </para>
/// </remarks>
public static class DriverInspectionFactsService
{
    private const string Category = "系統與軟體";
    public const string CountKey = "drvinsp.drivers.count";
    private const string Source = "驅動檔案靜態解析（唯讀）÷ PE/COFF";

    /// <summary>檢視上限：非系統目錄驅動最多檢視前 8 顆；超過如實說明截斷。</summary>
    private const int MaxDrivers = 8;

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyList<KernelModuleEntry>? modules,
        Func<string, byte[]?> readFile,
        string? blocklistPath = null)
    {
        if (modules is null)
            return [Unavailable(at, FactAvailability.ReadError, "核心模組清單讀不到——沒有可檢視的驅動，不猜")];

        var nonWin = new List<KernelModuleEntry>();
        foreach (var m in modules)
            if (!KernelModuleService.IsWindowsDirectory(m.Path)) nonWin.Add(m);
        if (nonWin.Count == 0)
            return [Unavailable(at, FactAvailability.NotApplicable,
                "載入模組全部在 \\Windows\\ 下——沒有非系統目錄的驅動需要檢視（不是錯誤，是環境狀態）")];

        var rules = LoadBlocklist(blocklistPath, out string? blocklistNote);
        var facts = new List<HardwareFact> { CountFact(nonWin.Count, at) };
        int truncated = 0;
        for (int i = 0; i < nonWin.Count; i++)
        {
            if (facts.Count >= MaxDrivers) { truncated = nonWin.Count - i; break; }
            var m = nonWin[i];
            var m2 = m;
            byte[]? bytes;
            try { bytes = readFile(m2.Path); }
            catch (Exception e) { bytes = null; m2 = m2 with { SignatureNote = e.Message }; }
            if (bytes is null)
            {
                // 裝置空間路徑（\Device\HarddiskVolumeN\…、\SystemRoot\…、\??\…）File API 讀不了——
                // 先正規化成 Win32 路徑再試一次；兩次都失敗才如實三態。
                string win32 = NormalizeDriverPath(m2.Path);
                if (!string.Equals(win32, m2.Path, StringComparison.OrdinalIgnoreCase))
                {
                    try { bytes = readFile(win32); } catch { bytes = null; }
                }
            }
            if (bytes is null)
            {
                facts.Add(new HardwareFact(
                    $"drvinsp.{i}.summary", Category, $"驅動靜態檢視 #{i}（{BaseName(m.Path)}）",
                    "檔案讀不到——靜態檢視無從進行；讀不到不是 0 也不是「沒有內容」", "",
                    Source, FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.ReadError, $"讀取失敗：{m.SignatureNote}"));
                continue;
            }

            facts.Add(SummaryFact(i, m.Path, bytes, rules, blocklistNote, at));
        }
        if (truncated > 0)
            facts.Add(new HardwareFact(
                "drvinsp.truncated", Category, "驅動靜態檢視截斷",
                $"另有 {truncated} 顆非系統目錄驅動超出本版檢視上限（{MaxDrivers} 顆）——如實說明，不假裝檢視完", "",
                Source, FactTrustLevel.Unknown, false, at));
        return facts;
    }

    private static HardwareFact CountFact(int total, DateTimeOffset at) =>
        new(CountKey, Category, "驅動靜態檢視總數",
            $"非系統目錄驅動 {total} 顆（本版檢視上限 {MaxDrivers} 顆）。檢視的是檔案內容的靜態形狀——PE 結構、內嵌字串、IOCTL 編碼候選；不載入驅動、不呼叫 IOCTL，也不是行為驗證", "",
            Source, FactTrustLevel.Measured, false, at, total);

    private static HardwareFact SummaryFact(int index, string path, byte[] bytes,
        IReadOnlyList<ByovdBlockRule> rules, string? blocklistNote, DateTimeOffset at)
    {
        var insp = PeInspect.Inspect(bytes);
        string name = BaseName(path);
        if (!insp.Parsed)
            return new HardwareFact(
                $"drvinsp.{index}.summary", Category, $"驅動靜態檢視 #{index}（{name}）",
                $"PE 解析失敗：{insp.ParseError}——如實回報，不猜其餘結構", "",
                Source, FactTrustLevel.Unknown, false, at);

        var text = new StringBuilder();
        text.Append($"{insp.MachineText}、{insp.SubsystemText}、{insp.Sections.Count} 個區段");
        if (insp.ImportedDlls.Count > 0)
            text.Append("；匯入 " + string.Join("、", insp.ImportedDlls.Take(4)) + (insp.ImportedDlls.Count > 4 ? " 等" : ""));
        if (insp.DeviceStrings.Count > 0)
            text.Append("；裝置字串 " + string.Join("、", insp.DeviceStrings.Take(3)) + (insp.DeviceStrings.Count > 3 ? " 等" : ""));
        text.Append($"；IOCTL 候選 {insp.IoctlCandidates.Count} 個");
        if (insp.IoctlCandidates.Count > 0)
            text.Append("（" + string.Join("、", insp.IoctlCandidates.Take(3).Select(c =>
                $"0x{c.Raw:X8} {c.MethodText}")) + (insp.IoctlCandidates.Count > 3 ? " 等" : "") + "）——候選不是確認");

        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        var hit = MatchBlocklist(sha, name, rules);
        text.Append(hit is { } h ? $"；SHA-256 命中封鎖清單（{h.FriendlyName ?? h.Sha256?[..16]}）——攻擊面事實，不是中毒判決" : "；SHA-256 未命中封鎖清單");
        if (blocklistNote is not null) text.Append($"；{blocklistNote}");

        return new HardwareFact(
            $"drvinsp.{index}.summary", Category, $"驅動靜態檢視 #{index}（{name}）",
            text.ToString(), "", Source, FactTrustLevel.Measured, false, at);
    }

    /// <summary>SHA-256 為主、檔名為輔的雙道比對（與 ByovdCompareService 同一條界線：命中是攻擊面事實）。</summary>
    internal static ByovdBlockRule? MatchBlocklist(string sha256Hex, string fileName, IReadOnlyList<ByovdBlockRule> rules)
    {
        foreach (var r in rules)
        {
            if (r.Sha256 is { } s && s.Equals(sha256Hex, StringComparison.OrdinalIgnoreCase)) return r;
            if (r.FileName is { } f && f.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return r;
        }
        return null;
    }

    private static IReadOnlyList<ByovdBlockRule> LoadBlocklist(string? path, out string? note)
    {
        note = null;
        string? p = path ?? ByovdCompareService.DefaultBlocklistPath();   // 推導不出路徑時如實 null，下一行已擋
        if (p is null || !File.Exists(p))
        {
            note = "封鎖清單不存在——沒有可比對的清單（如實標注，不假裝比對過）";
            return [];
        }
        try
        {
            return ByovdBlocklistDecoder.Parse(File.ReadAllText(p));
        }
        catch (Exception e)
        {
            note = $"封鎖清單解析失敗（{e.Message}）——比對以未命中處理並如實標注";
            return [];
        }
    }

    /// <summary>
    /// 把核心模組列舉回來的裝置空間路徑轉成 Win32 路徑：\SystemRoot\ → %SystemRoot%、
    /// \??\ 前綴剝掉、\Device\HarddiskVolumeN\ → 以 QueryDosDevice 對映到磁碟代號。
    /// 對不出來就原樣回傳——讓後續的讀取如實失敗，不猜。
    /// </summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string? deviceName, System.Text.StringBuilder target, int max);

    internal static string NormalizeDriverPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (path.StartsWith("\\SystemRoot\\", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable("SystemRoot") + path["\\SystemRoot".Length..];
        if (path.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase))
            return path["\\??\\".Length..];
        if (path.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                var sb = new System.Text.StringBuilder(260);
                if (QueryDosDevice(drive.Name.TrimEnd('\\'), sb, sb.Capacity) > 0
                    && path.StartsWith(sb.ToString() + "\\", StringComparison.OrdinalIgnoreCase))
                    return drive.Name + path[(sb.ToString().Length + 1)..];
            }
        }
        return path;
    }

    private static string BaseName(string path) => path.Split('\\', '/')[^1];

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason) =>
        new(CountKey, Category, "驅動靜態檢視總數", "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
