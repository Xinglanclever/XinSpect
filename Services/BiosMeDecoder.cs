using System.Text;

namespace XinSpect;

/// <summary>
/// BIOS／Intel ME 韌體與微碼的解碼純函式（單元測試涵蓋，不接觸硬體）。
/// </summary>
public static class BiosMeDecoder
{
    /// <summary>
    /// 登錄檔 <c>Update Revision</c>（REG_BINARY）→ 微碼修訂版。
    /// 這個值是小端序的四位元組；本機實測 06 70 00 02＝0x02007006，與 MSR 0x8B 高 32 位一致。
    /// </summary>
    public static uint? DecodeUpdateRevision(byte[]? raw)
    {
        if (raw is null || raw.Length < 4) return null;
        return BitConverter.ToUInt32(raw, 0);
    }

    /// <summary>
    /// 微碼來源比對：BIOS 載入的版本 vs Windows 自帶（mcupdate）偏好的版本。
    /// 兩者誰新誰舊會決定開機後實際跑的是哪一份——這是使用者最容易被誤導的地方。
    /// </summary>
    public static string CompareMicrocode(uint? current, uint? preferred)
    {
        if (current is null) return "—（讀不到目前微碼版本，不做比較）";
        if (preferred is null || preferred == 0)
            return $"目前 0x{current:X8}；Windows 未提供偏好版本（登錄檔無 Preferred Record Version），"
                 + "故這份微碼來自 BIOS／韌體。";
        if (preferred == current)
            return $"目前 0x{current:X8}＝Windows 偏好版本：兩邊同一份，無從分辨是誰載入的。";
        return preferred < current
            ? $"目前 0x{current:X8} 比 Windows 自帶的 0x{preferred:X8} 新："
              + "這份微碼來自 BIOS／韌體，Windows 不會降版覆蓋它。"
            : $"⚠ 目前 0x{current:X8} 比 Windows 自帶的 0x{preferred:X8} 舊："
              + "Windows 應會在開機時以 mcupdate 覆蓋；若重開後仍是舊值，代表覆蓋沒有生效。";
    }

    /// <summary>登錄檔 <c>Update Status</c>：已知值逐項命名，未知值原樣呈現。</summary>
    public static string DescribeUpdateStatus(uint? status) => status switch
    {
        null => "—（讀不到）",
        0 => "0（未套用任何更新）",
        _ => $"0x{status:X}（Windows 內部值，Intel／Microsoft 未公開完整對照表，故不翻譯）",
    };

    /// <summary>
    /// MKHI GET_FW_VERSION 回應（去掉 4 位元組標頭後的酬載）→ ME 韌體版本字串。
    /// 每個分割區 8 位元組，依序為 minor、major、buildno、hotfix（皆為小端 16 位）。
    /// </summary>
    public static string DecodeMeFwVersion(byte[]? payload)
    {
        if (payload is null || payload.Length < 8) return "—（回應長度不足，不猜版本）";
        var names = new[] { "作業碼（Operational）", "復原碼（Recovery）", "備援（NFTP／DLMP）" };
        var parts = new List<string>();
        for (int i = 0; i + 8 <= payload.Length && i / 8 < names.Length; i += 8)
        {
            ushort minor = BitConverter.ToUInt16(payload, i);
            ushort major = BitConverter.ToUInt16(payload, i + 2);
            ushort build = BitConverter.ToUInt16(payload, i + 4);
            ushort hotfix = BitConverter.ToUInt16(payload, i + 6);
            if (major == 0 && minor == 0 && build == 0 && hotfix == 0) continue;   // 未使用的分割區
            parts.Add($"{names[i / 8]} {major}.{minor}.{hotfix}.{build}");
        }
        return parts.Count == 0 ? "—（所有分割區皆回報 0.0.0.0）" : string.Join(" ・ ", parts);
    }

    /// <summary>ME 主版號 → 世代說法（只在有明確對應時才說，其餘原樣）。</summary>
    public static string DescribeMeGeneration(string version)
    {
        int dot = version.IndexOf('.');
        string headNum = dot > 0 ? version[..dot] : version;
        return int.TryParse(new string(headNum.Where(char.IsDigit).ToArray()), out int major)
            ? major switch
            {
                <= 0 => "—",
                <= 10 => $"ME {major}.x（Skylake 以前世代）",
                11 => "CSME 11.x（Skylake／Kaby Lake／Skylake-X 世代）",
                12 => "CSME 12.x（Cannon Lake／Coffee Lake 世代）",
                13 or 14 => $"CSME {major}.x（Comet Lake／Ice Lake 世代）",
                15 or 16 => $"CSME {major}.x（Tiger Lake／Alder Lake 世代）",
                _ => $"CSME {major}.x",
            }
            : "—";
    }

    /// <summary>
    /// 危險警告文字。曦覽自己不寫 SPI Flash——這不是「還沒做」，是刻意不做的界線：
    /// 使用者模式寫入 BIOS／ME 區域必須自帶核心驅動並繞過 Flash 保護，寫壞即無法開機。
    /// </summary>
    public const string DangerNotice =
        "⚠ 危險：BIOS 與 Intel ME 是主機板上唯一「寫壞就無法開機」的兩塊韌體。"
        + "刷寫過程斷電、刷錯型號、或用非官方映像，結果都是主機板變磚——一般沒有備份 BIOS 晶片的板子只能拆晶片外接程式器救回，"
        + "ME 區域寫壞更可能連 CPU 供電時序都起不來。\n"
        + "因此曦覽本身不寫入任何韌體：本卡片全部唯讀。下方按鈕只做兩件事——"
        + "（一）重開機進入主機板自己的 UEFI 設定畫面；（二）開啟你這張主機板的官方 BIOS 下載頁。"
        + "真正的刷寫請一律用主機板廠自己的工具（多數新板可用 BIOS 內建的 Flashback／Q-Flash，不必進 Windows），"
        + "並先確認：型號完全相符、變壓器／UPS 供電穩定、刷寫中不關機不重開。";

    /// <summary>主機板廠商 → 官方 BIOS 下載頁（比對不到就回 null，由呼叫端說「未知廠商」而不是亂連）。</summary>
    public static string? VendorBiosUrl(string? manufacturer)
    {
        string m = (manufacturer ?? "").ToLowerInvariant();
        if (m.Length == 0) return null;
        if (m.Contains("asus")) return "https://www.asus.com/tw/support/download-center/";
        if (m.Contains("gigabyte") || m.Contains("giga-byte")) return "https://www.gigabyte.com/tw/Support";
        if (m.Contains("msi") || m.Contains("micro-star")) return "https://tw.msi.com/support";
        if (m.Contains("asrock")) return "https://www.asrock.com/support/index.tw.asp";
        if (m.Contains("biostar")) return "https://www.biostar.com.tw/app/tw/support/download.php";
        if (m.Contains("colorful")) return "https://www.colorful.cn/service";
        if (m.Contains("lenovo")) return "https://support.lenovo.com/tw/zh";
        if (m.Contains("dell")) return "https://www.dell.com/support/home/zh-tw";
        if (m.Contains("hewlett") || m.Contains("hp ")) return "https://support.hp.com/tw-zh/drivers";
        if (m.Contains("acer")) return "https://www.acer.com/tw-zh/support";
        if (m.Contains("supermicro")) return "https://www.supermicro.com/en/support/resources/downloadcenter";
        if (m.Contains("intel")) return "https://www.intel.com.tw/content/www/tw/zh/download-center/home.html";
        return null;
    }

    /// <summary>ASCII 位元組轉字串，去尾端 NUL 與空白；空字串回 null。</summary>
    public static string? AsciiOrNull(byte[]? raw)
    {
        if (raw is null || raw.Length == 0) return null;
        string s = Encoding.ASCII.GetString(raw).TrimEnd('\0', ' ');
        return s.Length > 0 ? s : null;
    }
}
