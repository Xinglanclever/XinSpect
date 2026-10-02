using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 原始快照 UI 管線的服務級契約：重載時一併收集原始區（逐區三態）、存檔帶完整性信封、
/// 與舊檔差分時揮發遮罩位元組永不入列。用假讀取器與暫存檔，不碰硬體。
/// </summary>
public class RawSnapshotUiTests : IDisposable
{
    private readonly string _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        $"xinraw-test-{Guid.NewGuid():N}.xinraw");

    public void Dispose()
    {
        if (System.IO.File.Exists(_path)) System.IO.File.Delete(_path);
    }

    [Fact]
    public void 重載時一併收集原始區_逐區三態()
    {
        var svc = new EvidenceLabService();
        svc.ReloadDriverBackedFacts(new FakePciRaw(), new DeniedMsr(), new FakeSpiMmio(0x10, 0xAA),
            new EmptyAcpi(), new DeniedIoPort());

        // 2 PCI + 3 MSR + 1 acpi:* + 1 SPIBAR + 1 MCHBAR = 8 區；來源鍵不可重複（差分按鍵配對）。
        Assert.Equal(8, svc.RawRegions.Count);
        Assert.Equal(svc.RawRegions.Count, svc.RawRegions.Select(r => r.Source).Distinct().Count());
        Assert.Contains(svc.RawRegions, r => r.Source == "mmio:spi:0xfed10000+88" && r.Bytes is { Length: 0x88 });
        Assert.Contains(svc.RawRegions, r => r.Source == "msr:0x34" && r.Availability != FactAvailability.Present);
        Assert.Contains(svc.RawRegions, r => r.Source == "acpi:*" && r.Availability != FactAvailability.Present);
        Assert.Contains("8 區", svc.RawSummary);
    }

    [Fact]
    public void 存檔後載入_內容一致且完整性通過()
    {
        var svc = new EvidenceLabService();
        svc.ReloadDriverBackedFacts(new FakePciRaw(), new DeniedMsr(), new FakeSpiMmio(0x10, 0xAA),
            new EmptyAcpi(), new DeniedIoPort());

        svc.SaveRawSnapshotAsync(_path).Wait();
        Assert.Contains("已儲存", svc.RawStatus);

        var loaded = RawRegisterSnapshotStore.Load(_path);
        Assert.Equal(svc.RawRegions.Count, loaded.Regions.Count);
        Assert.Equal(0xAA, loaded.Regions.First(r => r.Source == "mmio:spi:0xfed10000+88").Bytes![0x10]);
    }

    [Fact]
    public void 與舊檔差分_遮罩位元組不入列_一般變動成列()
    {
        var svc = new EvidenceLabService();
        svc.ReloadDriverBackedFacts(new FakePciRaw(), new DeniedMsr(), new FakeSpiMmio(0x10, 0xAA),
            new EmptyAcpi(), new DeniedIoPort());
        svc.SaveRawSnapshotAsync(_path).Wait();

        // 重讀：0x10 變了（該入列）、0x04 也變了（揮發遮罩位元組，不該入列）
        svc.ReloadDriverBackedFacts(new FakePciRaw(), new DeniedMsr(), new FakeSpiMmio(0x10, 0xBB, maskedByte: 0xFF),
            new EmptyAcpi(), new DeniedIoPort());
        svc.CompareRawAsync(_path).Wait();

        var row = Assert.Single(svc.RawChanges, r => r.Source == "mmio:spi:0xfed10000+88");
        Assert.Equal("變更", row.Kind);
        Assert.Contains("0x10", row.Detail);
        Assert.DoesNotContain("0x04", row.Detail);
    }

    [Fact]
    public void 無現有區時差分如實拒比_損毀檔拒載()
    {
        // 先造一份合法檔；再讓「沒有現有區」的服務去比對——如實拒比，不猜。
        var donor = new EvidenceLabService();
        donor.ReloadDriverBackedFacts(new FakePciRaw(), new DeniedMsr(), new FakeSpiMmio(0x10, 0xAA),
            new EmptyAcpi(), new DeniedIoPort());
        donor.SaveRawSnapshotAsync(_path).Wait();

        var fresh = new EvidenceLabService();
        fresh.CompareRawAsync(_path).Wait();
        Assert.Contains("無從差分", fresh.RawStatus);
        Assert.Empty(fresh.RawChanges);

        // 損毀檔：完整性信封拒載。
        System.IO.File.WriteAllText(_path, "不是合法檔");
        var svc = new EvidenceLabService();
        svc.ReloadDriverBackedFacts(new FakePciRaw(), new DeniedMsr(), new FakeSpiMmio(0x10, 0xAA),
            new EmptyAcpi(), new DeniedIoPort());
        svc.CompareRawAsync(_path).Wait();
        Assert.Equal("差分失敗", svc.RawSummary);
        Assert.Empty(svc.RawChanges);
    }

    // ===== 假件 =====

    private sealed class FakePciRaw : IPciConfigReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public uint? ReadDword(byte bus, byte device, byte function, uint register) => (bus, device, function, register) switch
        {
            (0, 31, 0, 0xDC) => 0x22,   // BIOS_CNTL：SMM_BWP+BLE
            (0, 0, 0, 0x88) => 0x10,    // SMRAMC：D_LCK
            _ => 0xFFFF_FFFF,
        };
    }

    private sealed class DeniedMsr : IKernelMsrReader
    {
        public bool Available => false;
        public string? UnavailableReason => "缺 ring0（測試假件）";
        public ulong? ReadMsr(uint index) => null;
    }

    private sealed class EmptyAcpi : IAcpiTableSource
    {
        public bool Available => false;
        public string? UnavailableReason => "列舉失敗（測試假件）";
        public IReadOnlyList<byte[]> ReadAll() => [];
    }

    private sealed class DeniedIoPort : IIoPortAccess
    {
        public bool Available => false;
        public string? UnavailableReason => "缺 I/O 埠存取（測試假件）";
        public byte? InByte(uint port) => null;
        public bool OutByte(uint port, byte value) => false;
    }

    /// <summary>SPIBAR 0x88 位元組可控的假 MMIO：0x10 可編程、0x04（揮發遮罩位）預設 0x00 可另行編程。</summary>
    private sealed class FakeSpiMmio(int editableOffset, byte editableValue, byte maskedByte = 0x00) : IMmioReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public string? LastFailReason => null;

        public byte[]? ReadBlock(ulong physicalAddress, int length)
        {
            if (physicalAddress != 0xFED10000) return null;
            var bytes = new byte[length];
            bytes[editableOffset] = editableValue;
            bytes[0x04] = maskedByte;
            return bytes;
        }
    }
}
