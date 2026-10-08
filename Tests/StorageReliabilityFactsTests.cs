using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 儲存可靠性計數器事實（<see cref="StorageReliabilityFactsService"/>）。
///
/// 這一組測的是<b>誠實邊界</b>而不是數值本身：0 是值、空是空、讀不到要說得出原因。
/// 假的來源注入，不碰 WMI——這條接縫的用途正是如此。
/// </summary>
public class StorageReliabilityFactsTests
{
    /// <summary>全部欄位皆未提供的空殼；測試用 <c>with</c> 只覆蓋關心的欄位。</summary>
    private static readonly StorageReliabilityRaw Blank = new(
        DeviceId: 0, Wear: null, Temperature: null, TemperatureMax: null, PowerOnHours: null,
        StartStopCycleCount: null, StartStopCycleCountMax: null, LoadUnloadCycleCount: null,
        LoadUnloadCycleCountMax: null, ReadErrorsTotal: null, ReadErrorsCorrected: null,
        ReadErrorsUncorrected: null, WriteErrorsTotal: null, WriteErrorsCorrected: null,
        WriteErrorsUncorrected: null, ReadLatencyMax: null, WriteLatencyMax: null,
        FlushLatencyMax: null, ManufactureDate: null);

    private sealed class FakeSource : IStorageReliabilitySource
    {
        public bool Available { get; init; } = true;
        public string? UnavailableReason { get; init; }
        public IReadOnlyList<StorageReliabilityRaw> Rows { get; init; } = [];
        public IReadOnlyList<StorageReliabilityRaw> Read() => Rows;
    }

    private static readonly DateTimeOffset At = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 值為零必須是讀到而不是沒有()
    {
        // 磨損 0%、延遲 0 ms 都是合法值，是這條路上最容易踩的坑（把 0 當成缺）。
        var source = new FakeSource
        {
            Rows = [Blank with { DeviceId = 4, Wear = 0, ReadLatencyMax = 0, FlushLatencyMax = 0 }],
        };

        var facts = StorageReliabilityFactsService.Collect(At, source);

        foreach (string key in new[]
                 {
                     "storage.reliability.4.wear",
                     "storage.reliability.4.read_latency_max",
                     "storage.reliability.4.flush_latency_max",
                 })
        {
            var f = Assert.Single(facts, x => x.Key == key);
            Assert.Equal(FactAvailability.Present, f.Availability);
            Assert.Equal("0", f.Value);
            Assert.Equal(0d, f.NumericValue);
            Assert.Null(f.UnavailableReason);
        }
    }

    [Fact]
    public void 未提供的欄位不得生出事實只在收錄情形裡被數出來()
    {
        var source = new FakeSource { Rows = [Blank with { DeviceId = 4, Wear = 0, Temperature = 29 }] };

        var facts = StorageReliabilityFactsService.Collect(At, source);

        // 未提供 ≠ 0：這些鍵根本不該存在。
        Assert.DoesNotContain(facts, f => f.Key == "storage.reliability.4.power_on_hours");
        Assert.DoesNotContain(facts, f => f.Key == "storage.reliability.4.read_errors_total");

        // 但缺了什麼要被數出來、具名列出，而且說得出「這不是讀取失敗」。
        var coverage = Assert.Single(facts, f => f.Key == "storage.reliability.4.coverage");
        Assert.Contains("未提供：", coverage.Value);
        Assert.Contains("通電時數", coverage.Value);
        Assert.Equal(FactAvailability.NotApplicable, coverage.Availability);
        Assert.Contains("提供者沒有提供", coverage.UnavailableReason);
    }

    [Fact]
    public void 提供者全數提供時收錄情形不必掛不可用原因()
    {
        var full = Blank with
        {
            DeviceId = 1, Wear = 33, Temperature = 29, TemperatureMax = 40, PowerOnHours = 12000,
            StartStopCycleCount = 5, StartStopCycleCountMax = 100, LoadUnloadCycleCount = 7,
            LoadUnloadCycleCountMax = 200, ReadErrorsTotal = 0, ReadErrorsCorrected = 0,
            ReadErrorsUncorrected = 0, WriteErrorsTotal = 0, WriteErrorsCorrected = 0,
            WriteErrorsUncorrected = 0, ReadLatencyMax = 3, WriteLatencyMax = 4,
            FlushLatencyMax = 0, ManufactureDate = "2023-08",
        };

        var facts = StorageReliabilityFactsService.Collect(At, new FakeSource { Rows = [full] });

        var coverage = Assert.Single(facts, f => f.Key == "storage.reliability.1.coverage");
        Assert.Equal(FactAvailability.Present, coverage.Availability);
        Assert.Null(coverage.UnavailableReason);
        Assert.Contains("全數提供", coverage.Value);
        Assert.Contains(facts, f => f.Key == "storage.reliability.1.manufacture_date" && f.Value == "2023-08");
    }

    [Fact]
    public void 來源不可用時要說得出原因而不是安靜地什麼都沒有()
    {
        var facts = StorageReliabilityFactsService.Collect(At,
            new FakeSource { Available = false, UnavailableReason = "ManagementException：提供者載入失敗" });

        var f = Assert.Single(facts);
        Assert.Equal("storage.reliability.available", f.Key);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("提供者載入失敗", f.UnavailableReason);
    }

    [Fact]
    public void 來源可用但沒有任何計數器是環境狀態不是錯誤()
    {
        var facts = StorageReliabilityFactsService.Collect(At, new FakeSource { Rows = [] });

        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不是錯誤", f.UnavailableReason);
    }

    [Fact]
    public void 多顆碟各自成組且覆蓋數等於碟數()
    {
        var source = new FakeSource
        {
            Rows = [Blank with { DeviceId = 0, Wear = 0 }, Blank with { DeviceId = 1, Wear = 33 }],
        };

        var facts = StorageReliabilityFactsService.Collect(At, source);

        var count = Assert.Single(facts, f => f.Key == "storage.reliability.count");
        Assert.Equal("2", count.Value);
        Assert.Equal(2d, count.NumericValue);
        Assert.Contains(facts, f => f.Key == "storage.reliability.0.wear");
        Assert.Contains(facts, f => f.Key == "storage.reliability.1.wear");
    }

    /// <summary>
    /// 行為跟真實 WMI 來源一樣的假來源：<b>查詢之前 <see cref="Available"/> 是 true</b>
    /// （還沒試過，無從得知），失敗之後才翻成 false 並帶上原因。
    /// <para>
    /// 這一條是先前實作缺陷的迴歸測試：<c>Collect</c> 原本<b>先看 Available 再 Read</b>，
    /// 於是查詢失敗會走進「可用但 0 筆」那一支，把一次失敗寫成
    /// 「讀到了、只是沒有計數器——不是錯誤」。而舊的假來源測試沒抓到，因為它們的
    /// <c>Available</c> 是固定的、<c>Read()</c> 不會改它。
    /// </para>
    /// </summary>
    private sealed class FailsOnReadSource : IStorageReliabilitySource
    {
        public bool Available { get; private set; } = true;   // 還沒讀過＝還算能用
        public string? UnavailableReason { get; private set; }
        public IReadOnlyList<StorageReliabilityRaw> Read()
        {
            Available = false;
            UnavailableReason = "ManagementException：提供者載入失敗";
            return [];
        }
    }

    [Fact]
    public void 讀取當下才失敗的來源要如實說出原因不得掉進不是錯誤那一支()
    {
        var facts = StorageReliabilityFactsService.Collect(At, new FailsOnReadSource());

        var f = Assert.Single(facts);
        Assert.Equal("storage.reliability.available", f.Key);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("提供者載入失敗", f.UnavailableReason);
        Assert.DoesNotContain("不是錯誤", f.Value);
        Assert.DoesNotContain("不是錯誤", f.UnavailableReason);
    }

    [Fact]
    public void 共用組裝點要把來源接進事實清單不能讓它永遠是空的()
    {
        // 先前這一組事實接進了 AllFacts 與報告匯出，卻沒有任何地方呼叫載入——
        // 服務層測試全綠，真實 App 裡卻永遠是空的。這一條釘住「有人接線」。
        var svc = new EvidenceLabService();
        var source = new FakeSource { Rows = [Blank with { DeviceId = 0, Wear = 12 }] };

        EvidenceCollection.LoadUsermodeFacts(svc, source);

        Assert.NotEmpty(svc.StorageReliabilityFacts);
        Assert.Contains(svc.StorageReliabilityFacts, f => f.Key == "storage.reliability.0.wear" && f.Value == "12");
        Assert.Contains(svc.AllFacts, f => f.Key == "storage.reliability.0.wear");
        // 渲染列（頁面用的那一份）沒有 Key 欄，只有顯示文字——所以用顯示名對。
        // 渲染列的 Value 已含單位（"12 %"），所以比對前綴而不是全等。
        Assert.Contains(svc.FirmwareSecurityRows, r => r.Name.Contains("磨損程度") && r.Value.StartsWith("12", StringComparison.Ordinal));
    }

    [Fact]
    public void 共用組裝點在來源爆炸時不得把例外丟出去()
    {
        // 附加功能：來源爆炸（注入一個會丟的假來源）不能讓啟動或 CLI 整批失敗。
        var svc = new EvidenceLabService();

        EvidenceCollection.LoadUsermodeFacts(svc, new ThrowingSource());

        // 三態要留下痕跡，不是靜默什麼都沒有——空集合讀起來像「沒問題」。
        var f = Assert.Single(svc.StorageReliabilityFacts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("WMI 整個炸掉", f.UnavailableReason);
    }

    private sealed class ThrowingSource : IStorageReliabilitySource
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<StorageReliabilityRaw> Read() => throw new InvalidOperationException("WMI 整個炸掉");
    }

    [Fact]
    public void 兩條入口都要呼叫共用組裝點不能有一條漏掉()
    {
        // 這一條不是行為測試，是「接線還在不在」的守門，理由就是先前那次失敗：
        // 服務有、測試有、事實鍵也接進了 AllFacts——就是**沒有任何地方呼叫載入**，
        // 所以真實 App 與 CLI 裡永遠是空的。單元測試測不到「有沒有人接線」。
        // 原始碼層的守門是唯一能在不載入驅動的前提下檢查啟動路徑的辦法。
        foreach (string file in new[] { "App.xaml.cs", Path.Combine("ViewModels", "StartupSequence.cs") })
        {
            string path = Path.Combine(FindRepoRoot(), file);
            Assert.True(File.Exists(path), $"{file} 不存在——路徑改了就要一起改這裡");
            Assert.Contains("LoadUsermodeFacts", File.ReadAllText(path));
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到專案根目錄（XinSpect.csproj）");
    }

    [Fact]
    public void 鍵不得重複且每一筆都帶得出處()
    {
        var source = new FakeSource
        {
            Rows = [Blank with { DeviceId = 0, Wear = 0, Temperature = 29, ReadLatencyMax = 7 }],
        };

        var facts = StorageReliabilityFactsService.Collect(At, source);

        Assert.Equal(facts.Count, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(facts, f => Assert.Equal(StorageReliabilityFactsService.Source, f.Source));
        Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Category)));
        Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Name)));
    }
}
