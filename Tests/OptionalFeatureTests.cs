using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP16 第一組：虛擬化相關 Windows 選用功能的安裝狀態（WMI Win32_OptionalFeature，usermode）。
/// InstallState：1 啟用、2 停用、3 不存在——三態照抄系統口徑，不猜。
/// </summary>
public class OptionalFeatureTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 選用功能_四個目標狀態逐項釘值()
    {
        var entries = new List<XinSpect.OptionalFeatureEntry>
        {
            new("Microsoft-Hyper-V-Hypervisor", 2),           // 停用（有元件但沒啟用）
            new("VirtualMachinePlatform", 1),                 // 啟用
            new("Microsoft-Windows-Subsystem-Linux", 1),      // 啟用
            new("Containers", 3),                             // 系統上不存在
        };

        var facts = XinSpect.OptionalFeatureService.Collect(At, probe: () => entries);

        Assert.All(facts, f => Assert.Equal("系統與軟體", f.Category));
        Assert.Equal(4u, Assert.Single(facts, f => f.Key == "feat.count").NumericValue);

        var hyper = Assert.Single(facts, f => f.Key == "feat.hyperv");
        Assert.Contains("停用", hyper.Value);
        var wsl = Assert.Single(facts, f => f.Key == "feat.wsl");
        Assert.Contains("啟用", wsl.Value);
        var containers = Assert.Single(facts, f => f.Key == "feat.containers");
        Assert.Contains("不存在", containers.Value);
    }

    [Fact]
    public void 選用功能_未收錄的目標如實標_讀不到三態()
    {
        // 查詢結果裡沒有 Hyper-V 這個名——如實標「未回報」，不當成停用
        var partial = XinSpect.OptionalFeatureService.Collect(At,
            probe: () => new List<XinSpect.OptionalFeatureEntry> { new("Microsoft-Windows-Subsystem-Linux", 1) });
        var hyper = Assert.Single(partial, f => f.Key == "feat.hyperv");
        Assert.Contains("未回報", hyper.Value);

        var fail = XinSpect.OptionalFeatureService.Collect(At, probe: () => null);
        Assert.All(fail, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }
}
