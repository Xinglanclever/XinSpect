namespace XinSpect;

/// <summary>
/// 一個 NVMe 電源狀態。資料來自 Identify Controller 偏移 2048 起的電源狀態描述元
/// （Power State Descriptor，每筆 32 位元組）。
/// </summary>
/// <remarks>
/// 這些是<b>裝置自己宣告</b>的數字，不是量到的：進入／離開延遲代表控制器承諾的上限，
/// 實際多久要另外量。功耗欄位同理，是宣告值而非當下耗電。
/// </remarks>
public sealed class NvmePowerStateRow
{
    public required int State { get; init; }

    /// <summary>非運作狀態（NOPS＝1）：待在這個狀態時無法處理 I/O，要先花「離開延遲」爬回來。</summary>
    public required bool NonOperational { get; init; }

    /// <summary>最大功耗（W）；裝置回報 0 代表未回報，此時為 null。</summary>
    public double? MaxPowerW { get; init; }

    /// <summary>進入此狀態所需時間（µs）；0 代表未回報。</summary>
    public uint? EntryLatencyUs { get; init; }

    /// <summary>離開此狀態所需時間（µs）；0 代表未回報。這一欄就是「閒置之後第一筆 I/O 為什麼慢」。</summary>
    public uint? ExitLatencyUs { get; init; }

    /// <summary>閒置功耗（W）；刻度欄位為 0（未回報）時即為 null，不採用數值欄。</summary>
    public double? IdlePowerW { get; init; }

    /// <summary>作用功耗（W）；同樣看刻度欄位決定是否可用。</summary>
    public double? ActivePowerW { get; init; }

    /// <summary>作用功耗的工作型態提示（APW）；0 代表未回報。</summary>
    public byte ActiveWorkload { get; init; }

    // 以下四欄是「相對排名」（0 最好），不是吞吐量也不是延遲值
    public required byte RelRead { get; init; }
    public required byte RelReadLatency { get; init; }
    public required byte RelWrite { get; init; }
    public required byte RelWriteLatency { get; init; }

    public string Name => $"PS{State}";
    public string KindText => NonOperational ? "非運作（睡眠，不處理 I/O）" : "運作";
    public string MaxPowerText => MaxPowerW is { } w ? Watts(w) : "未回報";
    public string IdlePowerText => IdlePowerW is { } w ? Watts(w) : "未回報";
    public string ActivePowerText => ActivePowerW is { } w ? Watts(w) : "未回報";
    public string EntryLatencyText => Latency(EntryLatencyUs);
    public string ExitLatencyText => Latency(ExitLatencyUs);

    public string RelativeText =>
        $"讀取吞吐排名 第 {RelRead}、讀取延遲 第 {RelReadLatency}；"
        + $"寫入吞吐排名 第 {RelWrite}、寫入延遲 第 {RelWriteLatency}（0 最好，只在同一顆碟內比較）";

    /// <summary>功耗文字：小於 0.1 W 改用毫瓦，否則瓦。</summary>
    internal static string Watts(double w)
        => w < 0.1 ? $"{w * 1000:0.#} mW" : $"{w:0.##} W";

    /// <summary>延遲文字：未回報就說未回報，不寫成 0。</summary>
    internal static string Latency(uint? us)
    {
        if (us is not { } v || v == 0) return "未回報";
        if (v < 1_000) return $"{v} µs";
        if (v < 1_000_000) return $"{v / 1000.0:0.##} ms";
        return $"{v / 1_000_000.0:0.##} 秒";
    }
}

/// <summary>APST（自主電源狀態轉換）表的一列：閒置多久之後自動降到哪一個狀態。</summary>
public sealed class NvmeApstRow
{
    public required int State { get; init; }
    /// <summary>閒置門檻（ms）；0 代表這個狀態不自動降態。</summary>
    public required uint IdleMs { get; init; }
    public required int TargetState { get; init; }

    public string Name => $"PS{State}";
    public string Text => IdleMs == 0
        ? "不自動降態"
        : $"閒置 {IdleMs:N0} ms 後自動降到 PS{TargetState}";
}

/// <summary>一次「先閒置、再量單筆讀取」的結果。</summary>
/// <param name="IdleMs">量測前刻意閒置的毫秒數。</param>
/// <param name="FirstReadUs">閒置結束後<b>第一筆</b> 4K 讀取的耗時（µs）。</param>
public sealed record IdleLatencySample(int IdleMs, double FirstReadUs)
{
    public string IdleText => IdleMs == 0 ? "不閒置（基線）" : $"{IdleMs:N0} ms";

    /// <summary>實測值一律照實印出，不會出現「未回報」——量到了就是量到了。</summary>
    public string ReadText => Measured(FirstReadUs);

    /// <summary>實測耗時文字（µs／ms／秒）。與宣告值的格式一致，方便並排比較。</summary>
    internal static string Measured(double us)
        => us < 1_000 ? $"{us:0} µs"
         : us < 1_000_000 ? $"{us / 1000.0:0.##} ms"
         : $"{us / 1_000_000.0:0.##} 秒";
}

/// <summary>閒置後首筆讀取變慢的歸因判決。</summary>
public sealed class NvmeIdleVerdict
{
    public required string Headline { get; init; }
    public required string Detail { get; init; }
    /// <summary>0＝無事或無法判定、1＝有可解釋的停頓、2＝停頓超出宣告值。</summary>
    public required int Severity { get; init; }
}

/// <summary>
/// NVMe 電源狀態與 APST 的解碼（純函式，不碰硬體）。
/// </summary>
public static class NvmePowerDecoder
{
    /// <summary>電源狀態描述元表在 Identify Controller 內的起始位移。</summary>
    private const int PsdBase = 2048;

    /// <summary>規格上限：PS0–PS31。</summary>
    private const int MaxStates = 32;

    /// <summary>解出全部宣告存在的電源狀態。表格其餘部分是保留區，不能當成「0 W 的狀態」列出來。</summary>
    public static List<NvmePowerStateRow> PowerStates(byte[] identify)
    {
        var rows = new List<NvmePowerStateRow>();
        if (identify.Length < PsdBase + 32) return rows;

        // NPSS 是「支援數減一」；裝置回報壞值（規格上限是 31）時只取表內容納得下的
        int count = Math.Clamp(identify[263] + 1, 1, MaxStates);
        count = Math.Min(count, (identify.Length - PsdBase) / 32);

        for (int s = 0; s < count; s++) rows.Add(One(identify, PsdBase + s * 32, s));
        return rows;
    }

    private static NvmePowerStateRow One(byte[] id, int o, int state)
    {
        ushort mp = (ushort)(id[o] | (id[o + 1] << 8));
        byte flags = id[o + 3];
        bool mxps = (flags & 0x01) != 0;          // 1：MP 的單位是 0.0001 W
        bool nops = (flags & 0x02) != 0;

        uint enlat = BitConverter.ToUInt32(id, o + 4);
        uint exlat = BitConverter.ToUInt32(id, o + 8);

        ushort idlp = (ushort)(id[o + 16] | (id[o + 17] << 8));
        byte ips = (byte)(id[o + 18] >> 6);       // 0：未回報、1：0.0001 W、2：0.01 W
        ushort actp = (ushort)(id[o + 20] | (id[o + 21] << 8));
        byte apsFlags = id[o + 22];
        byte aps = (byte)(apsFlags >> 6);
        byte apw = (byte)(apsFlags & 0x07);

        return new NvmePowerStateRow
        {
            State = state,
            NonOperational = nops,
            MaxPowerW = mp == 0 ? null : mp * (mxps ? 0.0001 : 0.01),
            EntryLatencyUs = enlat == 0 ? null : enlat,
            ExitLatencyUs = exlat == 0 ? null : exlat,
            IdlePowerW = Scaled(idlp, ips),
            ActivePowerW = Scaled(actp, aps),
            ActiveWorkload = apw,
            RelRead = (byte)(id[o + 12] & 0x1F),
            RelReadLatency = (byte)(id[o + 13] & 0x1F),
            RelWrite = (byte)(id[o + 14] & 0x1F),
            RelWriteLatency = (byte)(id[o + 15] & 0x1F),
        };
    }

    /// <summary>依刻度欄位換算功耗；刻度為 0（未回報）時回 null——數值欄的內容此時無意義，不能拿來用。</summary>
    private static double? Scaled(ushort raw, byte scale) => scale switch
    {
        1 => raw * 0.0001,
        2 => raw * 0.01,
        _ => null,
    };

    /// <summary>APST 是否受支援（Identify Controller 的 APSTA，位移 265 位 0）。</summary>
    public static bool ApstSupported(byte[] identify)
        => identify.Length > 265 && (identify[265] & 0x01) != 0;

    /// <summary>
    /// 解 Get Features 0x0C 的資料區（32 筆 × 8 位元組）：
    /// 位 23:8＝閒置門檻（ms）、位 27:24＝目標電源狀態。
    /// </summary>
    public static List<NvmeApstRow> ApstTable(byte[] data, int stateCount)
    {
        var rows = new List<NvmeApstRow>();
        int count = Math.Clamp(stateCount, 0, Math.Min(MaxStates, data.Length / 8));
        for (int s = 0; s < count; s++)
        {
            ulong v = BitConverter.ToUInt64(data, s * 8);
            rows.Add(new NvmeApstRow
            {
                State = s,
                IdleMs = (uint)((v >> 8) & 0xFFFF),
                TargetState = (int)((v >> 24) & 0x1F),
            });
        }
        return rows;
    }

    // ── 宣告值 × 實測值 ───────────────────────────────────────────────────

    /// <summary>認定「有停頓」的門檻：比基線多出這麼多微秒才算，否則是量測雜訊。</summary>
    private const double RiseFloorUs = 500;

    /// <summary>
    /// 把實測的「閒置後首筆讀取」與宣告的離開延遲對起來下判決。
    /// <para>
    /// 四種結局：與宣告值相符（歸因於省電狀態）、遠小於宣告值（沒有真的進到那個狀態）、
    /// 遠大於宣告值（不是省電狀態能解釋的）、以及沒有可歸因的狀態（不猜）。
    /// </para>
    /// </summary>
    public static NvmeIdleVerdict Verdict(
        IReadOnlyList<NvmePowerStateRow> states, bool apstSupported, IReadOnlyList<IdleLatencySample> samples)
    {
        if (samples.Count < 2)
            return new NvmeIdleVerdict { Headline = "尚未量測", Severity = 0, Detail = "按下量測後才有實測值可以對照宣告的離開延遲。" };

        var baseline = samples.MinBy(s => s.IdleMs)!;
        var worst = samples.MaxBy(s => s.FirstReadUs)!;
        double rise = worst.FirstReadUs - baseline.FirstReadUs;

        string host = apstSupported
            ? "這顆碟宣告支援 APST（自主降態），降到哪一階由碟自己決定。"
            : "這顆碟未宣告支援 APST，降態若真的發生，是由主機端的電源管理決定的（Windows 的 NVMe 閒置電源政策）。";
        string measured =
            $"基線（閒置 {baseline.IdleMs} ms）{IdleLatencySample.Measured(baseline.FirstReadUs)}，"
            + $"最慢的一筆是閒置 {worst.IdleMs} ms 之後的 {IdleLatencySample.Measured(worst.FirstReadUs)}。";

        if (rise < RiseFloorUs)
            return new NvmeIdleVerdict
            {
                Headline = "沒有觀察到閒置造成的停頓",
                Severity = 0,
                Detail = $"{measured}差距在量測雜訊範圍內（不到 {RiseFloorUs:0} µs）。{host}",
            };

        // 可歸因的候選：宣告了離開延遲的非運作狀態，取離開延遲最大的那一個
        var deepest = states.Where(s => s.NonOperational && s.ExitLatencyUs is > 0)
                            .MaxBy(s => s.ExitLatencyUs!.Value);
        if (deepest is null)
            return new NvmeIdleVerdict
            {
                Headline = "有停頓，但無法歸因",
                Severity = 1,
                Detail = $"{measured}這顆碟沒有任何非運作狀態宣告離開延遲，所以無從對照——"
                       + $"停頓是真的，原因不在本頁能證明的範圍內。{host}",
            };

        double declared = deepest.ExitLatencyUs!.Value;
        string cmp = $"宣告最深的非運作電源狀態是 {deepest.Name}（離開延遲 {deepest.ExitLatencyText}）。";

        if (rise > declared * 3)
            return new NvmeIdleVerdict
            {
                Headline = "停頓超出宣告的離開延遲",
                Severity = 2,
                Detail = $"{measured}{cmp}實測多出的 {IdleLatencySample.Measured(rise)} 是宣告值的 "
                       + $"{rise / declared:0.#} 倍，光靠電源狀態解釋不了；其餘可能（媒體整理、韌體、主機端排程）本頁不做斷言。{host}",
            };

        if (rise < declared * 0.5)
            return new NvmeIdleVerdict
            {
                Headline = "有停頓，但沒有進到最深的電源狀態",
                Severity = 1,
                Detail = $"{measured}{cmp}實測只多出 {IdleLatencySample.Measured(rise)}，"
                       + $"不到宣告值的一半——閒置期間應該只降到較淺的狀態。{host}",
            };

        return new NvmeIdleVerdict
        {
            Headline = "閒置後的首筆停頓可歸因於電源狀態",
            Severity = 1,
            Detail = $"{measured}{cmp}實測多出的 {IdleLatencySample.Measured(rise)} 與宣告值同一個量級，"
                   + $"與「碟降到 {deepest.Name}、被喚醒時付出離開延遲」相符。{host}",
        };
    }
}
