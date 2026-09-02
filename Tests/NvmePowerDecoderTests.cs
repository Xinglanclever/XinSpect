using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// NVMe 電源狀態與 APST 的解碼（純函式）。
///
/// 這一份要守的是位移與單位：NVMe 規格把電源狀態描述元放在 Identify Controller 偏移 2048 起、
/// 每筆 32 位元組，而「最大功耗」的單位隨同一筆裡的一個位元（MXPS）在 0.01 W 與 0.0001 W 之間跳，
/// 閒置與作用功耗另有各自的刻度欄位。單位讀錯會差一百倍，而且畫面上看起來完全合理。
/// </summary>
public class NvmePowerDecoderTests
{
    // ── 造一份合成的 Identify Controller ──────────────────────────────────

    /// <summary>寫入一筆電源狀態描述元（欄位以原始位元組指定，才驗得到位移）。</summary>
    private static void Psd(byte[] id, int state,
        ushort mp, bool maxPowerScaleIs100uW, bool nonOperational,
        uint enlatUs, uint exlatUs,
        byte rrt = 0, byte rrl = 0, byte rwt = 0, byte rwl = 0,
        ushort idlePower = 0, byte idleScale = 0,
        ushort activePower = 0, byte activeScale = 0, byte activeWorkload = 0)
    {
        int o = 2048 + state * 32;
        id[o] = (byte)(mp & 0xFF);
        id[o + 1] = (byte)(mp >> 8);
        id[o + 3] = (byte)((maxPowerScaleIs100uW ? 1 : 0) | (nonOperational ? 2 : 0));
        BitConverter.GetBytes(enlatUs).CopyTo(id, o + 4);
        BitConverter.GetBytes(exlatUs).CopyTo(id, o + 8);
        id[o + 12] = rrt; id[o + 13] = rrl; id[o + 14] = rwt; id[o + 15] = rwl;
        id[o + 16] = (byte)(idlePower & 0xFF); id[o + 17] = (byte)(idlePower >> 8);
        id[o + 18] = (byte)(idleScale << 6);
        id[o + 20] = (byte)(activePower & 0xFF); id[o + 21] = (byte)(activePower >> 8);
        id[o + 22] = (byte)((activeScale << 6) | (activeWorkload & 0x07));
    }

    private static byte[] Identify(byte npss, bool apstSupported)
    {
        var id = new byte[4096];
        id[263] = npss;                              // NPSS：支援的電源狀態數減一
        id[265] = (byte)(apstSupported ? 1 : 0);     // APSTA 位 0
        return id;
    }

    // ── 只列宣告存在的狀態 ────────────────────────────────────────────────

    [Fact]
    public void 只列出宣告存在的電源狀態()
    {
        var id = Identify(npss: 3, apstSupported: true);
        for (int i = 0; i <= 3; i++) Psd(id, i, mp: 500, false, false, 0, 0);
        // 第 4 筆之後整片為零：那是保留區，不是「一個 0 W 的電源狀態」

        var rows = NvmePowerDecoder.PowerStates(id);
        Assert.Equal(4, rows.Count);
        Assert.Equal(0, rows[0].State);
        Assert.Equal(3, rows[^1].State);
    }

    [Fact]
    public void 電源狀態數超出表格容量時只取表內的()
    {
        var id = Identify(npss: 200, apstSupported: false);   // 規格上限 31，回報 200 就是壞資料
        var rows = NvmePowerDecoder.PowerStates(id);
        Assert.True(rows.Count <= 32);
    }

    // ── 單位 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 最大功耗的單位隨MXPS位元改變()
    {
        var id = Identify(npss: 1, apstSupported: false);
        Psd(id, 0, mp: 900, maxPowerScaleIs100uW: false, false, 0, 0);   // 900 × 0.01 W = 9 W
        Psd(id, 1, mp: 900, maxPowerScaleIs100uW: true, true, 0, 0);     // 900 × 0.0001 W = 0.09 W

        var rows = NvmePowerDecoder.PowerStates(id);
        Assert.Equal(9.0, rows[0].MaxPowerW!.Value, 6);
        Assert.Equal(0.09, rows[1].MaxPowerW!.Value, 6);
    }

    [Fact]
    public void 最大功耗為零視為未回報而不是零瓦()
    {
        var id = Identify(npss: 0, apstSupported: false);
        Psd(id, 0, mp: 0, false, false, 0, 0);

        var r = NvmePowerDecoder.PowerStates(id)[0];
        Assert.Null(r.MaxPowerW);
        Assert.Contains("未回報", r.MaxPowerText);
    }

    [Fact]
    public void 進入與離開延遲為零時說未回報()
    {
        var id = Identify(npss: 0, apstSupported: false);
        Psd(id, 0, mp: 500, false, false, enlatUs: 0, exlatUs: 0);

        var r = NvmePowerDecoder.PowerStates(id)[0];
        Assert.Null(r.EntryLatencyUs);
        Assert.Null(r.ExitLatencyUs);
        Assert.Contains("未回報", r.EntryLatencyText);
        Assert.Contains("未回報", r.ExitLatencyText);
    }

    [Fact]
    public void 延遲以微秒讀入並依大小換算單位()
    {
        var id = Identify(npss: 2, apstSupported: false);
        Psd(id, 0, 500, false, false, enlatUs: 250, exlatUs: 900);
        Psd(id, 1, 100, false, true, enlatUs: 5_000, exlatUs: 8_000);
        Psd(id, 2, 50, true, true, enlatUs: 1_500_000, exlatUs: 32_000_000);

        var rows = NvmePowerDecoder.PowerStates(id);
        Assert.Equal(900u, rows[0].ExitLatencyUs);
        Assert.Contains("µs", rows[0].ExitLatencyText);
        Assert.Contains("ms", rows[1].ExitLatencyText);     // 8000 µs → 8 ms
        Assert.Contains("秒", rows[2].ExitLatencyText);      // 32 s
    }

    [Fact]
    public void 非運作狀態要標示出來()
    {
        var id = Identify(npss: 1, apstSupported: false);
        Psd(id, 0, 500, false, nonOperational: false, 0, 0);
        Psd(id, 1, 50, false, nonOperational: true, 0, 0);

        var rows = NvmePowerDecoder.PowerStates(id);
        Assert.False(rows[0].NonOperational);
        Assert.True(rows[1].NonOperational);
        Assert.Contains("運作", rows[0].KindText);
        Assert.Contains("非運作", rows[1].KindText);
    }

    [Fact]
    public void 閒置與作用功耗依各自刻度換算_未回報時不猜()
    {
        var id = Identify(npss: 1, apstSupported: false);
        Psd(id, 0, 500, false, false, 0, 0, idlePower: 1200, idleScale: 1,      // 1 = 0.0001 W → 0.12 W
            activePower: 700, activeScale: 2, activeWorkload: 1);               // 2 = 0.01 W → 7 W
        Psd(id, 1, 100, false, true, 0, 0, idlePower: 5000, idleScale: 0);      // 0 = 未回報，數值不得採用

        var rows = NvmePowerDecoder.PowerStates(id);
        Assert.Equal(0.12, rows[0].IdlePowerW!.Value, 6);
        Assert.Equal(7.0, rows[0].ActivePowerW!.Value, 6);
        Assert.Null(rows[1].IdlePowerW);
        Assert.Contains("未回報", rows[1].IdlePowerText);
    }

    [Fact]
    public void 相對讀寫吞吐與延遲為排名而非數值()
    {
        var id = Identify(npss: 0, apstSupported: false);
        Psd(id, 0, 500, false, false, 0, 0, rrt: 0, rrl: 2, rwt: 1, rwl: 3);

        var r = NvmePowerDecoder.PowerStates(id)[0];
        // 這四個欄位是「第幾名」（0 最好），不是 MB/s 也不是微秒；文字必須說清楚
        Assert.Contains("第 0", r.RelativeText);
        Assert.Contains("排名", r.RelativeText);
    }

    // ── APST ──────────────────────────────────────────────────────────────

    [Fact]
    public void APST支援與否取自APSTA位元()
    {
        Assert.True(NvmePowerDecoder.ApstSupported(Identify(0, apstSupported: true)));
        Assert.False(NvmePowerDecoder.ApstSupported(Identify(0, apstSupported: false)));
    }

    [Fact]
    public void APST表解出每個狀態的閒置門檻與目標狀態()
    {
        // Get Features 0x0C 的資料區：32 筆 × 8 位元組，位 23:8＝閒置毫秒、位 27:24＝目標狀態
        var data = new byte[256];
        void Entry(int i, uint idleMs, byte target)
            => BitConverter.GetBytes(((ulong)target << 24) | ((ulong)idleMs << 8)).CopyTo(data, i * 8);
        Entry(0, 100, 3);
        Entry(1, 250, 4);

        var rows = NvmePowerDecoder.ApstTable(data, stateCount: 2);
        Assert.Equal(2, rows.Count);
        Assert.Equal(100u, rows[0].IdleMs);
        Assert.Equal(3, rows[0].TargetState);
        Assert.Equal(250u, rows[1].IdleMs);
        Assert.Equal(4, rows[1].TargetState);
    }

    [Fact]
    public void APST某狀態閒置門檻為零表示不自動降態()
    {
        var data = new byte[256];   // 全零
        var rows = NvmePowerDecoder.ApstTable(data, stateCount: 2);
        Assert.All(rows, r => Assert.Equal(0u, r.IdleMs));
        Assert.All(rows, r => Assert.Contains("不自動", r.Text));
    }
}
