using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 逐裝置事實（U10 第三圈 OS-0002～OS-0004，第 309 輪）：把裝置診斷的結果投影成事實。
///
/// <para>
/// 這一組釘住的都是「安靜地講錯話」的情形：把 0 當成「沒問題」、把沒有 finding 當成健康、
/// 把截斷當成全部、把 instance ID（常含序號）寫進報告。投影器本身不做 I/O，所以每一條都可以
/// 用假的裝置資料重跑，不必碰真機。
/// </para>
/// </summary>
public sealed class DeviceTruthFactsTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-29T00:00:00+00:00");

    private static DeviceDiagnosticDevice Device(
        string instanceId,
        uint? problemCode = 0,
        bool present = true,
        string className = "USB",
        string name = "USB 輸入裝置",
        string? provider = "Microsoft",
        string? version = "10.0.1.1",
        DateTime? driverDate = null,
        bool? signed = true,
        IReadOnlyList<DeviceDiagnosticFinding>? findings = null)
        => new()
        {
            InstanceId = instanceId,
            FriendlyName = name,
            Description = name,
            DeviceClass = className,
            Manufacturer = "模擬廠商",
            Status = present ? "OK" : "Unknown",
            ProblemCode = problemCode,
            ProblemCodeText = problemCode?.ToString() ?? DeviceDiagnosticUnknown.Value,
            ProblemExplanation = problemCode switch
            {
                0 => "Windows 未回報此裝置有問題。",
                28 => "尚未安裝此裝置的驅動程式。",
                45 => "此硬體裝置目前未連接到電腦。",
                _ => DeviceDiagnosticUnknown.Value,
            },
            Service = "usbhub",
            IsPresent = present,
            DriverVersion = version ?? DeviceDiagnosticUnknown.Value,
            DriverDate = driverDate,
            DriverProvider = provider ?? DeviceDiagnosticUnknown.Value,
            InfName = "usb.inf",
            IsSigned = signed,
            Findings = findings ?? [],
        };

    private static DeviceDiagnosticReport Report(params DeviceDiagnosticDevice[] devices)
        => new(devices, devices.Where(d => d.IsPresent).ToArray());

    [Fact]
    public void 健康裝置不進問題明細_計數仍要誠實()
    {
        var facts = DeviceTruthFactsService.Collect(Report(
            Device(@"USB\VID_1\healthy"),
            Device(@"USB\VID_2\healthy2")), At);

        Assert.DoesNotContain(facts, f => f.Key.StartsWith(DeviceTruthFactsService.IssuePrefix, StringComparison.Ordinal));

        var summary = Assert.Single(facts, f => f.Key == DeviceTruthFactsService.SummaryKey);
        Assert.Equal(FactAvailability.Present, summary.Availability);
        Assert.Contains("有問題碼 0 台", summary.Value);
        Assert.Equal(2, summary.NumericValue);
    }

    [Fact]
    public void 問題碼會帶語意而且鍵從0001開始()
    {
        var facts = DeviceTruthFactsService.Collect(Report(
            Device(@"USB\VID_3\missing", problemCode: 28)), At);

        var issue = Assert.Single(facts, f => f.Key == DeviceTruthFactsService.IssuePrefix + "0001");
        Assert.Contains("問題碼 28", issue.Value);
        Assert.Contains("尚未安裝此裝置的驅動程式", issue.Value);
        Assert.DoesNotContain("幽靈", issue.Value);
        Assert.Equal(28, issue.NumericValue);
        Assert.Equal(FactAvailability.Present, issue.Availability);
    }

    [Fact]
    public void 不在場的裝置要說幽靈而不是說有問題()
    {
        var facts = DeviceTruthFactsService.Collect(Report(
            Device(@"USB\VID_4\ghost", problemCode: 45, present: false)), At);

        var issue = Assert.Single(facts, f => f.Key.StartsWith(DeviceTruthFactsService.IssuePrefix, StringComparison.Ordinal));
        Assert.StartsWith("目前不在場（幽靈裝置）", issue.Value);
        Assert.Contains("未連接到電腦", issue.Value);
    }

    [Fact]
    public void 沒有回報問題碼要寫讀不到而不是0()
    {
        // 0 與 null 是兩件事：0 是「Windows 說沒問題」，null 是「Windows 沒說」。
        var facts = DeviceTruthFactsService.Collect(Report(
            Device(@"USB\VID_5\unknown", problemCode: null)), At);

        var issue = Assert.Single(facts, f => f.Key.StartsWith(DeviceTruthFactsService.IssuePrefix, StringComparison.Ordinal));
        Assert.Equal(FactAvailability.ReadError, issue.Availability);
        Assert.Null(issue.NumericValue);
        Assert.Contains("沒有回報", issue.UnavailableReason);
    }

    [Fact]
    public void 有finding的裝置才會進狀態家族()
    {
        var facts = DeviceTruthFactsService.Collect(Report(
            Device(@"USB\VID_6\healthy"),
            Device(@"PCI\VEN_8086\unsigned", findings:
            [
                new DeviceDiagnosticFinding(DeviceDiagnosticFindingKind.UnsignedDriver,
                    DeviceDiagnosticSeverity.Warning, "驅動未簽章", "簽章欄位為 false"),
            ])), At);

        var status = Assert.Single(facts, f => f.Key.StartsWith(DeviceTruthFactsService.StatusPrefix, StringComparison.Ordinal));
        Assert.Contains("驅動未簽章", status.Value);
        Assert.Contains("目前在場", status.Value);
        Assert.Equal(1, status.NumericValue);
    }

    [Fact]
    public void 有finding卻沒有摘要時要說說不出來()
    {
        // 「判為有問題但沒有任何說明」不能被寫成空字串——那看起來像「沒事」。
        var facts = DeviceTruthFactsService.Collect(Report(
            Device(@"PCI\VEN_8086\silent", findings:
            [
                new DeviceDiagnosticFinding(DeviceDiagnosticFindingKind.Disabled,
                    DeviceDiagnosticSeverity.Error, "", ""),
            ])), At);

        var status = Assert.Single(facts, f => f.Key.StartsWith(DeviceTruthFactsService.StatusPrefix, StringComparison.Ordinal));
        Assert.Equal(FactAvailability.ReadError, status.Availability);
        Assert.Contains("沒有任何一句可讀的說明", status.UnavailableReason);
    }

    [Fact]
    public void 鍵序按instance_ID排序_與輸入順序無關()
    {
        var a = Device(@"USB\VID_aaa\one", problemCode: 28);
        var b = Device(@"USB\VID_zzz\two", problemCode: 10);

        var first = DeviceTruthFactsService.Collect(Report(a, b), At)
            .Where(f => f.Key.StartsWith(DeviceTruthFactsService.IssuePrefix, StringComparison.Ordinal))
            .Select(f => (f.Key, f.Name)).ToList();
        var second = DeviceTruthFactsService.Collect(Report(b, a), At)
            .Where(f => f.Key.StartsWith(DeviceTruthFactsService.IssuePrefix, StringComparison.Ordinal))
            .Select(f => (f.Key, f.Name)).ToList();

        Assert.Equal(first, second);
        Assert.Contains("USB 輸入裝置", first[0].Name);
    }

    [Fact]
    public void 值裡不得出現instance_ID()
    {
        // instance ID 常含序號（USB\VID_..\5&2A3F...）；報告會離開本機，這個不能漏。
        const string instance = @"USB\VID_1234&PID_5678\SERIAL-ABCDEF";
        var facts = DeviceTruthFactsService.Collect(Report(
            Device(instance, problemCode: 28, findings:
            [
                new DeviceDiagnosticFinding(DeviceDiagnosticFindingKind.MissingDriver,
                    DeviceDiagnosticSeverity.Error, "缺少驅動", "無"),
            ])), At);

        Assert.NotEmpty(facts);
        foreach (var fact in facts)
        {
            Assert.DoesNotContain("SERIAL-ABCDEF", fact.Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SERIAL-ABCDEF", fact.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void 超過上限要說出還有幾台沒列()
    {
        var devices = Enumerable.Range(1, 5)
            .Select(i => Device(@"USB\VID_" + i + @"\dev", problemCode: 28))
            .ToArray();

        var facts = DeviceTruthFactsService.Collect(Report(devices), At, maxPerFamily: 2);

        Assert.Equal(2, facts.Count(f => f.Key.StartsWith(DeviceTruthFactsService.IssuePrefix, StringComparison.Ordinal)));
        var summary = Assert.Single(facts, f => f.Key == DeviceTruthFactsService.SummaryKey);
        Assert.Contains("另有 3 台未列出", summary.Value);
    }

    [Fact]
    public void 驅動證據要合併提供者版本日期與簽章()
    {
        var facts = DeviceTruthFactsService.Collect(Report(
            Device(@"PCI\VEN_10DE\gpu", className: "Display", provider: "NVIDIA", version: "31.0.15.5222",
                driverDate: new DateTime(2026, 3, 1), signed: false)), At);

        var driver = Assert.Single(facts, f => f.Key.StartsWith(DeviceTruthFactsService.DriverPrefix, StringComparison.Ordinal));
        Assert.Contains("提供者 NVIDIA", driver.Value);
        Assert.Contains("版本 31.0.15.5222", driver.Value);
        Assert.Contains("2026-03-01", driver.Value);
        Assert.Contains("未簽章", driver.Value);
    }

    [Fact]
    public void 沒有驅動證據的裝置不進驅動家族_但彙總要說出有幾台有()
    {
        // 系統內建節點（ACPI\PNP0C02 這類）本來就沒有獨立驅動套件；替每一台寫一把
        // 「讀不到驅動」的鍵只會把真正的問題淹掉，所以它們不進家族——但台數不能消失，
        // 由彙總的「有驅動證據 N 台」回答。
        var facts = DeviceTruthFactsService.Collect(Report(
            Device(@"ACPI\PNP0C02\io", provider: null, version: null, signed: null),
            Device(@"PCI\VEN_10DE\gpu", provider: "NVIDIA", version: "31.0.15.5222")), At);

        Assert.Equal(1, facts.Count(f => f.Key.StartsWith(DeviceTruthFactsService.DriverPrefix, StringComparison.Ordinal)));

        var summary = Assert.Single(facts, f => f.Key == DeviceTruthFactsService.SummaryKey);
        Assert.Contains("有驅動證據 1 台", summary.Value);
        Assert.Contains("未回報問題碼 0 台", summary.Value);
    }

    [Fact]
    public void 三個家族都在動態鍵目錄裡而且真的會生產()
    {
        Assert.True(FactKeyDynamicCatalog.Matches(DeviceTruthFactsService.IssuePrefix + "0001"));
        Assert.True(FactKeyDynamicCatalog.Matches(DeviceTruthFactsService.StatusPrefix + "0001"));
        Assert.True(FactKeyDynamicCatalog.Matches(DeviceTruthFactsService.DriverPrefix + "0001"));
        Assert.Contains(DeviceTruthFactsService.SummaryKey, FactKeyCatalog.Keys);
    }
}
