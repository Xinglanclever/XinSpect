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
        var result = svc.SmnRead(0x12345678);

        // 確認寫了 index
        Assert.Contains(pci.Writes, w => w.Reg == 0xB8 && w.Value == 0x12345678);
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
}
