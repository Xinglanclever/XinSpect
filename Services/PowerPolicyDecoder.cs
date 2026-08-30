namespace XinSpect;

/// <summary>電源政策的一列（名稱／目前值／說明）。</summary>
public sealed class PowerPolicyRow
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public string Note { get; init; } = "";
    /// <summary>0＝中性、1＝值得注意、2＝明顯影響效能或穩定度。僅用於著色。</summary>
    public int Severity { get; init; }
}

/// <summary>單一邏輯處理器的電源狀態（CallNtPowerInformation 的 PROCESSOR_POWER_INFORMATION）。</summary>
public readonly record struct ProcessorPowerSample(
    uint Number, uint MaxMhz, uint CurrentMhz, uint MhzLimit, uint MaxIdleState, uint CurrentIdleState);

/// <summary>
/// 電源政策與逐核停放狀況的純解碼（單元測試涵蓋，不呼叫任何 API）。
/// </summary>
public static class PowerPolicyDecoder
{
    /// <summary>
    /// <c>PROCESSOR_POWER_INFORMATION.CurrentMhz</c> 的誠實說明。
    /// </summary>
    /// <remarks>
    /// 這個欄位是這整張卡片最容易騙人的地方：Windows 回報的是「目前的 P-state 上限對應頻率」，
    /// 在多數平台上它等於標稱頻率，<b>而不是核心實際跑的時脈</b>。本機實測 CurrentMhz 一直是 2601
    /// （i9-7980XE 的標稱 2.6 GHz），而「頻率真相」卡片用 MPERF/APERF 量到的有效時脈是 4186 MHz。
    /// 兩個數字都對，量的是不同東西——不講清楚就會有人以為 CPU 只跑在 2.6 GHz。
    /// </remarks>
    public static string DescribeCurrentMhz(uint currentMhz, uint maxMhz, uint mhzLimit)
    {
        if (maxMhz == 0) return "—（作業系統未回報頻率資訊）";
        string s = $"{currentMhz:N0} MHz（作業系統回報值）；標稱上限 {maxMhz:N0} MHz";
        if (mhzLimit > 0 && mhzLimit < maxMhz)
            s += $"；目前政策上限 {mhzLimit:N0} MHz（＝標稱的 {100.0 * mhzLimit / maxMhz:0}%）";
        s += "。⚠ 這個「目前」是 P-state 上限換算值，不是核心實際時脈——"
           + "實際有效時脈請看「頻率真相」卡片（MPERF/APERF 實測），Turbo 之下兩者必然不同。";
        return s;
    }

    /// <summary>
    /// 逐核彙總：停放（parked）與頻率上限的實況。
    /// 停放的判定依據是 <c>CurrentIdleState</c> 與 <c>MaxIdleState</c>——
    /// 這個 API 不直接給「已停放」布林值，所以這裡只報可觀察到的事實，不宣稱某核被停放。
    /// </summary>
    public static string SummarizeProcessors(IReadOnlyList<ProcessorPowerSample> cores)
    {
        if (cores.Count == 0) return "—（CallNtPowerInformation(ProcessorInformation) 沒有回傳任何核心）";

        var maxMhz = cores.Select(c => c.MaxMhz).Distinct().ToList();
        var curMhz = cores.Select(c => c.CurrentMhz).Distinct().ToList();
        int limited = cores.Count(c => c.MhzLimit > 0 && c.MhzLimit < c.MaxMhz);
        var idleStates = cores.Select(c => c.MaxIdleState).Distinct().ToList();

        string s = $"{cores.Count} 顆邏輯處理器；";
        s += maxMhz.Count == 1
            ? $"標稱上限一致（{maxMhz[0]:N0} MHz）；"
            : $"⚠ 標稱上限不一致（{string.Join("／", maxMhz.Select(m => $"{m:N0}"))} MHz）；";
        s += curMhz.Count == 1
            ? $"回報頻率一致（{curMhz[0]:N0} MHz）；"
            : $"回報頻率分歧（{curMhz.Min():N0}–{curMhz.Max():N0} MHz）；";
        s += limited == 0
            ? "沒有任何核心被政策限頻；"
            : $"⚠ {limited} 顆被政策限頻；";
        s += idleStates.Count == 1
            ? $"閒置狀態上限 C{idleStates[0]}。"
            : $"閒置狀態上限不一致（C{idleStates.Min()}–C{idleStates.Max()}）。";
        return s;
    }

    /// <summary>核心停放設定值（0–100，百分比）→ 說明。100 表示不停放任何核心。</summary>
    public static PowerPolicyRow DescribeCoreParking(string name, uint? percent)
    {
        if (percent is null)
            return new PowerPolicyRow { Name = name, Value = "—", Note = "此電源計劃未定義該設定，或查詢失敗。" };
        uint p = percent.Value;
        return new PowerPolicyRow
        {
            Name = name,
            Value = $"{p}%",
            Note = p >= 100
                ? "全部核心保持可用，不停放。"
                : $"最多允許停放到只剩 {p}% 的核心——停放中的核心不接受排程，"
                + "對延遲敏感的工作（音訊、遊戲、即時串流）可能造成間歇卡頓。",
            Severity = p >= 100 ? 0 : 1,
        };
    }

    /// <summary>
    /// PCIe ASPM（連結狀態電源管理）設定索引 → 說明。
    /// 0＝關閉、1＝中度節能、2＝最大節能（官方文件的三個值）。
    /// </summary>
    public static PowerPolicyRow DescribeAspm(uint? index) => index switch
    {
        null => new PowerPolicyRow { Name = "PCIe 連結狀態電源管理（ASPM）", Value = "—", Note = "查詢失敗或此計劃未定義。" },
        0 => new PowerPolicyRow { Name = "PCIe 連結狀態電源管理（ASPM）", Value = "關閉", Note = "連結不進入低功耗狀態：延遲最低，耗電最高。" },
        1 => new PowerPolicyRow
        {
            Name = "PCIe 連結狀態電源管理（ASPM）", Value = "中度節能",
            Note = "L0s 進入低功耗；喚醒延遲通常在微秒級，對多數用途無感。", Severity = 0,
        },
        2 => new PowerPolicyRow
        {
            Name = "PCIe 連結狀態電源管理（ASPM）", Value = "最大節能",
            Note = "L1 進入低功耗；部分 NVMe 與擷取卡在此設定下會出現間歇延遲尖峰或裝置掉線。", Severity = 1,
        },
        _ => new PowerPolicyRow
        {
            Name = "PCIe 連結狀態電源管理（ASPM）", Value = $"0x{index:X}",
            Note = "非官方文件記載的三個值之一，故不翻譯，原樣呈現。",
        },
    };

    /// <summary>USB 選擇性暫停設定索引 → 說明。</summary>
    public static PowerPolicyRow DescribeUsbSuspend(uint? index) => index switch
    {
        null => new PowerPolicyRow { Name = "USB 選擇性暫停", Value = "—", Note = "查詢失敗或此計劃未定義。" },
        0 => new PowerPolicyRow { Name = "USB 選擇性暫停", Value = "停用", Note = "USB 裝置不會被個別暫停：耗電略高，但不會有裝置喚醒延遲。" },
        _ => new PowerPolicyRow
        {
            Name = "USB 選擇性暫停", Value = "啟用",
            Note = "閒置的 USB 裝置會被暫停。DAC／音訊介面、部分搖桿與擷取裝置在此設定下"
                 + "可能出現首次操作延遲或斷連。", Severity = 1,
        },
    };

    /// <summary>處理器效能提升模式（Turbo 政策）設定索引 → 說明。官方定義 0–5。</summary>
    public static PowerPolicyRow DescribeBoostMode(uint? index)
    {
        string name = "處理器效能提升模式（Turbo 政策）";
        string[] known =
        [
            "停用（不使用 Turbo）",
            "啟用（Enabled）",
            "積極（Aggressive）",
            "有效率地啟用（Efficient Enabled）",
            "有效率地積極（Efficient Aggressive）",
            "積極但保護（Aggressive At Guaranteed）",
        ];
        if (index is null) return new PowerPolicyRow { Name = name, Value = "—", Note = "查詢失敗或此計劃未定義。" };
        uint i = index.Value;
        return new PowerPolicyRow
        {
            Name = name,
            Value = i < known.Length ? known[i] : $"0x{i:X}",
            Note = i == 0
                ? "Turbo 被關閉：所有核心不會超過標稱頻率。"
                : i < known.Length
                    ? "Turbo 允許啟用；實際能升到多少由溫度牆與功耗牆決定（見「黏滯節流位元」卡片）。"
                    : "超出官方文件記載的 0–5，故不翻譯。",
            Severity = i == 0 ? 1 : 0,
        };
    }

    /// <summary>最小／最大處理器狀態（百分比）→ 一列。兩者相等即為鎖頻。</summary>
    public static PowerPolicyRow DescribeProcessorStateRange(uint? min, uint? max)
    {
        if (min is null || max is null)
            return new PowerPolicyRow { Name = "處理器狀態範圍", Value = "—", Note = "查詢失敗或此計劃未定義。" };
        string v = $"{min}% – {max}%";
        if (min == max)
            return new PowerPolicyRow
            {
                Name = "處理器狀態範圍", Value = v,
                Note = $"最小與最大相同：頻率被鎖在標稱的 {min}%，作業系統不做 P-state 調整。", Severity = 1,
            };
        if (min >= 100)
            return new PowerPolicyRow
            {
                Name = "處理器狀態範圍", Value = v,
                Note = "最小 100%：核心永不降頻，閒置也維持高頻——溫度與耗電都會偏高。", Severity = 1,
            };
        return new PowerPolicyRow
        {
            Name = "處理器狀態範圍", Value = v,
            Note = "作業系統可在此區間內調整 P-state。",
        };
    }

    /// <summary>
    /// <c>SYSTEM_POWER_CAPABILITIES</c> 的睡眠支援矩陣 → 逐列。
    /// 只列平台自己宣告的能力，不加任何「你應該啟用」的建議。
    /// </summary>
    public static List<PowerPolicyRow> DescribeSleepStates(
        bool s1, bool s2, bool s3, bool s4, bool s5, bool hiberFile, bool fastS4, bool aoac)
    {
        var rows = new List<PowerPolicyRow>
        {
            new() { Name = "S1（掛起，時脈停止）", Value = s1 ? "支援" : "不支援" },
            new() { Name = "S2（掛起，CPU 斷電）", Value = s2 ? "支援" : "不支援" },
            new() { Name = "S3（待命，記憶體保持）", Value = s3 ? "支援" : "不支援",
                    Note = s3 ? "" : "多數新平台以「現代待命（S0 低功耗閒置）」取代 S3，故此處為不支援是正常的。" },
            new() { Name = "S4（休眠，寫入磁碟）", Value = s4 ? "支援" : "不支援" },
            new() { Name = "S5（軟關機）", Value = s5 ? "支援" : "不支援" },
            new() { Name = "休眠檔（hiberfil.sys）", Value = hiberFile ? "存在" : "不存在",
                    Note = hiberFile ? "" : "沒有休眠檔時，S4 與快速啟動都無法使用。" },
            new() { Name = "快速啟動（Fast S4）", Value = fastS4 ? "支援" : "不支援" },
            new() { Name = "現代待命（AoAc／S0 低功耗閒置）", Value = aoac ? "支援" : "不支援",
                    Note = aoac ? "系統走 S0ix 而非 S3；此時「睡眠」期間背景工作仍會執行。" : "" },
        };
        return rows;
    }

    /// <summary>
    /// 這張卡片的界線說明。電源設定的寫入不做——理由和 BIOS 一樣是可逆性問題，
    /// 但這裡的理由較輕：改錯只是效能或耗電變差，不會變磚，所以說法必須誠實區分。
    /// </summary>
    public const string ScopeNotice =
        "本卡片唯讀：只呈現目前生效的電源政策，不修改任何設定。改電源計劃不會像刷韌體那樣把機器弄壞，"
        + "但它會同時影響效能、延遲、溫度與耗電四件事，而正確答案取決於你的用途——"
        + "所以這裡的立場是把事實攤開讓你自己判斷，要改請用 Windows 內建的電源選項或 powercfg"
        + "（工具箱分頁可直接開啟），或用「場景設定檔」一次套用一組取向。";
}
