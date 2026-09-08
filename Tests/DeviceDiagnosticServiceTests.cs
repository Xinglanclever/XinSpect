using Xunit;

namespace XinSpect.Tests;

public sealed class DeviceDiagnosticServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 8);

    [Theory]
    [InlineData(0, "未回報此裝置有問題")]
    [InlineData(22, "已被停用")]
    [InlineData(28, "尚未安裝")]
    [InlineData(43, "回報了問題")]
    public void ProblemCode保留原碼並給客觀繁中說明(uint code, string phrase)
    {
        var device = DeviceDiagnosticRules.Classify(Raw("DEV", problem: code), Now);

        Assert.Equal(code, device.ProblemCode);
        Assert.Equal(code.ToString(), device.ProblemCodeText);
        Assert.Contains(phrase, device.ProblemExplanation);
    }

    [Fact]
    public void Code0不產生故障finding()
    {
        var device = DeviceDiagnosticRules.Classify(Raw("DEV", problem: 0), Now);
        Assert.Empty(device.Findings);
    }

    [Theory]
    [InlineData(22, DeviceDiagnosticFindingKind.Disabled)]
    [InlineData(28, DeviceDiagnosticFindingKind.MissingDriver)]
    [InlineData(43, DeviceDiagnosticFindingKind.WindowsProblemCode)]
    public void Windows故障碼產生可追溯finding(uint code, DeviceDiagnosticFindingKind kind)
    {
        var finding = Assert.Single(DeviceDiagnosticRules.Classify(Raw("DEV", problem: code), Now).Findings);
        Assert.Equal(kind, finding.Kind);
        Assert.Equal(DeviceDiagnosticSeverity.Error, finding.Severity);
        Assert.Contains(code == 43 ? "43" : code == 22 ? "停用" : "驅動", finding.Summary);
    }

    [Fact]
    public void Ghost只標目前不在場不因ghost本身當故障()
    {
        var device = DeviceDiagnosticRules.Classify(Raw("GHOST", present: false, problem: 0), Now);

        Assert.True(device.IsGhost);
        Assert.Equal("目前未連接/不在場", device.PresenceText);
        Assert.Empty(device.Findings);
    }

    [Fact]
    public void Ghost仍保留Windows明確回報的歷史problemCode()
    {
        var device = DeviceDiagnosticRules.Classify(Raw("GHOST", present: false, problem: 43), Now);
        Assert.True(device.IsGhost);
        Assert.Contains(device.Findings, f => f.Kind == DeviceDiagnosticFindingKind.WindowsProblemCode);
        Assert.DoesNotContain(device.Findings, f => f.Summary.Contains("不在場", StringComparison.Ordinal));
    }

    [Fact]
    public void 所有缺漏資料明確顯示Unknown且不猜測簽章或problemCode()
    {
        var device = DeviceDiagnosticRules.Classify(new DeviceDiagnosticRawDevice { IsPresent = true }, Now);

        Assert.Equal(DeviceDiagnosticUnknown.Value, device.InstanceId);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.FriendlyName);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.Description);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.DeviceClass);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.ClassGuid);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.Manufacturer);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.Status);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.ProblemCodeText);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.ProblemExplanation);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.ParentInstanceId);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.Service);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.DriverVersion);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.DriverDateText);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.DriverProvider);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.InfName);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.SignedText);
        Assert.Equal(DeviceDiagnosticUnknown.Value, device.Signer);
        Assert.Empty(device.Findings);
    }

    [Fact]
    public void 明確未簽章才產生finding而Unknown不會()
    {
        var unsigned = DeviceDiagnosticRules.Classify(Raw("UNSIGNED") with { IsSigned = false }, Now);
        var unknown = DeviceDiagnosticRules.Classify(Raw("UNKNOWN") with { IsSigned = null }, Now);

        Assert.Contains(unsigned.Findings, f => f.Kind == DeviceDiagnosticFindingKind.UnsignedDriver);
        Assert.DoesNotContain(unknown.Findings, f => f.Kind == DeviceDiagnosticFindingKind.UnsignedDriver);
    }

    [Fact]
    public void 關鍵類別過舊才標記且明說舊不等於故障()
    {
        var oldDisplay = DeviceDiagnosticRules.Classify(Raw("GPU") with
        {
            DeviceClass = "DISPLAY",
            DriverDate = Now.AddYears(-6),
            DriverProvider = "Vendor",
            IsSigned = true,
        }, Now);
        var oldPrinter = DeviceDiagnosticRules.Classify(Raw("PRINTER") with
        {
            DeviceClass = "PRINTER",
            DriverDate = Now.AddYears(-20),
            DriverProvider = "Vendor",
            IsSigned = true,
        }, Now);

        var finding = Assert.Single(oldDisplay.Findings);
        Assert.Equal(DeviceDiagnosticFindingKind.OldCriticalDriver, finding.Kind);
        Assert.Contains("舊不等於故障", finding.Evidence);
        Assert.Empty(oldPrinter.Findings);
    }

    [Fact]
    public void ParentInstanceId建立拓樸且掃描邊界可注入()
    {
        var source = new FakeDataSource(
        [
            Raw("ROOT") with { FriendlyName = "控制器" },
            Raw("CHILD", parent: "ROOT") with { FriendlyName = "子裝置" },
            Raw("GRANDCHILD", parent: "CHILD") with { FriendlyName = "孫裝置" },
        ]);
        var report = new DeviceDiagnosticService(source).Scan(Now);

        Assert.Equal(3, report.Devices.Count);
        var root = Assert.Single(report.Roots);
        Assert.Equal("ROOT", root.InstanceId);
        var child = Assert.Single(root.Children);
        Assert.Equal("CHILD", child.InstanceId);
        Assert.Equal("ROOT", child.ParentInstanceId);
        Assert.Equal("GRANDCHILD", Assert.Single(child.Children).InstanceId);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public void 找不到父裝置的項目保留Parent證據並成為root()
    {
        var report = DeviceDiagnosticService.BuildReport(
            [Raw("ORPHAN", parent: "MISSING")], Now);

        var orphan = Assert.Single(report.Roots);
        Assert.Equal("MISSING", orphan.ParentInstanceId);
    }

    private static DeviceDiagnosticRawDevice Raw(
        string id, bool present = true, uint? problem = 0, string? parent = null)
        => new()
        {
            InstanceId = id,
            FriendlyName = id,
            Description = "測試裝置",
            DeviceClass = "TEST",
            ClassGuid = "{00000000-0000-0000-0000-000000000000}",
            Manufacturer = "測試廠商",
            Status = problem is > 0 ? "Problem" : "Started",
            ProblemCode = problem,
            ParentInstanceId = parent,
            Service = "testsvc",
            IsPresent = present,
            DriverVersion = "1.0.0.0",
            DriverDate = Now.AddYears(-1),
            DriverProvider = "測試廠商",
            InfName = "oem1.inf",
            IsSigned = true,
            Signer = "測試簽署者",
        };

    private sealed class FakeDataSource(IReadOnlyList<DeviceDiagnosticRawDevice> devices)
        : IDeviceDiagnosticDataSource
    {
        public int Calls { get; private set; }
        public IReadOnlyList<DeviceDiagnosticRawDevice> EnumerateDevices()
        {
            Calls++;
            return devices;
        }
    }
}
