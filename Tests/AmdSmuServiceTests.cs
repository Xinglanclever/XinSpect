using Xunit;

namespace XinSpect.Tests;

public class AmdSmuServiceTests
{
    private sealed class FakePci : AmdSmuService.IPciAccess
    {
        public string Vendor { get; set; } = "AuthenticAMD";
        public Dictionary<(byte, byte, byte, uint), uint> Registers { get; } = new();
        public List<(byte Bus, byte Dev, byte Fn, uint Reg, uint Value)> Writes { get; } = new();

        public string CpuVendor() => Vendor;

        public uint? ReadPci(byte bus, byte dev, byte fn, uint reg)
            => Registers.TryGetValue((bus, dev, fn, reg), out var v) ? v : null;

        public bool WritePci(byte bus, byte dev, byte fn, uint reg, uint value)
        {
            Writes.Add((bus, dev, fn, reg, value));
            Registers[(bus, dev, fn, reg)] = value;
            return true;
        }
    }

    // ── 平台判斷 ─────────────────────────────────────────

    [Fact]
    public void Intel平台_服務停用()
    {
        var pci = new FakePci { Vendor = "GenuineIntel" };
        var svc = new AmdSmuService(pci);
        Assert.False(svc.IsAmdPlatform());
    }

    [Fact]
    public void Intel平台_ReadTelemetry回正確降級訊息()
    {
        var pci = new FakePci { Vendor = "GenuineIntel" };
        var svc = new AmdSmuService(pci);
        var t = svc.ReadTelemetry();
        Assert.False(t.Success);
        Assert.Contains("AMD", t.Status);
    }

    [Fact]
    public void AMD平台_辨識正確()
    {
        var pci = new FakePci { Vendor = "AuthenticAMD" };
        // 讓 SMN 探測成功：寫 0xB8 後讀 0xBC 回合理值
        pci.Registers[(0, 0, 0, 0xBC)] = 0x00370000;  // 假的 SMU 版本
        var svc = new AmdSmuService(pci);
        Assert.True(svc.IsAmdPlatform());
    }

    // ── SMN 讀取流程 ─────────────────────────────────────

    [Fact]
    public void SmnRead_寫index再讀data()
    {
        var pci = new FakePci();
        // 先讓探測成功
        pci.Registers[(0, 0, 0, 0xBC)] = 0x00370000;
        var svc = new AmdSmuService(pci);
        svc.IsAmdPlatform(); // 觸發探測

        pci.Writes.Clear();
        pci.Registers[(0, 0, 0, 0xBC)] = 0xDEADBEEF;
        var result = svc.SmnRead(0x00059800);

        // 確認寫了 index
        Assert.Contains(pci.Writes, w => w.Reg == 0xB8 && w.Value == 0x00059800);
        Assert.Equal(0xDEADBEEFu, result);
    }

    [Fact]
    public void 非AMD平台_SmnRead回null()
    {
        var pci = new FakePci { Vendor = "GenuineIntel" };
        var svc = new AmdSmuService(pci);
        Assert.Null(svc.SmnRead(0x00059800));
    }

    // ── SMN 埠探測 ────────────────────────────────────────

    [Fact]
    public void 兩組埠都失敗_降級訊息正確()
    {
        var pci = new FakePci();
        // 不設定任何回傳值 → 兩組埠都讀回 null
        var svc = new AmdSmuService(pci);
        var t = svc.ReadTelemetry();
        Assert.False(t.Success);
        Assert.Contains("SMN 不可存取", t.Status);
    }

    [Fact]
    public void 第一組埠失敗_第二組成功()
    {
        var pci = new FakePci();
        // 只設定 0x64（第二組 data 埠）有回傳
        pci.Registers[(0, 0, 0, 0x64)] = 0x00370000;
        var svc = new AmdSmuService(pci);
        Assert.True(svc.IsAmdPlatform());
        // 應該選擇第二組
        Assert.NotNull(svc.SmnRead(0x00050000));
    }

    // ── 合理性檢查 ────────────────────────────────────────

    [Theory]
    [InlineData(0u)]
    [InlineData(0xFFFFFFFFu)]
    public void 無效值_判為不合理(uint value)
        => Assert.False(AmdSmuService.IsPlausible(value));

    [Fact]
    public void Null_判為不合理()
        => Assert.False(AmdSmuService.IsPlausible(null));

    [Theory]
    [InlineData(0x00370000u)]  // SMU 版本
    [InlineData(0x12345678u)]  // 任意非零非全一
    public void 合理值_通過(uint value)
        => Assert.True(AmdSmuService.IsPlausible(value));

    // ── 溫度換算 ──────────────────────────────────────────

    [Fact]
    public void 溫度換算_無rangeAdj()
    {
        var pci = new FakePci();
        pci.Registers[(0, 0, 0, 0xBC)] = 0x00370000; // 探測通過
        var svc = new AmdSmuService(pci);
        svc.IsAmdPlatform();

        // 模擬溫度讀值：60°C = 480 * 0.125，480 = 0x1E0，左移 21 位
        uint tempRaw = 480u << 21;
        pci.Registers[(0, 0, 0, 0xBC)] = tempRaw;
        var t = svc.ReadTelemetry();

        Assert.True(t.Success);
        Assert.NotNull(t.PackageTempC);
        Assert.Equal(60.0, t.PackageTempC!.Value, 1);
    }

    [Fact]
    public void 溫度換算_有rangeAdj減49度()
    {
        var pci = new FakePci();
        pci.Registers[(0, 0, 0, 0xBC)] = 0x00370000;
        var svc = new AmdSmuService(pci);
        svc.IsAmdPlatform();

        // 800 * 0.125 = 100°C，減 49 = 51°C；bit 19 = range select
        uint tempRaw = (800u << 21) | (1u << 19);
        pci.Registers[(0, 0, 0, 0xBC)] = tempRaw;
        var t = svc.ReadTelemetry();

        Assert.True(t.Success);
        Assert.Equal(51.0, t.PackageTempC!.Value, 1);
    }

    [Fact]
    public void 逐核頻率_尚未收錄_回空清單()
    {
        var pci = new FakePci();
        pci.Registers[(0, 0, 0, 0xBC)] = 0x00370000;
        var svc = new AmdSmuService(pci);
        var t = svc.ReadTelemetry();

        // 即使溫度讀到了，逐核也應該是空的（尚未收錄 PM table）
        Assert.Empty(t.Cores);
        Assert.Contains("尚未收錄", t.Status);
    }
    [Fact]
    public async Task TransactionsAcrossInstancesCannotInterleaveAddresses()
    {
        using var selected = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        var pci = new ScheduledPci();
        var first = new AmdSmuService(pci);
        var second = new AmdSmuService(pci);
        pci.AfterWrite = address =>
        {
            if (address != 0x59800) return;
            selected.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(3)));
        };
        var a = Task.Run(() => first.ReadTransaction(0xB8, 0xBC, 0x59800, default));
        Task<uint?>? b = null;
        try
        {
            Assert.True(selected.Wait(TimeSpan.FromSeconds(3)));
            b = Task.Run(() =>
            {
                secondStarted.Set();
                return second.ReadTransaction(0xB8, 0xBC, 0x50000, default);
            });
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(3)));
        }
        finally { release.Set(); }
        Assert.Equal(0x59800u, await a.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(b);
        Assert.Equal(0x50000u, await b!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "W59800", "R59800", "W50000", "R50000" }, pci.Events);
    }

    [Fact]
    public void ExceptionAndCancellationReleaseTransactionGate()
    {
        var pci = new ScheduledPci();
        var service = new AmdSmuService(pci);
        pci.AfterWrite = _ => throw new InvalidOperationException("injected");
        Assert.Throws<InvalidOperationException>(() => service.ReadTransaction(0xB8, 0xBC, 0x59800, default));
        using var cancellation = new CancellationTokenSource();
        pci.AfterWrite = _ => cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.ReadTransaction(0xB8, 0xBC, 0x59800, cancellation.Token));
        Assert.DoesNotContain(pci.Events, value => value.StartsWith("R"));
        pci.AfterWrite = null;
        Assert.Equal(0x50000u, service.ReadTransaction(0xB8, 0xBC, 0x50000, default));
    }

    [Fact]
    public void NativeTransactionHoldsOneSessionThroughReadAndDisposesOnFailure()
    {
        int opens = 0, disposals = 0;
        var session = new FakeSession(() => disposals++);
        var adapter = new AmdSmuService.WinRing0PciAccess(() => { opens++; return session; });
        var service = new AmdSmuService(adapter);
        Assert.Equal(7u, service.ReadTransaction(0xB8, 0xBC, 0x59800, default));
        Assert.Equal(1, opens);
        Assert.Equal(1, disposals);
        Assert.Equal(new[] { "write", "read", "dispose" }, session.Events);
        session.ThrowRead = true;
        Assert.Throws<InvalidOperationException>(() => service.ReadTransaction(0xB8, 0xBC, 0x59800, default));
        Assert.Equal(2, opens);
        Assert.Equal(2, disposals);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.ReadTransaction(0xB8, 0xBC, 0x59800, cancelled.Token));
        Assert.Equal(2, opens);
        Assert.Null(service.ReadTransaction(0xB8, 0xBC, 0x12345678, default));
        Assert.Equal(2, opens);
    }

    [Fact]
    public async Task CancelledWaiterNeverStartsHardwareTransaction()
    {
        using var selected = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var pci = new ScheduledPci();
        pci.AfterWrite = _ => { selected.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(3))); };
        var first = new AmdSmuService(pci);
        var second = new AmdSmuService(pci);
        var active = Task.Run(() => first.ReadTransaction(0xB8, 0xBC, 0x59800, default));
        try
        {
            Assert.True(selected.Wait(TimeSpan.FromSeconds(3)));
            using var started = new ManualResetEventSlim();
            var waiter = Task.Run(() =>
            {
                started.Set();
                return second.ReadTransaction(0xB8, 0xBC, 0x50000, cancellation.Token);
            });
            Assert.True(started.Wait(TimeSpan.FromSeconds(3)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await waiter.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(new[] { "W59800" }, pci.Events);
        }
        finally { release.Set(); }
        Assert.Equal(0x59800u, await active.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class ScheduledPci : AmdSmuService.IPciAccess
    {
        private uint _selected;
        public List<string> Events { get; } = [];
        public Action<uint>? AfterWrite { get; set; }
        public string CpuVendor() => "AuthenticAMD";
        public bool WritePci(byte bus, byte dev, byte fn, uint reg, uint value)
        {
            _selected = value;
            Events.Add($"W{value:X}");
            AfterWrite?.Invoke(value);
            return true;
        }
        public uint? ReadPci(byte bus, byte dev, byte fn, uint reg)
        {
            Events.Add($"R{_selected:X}");
            return _selected;
        }
    }

    private sealed class FakeSession(Action onDispose) : AmdSmuService.ISmnSession
    {
        public List<string> Events { get; } = [];
        public bool ThrowRead { get; set; }
        public bool WriteIndex(uint register, uint address) { Events.Add("write"); return true; }
        public uint? Read(uint register)
        {
            Events.Add("read");
            if (ThrowRead) throw new InvalidOperationException("injected");
            return 7;
        }
        public void Dispose() { Events.Add("dispose"); onDispose(); }
    }

}
