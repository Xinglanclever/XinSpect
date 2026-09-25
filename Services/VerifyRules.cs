namespace XinSpect;

/// <summary>
/// 一條驗機規則。<paramref name="RequiredFacts"/> 是它需要的事實；引擎會先檢查齊不齊，
/// 缺就自己判「無法判定」，所以 <paramref name="Evaluate"/> 裡不必再寫任何缺值分支。
/// </summary>
public sealed record VerifyRule(
    string Id,
    string Part,
    string Title,
    FactId[] RequiredFacts,
    Func<VerifyFacts, VerifyFinding> Evaluate,
    VerifyScope Scope = VerifyScope.Machine);

/// <summary>
/// FactId 的顯示名與權限需求。
/// </summary>
/// <remarks>
/// 缺事實時得說得出「缺的是什麼」以及「怎樣才讀得到」——只顯示一片「—」等於把使用者丟在原地。
/// 名稱不放在 <see cref="VerifyFact.Label"/> 是因為缺少的事實根本沒有實例可以問。
/// </remarks>
public static class FactCatalog
{
    private static readonly Dictionary<FactId, (string Name, bool Admin)> Map = new()
    {
        [FactId.DimmCount] = ("記憶體模組數", false),
        [FactId.DimmManufacturers] = ("各條模組製造商", false),
        [FactId.DimmPartNumbers] = ("各條模組料號", false),
        [FactId.DimmSerials] = ("各條模組序號", false),
        [FactId.DimmSpeedMts] = ("模組標稱速度", false),
        [FactId.DimmConfiguredMts] = ("模組實際運行速度", false),
        [FactId.DimmSizeTotalMiB] = ("已安裝記憶體總量", false),
        [FactId.ArrayMaxCapacityMiB] = ("記憶體陣列宣稱上限", false),
        [FactId.ArraySlotCount] = ("記憶體插槽數", false),
        [FactId.MemEccType] = ("記憶體陣列宣稱的錯誤更正型別", false),
        [FactId.MemEccBitsPresent] = ("模組是否帶 ECC 位元(總寬度>資料寬度)", false),
        [FactId.NvmePowerOnHours] = ("NVMe 通電小時", true),
        [FactId.NvmeDataUnitsWritten] = ("NVMe 累計寫入量", true),
        [FactId.NvmePercentageUsed] = ("NVMe 已用壽命", true),
        [FactId.NvmePowerCycles] = ("NVMe 通電次數", true),
        [FactId.NvmeUnsafeShutdowns] = ("NVMe 不安全關機次數", true),
        [FactId.NvmeCriticalWarning] = ("NVMe 關鍵警告", true),
        [FactId.SmartPowerOnHours] = ("SMART 通電小時", true),
        [FactId.SmartHostWritesGiB] = ("SMART 主機寫入量", true),
        [FactId.SmartPowerCycles] = ("SMART 通電次數", true),
        [FactId.AtaRotationRate] = ("標稱旋轉速率", true),
        [FactId.AtaTotalLba] = ("可定址 LBA 總數", true),
        [FactId.AtaAcsVersion] = ("ACS 版本", true),
        [FactId.DiskClaimedCapacityGB] = ("宣稱容量", false),
        [FactId.DiskModel] = ("磁碟型號", false),
        [FactId.SmartSpinUpPresent] = ("是否存在機械專屬屬性", true),
        [FactId.BatteryDesignCapacityMWh] = ("電池設計容量", false),
        [FactId.BatteryFullCapacityMWh] = ("電池滿充容量", false),
    };

    public static string Name(FactId id) => Map.TryGetValue(id, out var v) ? v.Name : id.ToString();

    public static bool NeedsAdmin(FactId id) => Map.TryGetValue(id, out var v) && v.Admin;

    /// <summary>目錄是否已涵蓋所有 FactId（由測試守住，新增事實時不得漏登記）。</summary>
    public static bool Covers(FactId id) => Map.ContainsKey(id);
}

/// <summary>
/// 規則引擎。純函式、零硬體相依：把事實袋餵進去，拿到每條規則的判定。
/// </summary>
public static class VerifyEngine
{
    /// <summary>
    /// 跑指定範圍的規則。整機事實跑一次 <see cref="VerifyScope.Machine"/>，
    /// 每顆碟各跑一次 <see cref="VerifyScope.Disk"/>——理由見 <see cref="VerifyScope"/>。
    /// </summary>
    public static IReadOnlyList<VerifyFinding> Run(VerifyFacts facts, VerifyScope scope = VerifyScope.Machine)
        => VerifyRules.All.Where(r => r.Scope == scope).Select(r => Evaluate(r, facts)).ToList();

    /// <summary>
    /// 缺依賴就直接判「無法判定」，並列出缺的是哪幾個事實、需不需要管理員權限。
    /// 規則本體因此只剩純比對邏輯。
    /// </summary>
    internal static VerifyFinding Evaluate(VerifyRule rule, VerifyFacts facts)
    {
        var missing = rule.RequiredFacts.Where(id => !facts.Has(id)).ToArray();
        if (missing.Length == 0) return rule.Evaluate(facts);

        bool admin = missing.Any(FactCatalog.NeedsAdmin);
        string why = "無法判定：缺 " + string.Join("、", missing.Select(FactCatalog.Name))
                   + (admin ? "（需要管理員權限才讀得到）" : "");
        return new(rule.Id, rule.Part, rule.Title, VerifyVerdict.Unread, Severity.Neutral, why, null, []);
    }
}

/// <summary>
/// 規則表。一條規則一列，由上而下全部都跑（規則之間互不遮蔽）。
/// </summary>
/// <remarks>
/// 判定只有三種：相符／矛盾／無法判定。**不給分數、不給「正品」結論**——
/// 綠勾會讓使用者停止思考，而賣家可能有正當理由。工具把矛盾指出來，判斷留給人。
/// </remarks>
public static class VerifyRules
{
    public const string PartMemory = "記憶體";
    public const string PartStorage = "儲存裝置";
    public const string PartBattery = "電池";

    private const string T01 = "各條記憶體模組並非同批";
    private const string T02 = "記憶體模組序號異常";
    private const string T03 = "記憶體未跑在標稱速度";
    private const string T04 = "記憶體陣列宣稱與實際安裝對不上";
    private const string T05 = "宣稱 ECC 與模組實際位元寬度對不上";
    private const string S01 = "通電小時與累計寫入量對不上";
    private const string S02 = "已用壽命與累計寫入量對不上";
    private const string S03 = "不安全關機次數多於通電次數";
    private const string S04 = "NVMe 回報關鍵警告";
    private const string S05 = "宣稱容量與可定址容量對不上";
    private const string S06 = "宣稱轉速與屬性集矛盾";
    private const string B01 = "電池滿充容量明顯低於設計容量";

    public static readonly VerifyRule[] All =
    [
        new("R-MEM-01", PartMemory, T01,
            [FactId.DimmCount, FactId.DimmManufacturers, FactId.DimmPartNumbers], MixedModules),
        new("R-MEM-02", PartMemory, T02,
            [FactId.DimmCount, FactId.DimmSerials], BadSerials),
        new("R-MEM-03", PartMemory, T03,
            [FactId.DimmSpeedMts, FactId.DimmConfiguredMts], UnderclockedMemory),
        new("R-MEM-04", PartMemory, T04,
            [FactId.DimmSizeTotalMiB, FactId.ArrayMaxCapacityMiB, FactId.ArraySlotCount, FactId.DimmCount],
            ArrayMismatch),
        new("R-MEM-05", PartMemory, T05,
            [FactId.MemEccType, FactId.MemEccBitsPresent], EccConsistency),

        // ── 儲存裝置：每顆碟各跑一次（VerifyScope.Disk）。翻新碟幾乎都在這幾條對帳上露餡。──
        new("R-SSD-01", PartStorage, S01,
            [FactId.NvmeDataUnitsWritten, FactId.NvmePowerOnHours], WriteRateImplausible, VerifyScope.Disk),
        new("R-SSD-02", PartStorage, S02,
            [FactId.NvmePercentageUsed, FactId.NvmeDataUnitsWritten], ZeroWearImplausible, VerifyScope.Disk),
        new("R-SSD-03", PartStorage, S03,
            [FactId.NvmeUnsafeShutdowns, FactId.NvmePowerCycles], UnsafeExceedsCycles, VerifyScope.Disk),
        new("R-SSD-04", PartStorage, S04,
            [FactId.NvmeCriticalWarning], CriticalWarning, VerifyScope.Disk),
        new("R-SSD-05", PartStorage, S05,
            [FactId.DiskClaimedCapacityGB, FactId.AtaTotalLba], CapacityMismatch, VerifyScope.Disk),
        new("R-SSD-06", PartStorage, S06,
            [FactId.AtaRotationRate, FactId.SmartSpinUpPresent], RotationMismatch, VerifyScope.Disk),

        // ── 電池：整機一份事實 ──
        new("R-BAT-01", PartBattery, B01,
            [FactId.BatteryDesignCapacityMWh, FactId.BatteryFullCapacityMWh], BatteryWorn),
    ];

    /// <summary>逐條模組的字串以 <c>|</c> 相連（collector 產出的形式）。</summary>
    internal static string[] Split(string? s) =>
        (s ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static VerifyFinding MixedModules(VerifyFacts f)
    {
        var evidence = new[] { f.Get(FactId.DimmManufacturers)!, f.Get(FactId.DimmPartNumbers)! };

        if ((f.Num(FactId.DimmCount) ?? 0) < 2)
            return new("R-MEM-01", PartMemory, T01, VerifyVerdict.Match, Severity.Good,
                "只有一條模組，無從混批。", null, evidence);

        bool mixed = Split(f.Text(FactId.DimmManufacturers)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                  || Split(f.Text(FactId.DimmPartNumbers)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

        return mixed
            ? new("R-MEM-01", PartMemory, T01, VerifyVerdict.Conflict, Severity.Warning,
                "製造商或料號不一致，這幾條不是同一批出廠的模組。",
                "使用者自己後來加裝的模組也會這樣；混批不影響保固，但可能造成時序回退到較慢的那一條。",
                evidence)
            : new("R-MEM-01", PartMemory, T01, VerifyVerdict.Match, Severity.Good,
                "所有模組的製造商與料號一致。", null, evidence);
    }

    /// <summary>全 0 或全 F 的序號：韌體沒燒序號，或序號被抹掉。</summary>
    private static bool IsBogusSerial(string s) =>
        s.Length > 0 && (s.All(c => c == '0') || s.All(c => c is 'F' or 'f'));

    private static VerifyFinding BadSerials(VerifyFacts f)
    {
        var ev = new[] { f.Get(FactId.DimmSerials)! };
        var serials = Split(f.Text(FactId.DimmSerials));

        // 重複序號在物理上不該出現（序號是模組廠逐條燒的），故比全 0／全 F 更嚴重。
        bool dup = serials.Length > 1 &&
                   serials.Distinct(StringComparer.OrdinalIgnoreCase).Count() != serials.Length;
        if (dup)
            return new("R-MEM-02", PartMemory, T02, VerifyVerdict.Conflict, Severity.Serious,
                "兩條以上模組回報同一組序號。序號是模組廠逐條燒進 SPD 的，正常不會重複。",
                "同一顆晶片廠的模組被不同品牌貼牌時偶有序號規則衝突；但完全相同的序號更常見於仿冒模組。",
                ev);

        if (serials.Any(IsBogusSerial))
            return new("R-MEM-02", PartMemory, T02, VerifyVerdict.Conflict, Severity.Warning,
                "有模組的序號是全 0 或全 F——SPD 裡沒有燒序號，或序號被抹掉了。",
                "少數白牌與工業用模組出廠就不燒序號；但翻新與貼牌模組也是這個樣子。", ev);

        return new("R-MEM-02", PartMemory, T02, VerifyVerdict.Match, Severity.Good,
            "各條模組序號互異，且不是全 0／全 F。", null, ev);
    }

    private static VerifyFinding UnderclockedMemory(VerifyFacts f)
    {
        double rated = f.Num(FactId.DimmSpeedMts)!.Value;
        double cur = f.Num(FactId.DimmConfiguredMts)!.Value;
        var ev = new[] { f.Get(FactId.DimmSpeedMts)!, f.Get(FactId.DimmConfiguredMts)! };

        return cur < rated
            ? new("R-MEM-03", PartMemory, T03, VerifyVerdict.Conflict, Severity.Warning,
                $"模組標稱 {rated:0} MT/s，實際只跑 {cur:0} MT/s。",
                "多數情況是主機板沒有啟用 XMP／EXPO，或處理器的記憶體控制器上限較低——"
                + "這不是模組本身的問題，進 BIOS 開啟設定檔即可。", ev)
            : new("R-MEM-03", PartMemory, T03, VerifyVerdict.Match, Severity.Good,
                "實際運行速度已達標稱值。", null, ev);
    }

    private static VerifyFinding ArrayMismatch(VerifyFacts f)
    {
        double total = f.Num(FactId.DimmSizeTotalMiB)!.Value;
        double max = f.Num(FactId.ArrayMaxCapacityMiB)!.Value;
        double slots = f.Num(FactId.ArraySlotCount)!.Value;
        double dimms = f.Num(FactId.DimmCount)!.Value;
        var ev = new[]
        {
            f.Get(FactId.DimmSizeTotalMiB)!, f.Get(FactId.ArrayMaxCapacityMiB)!,
            f.Get(FactId.ArraySlotCount)!, f.Get(FactId.DimmCount)!,
        };

        if (total > max)
            return new("R-MEM-04", PartMemory, T04, VerifyVerdict.Conflict, Severity.Warning,
                $"已安裝 {total:0} MiB，但記憶體陣列宣稱上限只有 {max:0} MiB。",
                "部分主機板的 SMBIOS 把陣列上限寫錯，這種情況機器照樣正常運作。", ev);

        if (dimms > slots)
            return new("R-MEM-04", PartMemory, T04, VerifyVerdict.Conflict, Severity.Warning,
                $"回報 {dimms:0} 條模組，但陣列只宣告 {slots:0} 個插槽。",
                "SMBIOS 表寫錯也會這樣；但也可能是其中一條沒有被正確列舉。", ev);

        return new("R-MEM-04", PartMemory, T04, VerifyVerdict.Match, Severity.Good,
            "安裝總量與模組數都在陣列宣告的範圍內。", null, ev);
    }

    /// <summary>SMBIOS Type16 錯誤更正碼:3=無、4=同位元、5=單位元 ECC、6=多位元 ECC、7=CRC。5 以上才算真 ECC。</summary>
    private static bool ClaimsEcc(double eccType) => eccType >= 5;

    private static VerifyFinding EccConsistency(VerifyFacts f)
    {
        double eccType = f.Num(FactId.MemEccType)!.Value;
        bool bits = f.Num(FactId.MemEccBitsPresent)!.Value > 0;
        var ev = new[] { f.Get(FactId.MemEccType)!, f.Get(FactId.MemEccBitsPresent)! };
        bool claims = ClaimsEcc(eccType);

        if (claims && !bits)
            return new("R-MEM-05", PartMemory, T05, VerifyVerdict.Conflict, Severity.Warning,
                "陣列宣稱支援 ECC,但每條模組的總寬度都等於資料寬度——沒有多出來的 ECC 位元。",
                "少數主機板的 SMBIOS 把錯誤更正欄位亂填;但買到「ECC 工作站」實際插的是無 ECC 記憶體也是這個樣子。",
                ev);

        // 反方向(有 ECC 位元卻宣稱無)刻意不判矛盾:本機 X299 實測就是這樣——模組帶 72 位元寬度、
        // 陣列卻回報「無錯誤更正」,這在消費／HEDT 板極常見(韌體沒填或 ECC 沒啟用),不是賣家造假。
        // 拿無謂的紅字嚇到買家、害他殺掉正當交易,誤判代價不對稱,故只單向判「宣稱 ECC 卻沒位元」。
        return new("R-MEM-05", PartMemory, T05, VerifyVerdict.Match, Severity.Good,
            claims ? "宣稱 ECC,且模組確實帶 ECC 位元,兩邊一致。"
            : bits ? "模組帶 ECC 位元(陣列雖回報無錯誤更正,多為韌體未填或未啟用,不算造假)。"
                   : "宣稱無 ECC,且模組也沒有 ECC 位元,兩邊一致。", null, ev);
    }

    // ── 儲存裝置六條：翻新碟幾乎都在這幾條對帳上露餡 ────────────────────────

    private static VerifyFinding Ok(string id, string title, string why, params VerifyFact[] ev)
        => new(id, PartStorage, title, VerifyVerdict.Match, Severity.Good, why, null, ev);

    private static VerifyFinding Bad(string id, string title, Severity sev, string why, string? benign,
        params VerifyFact[] ev)
        => new(id, PartStorage, title, VerifyVerdict.Conflict, sev, why, benign, ev);

    private static VerifyFinding WriteRateImplausible(VerifyFacts f)
    {
        double written = f.Num(FactId.NvmeDataUnitsWritten)!.Value;
        double hours = f.Num(FactId.NvmePowerOnHours)!.Value;
        var ev = new[] { f.Get(FactId.NvmeDataUnitsWritten)!, f.Get(FactId.NvmePowerOnHours)! };

        // 通電小時是整數：讀到 0 代表「不足 1 小時」，不是「零時間」。這種情況算不出有意義的
        // 平均速率（新碟複製一份資料就能寫進幾百 GiB），所以不判——不猜也不冤枉。
        if (hours <= 0)
            return new("R-SSD-01", PartStorage, S01, VerifyVerdict.Unread, Severity.Neutral,
                "通電小時為 0（不足一小時），算不出平均寫入速率，因此不判定。", null, ev);

        double rate = written / hours;
        return rate > VerifyThresholds.MaxPlausibleGiBPerHour
            ? Bad("R-SSD-01", S01, Severity.Serious,
                $"通電 {hours:N0} 小時卻累計寫入 {written:N0} GiB，平均 {rate:N0} GiB／小時。",
                "長期用於影音錄製、虛擬機主機或監控錄影的碟可以有很高的平均寫入速率；"
                + "但通電小時被歸零的翻新碟也是這個樣子。", ev)
            : Ok("R-SSD-01", S01, $"平均寫入速率 {rate:N1} GiB／小時，在合理範圍內。", ev);
    }

    private static VerifyFinding ZeroWearImplausible(VerifyFacts f)
    {
        double used = f.Num(FactId.NvmePercentageUsed)!.Value;
        double written = f.Num(FactId.NvmeDataUnitsWritten)!.Value;
        var ev = new[] { f.Get(FactId.NvmePercentageUsed)!, f.Get(FactId.NvmeDataUnitsWritten)! };

        return used == 0 && written > VerifyThresholds.ZeroWearImplausibleGiB
            ? Bad("R-SSD-02", S02, Severity.Serious,
                $"已累計寫入 {written:N0} GiB，但已用壽命仍顯示 0%。",
                "少數企業級碟的壽命計數解析度很粗，長時間仍停在 0%；不過壽命計數被重設也是這個樣子。", ev)
            : Ok("R-SSD-02", S02, $"已用壽命 {used:N0}% 與累計寫入 {written:N0} GiB 對得上。", ev);
    }

    private static VerifyFinding UnsafeExceedsCycles(VerifyFacts f)
    {
        double unsafeCount = f.Num(FactId.NvmeUnsafeShutdowns)!.Value;
        double cycles = f.Num(FactId.NvmePowerCycles)!.Value;
        var ev = new[] { f.Get(FactId.NvmeUnsafeShutdowns)!, f.Get(FactId.NvmePowerCycles)! };

        // 不安全關機是通電次數的子集，多於通電次數在物理上不可能——所以沒有正當成因可寫。
        return unsafeCount > cycles
            ? Bad("R-SSD-03", S03, Severity.Serious,
                $"不安全關機 {unsafeCount:N0} 次，卻只通電 {cycles:N0} 次。"
                + "不安全關機是通電次數的子集，這兩個數字不可能是這個關係。",
                null, ev)
            : Ok("R-SSD-03", S03, $"不安全關機 {unsafeCount:N0} 次，未超過通電次數 {cycles:N0} 次。", ev);
    }

    private static VerifyFinding CriticalWarning(VerifyFacts f)
    {
        double flags = f.Num(FactId.NvmeCriticalWarning)!.Value;
        var ev = new[] { f.Get(FactId.NvmeCriticalWarning)! };
        if (flags == 0)
            return Ok("R-SSD-04", S04, "沒有任何關鍵警告位元亮起。", ev);

        var warns = NvmeLogDecoder.CriticalWarnings((byte)flags);
        string what = string.Join("、", warns.Select(w => $"位元 {w.Bit}：{w.Name}"));
        return Bad("R-SSD-04", S04, Severity.Critical,
            $"碟自己回報了關鍵警告（{what}）。",
            "剛經歷異常斷電或溫度過高的碟會亮起警告，冷卻後未必仍成立；"
            + "但介質進入唯讀或可靠性降級是不會自己好的。", ev);
    }

    private static VerifyFinding CapacityMismatch(VerifyFacts f)
    {
        double claimed = f.Num(FactId.DiskClaimedCapacityGB)!.Value;
        double lba = f.Num(FactId.AtaTotalLba)!.Value;
        var ev = new[] { f.Get(FactId.DiskClaimedCapacityGB)!, f.Get(FactId.AtaTotalLba)! };

        double addressable = lba * 512.0 / 1_000_000_000;
        double diff = claimed > 0 ? Math.Abs(claimed - addressable) / claimed : 0;
        return diff > VerifyThresholds.CapacityTolerance
            ? Bad("R-SSD-05", S05, Severity.Critical,
                $"宣稱 {claimed:N0} GB，但實際只定址得到 {addressable:N1} GB（差 {diff:P0}）。",
                "廠商的十進位 GB 與作業系統的 GiB 換算差約 7%，已納入容許值；"
                + "差距超出容許值的通常是改過容量資訊的碟。", ev)
            : Ok("R-SSD-05", S05, $"宣稱 {claimed:N0} GB 與可定址 {addressable:N1} GB 相符。", ev);
    }

    private static VerifyFinding RotationMismatch(VerifyFacts f)
    {
        double rate = f.Num(FactId.AtaRotationRate)!.Value;
        bool spinUp = f.Num(FactId.SmartSpinUpPresent)!.Value > 0;
        var ev = new[] { f.Get(FactId.AtaRotationRate)!, f.Get(FactId.SmartSpinUpPresent)! };

        bool saysSolid = rate == 1;
        bool saysMechanical = rate is >= 0x0401 and <= 0xFFFE;

        if (saysSolid && spinUp)
            return Bad("R-SSD-06", S06, Severity.Warning,
                "自稱非旋轉裝置（固態），但 SMART 裡有起轉時間這個機械專屬屬性。",
                "部分 USB 外接盒與 RAID 控制器會轉述錯誤的旋轉速率或補上不存在的屬性。", ev);

        if (saysMechanical && !spinUp)
            return Bad("R-SSD-06", S06, Severity.Warning,
                $"自稱 {rate:N0} rpm 的機械碟，但 SMART 裡沒有起轉時間這個機械專屬屬性。",
                "部分 SSD 韌體會回報假的旋轉速率以相容舊系統；外接盒轉述錯誤也會這樣。", ev);

        return Ok("R-SSD-06", S06,
            saysSolid ? "自稱固態，且沒有機械專屬屬性，兩邊一致。"
            : saysMechanical ? "自稱機械碟，且有機械專屬屬性，兩邊一致。"
            : "這顆碟沒有回報標稱轉速，無從矛盾。", ev);
    }

    // ── 電池 ────────────────────────────────────────────────────────────────

    private static VerifyFinding BatteryWorn(VerifyFacts f)
    {
        double design = f.Num(FactId.BatteryDesignCapacityMWh)!.Value;
        double full = f.Num(FactId.BatteryFullCapacityMWh)!.Value;
        var ev = new[] { f.Get(FactId.BatteryDesignCapacityMWh)!, f.Get(FactId.BatteryFullCapacityMWh)! };

        // Windows 讀不到容量時回 0。0 mWh 的電池不存在，所以一律當「讀不到」，
        // 不要拿它算出 0% 健康度去嚇人。
        if (design <= 0 || full <= 0)
            return new("R-BAT-01", PartBattery, B01, VerifyVerdict.Unread, Severity.Neutral,
                "設計容量或滿充容量回報為 0——那是讀不到，不是真的沒有容量。", null, ev);

        double ratio = full / design;
        if (ratio >= VerifyThresholds.BatteryWornRatio)
            return new("R-BAT-01", PartBattery, B01, VerifyVerdict.Match, Severity.Good,
                $"滿充容量為設計容量的 {ratio:P0}，衰退在正常範圍內。", null, ev);

        return new("R-BAT-01", PartBattery, B01, VerifyVerdict.Conflict,
            ratio < VerifyThresholds.BatteryBadlyWornRatio ? Severity.Serious : Severity.Warning,
            $"滿充容量只有設計容量的 {ratio:P0}（{full:N0} / {design:N0} mWh）。",
            "電池是耗材，長期插電使用的機器衰退得特別快——這是正常老化，"
            + "與賣家是否隱瞞無關，但會直接影響續航與二手估價。", ev);
    }
}
