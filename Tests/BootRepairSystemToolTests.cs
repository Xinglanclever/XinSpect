using System.Diagnostics;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>All command execution is injected; these tests never launch a process or run repairs.</summary>
public sealed class BootRepairSystemToolTests
{
    private const string SystemDirectory = @"Q:\Windows\System32";

    [Theory]
    [InlineData("sfc", "sfc.exe")]
    [InlineData("DISM", "dism.exe")]
    [InlineData("chkdsk", "chkdsk.exe")]
    [InlineData("bootrec", "bootrec.exe")]
    [InlineData("bcdedit", "bcdedit.exe")]
    public void EachAllowedToolUsesAnExistingAbsoluteSystemExecutable(string tool, string executable)
    {
        string expected = Path.Combine(SystemDirectory, executable);
        var checkedPaths = new List<string>();
        var command = BootRepairService.CreateSystemCommand(tool, "/fixed", SystemDirectory, path =>
        {
            checkedPaths.Add(path);
            return path == expected;
        });
        Assert.Equal(new[] { expected }, checkedPaths);
        Assert.Equal(expected, command.FileName);
        Assert.Equal("/fixed", command.Arguments);
        Assert.False(command.UseShellExecute);
        Assert.True(command.CreateNoWindow);
        Assert.True(command.RedirectStandardOutput);
        Assert.True(command.RedirectStandardError);
    }

    [Theory]
    [InlineData(@"Q:\Windows\System32")]
    [InlineData(@"Q:\Windows\SysWOW64")]
    public void UsesTheSystemDirectorySuppliedByTheOperatingSystem(string directory)
    {
        var command = BootRepairService.CreateSystemCommand("sfc", "/scannow", directory, _ => true);
        Assert.Equal(Path.Combine(directory, "sfc.exe"), command.FileName);
    }

    [Theory]
    [InlineData("sfc")]
    [InlineData("bootrec")]
    [InlineData("bcdedit")]
    public void MissingSystemToolCannotFallBackToCwdOrPath(string tool)
    {
        var checkedPaths = new List<string>();
        Assert.Throws<FileNotFoundException>(() => BootRepairService.CreateSystemCommand(tool, "", SystemDirectory, path =>
        {
            checkedPaths.Add(path);
            // Model a same-name executable present everywhere except the Windows system directory.
            return !path.StartsWith(SystemDirectory + "\\", StringComparison.OrdinalIgnoreCase);
        }));
        Assert.Equal(new[] { Path.Combine(SystemDirectory, tool + ".exe") }, checkedPaths);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData(@"Q:\attacker\sfc.exe")]
    [InlineData(@"..\sfc")]
    public void ArbitraryCommandsAndPathsAreRejected(string tool)
        => Assert.Throws<ArgumentException>(() =>
            BootRepairService.CreateSystemCommand(tool, "", SystemDirectory, _ => true));

    [Fact]
    public void RelativeSystemDirectoryIsRejected()
        => Assert.Throws<InvalidOperationException>(() =>
            BootRepairService.CreateSystemCommand("sfc", "", "System32", _ => true));

    [Fact]
    public async Task PublicRepairEntrypointsDispatchOnlyAbsoluteSystemTools()
    {
        var commands = new List<ProcessStartInfo>();
        var service = new BootRepairService(SystemDirectory, _ => true, command =>
        {
            commands.Add(command);
            return Task.FromResult(0);
        });
        await service.RunSfcAsync();
        await service.RunDismAsync();
        await service.RunChkdskAsync();
        await service.RepairBcdAsync();
        await service.RunAllAsync();
        string[] executableNames = ["sfc.exe", "dism.exe", "chkdsk.exe", "bcdedit.exe", "sfc.exe", "dism.exe", "chkdsk.exe"];
        Assert.Equal(executableNames.Select(name => Path.Combine(SystemDirectory, name)), commands.Select(command => command.FileName));
        Assert.Equal(new[] { "/scannow", "/Online /Cleanup-Image /RestoreHealth", "/enum" },
            new[] { commands[0].Arguments, commands[1].Arguments, commands[3].Arguments });
        Assert.All(commands, command => Assert.False(command.UseShellExecute));
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task MissingExecutableFailsBeforePublicEntrypointCanInvokeExecutor()
    {
        int starts = 0;
        var service = new BootRepairService(SystemDirectory, _ => false, _ =>
        {
            starts++;
            return Task.FromResult(0);
        });
        await service.RunSfcAsync();
        Assert.Equal(0, starts);
        Assert.Contains(service.Log, line => line.Contains("[失敗]", StringComparison.Ordinal));
        Assert.False(service.IsRunning);
    }
}
