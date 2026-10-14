namespace XinSpect;

/// <summary>FPDT 的 POST 階段時長（秒）。<paramref name="FirmwareSeconds"/> 為 null ＝該紀錄不存在。</summary>
/// <param name="Phase">階段名稱（Firmware Basic Boot／POST 等，原文不譯）。</param>
/// <param name="FirmwareSeconds">秒數；null＝這個階段沒有紀錄。</param>
public sealed record FpdtPhase(string Phase, double? FirmwareSeconds);

/// <summary>
/// 韌體效能資料表（FPDT）解碼：<c>fw.fpdt.post</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>版面出處：</b>ACPI 規格第 5.2.24 節（FPDT）＋ Linux <c>include/linux/fpdt.h</c> 的
/// <c>fbpt_entry</c> 定義（2026-10 抓取核對）。FPDT 由一批「開機效能紀錄表」組成，
/// 每筆有 16 位元組的表頭（Signature／Length／Revision／Reserved）與各種紀錄類型；
/// 本項只處理 <c>FBPT</c> 裡的 <c>Basic Boot</c> 紀錄
/// （<c>0x00000001</c>＝Firmware Basic Boot Performance Record），
/// 其餘紀錄類型（S3／S4 恢復時長等）<b>認不得就說認不得</b>，不硬掰。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① <b>韌體自報的 POST 時長與作業系統觀察的開機時長是不同的量測點</b>——
/// 前者是韌體自己算的（並可能根本沒算完），後者是 Windows 用 EFI 效能輔助資料算的；
/// 兩者不一致是<b>常態</b>不是矛盾，所以值裡必須寫明這件事，免得被當成「開機變慢了」；
/// ② <b>表缺席＝不適用</b>，不是 0 秒——沒有這張表與「POST 花了 0 秒」是天差地別；
/// ③ <b>秒數為 0 也要出值</b>——某些實作真的回 0（沒量到），那是一個可看見的訊號，
/// 不是缺席；缺席是「連紀錄都沒有」；
/// ④ <b>不定義「幾秒才算慢」</b>——門檻隨平台、韌體、開機裝置而異，本項只出秒數。
/// </para>
/// </remarks>
public static class AcpiFpdtFactsService
{
    private const string Cat = "韌體";

    /// <summary>FW-024 FPDT 韌體 POST 時長。</summary>
    public const string PostKey = "fw.fpdt.post";

    // ── 版面常數（出處：ACPI 5.2.24＋fpdt.h）─────────────────────────────

    /// <summary>主表簽章。</summary>
    public const string TableSignature = "FPDT";
    /// <summary>紀錄表簽章：開機效能紀錄表。</summary>
    public const string BootPerformanceSignature = "FBPT";
    /// <summary>Firmware Basic Boot Performance Record 的類型碼。</summary>
    public const uint TypeBasicBoot = 0x00000001;
    /// <summary>每筆紀錄的表頭長度（Signature 4＋Length 4＋Revision 1＋Reserved 3）。</summary>
    public const int RecordHeaderLength = 16;
    /// <summary>Basic Boot 紀錄在表頭之後、時間欄位之前的固定欄位長度。
    /// （Reserved 4＋ResetEnd 8＋OsLoaderLoadImageStart 8＋OsLoaderStartImageStart 8＋ExitBootServicesEntry 8＋ExitBootServicesExit 8）</summary>
    public const int BasicBootFixedLength = 44;

    // ── 解碼（純函式，可單獨驗）─────────────────────────────────────────

    /// <summary>
    /// 從 FPDT 表位元組取出 POST 階段時長。<b>找不到主表／找不到 FBPT／找不到 Basic Boot 紀錄
    /// 一律回空清單</b>——三種缺席都由呼叫端轉成不適用，不互相冒充。
    /// </summary>
    public static IReadOnlyList<FpdtPhase> Decode(byte[]? table)
    {
        if (table is null || table.Length < RecordHeaderLength) return [];
        if (!table.AsSpan(0, 4).SequenceEqual(TableSignature.ToCharArray().Select(c => (byte)c).ToArray()))
            return [];

        int offset = AcpiTable.HeaderLength;   // 36：主表內容從標準表頭之後開始
        while (offset + RecordHeaderLength <= table.Length)
        {
            string sig = System.Text.Encoding.ASCII.GetString(table, offset, 4);
            uint len = BitConverter.ToUInt32(table, offset + 4);
            int rev = table[offset + 8];
            _ = rev;   // revision 目前不影響解讀，讀出來只是為了對齊位移

            // 下一步就先算出來：即使這一筆認不得，也要能前進，否則會卡在同一筆上。
            long next = (long)offset + (len >= RecordHeaderLength ? len : RecordHeaderLength);
            if (next <= offset || next > table.Length) break;

            if (sig == BootPerformanceSignature &&
                len >= RecordHeaderLength + 4 + BasicBootFixedLength)
            {
                // 紀錄類型碼緊接在紀錄表頭之後。
                uint type = BitConverter.ToUInt32(table, offset + RecordHeaderLength);
                if (type == TypeBasicBoot)
                {
                    int baseAt = offset + RecordHeaderLength + 4;
                    long resetEnd = BitConverter.ToInt64(table, baseAt + 4);
                    long osLoaderLoad = BitConverter.ToInt64(table, baseAt + 12);
                    long exitBootEntry = BitConverter.ToInt64(table, baseAt + 28);
                    long exitBootExit = BitConverter.ToInt64(table, baseAt + 36);

                    // 所有時間都是「自開機起的絕對時間（100 ns 單位）」，
                    // 所以 POST 時長＝OS 載入映像起點 − 重置結束。
                    double? sw = Duration(resetEnd, osLoaderLoad);
                    double? efi = Duration(osLoaderLoad, exitBootExit);
                    double? exit = Duration(exitBootEntry, exitBootExit);

                    var list = new List<FpdtPhase>(3);
                    if (sw is not null) list.Add(new FpdtPhase("Firmware Basic Boot", sw));
                    if (efi is not null) list.Add(new FpdtPhase("OS 載入映像", efi));
                    if (exit is not null) list.Add(new FpdtPhase("ExitBootServices", exit));
                    return list;
                }
            }

            offset = (int)next;
        }
        return [];
    }

    /// <summary>
    /// 兩個「自開機起的絕對時間」相減換算成秒。<b>負值回 <c>null</c> 而不是 0</b>——
    /// 時間軸回捲代表這筆紀錄沒填完或欄位位移解錯，出 0 秒會被讀成「很快」。
    /// </summary>
    private static double? Duration(long from, long to)
    {
        if (from == 0 && to == 0) return null;
        long delta = to - from;
        if (delta < 0) return null;
        return delta / 10_000_000.0;   // 100 ns 單位 → 秒
    }

    // ── 事實 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 收集 FPDT 事實。<paramref name="acpi"/> 為 ACPI 表來源；
    /// 來源不可用→<see cref="FactAvailability.InsufficientPrivilege"/>，
    /// 表缺席→<see cref="FactAvailability.NotApplicable"/>（缺席不是 0 秒）。
    /// </summary>
    public static HardwareFact Collect(IAcpiTableSource acpi, DateTimeOffset at)
    {
        if (!acpi.Available)
            return Unavailable(at, FactAvailability.InsufficientPrivilege,
                (acpi.UnavailableReason ?? "無法列舉 ACPI 表") + "——讀不到就是不猜 POST 時長");

        byte[]? fpdt = null;
        foreach (var t in acpi.ReadAll())
            if (t.Length >= AcpiTable.HeaderLength && t.AsSpan(0, 4).SequenceEqual("FPDT"u8))
            {
                fpdt = t;
                break;
            }

        if (fpdt is null)
            return Unavailable(at, FactAvailability.NotApplicable,
                "本平台無 FPDT（Firmware Performance Data Table）——不適用，不是 0 秒。" +
                "沒有這張表與「POST 花了 0 秒」是天差地別：前者是韌體沒提供，" +
                "後者是一個可看見的訊號；兩者不可互相冒充");

        var phases = Decode(fpdt);
        if (phases.Count == 0)
            return Unavailable(at, FactAvailability.NotApplicable,
                "FPDT 表存在但沒有可解讀的 Basic Boot 效能紀錄（可能只有 S3／S4 恢復紀錄，" +
                "或紀錄欄位未填）——缺席就不出值");

        // 主值取第一個階段（Firmware Basic Boot）；其餘階段並列在同一值裡。
        double primary = phases[0].FirmwareSeconds ?? 0;
        string detail = string.Join("、", phases.Select(p =>
            $"{p.Phase} {p.FirmwareSeconds:0.000} 秒"));

        return new HardwareFact(PostKey, Cat, "FPDT 韌體開機階段",
            $"共 {phases.Count} 個階段：{detail}。" +
            "<b>韌體自報的時長與作業系統觀察的開機時長是不同的量測點</b>——" +
            "FPDT 是韌體自己算的，Windows 的開機耗時是另一條路（見 ms.boot）；" +
            "兩者不一致是<b>常態不是矛盾</b>，不要拿其中一個去否定另一個。" +
            "<b>本項不定義「幾秒才算慢」</b>：門檻隨平台、韌體與開機裝置而異，" +
            "同樣 12 秒在一台老伺服器上是正常、在一台新筆電上是異常——" +
            "要判斷得拿這台機器自己的歷史比，不是拿別人的數字比。" +
            "時間軸回捲（負值）的紀錄不回 0，而是當成沒填完略過——0 秒會被讀成「很快」。",
            "秒", "ACPI FPDT／FBPT（唯讀）", FactTrustLevel.Reported, false, at, primary);
    }

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason)
        => new(PostKey, Cat, "FPDT 韌體開機階段", "", "", "ACPI FPDT／FBPT（唯讀）",
            FactTrustLevel.Unknown, false, at, null, availability, reason);
}
