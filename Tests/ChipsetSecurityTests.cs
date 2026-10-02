using Xunit;

namespace XinSpect.Tests;

public sealed class ChipsetSecurityTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    // BIOS_CNTL (Intel PCH D31:F0 +0xDC) 低位元：bit0 BIOSWE、bit1 BLE、bit5 SMM_BWP。
    [Fact]
    public void BIOS_CNTL_BLE關且BIOSWE開_判為可被任何ring0覆寫()
    {
        var d = ChipsetSecurity.DecodeBiosCntl(0x01);
        Assert.True(d.BiosWe);
        Assert.False(d.Ble);
        Assert.False(d.SmmBwp);
        Assert.Equal(ChipsetSecurityVerdict.Unprotected, d.Verdict);
    }

    [Fact]
    public void BIOS_CNTL_BLE開_判為有鎖保護()
    {
        var d = ChipsetSecurity.DecodeBiosCntl(0x02);
        Assert.True(d.Ble);
        Assert.Equal(ChipsetSecurityVerdict.Protected, d.Verdict);
    }

    [Fact]
    public void BIOS_CNTL_SMM_BWP開_判為最強SMM保護()
    {
        var d = ChipsetSecurity.DecodeBiosCntl(0x20);
        Assert.True(d.SmmBwp);
        Assert.Equal(ChipsetSecurityVerdict.SmmProtected, d.Verdict);
    }

    // SMRAMC (Intel D0:F0 +0x88)：bit4 D_LCK、bit5 D_CLS、bit6 D_OPEN。
    [Fact]
    public void SMRAMC_D_LCK開_判為已鎖保護()
    {
        var d = ChipsetSecurity.DecodeSmramc(0x10);
        Assert.True(d.DLck);
        Assert.Equal(ChipsetSecurityVerdict.Protected, d.Verdict);
    }

    [Fact]
    public void SMRAMC_D_OPEN開且未鎖_判為SMRAM對外開放()
    {
        var d = ChipsetSecurity.DecodeSmramc(0x40);
        Assert.True(d.DOpen);
        Assert.False(d.DLck);
        Assert.Equal(ChipsetSecurityVerdict.Unprotected, d.Verdict);
    }

    [Fact]
    public void SMRAMC_未鎖_判為無保護()
    {
        var d = ChipsetSecurity.DecodeSmramc(0x00);
        Assert.False(d.DLck);
        Assert.Equal(ChipsetSecurityVerdict.Unprotected, d.Verdict);
    }

    // HFSTS1 (HECI1 D22:F0 +0x40)：working_state[3:0]、fw_init_complete bit9、operation_mode[19:16]（coreboot me_hfs 權威佈局）。
    [Fact]
    public void HFS_operation_mode_0_判Normal且解出工作狀態與初始化()
    {
        var d = ChipsetSecurity.DecodeHfs(0x00000205); // working_state=5, fw_init(bit9)=1, operation_mode=0
        Assert.Equal((byte)5, d.WorkingState);
        Assert.True(d.FwInitComplete);
        Assert.Equal(MeOperationMode.Normal, d.OperationMode);
    }

    [Fact]
    public void HFS_operation_mode_3_判軟性停用()
    {
        var d = ChipsetSecurity.DecodeHfs(0x00030000); // operation_mode=3
        Assert.Equal(MeOperationMode.SoftDisable, d.OperationMode);
        Assert.Equal((byte)3, d.OperationModeRaw);
    }

    [Fact]
    public void HFS_未知operation_mode_標Other保留原始值不臆測()
    {
        var d = ChipsetSecurity.DecodeHfs(0x00070000); // operation_mode=7（未定義）
        Assert.Equal(MeOperationMode.Other, d.OperationMode);
        Assert.Equal((byte)7, d.OperationModeRaw);
    }

    // 服務層三態：讀不到就誠實標 Availability，絕不謊報。這些用假讀取器脫離硬體驗證。
    [Fact]
    public void 服務_讀取器不可用_事實標缺ring0不謊報()
    {
        var facts = ChipsetSecurityService.Collect(new FakeReader(available: false, reason: "WinRing0 未載入", value: null), At);
        var f = facts.Single(x => x.Key == "chipset.bios_cntl");
        Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability);
        Assert.Contains("WinRing0", f.UnavailableReason);
        Assert.Equal("", f.Value);
    }

    [Fact]
    public void 服務_讀到BLE關_事實為可用且判未保護()
    {
        var facts = ChipsetSecurityService.Collect(new FakeReader(true, null, 0x01u), At);
        var f = facts.Single(x => x.Key == "chipset.bios_cntl");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("未保護", f.Value);
    }

    [Fact]
    public void 服務_裝置無回應_標為不適用而非誤判()
    {
        var facts = ChipsetSecurityService.Collect(new FakeReader(true, null, 0xFFFFFFFFu), At);
        var f = facts.Single(x => x.Key == "chipset.bios_cntl");
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 服務_HFS_非IntelHECI_標不適用不誤判ME狀態()
    {
        // reg 0x00 低 16 位 != 0x8086 → 不是 Intel HECI，不可把 0x40 當 HFSTS1 解讀
        var facts = ChipsetSecurityService.Collect(new FakeReader(true, null, 0x12345678u), At);
        var f = facts.Single(x => x.Key == "chipset.me_hfs");
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 服務_HFS_IntelHECI_可用並解出ME狀態()
    {
        // 低 16 位 = 0x8086 → Intel HECI；operation_mode nibble = 0 → Normal
        var facts = ChipsetSecurityService.Collect(new FakeReader(true, null, 0x00008086u), At);
        var f = facts.Single(x => x.Key == "chipset.me_hfs");
        Assert.Equal(FactAvailability.Present, f.Availability);
    }

    private sealed class FakeReader(bool available, string? reason, uint? value) : IPciConfigReader
    {
        public bool Available => available;
        public string? UnavailableReason => reason;
        public uint? ReadDword(byte bus, byte device, byte function, uint register) => value;
    }
}
