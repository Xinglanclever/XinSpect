using System.Buffers.Binary;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Production Collect with finite, synthetic readers only; never constructs a native reader.</summary>
public sealed class EcamAerBoundedScanTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private const ulong Base = 0xE0000000;

    [Theory]
    [InlineData(0, 255, 8192, 31)]
    [InlineData(223, 255, 8192, 254)]
    [InlineData(224, 255, 8192, 255)]
    [InlineData(225, 255, 7936, 255)]
    [InlineData(254, 255, 512, 255)]
    public void EmptyRange_StopsAtComputedEndWithoutWrapping(int start, int end, int probes, int last)
    {
        var mmio = EmptyReader(Base, start, last, probes);
        var facts = EcamAerService.Collect(mmio, new Tables(Mcfg(Base, (byte)start, (byte)end)), At);

        Assert.Equal(probes, mmio.Calls.Count);
        Assert.All(mmio.Calls, c => Assert.Equal(16, c.Length));
        Assert.Equal(Address(Base, start, 0, 0), mmio.Calls[0].Address);
        Assert.Equal(Address(Base, last, 31, 7), mmio.Calls[^1].Address);
        // Each visited bus contributes exactly 32*8 probes, in ascending order.
        Assert.Equal(Enumerable.Range(start, last - start + 1),
            mmio.Calls.Select(c => (int)((c.Address - Base) >> 20)).Distinct());
        var scan = Scan(facts);
        Assert.Equal(FactAvailability.Present, scan.Availability);
        Assert.Contains($"bus {start}-{last}", scan.Value);
        Assert.Contains("0 個裝置、0 個帶 AER 能力", scan.Value);
        if (last < end) Assert.Contains($"bus {last + 1}-{end} 未掃", scan.Value);
        Assert.Single(facts, f => f.Key == "pcieaer.ecam");
        Assert.DoesNotContain(facts, f => f.Key.StartsWith("pcieaer.aer.", StringComparison.Ordinal));
    }

    [Fact]
    public void SingleBus255_Exactly256EmptyProbesAndReturns()
    {
        // Original byte loop reaches its 257th read at bus 0; the fake throws immediately.
        var mmio = EmptyReader(Base, 255, 255, 256);
        var facts = EcamAerService.Collect(mmio, new Tables(Mcfg(Base, 255, 255)), At);

        Assert.Equal(256, mmio.Calls.Count);
        Assert.Equal(FactAvailability.Present, Scan(facts).Availability);
        Assert.Contains("bus 255-255", Scan(facts).Value);
        Assert.Equal(Address(Base, 255, 31, 7), mmio.Calls[^1].Address);
    }

    [Fact]
    public void AddressAdditionOverflow_RejectsBeforeAnyReaderCall()
    {
        ulong nearEnd = ulong.MaxValue - 0xFFF;
        var mmio = NoReadExpected();
        var facts = EcamAerService.Collect(mmio, new Tables(Mcfg(nearEnd, 1, 1)), At);

        Assert.Empty(mmio.Calls);
        Assert.Equal(FactAvailability.ReadError, Scan(facts).Availability);
        Assert.Contains("地址溢位", Scan(facts).UnavailableReason);
        Assert.Contains("未讀取 bus 1 dev 00.0", Scan(facts).UnavailableReason);
    }

    [Fact]
    public void FullPageEndOverflow_RejectsBeforeEvenHeaderRead()
    {
        var mmio = NoReadExpected();
        var facts = EcamAerService.Collect(mmio, new Tables(Mcfg(ulong.MaxValue - 14, 0, 0)), At);

        Assert.Empty(mmio.Calls);
        Assert.Equal(FactAvailability.ReadError, Scan(facts).Availability);
        Assert.Contains("完整 4KB", Scan(facts).UnavailableReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(15)]
    public void ShortHeader_IsReadErrorAndNeverDecodedAsAbsent(int returned)
    {
        var mmio = new FiniteMmio(1, (_, length) =>
        {
            Assert.Equal(16, length);
            return Enumerable.Repeat((byte)0xFF, returned).ToArray();
        });
        var facts = EcamAerService.Collect(mmio, new Tables(Mcfg(Base, 255, 255)), At);

        Assert.Single(mmio.Calls);
        Assert.Equal(FactAvailability.ReadError, Scan(facts).Availability);
        Assert.Contains("探頭短讀", Scan(facts).UnavailableReason);
        Assert.Contains($"實得 {returned}", Scan(facts).UnavailableReason);
        Assert.DoesNotContain(facts, f => f.Key.StartsWith("pcieaer.aer.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(255)]
    [InlineData(4095)]
    public void ShortConfigurationPage_IsReadErrorBeforeCapabilityParsing(int returned)
    {
        var mmio = new FiniteMmio(2, (_, length) =>
            length == 16 ? PresentHeader() : new byte[returned]);
        var facts = EcamAerService.Collect(mmio, new Tables(Mcfg(Base, 255, 255)), At);

        Assert.Equal(2, mmio.Calls.Count);
        Assert.Equal(FactAvailability.ReadError, Scan(facts).Availability);
        Assert.Contains("組態頁短讀", Scan(facts).UnavailableReason);
        Assert.Contains($"實得 {returned}", Scan(facts).UnavailableReason);
        Assert.DoesNotContain(facts, f => f.Key.StartsWith("pcieaer.aer.", StringComparison.Ordinal));
    }

    [Fact]
    public void LaterShortHeader_PreservesEarlierHighBusAerWithFailureLocation()
    {
        int calls = 0;
        var mmio = new FiniteMmio(3, (_, _) => ++calls switch
        {
            1 => PresentHeader(),
            2 => AerPage(),
            _ => new byte[15]
        });
        var facts = EcamAerService.Collect(mmio, new Tables(Mcfg(Base, 255, 255)), At);

        Assert.Equal(3, mmio.Calls.Count);
        var aer = Assert.Single(facts, f => f.Key == "pcieaer.aer.255.0.0");
        Assert.Equal(FactAvailability.Present, aer.Availability);
        Assert.Contains("資料鏈路協定錯誤", aer.Value);
        Assert.Equal(FactAvailability.ReadError, Scan(facts).Availability);
        Assert.Contains("bus 255 dev 00.1", Scan(facts).UnavailableReason);
        Assert.Contains("短讀", Scan(facts).UnavailableReason);
    }

    [Fact]
    public void AerCapabilityAtPageEnd_ReturnsReadErrorAndPreservesEarlierAer()
    {
        int calls = 0;
        var mmio = new FiniteMmio(4, (_, length) => ++calls switch
        {
            1 => PresentHeader(),
            2 => AerPage(),
            3 => PresentHeader(),
            _ => AerAtPageEnd()
        });
        var facts = EcamAerService.Collect(mmio, new Tables(Mcfg(Base, 255, 255)), At);

        Assert.Equal(4, mmio.Calls.Count);
        Assert.Contains(facts, f => f.Key == "pcieaer.aer.255.0.0");
        Assert.Equal(FactAvailability.ReadError, Scan(facts).Availability);
        Assert.Contains("AER @0xFFC", Scan(facts).UnavailableReason);
        Assert.Contains("bus 255 dev 00.1", Scan(facts).UnavailableReason);
    }

    [Fact]
    public void ValidHighBusDevice_DecodesAerAndCompletesWithinExactReadBudget()
    {
        ulong first = Address(Base, 255, 0, 0);
        var mmio = new FiniteMmio(257, (address, length) =>
        {
            if (address == first) return length == 16 ? PresentHeader() : AerPage();
            Assert.Equal(16, length);
            Assert.InRange(address, first, Address(Base, 255, 31, 7));
            return Enumerable.Repeat((byte)0xFF, 16).ToArray();
        });
        var facts = EcamAerService.Collect(mmio, new Tables(Mcfg(Base, 255, 255)), At);

        Assert.Equal(257, mmio.Calls.Count);
        Assert.Single(mmio.Calls, c => c.Length == 4096);
        Assert.Equal(FactAvailability.Present, Scan(facts).Availability);
        Assert.Contains("1 個裝置、1 個帶 AER 能力", Scan(facts).Value);
        var aer = Assert.Single(facts, f => f.Key == "pcieaer.aer.255.0.0");
        Assert.Contains("AER 255:00.0", aer.Name);
        Assert.Contains("資料鏈路協定錯誤", aer.Value);
    }

    [Fact]
    public void NullHeader_IsReadErrorAndStops()
    {
        var mmio = new FiniteMmio(1, (_, _) => null);
        var facts = EcamAerService.Collect(mmio, new Tables(Mcfg(Base, 255, 255)), At);

        Assert.Single(mmio.Calls);
        Assert.Equal(FactAvailability.ReadError, Scan(facts).Availability);
        Assert.Contains("讀取失敗", Scan(facts).UnavailableReason);
        Assert.Contains("bus 255 dev 00.0", Scan(facts).UnavailableReason);
    }

    [Fact]
    public void NullPage_StopsAfterSingleHeaderAndPageAttempt()
    {
        var mmio = new FiniteMmio(2, (_, length) => length == 16 ? PresentHeader() : null);
        var facts = EcamAerService.Collect(mmio, new Tables(Mcfg(Base, 255, 255)), At);

        Assert.Equal(2, mmio.Calls.Count);
        Assert.Equal(FactAvailability.ReadError, Scan(facts).Availability);
        Assert.Contains("讀取失敗", Scan(facts).UnavailableReason);
        Assert.Contains("bus 255 dev 00.0", Scan(facts).UnavailableReason);
    }

    private static HardwareFact Scan(IReadOnlyList<HardwareFact> facts)
        => Assert.Single(facts, f => f.Key == "pcieaer.scan");

    private static ulong Address(ulong baseAddress, int bus, int device, int function)
        => checked(baseAddress + ((ulong)bus << 20 | (ulong)device << 15 | (ulong)function << 12));

    private static FiniteMmio EmptyReader(ulong baseAddress, int start, int last, int budget)
        => new(budget, (address, length) =>
        {
            Assert.Equal(16, length);
            Assert.InRange(address, Address(baseAddress, start, 0, 0), Address(baseAddress, last, 31, 7));
            return Enumerable.Repeat((byte)0xFF, 16).ToArray();
        });

    private static FiniteMmio NoReadExpected()
        => new(0, (_, _) => throw new InvalidOperationException("No MMIO read was authorized by this fixture."));

    private static byte[] PresentHeader()
    {
        var head = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(head, 0x1234_8086);
        return head;
    }

    private static byte[] AerPage()
    {
        var page = new byte[4096];
        BinaryPrimitives.WriteUInt32LittleEndian(page, 0x1234_8086);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0x100), 0x0001_0001);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0x104), 1u << 4);
        return page;
    }

    private static byte[] AerAtPageEnd()
    {
        var page = new byte[4096];
        BinaryPrimitives.WriteUInt32LittleEndian(page, 0x1234_8086);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0x100), 0xFFC0_0002);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0xFFC), 0x0001_0001);
        return page;
    }

    private static byte[] Mcfg(ulong baseAddress, byte start, byte end)
    {
        var bytes = new byte[60];
        "MCFG"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 60);
        bytes[8] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(44), baseAddress);
        bytes[54] = start;
        bytes[55] = end;
        // A valid standard header checksum; strict MCFG validation remains separate R388 work.
        bytes[9] = unchecked((byte)-bytes.Sum(b => (int)b));
        return bytes;
    }

    private sealed class Tables(byte[] table) : IAcpiTableSource
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<byte[]> ReadAll() => [table];
    }

    private sealed class FiniteMmio(int budget, Func<ulong, int, byte[]?> read) : IMmioReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public List<(ulong Address, int Length)> Calls { get; } = [];

        public byte[]? ReadBlock(ulong physicalAddress, int length)
        {
            // The negative control must fail finitely instead of leaving a spinning Task behind.
            if (Calls.Count >= budget)
                throw new InvalidOperationException($"Synthetic MMIO budget {budget} exceeded.");
            Calls.Add((physicalAddress, length));
            return read(physicalAddress, length);
        }
    }
}
