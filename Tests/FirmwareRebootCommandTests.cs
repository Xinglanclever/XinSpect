using System.Diagnostics;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

// All executable checks and launch outcomes are injected. No process, UAC, WMI or reboot.
public sealed class FirmwareRebootCommandTests
{
    private const string SystemDirectory = @"Q:\Windows\System32";

    [Theory]
    [InlineData(@"Q:\Windows\System32")]
    [InlineData(@"Q:\Windows\SysWOW64")]
    public void ExactOperatingSystemDirectoryAndFixedArgumentsReachThePublicExecutor(string directory)
    {
        var checks = new List<string>();
        var launched = new List<ProcessStartInfo>();
        var service = new BiosMeService(() => directory, path => { checks.Add(path); return true; },
            command => { launched.Add(command); return true; }) { RiskAccepted = true };
        Assert.True(service.RebootToFirmwareSetup());
        Assert.Equal(new[] { Path.Combine(directory, "shutdown.exe") }, checks);
        var command = Assert.Single(launched);
        Assert.Equal(checks[0], command.FileName);
        Assert.True(Path.IsPathFullyQualified(command.FileName));
        Assert.Equal("/r /fw /t 0", command.Arguments);
        Assert.True(command.UseShellExecute);
        Assert.Equal("runas", command.Verb);
        Assert.True(command.CreateNoWindow);
        Assert.False(command.RedirectStandardOutput);
        Assert.Contains("已送出", service.ActionStatus);
    }

    [Fact]
    public void MissingSystemToolNeverSearchesCwdOrPath()
    {
        var checks = new List<string>();
        int launched = 0;
        var service = new BiosMeService(() => SystemDirectory, path =>
        {
            checks.Add(path);
            return !path.StartsWith(SystemDirectory + "\\", StringComparison.OrdinalIgnoreCase);
        }, _ => { launched++; return true; }) { RiskAccepted = true };
        Assert.False(service.RebootToFirmwareSetup());
        Assert.Equal(new[] { Path.Combine(SystemDirectory, "shutdown.exe") }, checks);
        Assert.Equal(0, launched);
        Assert.Contains("不存在", service.ActionStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("System32")]
    [InlineData(@"Q:Windows\System32")]
    [InlineData(@"\Windows\System32")]
    public void InvalidSystemDirectoryRefusesBeforeFileLookupOrLaunch(string directory)
    {
        int checks = 0, launched = 0;
        var service = new BiosMeService(() => directory, _ => { checks++; return true; },
            _ => { launched++; return true; }) { RiskAccepted = true };
        Assert.False(service.RebootToFirmwareSetup());
        Assert.Equal(0, checks);
        Assert.Equal(0, launched);
        Assert.Contains("完整路徑", service.ActionStatus);
    }

    [Fact]
    public void MissingRiskConfirmationTouchesNoExecutableSource()
    {
        var service = new BiosMeService(() => throw new Exception("forbidden directory"),
            _ => throw new Exception("forbidden lookup"), _ => throw new Exception("forbidden launch"));
        Assert.False(service.RebootToFirmwareSetup());
        Assert.Contains("勾選", service.ActionStatus);
    }

    [Fact]
    public void RevokedConfirmationDuringValidationPreventsDispatch()
    {
        int launched = 0;
        BiosMeService? service = null;
        service = new BiosMeService(() => SystemDirectory, _ => { service!.RiskAccepted = false; return true; },
            _ => { launched++; return true; }) { RiskAccepted = true };
        Assert.False(service.RebootToFirmwareSetup());
        Assert.Equal(0, launched);
        Assert.Contains("撤銷", service.ActionStatus);
    }

    [Fact]
    public void NoLaunchAcknowledgementCannotReportSuccess()
    {
        var service = new BiosMeService(() => SystemDirectory, _ => true, _ => false) { RiskAccepted = true };
        Assert.False(service.RebootToFirmwareSetup());
        Assert.Contains("無法確認", service.ActionStatus);
    }

    [Fact]
    public void LaunchExceptionIsReportedWithoutFallbackOrRetry()
    {
        int launched = 0;
        var service = new BiosMeService(() => SystemDirectory, _ => true,
            _ => { launched++; throw new InvalidOperationException("injected launch refusal"); }) { RiskAccepted = true };
        Assert.False(service.RebootToFirmwareSetup());
        Assert.Equal(1, launched);
        Assert.Contains("injected launch refusal", service.ActionStatus);
    }
}
