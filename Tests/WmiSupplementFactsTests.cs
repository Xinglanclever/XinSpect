using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

public class WmiSupplementFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] RuntimeKeys =
        ["wmi.bus", "wmi.portres", "wmi.irqres", "wmi.cpu.cache.assoc"];

    private static WmiSupplementSnapshot Snapshot(
        WmiClassQueryResult<WmiBusRow>? buses = null,
        WmiClassQueryResult<WmiPortResourceRow>? ports = null,
        WmiClassQueryResult<WmiIrqResourceRow>? irqs = null,
        WmiClassQueryResult<WmiProcessorCacheAssociation>? associations = null) =>
        new(
            buses ?? WmiClassQueryResult<WmiBusRow>.Success([]),
            ports ?? WmiClassQueryResult<WmiPortResourceRow>.Success([]),
            irqs ?? WmiClassQueryResult<WmiIrqResourceRow>.Success([]),
            associations ?? WmiClassQueryResult<WmiProcessorCacheAssociation>.Success([]));

    [Fact]
    public void 匯流排保留未知類型碼並只報原始分布()
    {
        var facts = WmiSupplementFactsService.Collect(At, () => Snapshot(
            buses: WmiClassQueryResult<WmiBusRow>.Success([new(5), new(15), new(17), new(null)])));

        var bus = Assert.Single(facts, x => x.Key == "wmi.bus");
        Assert.Equal(4d, bus.NumericValue);
        Assert.Contains("碼 17 ×1", bus.Value);
        Assert.Contains("BusType 缺值 1 筆", bus.Value);
        Assert.Contains("未收錄碼不猜名稱", bus.Value);
    }

    [Fact]
    public void 埠與IRQ摘要分開統計別名及硬體標記且不誤稱唯一中斷數()
    {
        var facts = WmiSupplementFactsService.Collect(At, () => Snapshot(
            ports: WmiClassQueryResult<WmiPortResourceRow>.Success([new(false), new(true), new(null)]),
            irqs: WmiClassQueryResult<WmiIrqResourceRow>.Success([new(true), new(false), new(null)])));

        var ports = Assert.Single(facts, x => x.Key == "wmi.portres");
        Assert.Contains("基底列 1、別名列 1、未回報 1", ports.Value);
        Assert.Contains("不輸出地址", ports.Value);

        var irq = Assert.Single(facts, x => x.Key == "wmi.irqres");
        Assert.Contains("Hardware 標記：true 1、false 1、未回報 1", irq.Value);
        Assert.Contains("不是唯一 IRQ 線或處理器向量數", irq.Value);
    }

    [Fact]
    public void 快取關聯只計參照與列數不冒充快取容量或CPU頻率()
    {
        var facts = WmiSupplementFactsService.Collect(At, () => Snapshot(
            associations: WmiClassQueryResult<WmiProcessorCacheAssociation>.Success(
            [
                new("cache-a", "cpu-a", null),
                new("cache-b", "cpu-a", 4000),
                new("cache-c", "cpu-b", null),
            ])));

        var cache = Assert.Single(facts, x => x.Key == "wmi.cpu.cache.assoc");
        Assert.Equal(3d, cache.NumericValue);
        Assert.Contains("不同處理器參照 2、快取參照 3", cache.Value);
        Assert.Contains("BusSpeed 有值 1/3（4000 MHz ×1）", cache.Value);
        Assert.Contains("參照數不是快取層級／容量", cache.Value);
        Assert.Contains("不是 CPU 頻率", cache.Value);
    }

    [Fact]
    public void WMI關聯端點方向依官方Antecedent與Dependent欄位定義()
    {
        var row = WmiSupplementFactsService.MapProcessorCacheAssociation("cache-ref", "processor-ref", 4000);

        Assert.Equal("cache-ref", row.CacheReference);
        Assert.Equal("processor-ref", row.ProcessorReference);
    }

    [Fact]
    public void 成功零筆與查詢不可用分開()
    {
        var empty = WmiSupplementFactsService.Collect(At, () => Snapshot());
        Assert.Equal(5, empty.Count);
        Assert.All(empty.Where(x => x.Key != WmiSupplementFactsService.OfficialCoverageKey), x =>
        {
            Assert.Equal(FactAvailability.Present, x.Availability);
            Assert.Equal(0d, x.NumericValue);
            Assert.Contains("不等於", x.Value);
        });
        var coverage = Assert.Single(empty, x => x.Key == WmiSupplementFactsService.OfficialCoverageKey);
        Assert.Equal(FactAvailability.Present, coverage.Availability);
        Assert.Equal((double)WmiSupplementFactsService.DirectWmiQueryClasses.Count, coverage.NumericValue);
        Assert.Contains("官方頁列 86 類", coverage.Value);
        Assert.Contains("直接 WQL 查詢 22 類，未直接查詢 64 類", coverage.Value);

        var unavailable = WmiSupplementFactsService.Collect(At, () => Snapshot(
            buses: WmiClassQueryResult<WmiBusRow>.Failure(FactAvailability.NotSupported, "類別不存在"),
            ports: WmiClassQueryResult<WmiPortResourceRow>.Failure(FactAvailability.InsufficientPrivilege, "拒絕存取"),
            irqs: WmiClassQueryResult<WmiIrqResourceRow>.Failure(FactAvailability.ReadError, "提供者失敗"),
            associations: WmiClassQueryResult<WmiProcessorCacheAssociation>.Failure(FactAvailability.Unknown, "未確認")));

        Assert.Equal(FactAvailability.NotSupported, Assert.Single(unavailable, x => x.Key == "wmi.bus").Availability);
        Assert.Equal(FactAvailability.InsufficientPrivilege, Assert.Single(unavailable, x => x.Key == "wmi.portres").Availability);
        Assert.Equal(FactAvailability.ReadError, Assert.Single(unavailable, x => x.Key == "wmi.irqres").Availability);
        Assert.Equal(FactAvailability.Unknown, Assert.Single(unavailable, x => x.Key == "wmi.cpu.cache.assoc").Availability);
        Assert.All(unavailable.Where(x => x.Key != WmiSupplementFactsService.OfficialCoverageKey),
            x => Assert.Null(x.NumericValue));
    }

    [Fact]
    public void 探針丟例外時四鍵都明確標讀取失敗()
    {
        var facts = WmiSupplementFactsService.Collect(At,
            () => throw new InvalidOperationException("probe failure"));

        Assert.Equal(5, facts.Count);
        Assert.All(facts.Where(x => RuntimeKeys.Contains(x.Key)),
            x => Assert.Equal(FactAvailability.ReadError, x.Availability));
        Assert.Equal(FactAvailability.Present,
            Assert.Single(facts, x => x.Key == WmiSupplementFactsService.OfficialCoverageKey).Availability);
    }

    [Fact]
    public void 真實查詢受WmiGuard保護且不含寫入呼叫()
    {
        string root = RepoRoot();
        string source = File.ReadAllText(Path.Combine(root, "Services", "WmiSupplementFactsService.cs"));
        Assert.Contains("WmiGuard.Probe", source, StringComparison.Ordinal);
        Assert.Contains("SELECT BusType FROM Win32_Bus", source, StringComparison.Ordinal);
        Assert.Contains("SELECT Alias FROM Win32_PortResource", source, StringComparison.Ordinal);
        Assert.Contains("SELECT Hardware FROM Win32_IRQResource", source, StringComparison.Ordinal);
        Assert.Contains("SELECT Antecedent, Dependent, BusSpeed FROM Win32_AssociatedProcessorMemory", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".Put(", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvokeMethod(", source, StringComparison.OrdinalIgnoreCase);

        var official = new HashSet<string>(WmiSupplementFactsService.OfficialHardwareClasses,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(86, WmiSupplementFactsService.OfficialHardwareClasses.Count);
        Assert.Equal(86, official.Count);

        string services = string.Join(Environment.NewLine,
            Directory.EnumerateFiles(Path.Combine(root, "Services"), "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText)
                .Select(text => Regex.Replace(text, @"//[^\r\n]*|/\*[\s\S]*?\*/", "")));
        var direct = Regex.Matches(services, @"\bFROM\s+(?<class>Win32_[A-Za-z0-9_]+)\b",
                RegexOptions.IgnoreCase)
            .Select(m => m.Groups["class"].Value)
            .Where(official.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(WmiSupplementFactsService.DirectWmiQueryClasses.SetEquals(direct),
            "官方頁 86 類與直接 WQL 查詢對帳漂移。原始碼有而目錄沒有：" +
            string.Join("、", direct.Except(WmiSupplementFactsService.DirectWmiQueryClasses, StringComparer.OrdinalIgnoreCase)) +
            "；目錄有而原始碼沒有：" +
            string.Join("、", WmiSupplementFactsService.DirectWmiQueryClasses.Except(direct, StringComparer.OrdinalIgnoreCase)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("找不到倉庫根");
    }
}
