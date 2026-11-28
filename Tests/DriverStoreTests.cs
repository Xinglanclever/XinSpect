using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Injected exporters write temp fixtures only. These tests never export a real installed driver.</summary>
public sealed class DriverStoreTests
{
    private static DriverStorePackage Package(string name = "oem42.inf") => new()
    {
        PublishedName = name, OriginalFileName = @"C:\Windows\System32\DriverStore\FileRepository\example.inf_amd64_test\example.inf",
        Version = "1.2.3.4", Provider = "Test provider", DriverDate = new(2024, 2, 29),
    };
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XinSpect-DriverStore-tests-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }
    private sealed class FakeBackend : IDriverStoreBackend
    {
        public DriverStoreInventory Inventory = new() { Availability = FactAvailability.Present, Packages = [Package()] };
        public Func<CancellationToken, DriverStoreInventory>? Read;
        public Func<string, string, CancellationToken, Task<DriverStoreProcessResult>>? Export;
        public List<(string Inf, string Directory)> Calls { get; } = [];
        public DriverStoreInventory ReadInventory(CancellationToken token) { token.ThrowIfCancellationRequested(); return Read?.Invoke(token) ?? Inventory; }
        public async Task<DriverStoreProcessResult> ExportPackageAsync(string inf, string directory, CancellationToken token)
        {
            Calls.Add((inf, directory));
            if (Export != null) return await Export(inf, directory, token);
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "example.inf"), "[Version]\nSignature=\"$WINDOWS NT$\"", token);
            await File.WriteAllBytesAsync(System.IO.Path.Combine(directory, "example.sys"), [0, 1, 2, 3, 255], token);
            return new(0, "Localized output is not parsed.");
        }
    }

    [Theory]
    [InlineData("oem0.inf", true)] [InlineData("OEM0042.INF", true)]
    [InlineData("oem*.inf", false)] [InlineData("../oem1.inf", false)]
    [InlineData("oem1.inf /delete-driver", false)] [InlineData("oem1.inf\n", false)]
    [InlineData(@"C:\Windows\INF\oem1.inf", false)] [InlineData("oem١.inf", false)]
    [InlineData("*.inf", false)] [InlineData("oem.inf", false)]
    public void ExportIdentityAllowsOnlyOneLiteralOemInf(string name, bool expected) => Assert.Equal(expected, DriverStoreService.IsPublishedOemInf(name));

    [Fact]
    public void NativeStructureUsesSdkPointerAlignmentAndBoolWidth()
    {
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(104, Marshal.SizeOf<DriverStoreNativeBackend.DismDriverPackage>());
        Assert.Equal(16, Marshal.OffsetOf<DriverStoreNativeBackend.DismDriverPackage>(nameof(DriverStoreNativeBackend.DismDriverPackage.InBox)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<DriverStoreNativeBackend.DismDriverPackage>(nameof(DriverStoreNativeBackend.DismDriverPackage.ProviderName)).ToInt32());
        Assert.Equal(72, Marshal.OffsetOf<DriverStoreNativeBackend.DismDriverPackage>(nameof(DriverStoreNativeBackend.DismDriverPackage.Date)).ToInt32());
        Assert.Equal(88, Marshal.OffsetOf<DriverStoreNativeBackend.DismDriverPackage>(nameof(DriverStoreNativeBackend.DismDriverPackage.MajorVersion)).ToInt32());
    }

    [Fact]
    public void BindingAssociationIsByPublishedInfAndNeverByProviderOrVersion()
    {
        var result = DriverStoreNativeBackend.AttachBindings([Package(), Package("oem43.inf")],
            [new("OEM42.INF", @"PCI\DEV_A", "Device A"), new("oem43.inf", @"PCI\DEV_B", "Device B")], FactAvailability.Present, "Complete");
        Assert.Equal(@"PCI\DEV_A", Assert.Single(result.Packages[0].Bindings).DeviceId);
        Assert.Equal(@"PCI\DEV_B", Assert.Single(result.Packages[1].Bindings).DeviceId);
    }

    [Fact]
    public void FailedBindingsCannotTurnStorePackagesIntoUnusedDrivers()
    {
        var result = DriverStoreNativeBackend.AttachBindings([Package()], [new("oem42.inf", "A", "A")], FactAvailability.ReadError, "WMI timeout");
        var row = Assert.Single(result.Packages);
        Assert.Equal(FactAvailability.Present, row.Availability);
        Assert.Equal(FactAvailability.ReadError, row.BindingAvailability);
        Assert.Empty(row.Bindings);
        Assert.Contains("綁定未知", row.BindingText);
        Assert.DoesNotContain("未回報綁定", row.BindingText);
    }

    [Fact]
    public async Task ExportWritesActualHashesAndCompleteManifestIntoNewDirectory()
    {
        using var temp = new TempFolder();
        string sentinel = System.IO.Path.Combine(temp.Path, "manifest.json");
        await File.WriteAllTextAsync(sentinel, "existing file");
        var backend = new FakeBackend(); var service = new DriverStoreService(backend);
        var result = await service.ExportAsync([Package()], temp.Path);
        Assert.Equal("Completed", result.Status); Assert.Equal(FactAvailability.Present, result.Availability);
        Assert.NotNull(result.ManifestPath);
        Assert.Equal("existing file", await File.ReadAllTextAsync(sentinel));
        Assert.Single(backend.Calls);
        Assert.Equal("oem42.inf", backend.Calls[0].Inf);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestPath));
        Assert.Equal("ExportOnly", json.RootElement.GetProperty("Operation").GetString());
        Assert.Equal("Completed", json.RootElement.GetProperty("Status").GetString());
        var entry = Assert.Single(result.Manifest!.Packages); Assert.Equal("Exported", entry.Status);
        Assert.Equal(2, entry.Files.Count);
        var sys = Assert.Single(entry.Files, f => f.RelativePath == "example.sys");
        Assert.Equal(5, sys.Length); Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 0, 1, 2, 3, 255 })), sys.Sha256);
        var second = await service.ExportAsync([Package()], temp.Path);
        Assert.NotEqual(result.Directory, second.Directory);
        Assert.True(File.Exists(result.ManifestPath));
    }

    [Fact]
    public async Task IdentityChangeAfterSelectionStopsBeforeAnyExportOrDirectory()
    {
        using var temp = new TempFolder();
        var backend = new FakeBackend { Inventory = new() { Availability = FactAvailability.Present, Packages = [Package() with { Version = "9.0.0.0" }] } };
        var result = await new DriverStoreService(backend).ExportAsync([Package()], temp.Path);
        Assert.Equal("Failed", result.Status); Assert.Contains("已變更", result.Detail);
        Assert.Empty(backend.Calls); Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task DestinationIsPinnedAgainstRenameWhileExporterRuns()
    {
        using var temp = new TempFolder();
        var backend = new FakeBackend { Export = async (_, directory, token) =>
        {
            string run = System.IO.Path.GetDirectoryName(directory)!;
            Assert.ThrowsAny<IOException>(() => Directory.Move(run, run + "-moved"));
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "test.inf"), "test", token);
            return new(0, "");
        } };
        var result = await new DriverStoreService(backend).ExportAsync([Package()], temp.Path);
        Assert.Equal("Completed", result.Status);
    }

    [Fact]
    public void ExistingDirectoryIsNeverReusedEvenWhenEmpty()
    {
        using var temp = new TempFolder();
        Assert.Throws<System.ComponentModel.Win32Exception>(() => DriverStoreService.CreateNewDirectory(temp.Path));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public void Win32NormalizedSystemPathCannotBypassDestinationGuard()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Throws<ArgumentException>(() => DriverStoreService.ValidateDestination(system));
        Assert.Throws<ArgumentException>(() => DriverStoreService.ValidateDestination(system + "."));
    }

    [Fact]
    public async Task InboxPackagesAndDuplicateSelectionsNeverReachExporter()
    {
        using var temp = new TempFolder(); var backend = new FakeBackend(); var service = new DriverStoreService(backend);
        Assert.Equal("Failed", (await service.ExportAsync([Package() with { InBox = true }], temp.Path)).Status);
        Assert.Equal("Failed", (await service.ExportAsync([Package(), Package()], temp.Path)).Status);
        Assert.Empty(backend.Calls); Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData(@"relative\backup")] [InlineData(@"\\server\share")] [InlineData(@"\\?\C:\backup")]
    [InlineData(@"C:\backup:stream")]
    public async Task UnsafeDestinationNeverReachesExporter(string path)
    {
        var backend = new FakeBackend(); var result = await new DriverStoreService(backend).ExportAsync([Package()], path);
        Assert.Equal("Failed", result.Status); Assert.Null(result.Directory); Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task OsNonzeroExitWithFilesIsFailureAndKeepsManifestAndPartialEvidence()
    {
        using var temp = new TempFolder();
        var backend = new FakeBackend { Export = async (_, directory, token) =>
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "partial.inf"), "partial", token);
            return new(5, "arbitrary localized diagnostic");
        } };
        var result = await new DriverStoreService(backend).ExportAsync([Package()], temp.Path);
        Assert.Equal("Failed", result.Status); Assert.True(File.Exists(result.ManifestPath));
        Assert.Equal(FactAvailability.InsufficientPrivilege, result.Availability);
        var entry = Assert.Single(result.Manifest!.Packages); Assert.Equal("Failed", entry.Status); Assert.Equal(5, entry.ExitCode);
        Assert.Empty(entry.Files); Assert.Single(Directory.GetFiles(backend.Calls[0].Directory));
    }

    [Fact]
    public async Task ExitZeroWithoutInfIsNotACompleteBackup()
    {
        using var temp = new TempFolder(); var backend = new FakeBackend { Export = (_, _, _) => Task.FromResult(new DriverStoreProcessResult(0, "success-looking text")) };
        var result = await new DriverStoreService(backend).ExportAsync([Package()], temp.Path);
        Assert.Equal("Failed", result.Status); Assert.Contains("沒有 INF", result.Detail);
    }

    [Fact]
    public async Task CancellationDuringExportStopsLaterPackagesAndPersistsCancelledManifest()
    {
        using var temp = new TempFolder(); using var cts = new CancellationTokenSource();
        var backend = new FakeBackend { Inventory = new() { Availability = FactAvailability.Present, Packages = [Package(), Package("oem43.inf")] }, Export = async (_, directory, token) =>
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "partial.inf"), "partial", token);
            cts.Cancel(); token.ThrowIfCancellationRequested(); return new(0, "unreachable");
        } };
        var result = await new DriverStoreService(backend).ExportAsync([Package(), Package("oem43.inf")], temp.Path, token: cts.Token);
        Assert.Equal("Cancelled", result.Status); Assert.Equal(FactAvailability.Unknown, result.Availability); Assert.Single(backend.Calls);
        Assert.True(File.Exists(result.ManifestPath)); Assert.Equal("Cancelled", result.Manifest!.Packages[0].Status);
        Assert.Equal("NotAttempted", result.Manifest.Packages[1].Status);
    }

    [Fact]
    public async Task ReparseOutputIsRejectedBeforeDirectoryTraversalOrHashing()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "link-shaped-entry"));
        // Inject the filesystem metadata rather than requiring privileged symlink creation on the test host.
        var ex = await Assert.ThrowsAsync<IOException>(() => DriverStoreService.HashOutputAsync(temp.Path, CancellationToken.None,
            _ => FileAttributes.Directory | FileAttributes.ReparsePoint));
        Assert.Contains("reparse point", ex.Message);
    }

    [Fact]
    public async Task InventoryErrorsAndCancellationAreNotEmptySuccesses()
    {
        var service = new DriverStoreService(new FakeBackend { Read = _ => throw new UnauthorizedAccessException("denied") });
        var denied = await service.ReadInventoryAsync(); Assert.Equal(FactAvailability.InsufficientPrivilege, denied.Availability);
        var cancelled = await new DriverStoreService(new FakeBackend { Read = _ => throw new OperationCanceledException() }).ReadInventoryAsync();
        Assert.True(cancelled.Cancelled); Assert.NotEqual(FactAvailability.Present, cancelled.Availability);
        var unsupported = await new DriverStoreService(new FakeBackend { Read = _ => throw new DllNotFoundException() }).ReadInventoryAsync();
        Assert.Equal(FactAvailability.NotSupported, unsupported.Availability);
    }

    [Fact]
    public async Task ConcurrentActionCannotOverlapAnActiveExport()
    {
        using var temp = new TempFolder();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend { Export = async (_, directory, token) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(token);
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "test.inf"), "test", token); return new(0, "");
        } };
        var service = new DriverStoreService(backend); var export = service.ExportAsync([Package()], temp.Path);
        await entered.Task;
        try { Assert.Equal(FactAvailability.ReadError, (await service.ReadInventoryAsync()).Availability); Assert.Single(backend.Calls); }
        finally { release.SetResult(); }
        Assert.Equal("Completed", (await export).Status);
    }
}
