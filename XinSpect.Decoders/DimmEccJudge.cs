using System;
using System.Collections.Generic;

namespace XinSpect;

/// <summary>一支記憶體模組的匯流排寬度（Type 17 位移 0x0C／0x0D）。0 表示讀不到。</summary>
/// <param name="DataWidth">資料寬度（位元）：64＝無 ECC 的標準模組。</param>
/// <param name="TotalWidth">總寬度（位元）：72＝含 8 位元 ECC 的模組。</param>
public readonly record struct DimmBusWidth(int DataWidth, int TotalWidth);

/// <summary>
/// 記憶體模組匯流排寬度的判讀：<b>ECC 與 registered 是兩件不同的事，不能混為一談。</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼容易講錯：</b>「伺服器記憶體」在一般人印象裡是一個整體，實際上它由三個獨立決定的
/// 屬性組成，而且它們在 SMBIOS 裡是三組不同的欄位：
/// </para>
/// <list type="number">
/// <item><b>匯流排寬度</b>（Type 17 位移 0x0C／0x0D）：資料寬度 64 位元＝無 ECC 的標準模組；
/// 總寬度 72 位元＝帶 8 位元 ECC。<b>這一項才是「這支模組有沒有 ECC 顆粒」的證據。</b></item>
/// <item><b>Registered／Buffered</b>（Type 17 位移 0x15 的 bits 1:0）：registered（RDIMM）、
/// unbuffered（UDIMM）、或 load-reduced（LRDIMM）。這是「記憶體控制器與模組之間有沒有緩衝晶片」，
/// 與 ECC 是兩件事——<b>UDIMM 也可以有 ECC（ECC UDIMM）</b>。</item>
/// <item><b>平台層的錯誤更正能力</b>（Type 16 位移 0x06）：整個陣列是無、同位、單位元 ECC、
/// 多位元 ECC 還是 CRC。這是<b>平台</b>的宣告，不是單支模組的。</item>
/// </list>
/// <para>
/// 三者混著講就會出現「這台有 ECC 所以是伺服器記憶體」這種錯——一台用 ECC UDIMM 的工作站
/// 與一台用 RDIMM 的伺服器，在這一欄會一模一樣。
/// </para>
/// <para><b>本判讀只陳述 SMBIOS 欄位說了什麼，不推論記憶體顆粒或平台架構。</b></para>
/// </remarks>
public static class DimmEccJudge
{
    /// <summary>模組型態（Type 17 的 Registered／Buffered 欄位）。</summary>
    public enum ModuleForm
    {
        /// <summary>一般無緩衝模組（UDIMM）。</summary>
        Unbuffered,
        /// <summary>帶緩衝（registered，RDIMM）。</summary>
        Registered,
        /// <summary>降低負載（LRDIMM）。</summary>
        LoadReduced,
        /// <summary>讀不到或規格外的值。</summary>
        Unknown,
    }

    /// <summary>資料寬度 64 位元的標準值。</summary>
    public const int StandardDataWidth = 64;
    /// <summary>含 ECC 的總寬度（64 資料 + 8 ECC）。</summary>
    public const int EccTotalWidth = 72;

    /// <summary>
    /// 由匯流排寬度判斷模組是否帶 ECC。
    /// 總寬度比資料寬度多＝多出來的就是 ECC 位元；兩者相等＝沒有 ECC。
    /// <b>讀不到（0）時回 null，不當成「沒有 ECC」。</b>
    /// </summary>
    [SpecRef("SMBIOS Specification, Memory Device (Type 17)：offset 0x0C 為 Size（此處指 Total Width，單位位元）、0x0D 為 Data Width。Total Width ＝ Data Width 表示無錯誤更正位元；Total Width ＞ Data Width 表示含 ECC 位元（標準 64+8＝72）。欄位為 0 或 0xFFFF 表示未知，不得解讀為 64。")]
    public static bool? HasEcc(DimmBusWidth w)
    {
        if (w.DataWidth <= 0 || w.TotalWidth <= 0) return null;
        if (w.TotalWidth == 0xFFFF || w.DataWidth == 0xFFFF) return null;
        return w.TotalWidth > w.DataWidth;
    }

    /// <summary>由 Registered／Buffered 欄位判斷模組型態。</summary>
    [SpecRef("SMBIOS Specification, Memory Device (Type 17) offset 0x15：Registered/Unbuffered 欄位（bits 1:0）——00h 未知、01h 其他、02h 未知、03h 已註冊（Registered／Buffered）、04h 未緩衝（Unbuffered）。保留或未收錄的值如實標未知，不猜。")]
    public static ModuleForm FormOf(byte registeredField) => registeredField switch
    {
        0x03 => ModuleForm.Registered,
        0x04 => ModuleForm.Unbuffered,
        _ => ModuleForm.Unknown,
    };

    /// <summary>模組型態的中文名。</summary>
    [SpecRef("SMBIOS Specification, Memory Device (Type 17) offset 0x15：Registered/Unbuffered 欄位（bits 1:0）的代碼 03h＝Registered／Buffered、04h＝Unbuffered（規格 7.18.6）。名稱僅為代碼的翻譯，不改變量到的值。")]
    public static string FormName(ModuleForm f) => f switch
    {
        ModuleForm.Registered => "Registered（RDIMM，帶緩衝）",
        ModuleForm.Unbuffered => "Unbuffered（UDIMM，無緩衝）",
        ModuleForm.LoadReduced => "Load-Reduced（LRDIMM）",
        _ => "—（未回報）",
    };

    /// <summary>平台層錯誤更正（Type 16 位移 0x06）的名稱——沿用 <see cref="SmbiosService.ArrayEcName"/> 的同義。</summary>
    [SpecRef("SMBIOS Specification, Physical Memory Array (Type 16) offset 0x06：Error Correction Type。代碼表見規格 7.18.2：01h 其他、02h 未知、03h 無、04h 同位、05h 單位元 ECC、06h 多位元 ECC、07h CRC。此欄陳述的是整個記憶體陣列，不是單支模組。")]
    public static string PlatformEccName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "無", 0x04 => "同位",
        0x05 => "單位元 ECC", 0x06 => "多位元 ECC", 0x07 => "CRC",
        _ => $"0x{code:X2}（規格未收錄）",
    };

    /// <summary>
    /// 一句話交代「這台機器的記憶體有沒有錯誤更正、是哪一種」。
    /// 三個層次分開講，並在只有其中一層讀到時標明另一層未知。
    /// </summary>
    [SpecRef("綜合 SMBIOS Specification Type 16（Physical Memory Array 的 Error Correction Type）與 Type 17（Memory Device 的 Total／Data Width 與 Registered/Unbuffered）。兩者為獨立欄位，本判讀不從其中之一推論另一個；任一讀不到都如實標未知。")]
    public static string Describe(bool? moduleHasEcc, ModuleForm form, string? platformEcc)
    {
        string module = moduleHasEcc switch
        {
            true => "模組帶 ECC 位元（總寬度大於資料寬度）",
            false => "模組無 ECC 位元（總寬度等於資料寬度）",
            null => "模組匯流排寬度讀不到，無法判斷有無 ECC",
        };
        string plat = string.IsNullOrWhiteSpace(platformEcc)
            ? "平台層的錯誤更正類型讀不到"
            : $"平台的錯誤更正類型為「{platformEcc}」";

        string mismatch = moduleHasEcc switch
        {
            true when platformEcc is "無" =>
                " 注意：模組帶 ECC 位元但平台回報「無」錯誤更正——兩者不一致，"
                + "常見於韌體未正確填寫，或模組插在只當一般記憶體使用的位置；請以平台宣告為準並留意。",
            false when platformEcc is "單位元 ECC" or "多位元 ECC" =>
                " 注意：平台宣告有 ECC 但模組沒有 ECC 位元——兩者不一致，"
                + "請確認讀到的模組與平台宣告是否對得上。",
            _ => "",
        };

        return $"{module}；{plat}；模組型態為 {FormName(form)}。"
             + "ECC、Registered 與平台錯誤更正能力是三個獨立的欄位，本判讀不從其中一項推論另一項。"
             + mismatch;
    }
}
