using System.IO;
using System.Windows;
using Xunit;

namespace XinSpect.Tests;

[Collection(WpfCollection.Name)]
public sealed class EvidenceLabIntegrationTests
{
    [Fact]
    public void 時間膠囊可由真實ViewModel建立保存載入與自我比較()
    {
        var vm = new MainViewModel
        {
            System = new SystemSummary
            {
                SystemManufacturer = "ASUS", SystemModel = "X299",
                BoardVendor = "ASUS", BoardModel = "RAMPAGE", BoardSerial = "SERIAL-SECRET",
                SystemUuid = "UUID-SECRET", BiosVendor = "AMI", BiosVersion = "4201", BiosDate = "2024-11-06",
            },
            Cpu = new CpuStatic { Name = "Intel CPU", Cores = 18, Threads = 36, ProcessorId = "CPU-SECRET" },
            PhysicalDisks = [new PhysicalDiskInfo { Index = 0, Model = "NVMe", SizeBytes = 1_000_000_000, SerialNumber = "DISK-SECRET", Firmware = "1.0" }],
        };
        var snapshot = vm.EvidenceLab.Capture(vm, includeSensitive: false);
        string path = Path.Combine(Path.GetTempPath(), "XinSpect_snapshot_" + Guid.NewGuid().ToString("N") + ".xinsnapshot");
        try
        {
            HardwareSnapshotService.Save(path, snapshot);
            var loaded = HardwareSnapshotService.Load(path);
            var diff = HardwareSnapshotService.Diff(loaded, snapshot);
            Assert.Equal(snapshot.Facts.Count, diff.Unchanged);
            Assert.All(loaded.Facts.Where(x => x.Sensitive), x => Assert.True(HardwareSnapshotService.IsRedacted(x.Value)));
            Assert.DoesNotContain("SERIAL-SECRET", File.ReadAllText(path));
            Assert.DoesNotContain("UUID-SECRET", File.ReadAllText(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task 敏感值選項與比較政策保持一致()
    {
        var vm = SampleVm();
        string redacted = TempSnapshot();
        string preserved = TempSnapshot();
        try
        {
            await vm.EvidenceLab.SaveAsync(vm, redacted, includeSensitive: false);
            Assert.False(HardwareSnapshotService.Load(redacted).SensitiveValuesPreserved);
            Assert.DoesNotContain("SERIAL-SECRET", File.ReadAllText(redacted));
            await vm.EvidenceLab.CompareAsync(vm, redacted);
            Assert.Empty(vm.EvidenceLab.Changes);

            await vm.EvidenceLab.SaveAsync(vm, preserved, includeSensitive: true);
            var loaded = HardwareSnapshotService.Load(preserved);
            Assert.True(loaded.SensitiveValuesPreserved);
            Assert.Contains("SERIAL-SECRET", File.ReadAllText(preserved));
        }
        finally { TryDelete(redacted); TryDelete(preserved); }
    }

    [Fact]
    public void 同型號多顯卡不會產生重複快照鍵()
    {
        var vm = SampleVm();
        vm.GpuDetails =
        [
            new GpuDetail { Name = "Same GPU", VendorId = "10DE", ModelId = "1234", RevisionId = "A1" },
            new GpuDetail { Name = "Same GPU", VendorId = "10DE", ModelId = "1234", RevisionId = "A1" },
        ];
        var snapshot = vm.EvidenceLab.Capture(vm, includeSensitive: false);
        Assert.Equal(4, snapshot.Facts.Count(x => x.Category == "顯示卡"));
        Assert.Equal(4, snapshot.Facts.Where(x => x.Category == "顯示卡").Select(x => x.Key).Distinct().Count());
    }

    private static MainViewModel SampleVm() => new()
    {
        System = new SystemSummary
        {
            SystemManufacturer = "ASUS", SystemModel = "X299",
            BoardVendor = "ASUS", BoardModel = "RAMPAGE", BoardSerial = "SERIAL-SECRET",
            SystemUuid = "UUID-SECRET", BiosVendor = "AMI", BiosVersion = "4201", BiosDate = "2024-11-06",
        },
        Cpu = new CpuStatic { Name = "Intel CPU", Cores = 18, Threads = 36, ProcessorId = "CPU-SECRET" },
        PhysicalDisks = [new PhysicalDiskInfo { Index = 0, Model = "NVMe", SizeBytes = 1_000_000_000, SerialNumber = "DISK-SECRET", Firmware = "1.0" }],
    };

    private static string TempSnapshot() => Path.Combine(Path.GetTempPath(), "XinSpect_snapshot_" + Guid.NewGuid().ToString("N") + ".xinsnapshot");
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    [Fact]
    public async Task 損壞快照由頁面內回報而不拋成全域錯誤()
    {
        var vm = SampleVm();
        string path = TempSnapshot();
        try
        {
            File.WriteAllText(path, "not a snapshot");
            await vm.EvidenceLab.CompareAsync(vm, path);
            Assert.Equal("比較失敗", vm.EvidenceLab.Summary);
            Assert.False(vm.EvidenceLab.IsBusy);

            await vm.EvidenceLab.InspectAsync(path);
            Assert.Equal("載入失敗", vm.EvidenceLab.Summary);
            Assert.Empty(vm.EvidenceLab.Facts);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void 遮蔽報告不洩漏裝置實例識別()
    {
        var vm = SampleVm();
        vm.Settings.ReportMaskIdentity = true;
        vm.HardwareEvidence.Rows.Add(new EvidenceAuditRow(
            "缺少裝置驅動程式", "USB 裝置", "Windows problem code 28 ・ USBSTOR\\DISK\\SERIAL-SECRET",
            "USBSTOR\\DISK\\SERIAL-SECRET", "Windows PnP", Severity.Warning));

        string report = ReportService.BuildMarkdownForTests(vm);
        Assert.DoesNotContain("SERIAL-SECRET", report);
        Assert.Contains("problem code 28", report);
    }

    [Fact]
    public void 證據實驗室頁可建構且五類擷取入口可依序執行()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                WpfEnv.Ensure();
                var vm = new MainViewModel();
                var view = new EvidenceLabView { DataContext = vm };
                view.Measure(new Size(1280, 800));
                view.Arrange(new Rect(0, 0, 1280, 800));
                view.RunSmokeAsync().GetAwaiter().GetResult();
                Assert.Equal(4, vm.HardwareEvidence.SelectedSection);
                Assert.False(vm.HardwareEvidence.IsBusy);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "證據實驗室五類擷取逾時。");
        Assert.Null(failure);
    }
}
