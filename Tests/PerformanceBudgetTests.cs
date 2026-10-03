using System.IO;
using System.Diagnostics;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// A54 效能預算的契約：<b>預算是測試</b>——可重複量測的兩項（全套掃描編排／報告產生）真量真擋；
/// 三個執行期指標（冷啟動／常駐／CPU 閒置）標 RuntimeOnly，測試釘「登記存在＋門檻未被放寬」，
/// 實際量測由 SelfTelemetry 在應用程式內記錄（單元測試量它們必然不可重複——誠實界線）。
/// SelfTelemetry：預設關閉（未啟用 Record 是 no-op）、匿名（只有數字與時間）、本機不上傳。
/// </summary>
public class PerformanceBudgetTests : IDisposable
{
    private readonly string _telemetryPath = Path.Combine(Path.GetTempPath(), $"xintelemetry-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_telemetryPath)) File.Delete(_telemetryPath);
    }

    // ===== 預算登記完整性（門檻釘 V7 原值，放寬即紅） =====

    [Fact]
    public void 預算登記_五項齊門檻釘V7原值_執行期指標正確標記()
    {
        Assert.Equal(5, PerfBudget.All.Count);
        Assert.Equal(3, PerfBudget.ColdStart.Threshold);
        Assert.Equal(300, PerfBudget.ResidentMemory.Threshold);
        Assert.Equal(1, PerfBudget.IdleCpu.Threshold);
        Assert.Equal(60, PerfBudget.FullScan.Threshold);
        Assert.Equal(5, PerfBudget.HtmlReport.Threshold);
        Assert.True(PerfBudget.ColdStart.RuntimeOnly);
        Assert.True(PerfBudget.ResidentMemory.RuntimeOnly);
        Assert.True(PerfBudget.IdleCpu.RuntimeOnly);
        Assert.False(PerfBudget.FullScan.RuntimeOnly);
        Assert.False(PerfBudget.HtmlReport.RuntimeOnly);
    }

    // ===== 可重複量測一：報告產生 < 5 秒 =====

    [Fact]
    public void 預算_報告產生_2000列大表低於5秒()
    {
        var rows = MakeRows(2000);
        var sw = Stopwatch.StartNew();
        var html = HtmlReportService.Build(rows, "效能預算量測", DateTimeOffset.UtcNow);
        sw.Stop();

        Assert.True(html.Length > 100_000, "報告應實質渲染（非空殼）");
        Assert.True(sw.Elapsed.TotalSeconds < PerfBudget.HtmlReport.Threshold,
            $"報告產生 {sw.Elapsed.TotalSeconds:0.###} 秒超過預算 {PerfBudget.HtmlReport.Threshold} 秒");
    }

    // ===== 可重複量測二：全套掃描編排 < 60 秒（假讀取器，量編排不量硬體） =====

    [Fact]
    public void 預算_全套掃描編排_20輪低於60秒()
    {
        var svc = new EvidenceLabService();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
            svc.ReloadDriverBackedFacts(new FakeBenchPci(), new FakeBenchMsr(), new FakeBenchMmio(),
                new FakeBenchAcpi(), new FakeBenchIo(), new FakeSmbusIo());
        sw.Stop();

        Assert.True(svc.AllFacts.Count > 20, "掃描應實質產生事實（非空轉）");
        Assert.True(sw.Elapsed.TotalSeconds < PerfBudget.FullScan.Threshold,
            $"20 輪掃描編排 {sw.Elapsed.TotalSeconds:0.###} 秒超過預算 {PerfBudget.FullScan.Threshold} 秒");
    }

    // ===== SelfTelemetry：預設關閉、匿名、本機 =====

    [Fact]
    public void 遙測_預設關閉Record無聲跳過_啟用後記錄且停用即停()
    {
        var t = new SelfTelemetry();
        t.RecordScan(TimeSpan.FromMilliseconds(100), 10, 1, 0); // 未啟用：no-op
        Assert.Null(t.Summary());

        t.Enable();
        t.RecordScan(TimeSpan.FromMilliseconds(100), 10, 1, 0);
        t.RecordScan(TimeSpan.FromMilliseconds(300), 30, 3, 1);
        var summary = t.Summary();
        Assert.NotNull(summary);
        Assert.Equal(2, summary.Scans);
        Assert.Equal(200, summary.AvgDurationMs, precision: 0);
        Assert.Equal(0.1, summary.UnavailableRate, precision: 4);
        Assert.Equal(1, summary.ExceptionCount);

        t.Disable();
        t.RecordScan(TimeSpan.FromMilliseconds(999), 99, 99, 99);
        Assert.Equal(2, t.Scans.Count); // 停用後不再記錄
    }

    [Fact]
    public void 遙測檔_只有數字與時間_不含任何事實內容()
    {
        var t = new SelfTelemetry();
        t.Enable();
        t.RecordScan(TimeSpan.FromMilliseconds(50), 2816, 3, 0);
        t.SaveToFile(_telemetryPath);

        var text = File.ReadAllText(_telemetryPath);
        Assert.Contains("durationms", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("spi.hsfsts", text, StringComparison.OrdinalIgnoreCase); // 無事實鍵
        Assert.DoesNotContain("最強保護", text, StringComparison.OrdinalIgnoreCase);    // 無事實值

        var t2 = new SelfTelemetry();
        t2.LoadFromFile(_telemetryPath);
        Assert.Equal(t.Scans.Count, t2.Scans.Count);
    }

    [Fact]
    public void 遙測_只記數字_不含任何事實內容()
    {
        var t = new SelfTelemetry();
        t.Enable();
        t.RecordScan(TimeSpan.FromMilliseconds(1234), 2816, 3, 1);
        var summary = t.Summary();
        Assert.NotNull(summary);
        Assert.Equal(1, summary.Scans);
        Assert.Equal(1234, summary.AvgDurationMs, precision: 0);
        Assert.Equal(1, summary.ExceptionCount);
        // 記錄型別本身只有數字與時間（TelemetryScan 無內容欄位）——設計即界線。
    }

    [Fact]
    public void 遙測檔_存載往返_內容不含事實材料()
    {
        var t = new SelfTelemetry();
        t.Enable();
        t.RecordScan(TimeSpan.FromMilliseconds(50), 2816, 3, 0);
        t.SaveToFile(_telemetryPath);

        var text = File.ReadAllText(_telemetryPath);
        Assert.Contains("durationms", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("spi.hsfsts", text, StringComparison.OrdinalIgnoreCase); // 無事實鍵
        Assert.DoesNotContain("最強保護", text, StringComparison.OrdinalIgnoreCase);    // 無事實值

        var t2 = new SelfTelemetry();
        t2.LoadFromFile(_telemetryPath);
        Assert.Equal(t.Scans.Count, t2.Scans.Count);
    }

    /// <summary>造 N 列混合型態的事實列（可讀／三態／警示各三分之一）——報告產生的量測材料。</summary>
    private static IReadOnlyList<EvidenceFactRow> MakeRows(int count)
    {
        var rows = new List<EvidenceFactRow>(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add((i % 3) switch
            {
                0 => new EvidenceFactRow("量測", $"項目 {i}", "最強保護：SMM_BWP=1", "s", "裝置直讀", false),
                1 => new EvidenceFactRow("量測", $"項目 {i}", "", "s", "裝置直讀", false,
                    FactAvailability.ReadError, "讀取失敗（量測材料）"),
                _ => new EvidenceFactRow("量測", $"項目 {i}", "未保護：BLE=0", "s", "裝置直讀", false),
            });
        }
        return rows;
    }

    private sealed class FakeBenchPci : IPciConfigReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;

        public uint? ReadDword(byte bus, byte device, byte function, uint register) =>
            (device, function, register) switch
            {
                (0x00, 0, 0x00) => 0x70A4_8086,      // 主機橋
                (0x00, 0, 0x08) => 0x0600_0000u,
                (0x00, 0, 0x48) => 0xFEDC0001u,      // MCHBAR（啟用）
                (0x00, 0, 0x88) => 0x00000010u,      // SMRAMC D_LCK
                (0x1F, 0, 0x00) => 0x06D1_8086,      // LPC
                (0x1F, 0, 0x08) => 0x0601_0000u,
                (0x1F, 0, 0x0C) => 0x0080_0000u,     // 多功能
                (0x1F, 5, 0x00) => 0x06C0_8086,      // SPI 控制器
                (0x1F, 5, 0x10) => 0xFED10000u,      // SPIBAR
                (0x1F, 5, 0x08) => 0xFF00_0000u,
                _ => 0xFFFF_FFFF,
            };
    }

    private sealed class FakeBenchMsr : IKernelMsrReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public ulong? ReadMsr(uint index) => index switch
        {
            0x8B => 0x0000_0002_0600_0600uL, // 微碼
            0x1A2 => 0x005A_0000uL,          // TjMax=90
            _ => 0,
        };
    }

    private sealed class FakeBenchMmio : IMmioReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;

        public byte[]? ReadBlock(ulong address, int length)
        {
            if (address == 0xFED10000) // SPIBAR：FREG 有內容、PR 全停
            {
                var block = new byte[0x88];
                BitConverter.GetBytes(0x0000_000Fu).CopyTo(block, 0x54); // FREG0
                BitConverter.GetBytes(0x0FFF_0100u).CopyTo(block, 0x58); // FREG1 BIOS
                return block;
            }
            if (address == 0xFEDC0000) return new byte[length]; // MCHBAR 視窗（全 0＝可讀）
            if (address >= 0xE000_0000 && address < 0xE800_0000)
                return Enumerable.Repeat((byte)0xFF, length).ToArray(); // ECAM：全 F＝無裝置
            return null;
        }
    }

    private sealed class FakeBenchAcpi : IAcpiTableSource
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<byte[]> ReadAll() => [];
    }

    private sealed class FakeBenchIo : IIoPortAccess
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        private byte _index;
        public byte? InByte(uint port) => port is 0x71 or 0x2F or 0x4F ? (byte)0xFF : (byte)0x00;
        public bool OutByte(uint port, byte value) { if (port is 0x70 or 0x2E or 0x4E) _index = value; return true; }
    }
}
