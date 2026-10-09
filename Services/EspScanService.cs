using System.IO;
using System.Management;
using System.Security.Cryptography;

namespace XinSpect;

/// <summary>ESP 上的一個 .efi 檔：相對路徑、大小、SHA-256、是否命中 dbx。</summary>
public sealed record EspFileInfo(string RelativePath, long SizeBytes, string Sha256Hex, bool DbxHit);

/// <summary>
/// EFI 系統分割區（ESP）的檔案層掃描（Velociraptor UEFI Artifacts 那條能力的唯讀版）：
/// 列舉 ESP 上的 .efi 檔（名稱、大小、SHA-256），並與韌體 dbx（撤銷簽章資料庫）交叉引用。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼是它：</b>本專案讀 db/dbx（韌體層，<see cref="UefiSignatureFactsService"/>）卻從來沒看過
/// ESP 檔案層——Secure Boot 撤銷的是「特定雜湊的 .efi 檔」，那些檔就躺在 ESP 上；兩層對起來，
/// 「這台機器的 ESP 上有沒有被 dbx 撤銷的檔」才答得出來。唯讀、離線、需要管理員讀檔。
/// </para>
/// <para>
/// <b>誠實界線：</b>命中 dbx 是<b>攻擊面事實</b>——代表該檔已被微軟撤銷，不判決「已中毒」；
/// 沒命中也不代表安全（dbx 只收錄已知惡意）。檔案讀不到（權限、佔用）如實標注；
/// dbx 讀不到時比對未進行、如實說明，不假裝比對過。檔案數上限 64——超過如實截斷，
/// 不假裝掃完。
/// </para>
/// </remarks>
public static class EspScanService
{
    private const string Category = "韌體安全";
    public const string CountKey = "esp.partitions.count";
    private const string Source = "ESP 檔案層掃描（唯讀）÷ dbx 交叉引用";
    private const int MaxFiles = 64;

    /// <summary>
    /// 掃描 ESP。三個接縫供測試注入：分割區列舉、檔案讀取、dbx 讀取——
    /// 生產路徑分別是 WMI（Win32_DiskPartition → Win32_Volume）、File.ReadAllBytes、
    /// UefiSignatureFactsService.ReadFirmwareVar("dbx")。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<string>?>? listVolumes = null,
        Func<string, byte[]?>? readFile = null,
        Func<byte[]?>? readDbx = null)
    {
        listVolumes ??= ListEspVolumes;
        readFile ??= path => { try { return File.ReadAllBytes(path); } catch { return null; } };
        readDbx ??= () =>
        {
            // dbx 要 SeSystemEnvironmentPrivilege——與 UefiBootFactsService 同一個啟用流程（best-effort；
            // 拿不到時 GetFirmwareEnvironmentVariableEx 回 null，由後續如實標「dbx 讀不到」）
            UefiBootFactsService.EnableFirmwarePrivilege();
            return UefiSignatureFactsService.ReadFirmwareVar("dbx");
        };

        var volumes = listVolumes();
        if (volumes is null)
            return [Unavailable(at, FactAvailability.ReadError,
                "ESP 列舉失敗（WMI 查詢 Win32_DiskPartition／Win32_Volume 擲例外）——讀不到不猜")];
        if (volumes.Count == 0)
            return [Unavailable(at, FactAvailability.NotApplicable,
                "找不到 EFI 系統分割區——這台機器沒有 ESP（非 UEFI 開機或特殊配置），不是錯誤，是環境狀態")];

        // 列舉每個 ESP 的 .efi 檔
        var files = new List<EspFileInfo>();
        bool truncated = false, anyReadError = false;
        foreach (var vol in volumes)
        {
            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(vol, "*.efi", SearchOption.AllDirectories)
                    .Take(MaxFiles + 1);
            }
            catch
            {
                anyReadError = true;
                files.Add(new EspFileInfo($"（列舉失敗：{vol}）", 0, "", false));
                continue;
            }
            foreach (var p in paths)
            {
                if (files.Count >= MaxFiles) { truncated = true; break; }
                byte[]? bytes;
                try { bytes = readFile(p); }
                catch { bytes = null; }
                if (bytes is null)
                {
                    anyReadError = true;
                    files.Add(new EspFileInfo(Relative(vol, p) + "（讀不到）", 0, "", false));
                    continue;
                }
                string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                files.Add(new EspFileInfo(Relative(vol, p), bytes.Length, sha, false));
            }
            if (truncated) break;
        }

        // dbx 交叉引用：dbx 讀得到才比對；讀不到如實說明，不假裝比對過
        byte[]? dbx = null;
        bool dbxReadFailed = false;
        try { dbx = readDbx(); }
        catch (Exception e)
        {
            dbxReadFailed = true;
            _ = e.Message;
        }
        var dbxHashes = !dbxReadFailed && dbx is not null
            ? new HashSet<string>(EfiSigListDecoder.DecodeSha256Hashes(dbx), StringComparer.Ordinal)
            : null;
        int hits = 0;
        if (dbxHashes is not null)
        {
            for (int i = 0; i < files.Count; i++)
                if (files[i].Sha256Hex.Length > 0 && dbxHashes.Contains(files[i].Sha256Hex))
                    files[i] = files[i] with { DbxHit = true };
            hits = files.Count(f => f.DbxHit);
        }
        string dbxText;
        if (dbxHashes is null)
            dbxText = dbxReadFailed
                ? "dbx 讀取失敗——比對未進行，如實標注，不假裝比對過"
                : "dbx 讀不到（變數不存在或無權）——比對未進行，如實標注，不假裝比對過";
        else
            dbxText = hits > 0
                ? $"dbx（撤銷簽章資料庫）比對：{dbxHashes.Count} 條 SHA-256 簽章，命中 {hits} 個檔案——攻擊面事實（已被微軟撤銷），不是中毒判決"
                : $"dbx 比對：{dbxHashes.Count} 條 SHA-256 簽章，未命中——沒命中也不代表安全（dbx 只收錄已知惡意）";

        var facts = new List<HardwareFact>
        {
            new(CountKey, Category, "EFI 系統分割區（ESP）",
                $"{volumes.Count} 個 ESP；.efi 檔案 {files.Count(f => f.SizeBytes > 0)} 個"
                + (truncated ? $"（超出上限 {MaxFiles}，已截斷——不假裝掃完）" : "")
                + (anyReadError ? "；有讀取失敗的項目（已逐項標注）" : "")
                + "。掃描是唯讀的；這是檔案清單與雜湊的事實，不是對任何檔案的好壞判決", "",
                Source, FactTrustLevel.Measured, false, at, volumes.Count),
        };

        var list = new System.Text.StringBuilder();
        list.Append(string.Join("；", files.Take(16).Select(f =>
        {
            string name = f.RelativePath;
            string body = f.Sha256Hex.Length > 0
                ? $"{FormatSize(f.SizeBytes)}, SHA-256 {f.Sha256Hex[..16]}…{(f.DbxHit ? "，dbx 命中" : "")}"
                : "（無雜湊）";
            return $"{name}：{body}";
        })));
        if (files.Count > 16) list.Append($" 等 {files.Count} 個");
        facts.Add(new HardwareFact("esp.files", Category, "ESP 上的 .efi 檔案",
            files.Count == 0 ? "沒有找到任何 .efi 檔——ESP 可能是空的或只裝了非 .efi 內容，如實回報" : list.ToString(),
            "", Source, FactTrustLevel.Measured, false, at));
        facts.Add(new HardwareFact("esp.dbx", Category, "ESP 檔案 vs dbx 撤銷清單",
            dbxText, "", Source, dbxHashes is not null ? FactTrustLevel.Measured : FactTrustLevel.Unknown, false, at));
        return facts;
    }

    /// <summary>GPT 分割區型別 GUID：EFI 系統分割區（UEFI Spec §5.3.2 定義，語言中立）。</summary>
    public const string EspGptTypeGuid = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";

    /// <summary>
    /// 列舉 ESP 的磁碟區路徑（\\?\Volume{…}\ 形狀）。
    /// <para>
    /// <b>為什麼用 GPT 型別 GUID 而不用 Win32_DiskPartition.Type：</b>Type 是<b>在地化字串</b>——
    /// 本機實測繁中系統回「GPT: 系統」而不是「GPT: EFI System Partition」，<c>LIKE '%EFI%'</c>
    /// 在中文機器上永遠落空、把有 ESP 的機器誤報成「沒有 ESP」（與繁中 Msvm Caption
    /// 「主機電腦系統」同一族的地雷）。GptType 是語言中立的 GUID；MSFT_Partition（Storage WMI）
    /// 自 Win8 起就有，Win32_DiskPartition 的 GptType 欄在本機是空的。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string>? ListEspVolumes()
    {
        try
        {
            var volumes = new List<string>();
            using var searcher = new ManagementObjectSearcher(
                "root\\Microsoft\\Windows\\Storage",
                "SELECT AccessPaths FROM MSFT_Partition WHERE GptType = '" + EspGptTypeGuid + "'");
            foreach (var mo in searcher.Get().Cast<ManagementObject>())
            {
                if (mo["AccessPaths"] is not string[] paths) continue;
                foreach (var p in paths)
                {
                    // AccessPaths 可能含磁碟代號與 \\?\Volume{…}\ 多條；ESP 通常沒有代號，
                    // 取 \\?\Volume 形狀的那條（File API 可用），補齊尾端反斜線供 EnumerateFiles 走子目錄
                    if (p.StartsWith("\\\\?\\Volume{", StringComparison.OrdinalIgnoreCase))
                    {
                        volumes.Add(p.EndsWith("\\") ? p : p + "\\");
                        break;
                    }
                }
            }
            return volumes;
        }
        catch { return null; }
    }

    private static string Relative(string volume, string path) =>
        path.StartsWith(volume, StringComparison.OrdinalIgnoreCase)
            ? path[volume.Length..].TrimStart('\\')
            : path;

    private static string FormatSize(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes >> 20} MB" : $"{Math.Max(1, bytes >> 10)} KiB";

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason) =>
        new(CountKey, Category, "EFI 系統分割區（ESP）", "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
