using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// UEFI 簽章資料庫的另外兩個成員（U10-FW-0005 的同構擴充）：
/// <b>dbt</b>（<c>EFI_IMAGE_SECURITY_DATABASE2</c>，授權時戳簽章資料庫）與
/// <b>dbr</b>（授權復原簽章資料庫）的存在性、計數與逐條清單。
/// 形狀與 <see cref="UefiSignatureFactsService"/> 的 db／dbx／KEK 完全同構
/// （讀變數 → <see cref="EfiSigListDecoder"/> 解 EFI_SIGNATURE_LIST → count ＋ list.0..5）。
///
/// <para>
/// <b>GUID 與變數名的查證（2026-10-25）：</b>
/// </para>
/// <list type="bullet">
/// <item><b>dbt</b>：EDK2 <c>MdePkg/Include/Guid/ImageAuthentication.h</c> 明文
///   <c>#define EFI_IMAGE_SECURITY_DATABASE2 L"dbt"</c>，與 db／dbx 共用同一個命名空間 GUID
///   <c>EFI_IMAGE_SECURITY_DATABASE_GUID</c>＝
///   <c>{d719b2cb-3d3a-4596-a3bc-dad00e67656f}</c>（UEFI 2.5 起收錄）。
///   該 GUID 與本倉庫 <see cref="FirmwareVariableAccess.ImageSecurityGuid"/> 同值——
///   那個值在 2026-10-18 已用真機實測釘死（db 8188 bytes、dbx 3724 bytes）。</item>
/// <item><b>dbr</b>：UEFI 2.10 §32.4.2 把 <c>db、dbt、dbx、dbr</c> 並列為
///   「UEFI signature database variables」（四者都須存於防竄改 NVRAM），
///   同屬 <c>EFI_IMAGE_SECURITY_DATABASE_GUID</c> 命名空間；UEFI 2.10 §3（Boot Manager）的
///   Secure Boot Policy Variables 亦含 dbr。其語意是<b>授權復原（OsRecovery）簽章資料庫</b>。
///   <b>它與「出廠預設」的 <c>dbDefault／dbxDefault／dbtDefault／dbrDefault</c> 是不同的變數</b>
///   （§32.4.2：平台廠商可用那組 <c>*Default</c> 變數提供預設集）——本服務只讀 <c>dbr</c> 本身，
///   不讀 <c>*Default</c>，兩者不得混為一談。</item>
/// </list>
///
/// <para>
/// <b>界線：</b>通路完全走 <see cref="FirmwareVariableAccess"/>（單一來源：權限啟用一次、
/// 富結果帶回 Win32 錯誤碼），<b>本服務不寫第二份權限啟用、不做第二條 P/Invoke</b>。
/// 解碼完全走 <see cref="EfiSigListDecoder"/>（UEFI Spec §32.4.1），不重寫。
/// </para>
/// </summary>
public static class UefiDbtDbrFactsService
{
    private const string Category = "信賴根";

    /// <summary>
    /// 與 <see cref="UefiSignatureFactsService"/> 同一條字面規則：
    /// <c>*CountKey</c> 的名字裡有 <c>Key</c>（掃描器收錄成鍵），
    /// <c>*ListPrefix</c> 刻意不含 <c>Key</c>（動態家族前綴，由
    /// <c>FactKeyDynamicCatalog</c> 收錄，進鍵集會憑空多出不存在的一把）。
    /// </summary>
    public const string DbtCountKey = "uefi.dbt.count";
    public const string DbtListPrefix = "uefi.dbt.list.";
    public const string DbrCountKey = "uefi.dbr.count";
    public const string DbrListPrefix = "uefi.dbr.list.";

    /// <summary>逐條清單最多列幾條（與 db／dbx／KEK 的 5 條一致）。</summary>
    private const int MaxListFacts = 5;

    /// <summary>變數名 → 顯示名（值裡的稱呼，含語意）。</summary>
    private static string DisplayName(string varName) => varName switch
    {
        "dbt" => "dbt（授權時戳簽章資料庫）",
        "dbr" => "dbr（授權復原簽章資料庫）",
        _ => varName,
    };

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<string, byte[]?>? readVariable = null)
        => CollectCore(at, readVariable is not null ? RichWrap(readVariable) : RealReader());

    /// <summary>
    /// 富讀取版（測試用）：直接給 <see cref="FirmwareVariableAccess.VariableReadResult"/>，
    /// 才能注入「權限不足」「其他 Win32 錯誤」這些 byte[]? 表達不了的狀態。
    /// </summary>
    internal static IReadOnlyList<HardwareFact> CollectWithRichReader(DateTimeOffset at,
        Func<string, FirmwareVariableAccess.VariableReadResult> reader)
        => CollectCore(at, reader);

    private static IReadOnlyList<HardwareFact> CollectCore(DateTimeOffset at,
        Func<string, FirmwareVariableAccess.VariableReadResult> reader)
    {
        var facts = new List<HardwareFact>();
        CollectDb(facts, "dbt", DbtCountKey, DbtListPrefix, reader, at);
        CollectDb(facts, "dbr", DbrCountKey, DbrListPrefix, reader, at);
        return facts;
    }

    private static void CollectDb(List<HardwareFact> facts, string varName, string countKey,
        string listPrefix, Func<string, FirmwareVariableAccess.VariableReadResult> reader,
        DateTimeOffset at)
    {
        string name = DisplayName(varName);
        var read = reader(varName);
        byte[]? data = read.Data;
        if (data is null || data.Length < 28)
        {
            // 「為什麼讀不到」由 Win32 錯誤碼決定（FirmwareVariableAccess.DescribeFailure），
            // 六態照原因如實分：變數不存在＝NotApplicable（UEFI 允許韌體不佈建 dbt/dbr），
            // 權限沒啟用＝InsufficientPrivilege，其餘＝NotSupported——不把失敗讀成「沒有」。
            if (data is null)
            {
                var (availability, note) = ClassifyFailure(read);
                facts.Add(new HardwareFact(countKey, Category, name, "", "",
                    FirmwareVariableAccess.Source, FactTrustLevel.Unknown, false, at, null,
                    availability,
                    FirmwareVariableAccess.DescribeFailure(varName, read) + " " + note));
                return;
            }
            facts.Add(new HardwareFact(countKey, Category, name, "", "",
                FirmwareVariableAccess.Source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotSupported,
                $"{varName} 存在但只有 {data.Length} bytes（< 28：連一個 EFI_SIGNATURE_LIST 標頭都放不下）"
                + "——如實拒解，不猜"));
            return;
        }

        var lists = EfiSigListDecoder.Decode(data);
        int totalSigs = lists.Sum(l => l.SignatureCount);
        facts.Add(new HardwareFact(countKey, Category, name,
            $"{totalSigs} 條簽章（{lists.Count} 條清單）、{data.Length} bytes、"
            + FirmwareVariableAccess.DescribeAttributes(read.Attributes), "條",
            $"{FirmwareVariableAccess.Source} + EfiSigListDecoder", FactTrustLevel.Derived, false,
            at, totalSigs));
        for (int i = 0; i < lists.Count && i < MaxListFacts; i++)
        {
            var l = lists[i];
            facts.Add(new HardwareFact($"{listPrefix}{i}", Category, $"{name} 清單 {i}",
                $"類型 {l.TypeGuid}・{l.SignatureCount} 條簽章・{l.TotalBytes} bytes", "",
                "EfiSigListDecoder（UEFI Spec §32.4.1）", FactTrustLevel.Measured, false, at,
                l.SignatureCount));
        }
    }

    /// <summary>
    /// 「變數讀不到」的六態歸類：不存在＝NotApplicable（那是韌體狀態，不是失敗）、
    /// 權限＝InsufficientPrivilege、其餘＝NotSupported。附一句各自的界線。
    /// </summary>
    private static (FactAvailability Availability, string Note) ClassifyFailure(
        FirmwareVariableAccess.VariableReadResult read)
    {
        if (read.Win32Error == FirmwareVariableAccess.ErrorFileNotFound)
            return (FactAvailability.NotApplicable,
                "這個變數不存在的意思是「韌體沒有佈建這個資料庫」——UEFI 允許 dbt／dbr 不存在"
                + "（它們不是 Secure Boot 的必備變數），這是環境事實，不是讀取失敗");
        if (read.Win32Error == FirmwareVariableAccess.ErrorPrivilegeNotHeld || !read.PrivilegeEnabled)
            return (FactAvailability.InsufficientPrivilege,
                "**這不是「變數不存在」**——是這條通路的前置條件沒成立（需管理員並啟用 SeSystemEnvironmentPrivilege）");
        return (FactAvailability.NotSupported,
            "讀取失敗的原因由上面的 Win32 錯誤碼決定，不能讀成「沒有這個資料庫」");
    }

    /// <summary>注入路徑：byte[]? 包成富結果（null 一律讀作「變數不存在」＝Win32 2）。</summary>
    private static Func<string, FirmwareVariableAccess.VariableReadResult> RichWrap(
        Func<string, byte[]?> injected)
        => name =>
        {
            byte[]? data = injected(name);
            return new FirmwareVariableAccess.VariableReadResult(data,
                data is null ? FirmwareVariableAccess.ErrorFileNotFound : 0u, 0u, true);
        };

    /// <summary>
    /// 真機路徑：<b>權限啟用一次</b>（與 <see cref="UefiSignatureFactsService"/> 同一輪各啟用一次
    /// 是現狀——同一輪不重複動權杖指的是本服務內部），逐變數走
    /// <see cref="FirmwareVariableAccess.Read"/>。dbt／dbr 與 db／dbx 同屬
    /// <see cref="FirmwareVariableAccess.ImageSecurityGuid"/> 命名空間（見檔頭查證）。
    /// </summary>
    private static Func<string, FirmwareVariableAccess.VariableReadResult> RealReader()
    {
        bool privileged = FirmwareVariableAccess.EnablePrivilege();
        return name => FirmwareVariableAccess.Read(name, FirmwareVariableAccess.ImageSecurityGuid,
            privileged);
    }
}
