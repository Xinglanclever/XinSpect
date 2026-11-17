using System.IO;
using System.Runtime.ExceptionServices;
using Xunit;

namespace XinSpect.Tests;

// 只隔離 CPU 腳位／DIMM／M.2 這三個參考來源；完整 MainViewModel 仍有既有建構子 I/O。
[Collection(WpfCollection.Name)]
public class ReferenceDataStartupTests
{
    [Fact]
    public void MainViewModel純建構不啟動三個參考來源() => OnSta(() =>
    {
        var vm = new MainViewModel();
        Assert.False(vm.CpuPinout.DetectionStarted);
        Assert.False(vm.DimmReference.DetectionStarted);
        Assert.False(vm.M2Analysis.DetectionStarted);
        Assert.NotEmpty(vm.DimmReference.AllGenerations);
        Assert.NotEmpty(vm.M2Analysis.KeySpecs);
        Assert.NotEmpty(vm.M2Analysis.FormFactors);
    });

    [Fact]
    [Trait("Category", "InjectedComposition")]
    public void 正式Startup參考階段啟動注入來源一次且保留供頁面與報告使用的資料() => OnSta(() =>
    {
        int sockets = 0, modules = 0, disks = 0, slots = 0;
        var cpu = new CpuPinoutService(false, () => { Interlocked.Increment(ref sockets); return "LGA 1700"; });
        var dimm = new DimmReferenceService(false, () =>
        {
            Interlocked.Increment(ref modules);
            return [new DimmReferenceService.DimmModuleRow(0x1A, 20, 8)];
        });
        var m2 = new M2AnalysisService(false,
            readDisks: () => { Interlocked.Increment(ref disks); return [Disk()]; },
            readSlots: () =>
            {
                Interlocked.Increment(ref slots);
                return [new SmbiosSlotRow("SYNTH-M2", "M.2 Socket 3", "x4", "In Use")];
            }, publish: action => action());
        var vm = new MainViewModel(cpu, dimm, m2);
        Assert.Same(cpu, vm.CpuPinout);
        Assert.Same(dimm, vm.DimmReference);
        Assert.Same(m2, vm.M2Analysis);
        Assert.Equal(0, sockets + modules + disks + slots);

        Assert.True(Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => StartupSequence.StartReferenceDataAsync(vm)))).Wait(TimeSpan.FromSeconds(5)));
        Assert.True(StartupSequence.StartReferenceDataAsync(vm).Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, sockets);
        Assert.Equal(1, modules);
        Assert.Equal(1, disks);
        Assert.Equal(1, slots);
        Assert.Equal("LGA 1700", vm.CpuPinout.SocketName);
        Assert.Equal("DDR4", vm.DimmReference.DetectedType);
        Assert.Equal("SYNTH-M2", Assert.Single(vm.M2Analysis.Slots).Designation);
        Assert.Contains("SYNTH-NVME", Assert.Single(vm.M2Analysis.NvmeDriveNotes));
        Assert.Contains("SYNTH-NVME", Assert.Single(vm.M2Analysis.DriveNotes));
    });

    [Fact]
    public async Task 獨立服務自動啟動仍沿用同一個once工作()
    {
        int sockets = 0, modules = 0, disks = 0;
        var cpu = new CpuPinoutService(true, () => { Interlocked.Increment(ref sockets); return "AM5"; });
        var dimm = new DimmReferenceService(true, () => { Interlocked.Increment(ref modules); return []; });
        var m2 = new M2AnalysisService(true,
            readDisks: () => { Interlocked.Increment(ref disks); return [Disk()]; },
            readSlots: () => [], publish: action => action());
        var cpuTask = cpu.StartDetectionAsync();
        var dimmTask = dimm.StartDetectionAsync();
        var m2Task = m2.StartDetectionAsync();
        await Task.WhenAll(cpuTask, dimmTask, m2Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(cpuTask, cpu.StartDetectionAsync());
        Assert.Same(dimmTask, dimm.StartDetectionAsync());
        Assert.Same(m2Task, m2.StartDetectionAsync());
        Assert.Equal(1, sockets);
        Assert.Equal(1, modules);
        Assert.Equal(1, disks);
        Assert.Equal("AM5", cpu.DetectedSocket);
        Assert.Equal("AM5 (LGA 1718)", cpu.SocketName);
        Assert.Contains("SYNTH-NVME", Assert.Single(m2.NvmeDriveNotes));
    }

    [Fact]
    public void 正式RunAsync仍呼叫參考資料階段且獨立公開建構子保留自動啟動()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string startup = File.ReadAllText(Path.Combine(dir.FullName, "ViewModels", "StartupSequence.cs"));
        int run = startup.IndexOf("public static async Task RunAsync(", StringComparison.Ordinal);
        int call = startup.IndexOf("_ = StartReferenceDataAsync(vm);", run, StringComparison.Ordinal);
        int saved = startup.IndexOf("vm.ApplySavedEra();", run, StringComparison.Ordinal);
        Assert.True(call > run && call < saved, "參考資料必須在一般Startup明確啟動，不能只留未呼叫helper或放入深度診斷分支。");
        foreach (string service in new[] { "CpuPinoutService", "DimmReferenceService", "M2AnalysisService" })
            Assert.Contains($"public {service}() : this(autoDetect: true)",
                File.ReadAllText(Path.Combine(dir.FullName, "Services", service + ".cs")));
    }

    [Fact]
    public async Task M2發布失敗的cached工作重入不會另查來源或再次發布()
    {
        int disks = 0, slots = 0, publishes = 0;
        var m2 = new M2AnalysisService(false,
            readDisks: () => { Interlocked.Increment(ref disks); return [Disk()]; },
            readSlots: () => { Interlocked.Increment(ref slots); return []; },
            publish: _ =>
            {
                Interlocked.Increment(ref publishes);
                throw new InvalidOperationException("synthetic publisher failure");
            });
        var first = m2.StartDetectionAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("synthetic publisher failure", error.Message);
        Assert.True(first.IsFaulted);
        var again = m2.StartDetectionAsync();
        Assert.Same(first, again);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await again.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, disks);
        Assert.Equal(1, slots);
        Assert.Equal(1, publishes);
        Assert.Empty(m2.DriveNotes);
        Assert.Empty(m2.NvmeDriveNotes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task M2發布排隊或丟棄不阻塞Task且once重入不重複Apply(bool drop)
    {
        int disks = 0, slots = 0, publishes = 0;
        Action? pending = null;
        var m2 = new M2AnalysisService(false,
            readDisks: () => { Interlocked.Increment(ref disks); return [Disk()]; },
            readSlots: () => { Interlocked.Increment(ref slots); return []; },
            publish: callback =>
            {
                Interlocked.Increment(ref publishes);
                if (!drop) pending = callback;
            });
        var first = m2.StartDetectionAsync();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first.IsCompletedSuccessfully);
        Assert.Equal(1, publishes);
        Assert.Empty(m2.DriveNotes);
        Assert.Empty(m2.NvmeDriveNotes);
        Assert.Equal("（偵測中…）", m2.DetectedSlots);
        var beforeApply = m2.StartDetectionAsync();
        Assert.Same(first, beforeApply);
        await beforeApply.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, publishes);
        if (drop)
        {
            Assert.Null(pending);
            Assert.Empty(m2.DriveNotes);
            Assert.Empty(m2.NvmeDriveNotes);
        }
        else
        {
            Assert.NotNull(pending);
            pending(); // 模擬 dispatcher 之後執行一次；cached Task 已先完成。
            Assert.Contains("SYNTH-NVME", Assert.Single(m2.DriveNotes));
            Assert.Contains("SYNTH-NVME", Assert.Single(m2.NvmeDriveNotes));
            Assert.Same(first, m2.StartDetectionAsync());
            Assert.Single(m2.DriveNotes);
            Assert.Single(m2.NvmeDriveNotes);
        }
        Assert.Equal(1, disks);
        Assert.Equal(1, slots);
        Assert.Equal(1, publishes);
    }

    private static PhysicalDiskInfo Disk() => new()
    {
        Index = 0, Model = "SYNTH-NVME", Kind = DiskKind.NvmeSsd, SizeBytes = 512L * 1024 * 1024 * 1024,
    };

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { WpfEnv.Ensure(); action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA構造／注入階段未在30秒內結束。");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
