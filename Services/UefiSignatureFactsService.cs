using System.Text;

namespace XinSpect;

/// <summary>
/// UEFI 變數完整審計（V7 WP14 延伸）：db/dbx/KEK/PK 簽章清單＋Boot#### 條目名稱。
/// 全部透過 GetFirmwareEnvironmentVariableEx（唯讀），需系統管理員。
/// </summary>
public static class UefiSignatureFactsService
{
    private const string Category = "信賴根";

    /// <summary>
    /// db／dbx／KEK 的「條數」鍵與「逐條清單」家族前綴，<b>以字面宣告在這裡</b>。
    ///
    /// <para>
    /// <b>為什麼不直接在 <see cref="CollectSigList"/> 裡用 <c>keyPrefix + ".count"</c> 組出來就好：</b>
    /// 那些鍵的成員是<b>固定的</b>（db／dbx／KEK 各一組），所以它們該進靜態目錄；
    /// 而靜態目錄是掃原始碼長出來的，<b>掃描器只認字面</b>——
    /// 組出來的鍵它看不到，於是「目錄沒有這把鍵」與「執行期真的在產這把鍵」同時成立。
    /// 2026-10-17 由 <c>EfiVariableFactsTests.執行期產生的鍵都要被目錄或動態家族收錄</c> 照出來；
    /// 同一件事在 <c>FactKeyCatalogTests.目錄與原始碼掃描逐鍵相等</c> 那一邊會變成
    /// 「目錄有而原始碼沒有」的紅燈。<b>兩邊都要收，就得讓它成為字面。</b>
    /// </para>
    ///
    /// <para>
    /// <b>命名有個講究：</b><c>*CountKey</c> 的名字裡有 <c>Key</c>，因為它是<b>一把鍵</b>，
    /// 要讓掃描器收錄；<c>*ListPrefix</c> 的名字刻意<b>不含</b> <c>Key</c>，因為它是
    /// <b>動態家族的前綴、不是一把鍵</b>——掃描器的字面規則把 <c>const string ...Key... = "..."</c>
    /// 當成鍵，前綴進鍵集會憑空多出一把不存在的鍵。前綴仍能被
    /// <c>FactKeyCatalogTests.動態鍵家族的每個前綴都要真的有人在原始碼裡生產</c>
    /// 查證（它查的是原始碼裡有沒有那個字面）。
    /// </para>
    /// </summary>
    public const string DbCountKey = "uefi.db.count";
    public const string DbListPrefix = "uefi.db.list.";
    public const string DbxCountKey = "uefi.dbx.count";
    public const string DbxListPrefix = "uefi.dbx.list.";
    public const string KekCountKey = "uefi.kek.count";
    public const string KekListPrefix = "uefi.kek.list.";

    // ═══ 這一支不再自己宣告 P/Invoke，也不再自己啟用權限 ═══
    //
    // 2026-10-18 修掉一個實質缺陷：**這一支原本根本沒有啟用 SeSystemEnvironmentPrivilege**，
    // 而該權限在預設權杖裡是「持有但未啟用」——所以 `uefi.db`／`dbx`／`kek`／`pk`／
    // `uefi.boot_entries.count` 在真機上全部讀不到，**而理由欄寫的是「可能未進入 Setup Mode」：
    // 一個沒有查證的因果推測**。實測該機器的真實錯誤是 1314 ERROR_PRIVILEGE_NOT_HELD，
    // 手動啟用權限後同一個變數立刻讀得到（BootOrder 18 bytes、PK 886 bytes）。
    //
    // 而舊版的「兩段式查大小」也是錯的：`GetFirmwareEnvironmentVariableEx` 的第五個參數是
    // `pdwAttributes`（in/out），**不是大小**；以 dwSize＝0 呼叫不會回報所需大小，
    // 所以 `size` 永远是 0、每次都回 null。
    //
    // 現在：啟用權限與讀取都在 <see cref="FirmwareVariableAccess"/>，
    // **一趟只啟用一次**，失敗原因由 Win32 錯誤碼決定（見 DescribeFailure）。

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<string, byte[]?>? readVariable = null)
    {
        var reader = RichReader(readVariable);
        var facts = new List<HardwareFact>();

        // ── db（允許的簽章資料庫）──
        CollectSigList(facts, "uefi.db", DbCountKey, DbListPrefix, "db（允許簽章資料庫）", "db", FirmwareVariableAccess.ImageSecurityGuid, reader, at);
        // ── dbx（撤銷簽章資料庫）──
        CollectSigList(facts, "uefi.dbx", DbxCountKey, DbxListPrefix, "dbx（撤銷簽章資料庫）", "dbx", FirmwareVariableAccess.ImageSecurityGuid, reader, at);
        // ── KEK（金鑰交換金鑰）──
        CollectSigList(facts, "uefi.kek", KekCountKey, KekListPrefix, "KEK（金鑰交換金鑰）", "KEK", FirmwareVariableAccess.GlobalVariableGuid, reader, at);
        // ── PK（平台金鑰）──
        var pk = reader("PK");
        facts.Add(pk.Data is { Length: > 16 }
            ? new HardwareFact("uefi.pk", Category, "PK（平台金鑰）",
                $"存在（{pk.Data.Length} bytes）、{FirmwareVariableAccess.DescribeAttributes(pk.Attributes)}", "",
                FirmwareVariableAccess.Source, FactTrustLevel.Reported, false, at, pk.Data.Length)
            : new HardwareFact("uefi.pk", Category, "PK（平台金鑰）", "", "",
                FirmwareVariableAccess.Source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotSupported,
                pk.Data is null
                    ? FirmwareVariableAccess.DescribeFailure("PK", pk)
                    : "PK 存在但長度 ≤16 bytes——**不當成一把有效的平台金鑰**（PK 是 EFI_SIGNATURE_LIST）"));

        // ── Boot#### 條目 ──
        CollectBootEntries(facts, reader, at);
        return facts;
    }

    private static void CollectSigList(List<HardwareFact> facts, string keyPrefix, string countKey,
        string listPrefix, string name, string varName, string guid,
        Func<string, FirmwareVariableAccess.VariableReadResult> reader, DateTimeOffset at)
    {
        var read = reader(varName);
        byte[]? data = read.Data;
        if (data is null || data.Length < 28)
        {
            // 理由由 Win32 錯誤碼決定：權限不足／變數不存在／其他——**不是「可能未進入 Setup Mode」這種推測**
            // （PK 在 Setup Mode 會不存在，但那是 PK 那一格的事，而且它也只在自己真的回 2 時才這樣說）。
            string reason = data is null
                ? FirmwareVariableAccess.DescribeFailure(varName, read)
                : $"{varName} 存在但只有 {data.Length} bytes（< 28：連一個 EFI_SIGNATURE_LIST 標頭都放不下）";
            facts.Add(new HardwareFact(keyPrefix, Category, name, "", "",
                FirmwareVariableAccess.Source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotSupported, reason));
            return;
        }
        var lists = EfiSigListDecoder.Decode(data);
        int totalSigs = lists.Sum(l => l.SignatureCount);
        facts.Add(new HardwareFact(countKey, Category, name,
            $"{totalSigs} 條簽章（{lists.Count} 條清單）、{data.Length} bytes、"
            + FirmwareVariableAccess.DescribeAttributes(read.Attributes), "條",
            $"{FirmwareVariableAccess.Source} + EfiSigListDecoder", FactTrustLevel.Derived, false, at, totalSigs));
        for (int i = 0; i < lists.Count && i < 5; i++)
        {
            var l = lists[i];
            facts.Add(new HardwareFact($"{listPrefix}{i}", Category, $"{name} 清單 {i}",
                $"類型 {l.TypeGuid}・{l.SignatureCount} 條簽章・{l.TotalBytes} bytes", "",
                "EfiSigListDecoder（UEFI Spec §32.4.1）", FactTrustLevel.Measured, false, at, l.SignatureCount));
        }
    }

    private static void CollectBootEntries(List<HardwareFact> facts,
        Func<string, FirmwareVariableAccess.VariableReadResult> reader, DateTimeOffset at)
    {
        var bootEntries = new List<(ushort Index, string Description)>();
        var failures = new List<uint>();
        for (ushort idx = 0; idx < 0x20; idx++)
        {
            string varName = $"Boot{idx:X4}";
            var read = reader(varName);
            byte[]? data = read.Data;
            if (data is null) { failures.Add(read.Win32Error); continue; }
            if (data.Length < 6) continue;
            // EFI_LOAD_OPTION：Attributes u32 + FilePathListLength u16 + Description（UTF-16LE null-terminated）
            int descOff = 6;
            var sb = new StringBuilder();
            while (descOff + 1 < data.Length)
            {
                ushort ch = BitConverter.ToUInt16(data, descOff);
                if (ch == 0) break;
                sb.Append((char)ch);
                descOff += 2;
            }
            bootEntries.Add((idx, sb.ToString()));
            if (bootEntries.Count >= 20) break;
        }

        // **「一個都沒有」與「全都讀不到」是不同的斷言**：前者是查證後的答案，
        // 後者是通路失敗——而失敗的原因由錯誤碼決定（權限／不存在／其他），不猜。
        bool blocked = bootEntries.Count == 0 && failures.Count > 0
            && failures.All(e => e != FirmwareVariableAccess.ErrorFileNotFound);
        string bootReason = blocked
            ? $"Boot0000–Boot001F 全部讀不到（Win32 {(failures.Count > 0 ? failures[0] : 0u)}）——"
              + (failures.Contains(FirmwareVariableAccess.ErrorPrivilegeNotHeld)
                  ? "**行程權杖沒有 SeSystemEnvironmentPrivilege**，這不是「沒有開機條目」"
                  : "**通路失敗，不能讀成「沒有開機條目」**")
            : "";
        facts.Add(new HardwareFact("uefi.boot_entries.count", Category, "UEFI 開機條目",
            bootEntries.Count == 0 ? "0 個開機條目（查證後沒有）" : $"{bootEntries.Count} 個開機條目", "個",
            FirmwareVariableAccess.Source + "（Boot####）", FactTrustLevel.Reported, false, at,
            bootEntries.Count, blocked ? FactAvailability.NotSupported : FactAvailability.Present,
            blocked ? bootReason : null));
        for (int i = 0; i < bootEntries.Count && i < 10; i++)
        {
            var (idx, desc) = bootEntries[i];
            facts.Add(new HardwareFact($"uefi.boot.{idx}", Category, $"Boot{idx:X4}",
                string.IsNullOrEmpty(desc) ? $"Boot{idx:X4}（無描述）" : desc, "",
                "GetFirmwareEnvironmentVariableEx（EFI_LOAD_OPTION Description）", FactTrustLevel.Reported, false, at, null));
        }
    }

    /// <summary>
    /// 讀韌體環境變數（薄通路）。回 <c>null</c> 表示讀不到——
    /// <b>而「讀不到」的原因要看 <see cref="FirmwareVariableAccess.Read"/> 的富結果</b>，
    /// 這一支只給需要 <c>byte[]?</c> 的既有呼叫端（<c>EspScanService</c>、<c>EvidenceLabService</c>）。
    /// </summary>
    internal static byte[]? ReadFirmwareVar(string name)
        => FirmwareVariableAccess.Read(name, GuidFor(name)).Data;

    /// <summary>變數名 → 它所屬的 UEFI 命名空間（<c>db*</c> 在 Image Security，其餘在 Global）。</summary>
    internal static string GuidFor(string name)
        => name.StartsWith("db", StringComparison.Ordinal)
            ? FirmwareVariableAccess.ImageSecurityGuid
            : FirmwareVariableAccess.GlobalVariableGuid;

    /// <summary>
    /// 建立本輪的讀取器：注入路徑（測試）把 <c>byte[]?</c> 包成富結果
    /// （<c>null</c> 一律讀作「變數不存在」＝Win32 2，且不宣稱權限有問題）；
    /// 真機路徑則<b>先啟用一次權限</b>再逐變數讀——同一輪不重複動權杖。
    /// </summary>
    private static Func<string, FirmwareVariableAccess.VariableReadResult> RichReader(
        Func<string, byte[]?>? injected)
    {
        if (injected is not null)
            return name =>
            {
                byte[]? data = injected(name);
                return new FirmwareVariableAccess.VariableReadResult(data,
                    data is null ? FirmwareVariableAccess.ErrorFileNotFound : 0u, 0u, true);
            };

        bool privileged = FirmwareVariableAccess.EnablePrivilege();
        return name => FirmwareVariableAccess.Read(name, GuidFor(name), privileged);
    }
}
