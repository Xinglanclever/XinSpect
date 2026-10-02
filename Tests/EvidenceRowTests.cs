using Xunit;

namespace XinSpect.Tests;

public sealed class EvidenceRowTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 讀不到的事實列_顯示原因而非空白或誤導值()
    {
        var fact = new HardwareSnapshotFact
        {
            Key = "chipset.bios_cntl",
            Category = "韌體安全",
            Name = "BIOS 寫入保護",
            Value = "",
            Source = "PCI 0:31.0+0xDC",
            Trust = FactTrustLevel.Unknown,
            MeasuredAtUtc = At,
            Availability = FactAvailability.InsufficientPrivilege,
            UnavailableReason = "缺 ring0：自家驅動未載入",
        };

        var row = EvidenceFactRow.From(fact);

        Assert.Contains("讀不到", row.ValueText);
        Assert.Contains("缺 ring0", row.ValueText);
    }

    [Fact]
    public void 不支援的事實列_顯示不支援標籤()
    {
        var fact = new HardwareSnapshotFact
        {
            Key = "gpu.row_remapper",
            Category = "顯示卡",
            Name = "顯存列重映射",
            Value = "",
            Source = "NVML",
            Trust = FactTrustLevel.Unknown,
            MeasuredAtUtc = At,
            Availability = FactAvailability.NotSupported,
            UnavailableReason = "此卡為 Pascal，無 Ampere+ 列重映射",
        };

        Assert.Contains("不支援", EvidenceFactRow.From(fact).ValueText);
    }

    [Fact]
    public void 變更列_某側讀不到時顯示原因而非破折號()
    {
        var before = new HardwareFact("mchbar.tcl", "記憶體", "tCL", "36", "clk", "MCHBAR",
            FactTrustLevel.Measured, false, At, 36);
        var after = new HardwareFact("mchbar.tcl", "記憶體", "tCL", "", "", "MCHBAR",
            FactTrustLevel.Unknown, false, At, null,
            FactAvailability.InsufficientPrivilege, "缺 ring0");
        var change = new HardwareFactChange
        {
            Key = "mchbar.tcl",
            Kind = SnapshotChangeKind.Changed,
            Previous = before,
            Current = after,
        };

        var row = EvidenceChangeRow.From(change);

        Assert.Equal("36", row.Before);
        Assert.Contains("缺 ring0", row.After);
    }

    [Fact]
    public void 韌體安全列_反映已載入事實且讀不到時顯示原因()
    {
        var svc = new EvidenceLabService();
        svc.LoadChipsetSecurity(new FakePci());

        var rows = svc.FirmwareSecurityRows;

        Assert.Contains(rows, r => r.Name == "BIOS 寫入保護" && r.ValueText.Contains("讀不到"));
    }

    // ===== 裁決警示：不利裁決標警示色（IsWarning），讀不到的列一律不算警示 =====

    [Theory]
    [InlineData("未保護：BLE=0，任何 ring0 皆可寫 BIOS（BIOSWE=0）", true)]
    [InlineData("未鎖定（FLOCKDN=0）：保護範圍與寫入停用設定仍可被 ring0 改動", true)]
    [InlineData("未鎖：D_LCK=0", true)]
    [InlineData("SMRAM 對外開放：D_OPEN=1 且未鎖", true)]
    [InlineData("BIOS 區域可寫入：主機軟體獲准（BRWA=0x2 bit1）", true)]
    [InlineData("除錯埠啟用中且未鎖（ENABLE=1、LOCK=0）：外部除錯連線可行", true)]
    [InlineData("最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS", false)]
    [InlineData("已鎖：D_LCK=1，SMRAM 設定鎖定", false)]
    [InlineData("已鎖定（FLOCKDN=1）：SPI 保護設定不可改直至重置", false)]
    [InlineData("BIOS 區域不可寫入：主機軟體未獲准（BRWA=0x0 bit1=0）", false)]
    [InlineData("正常運作（working_state=0，fw_init=完成）", false)]
    public void 裁決警示_前綴與服務裁決文字一一對應(string value, bool expected)
    {
        var row = new EvidenceFactRow("韌體安全", "測試", value, "", "", false);
        Assert.Equal(expected, row.IsWarning);
    }

    [Fact]
    public void 讀不到的列_不冒充危險不標警示()
    {
        var row = new EvidenceFactRow("韌體安全", "BIOS 寫入保護", "", "PCI 0:1F.0+0xDC", "", false,
            FactAvailability.InsufficientPrivilege, "缺 ring0：特權讀取未就緒");
        Assert.False(row.IsWarning);
    }

    private sealed class FakePci : IPciConfigReader
    {
        public bool Available => false;
        public string? UnavailableReason => "WinRing0 未載入";
        public uint? ReadDword(byte bus, byte device, byte function, uint register) => null;
    }
}
