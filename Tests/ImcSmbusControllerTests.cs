using Xunit;

namespace XinSpect.Tests;

/// <summary>Fake-only regression for the Skylake-X proxy state machine; production refuses unverified ownership.</summary>
public class ImcSmbusControllerTests
{
    private static ImcSmbusController Make(FakeImcPci pci, int segment = 0)
        => new(pci, new ImcSmbusLocation(FakeImcPci.Bus, FakeImcPci.Dev, FakeImcPci.Fn, segment),
               transactionTimeoutMs: 30, injectedProtocol: true);

    private static PciFunctionAddress Target => new(FakeImcPci.Bus, FakeImcPci.Dev, FakeImcPci.Fn);

    private static byte[] Image(byte seed)
    {
        var img = new byte[SpdReader.Ddr4Size];
        for (int i = 0; i < 256; i++) img[i] = (byte)(seed ^ i);
        for (int i = 0; i < 256; i++) img[256 + i] = (byte)(seed ^ 0x5A ^ i);
        img[2] = SpdReader.Ddr4TypeCode;
        return img;
    }

    [Fact]
    public void ProductionEntryRefusesUnverifiedOwnershipWithoutPciAccess()
    {
        var pci = new FakeImcPci();
        var bus = new ImcSmbusController(pci, new(FakeImcPci.Bus, FakeImcPci.Dev, FakeImcPci.Fn, 0));
        Assert.False(bus.TryAcquireBus(out var reason));
        Assert.Equal(SmbusStatus.NotSupported, bus.LastStatus);
        Assert.Contains("ownership", reason);
        bus.ReleaseBus();
        Assert.Equal(0, pci.ReadCount);
        Assert.Empty(pci.Writes);
    }

    [Fact]
    public void BusyWithoutTsodNeverWritesAndCannotBeReacquired()
    {
        var pci = new FakeImcPci { InitiallyBusy = true };
        var bus = Make(pci);
        Assert.False(bus.TryAcquireBus(out _));
        Assert.Equal(SmbusStatus.BusError, bus.LastStatus);
        pci.InitiallyBusy = false;
        int reads = pci.ReadCount;
        Assert.False(bus.TryAcquireBus(out _));
        bus.ReleaseBus();
        Assert.Equal(reads, pci.ReadCount);
        Assert.Empty(pci.Writes);
    }

    [Theory]
    [InlineData(0x20085500u)]
    [InlineData(0x20105500u)]
    public void InitialGoOrTsodCannotBeClearedOrReplayed(uint command)
    {
        var pci = new FakeImcPci { CommandOverride = command };
        var bus = Make(pci);
        Assert.False(bus.TryAcquireBus(out _));
        bus.ReleaseBus();
        Assert.Empty(pci.Writes);
    }

    [Fact]
    public void TimeoutLatchesOriginalFailureAndReleaseDoesNotReadOrWrite()
    {
        var pci = new FakeImcPci { NeverCompletes = true };
        var bus = Make(pci);
        Assert.True(bus.TryAcquireBus(out _));
        Assert.Null(bus.ReadByteData(0x50, 0));
        Assert.Equal(SmbusStatus.Timeout, bus.LastStatus);
        string first = bus.LastError;
        int reads = pci.ReadCount, writes = pci.Writes.Count;
        pci.NeverCompletes = false;
        Assert.Null(bus.ReadByteData(0x51, 0));
        Assert.False(bus.SendByte(0x36, 0));
        Assert.False(bus.WriteByteData(0x50, 0, 0));
        bus.ReleaseBus();
        Assert.False(bus.TryAcquireBus(out _));
        Assert.Equal(reads, pci.ReadCount);
        Assert.Equal(writes, pci.Writes.Count);
        Assert.Equal(first, bus.LastError);
        Assert.Equal(SmbusStatus.Timeout, bus.LastStatus);
    }

    [Fact]
    public void ChangedOwnerBeforeTransactionIsNeverOverwritten()
    {
        var pci = new FakeImcPci();
        var bus = Make(pci);
        Assert.True(bus.TryAcquireBus(out _));
        pci.CommandOverride = 0x20006600;
        Assert.Null(bus.ReadByteData(0x50, 0));
        bus.ReleaseBus();
        Assert.Empty(pci.Writes);
        Assert.Equal(SmbusStatus.BusError, bus.LastStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreMustSucceedAndReadBackOrControllerStaysFaulted(bool reject)
    {
        var pci = new FakeImcPci { Modules = { [(0, 0x50)] = Image(0x44) } };
        var bus = Make(pci);
        Assert.True(bus.TryAcquireBus(out _));
        Assert.NotNull(bus.ReadByteData(0x50, 0));
        pci.FailRestore = reject;
        pci.IgnoreRestore = !reject;
        bus.ReleaseBus();
        Assert.Equal(SmbusStatus.IoUnavailable, bus.LastStatus);
        Assert.Contains("verification", bus.LastError);
        int writes = pci.Writes.Count;
        Assert.False(bus.TryAcquireBus(out _));
        bus.ReleaseBus();
        Assert.Equal(writes, pci.Writes.Count);
        Assert.All(pci.Writes.Skip(1), entry => Assert.Equal(0u, entry.Value & 0x80000u));
    }

    [Fact]
    public void ReleaseDoesNotClobberAChangedController()
    {
        var pci = new FakeImcPci { Modules = { [(0, 0x50)] = Image(0x44) } };
        var bus = Make(pci);
        Assert.True(bus.TryAcquireBus(out _));
        Assert.NotNull(bus.ReadByteData(0x50, 0));
        int writes = pci.Writes.Count;
        pci.CommandOverride = 0x20006600;
        bus.ReleaseBus();
        Assert.Equal(writes, pci.Writes.Count);
        Assert.Equal(SmbusStatus.BusError, bus.LastStatus);
    }

    [Fact]
    public void AmbiguousErrorLatchesFailureRatherThanClaimingEmptySlot()
    {
        var pci = new FakeImcPci { ErrorOnTransfer = true };
        var bus = Make(pci);
        Assert.True(bus.TryAcquireBus(out _));
        Assert.Null(bus.ReadByteData(0x50, 0));
        Assert.Equal(SmbusStatus.TransactionFailed, bus.LastStatus);
        int writes = pci.Writes.Count;
        Assert.Null(bus.ReadByteData(0x51, 0));
        bus.ReleaseBus();
        Assert.Equal(writes, pci.Writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IoExceptionsLatchFailureAndCannotTriggerCleanupTransactions(bool failWrite)
    {
        var pci = new FakeImcPci();
        var bus = Make(pci);
        Assert.True(bus.TryAcquireBus(out _));
        pci.ThrowOnWrite = failWrite;
        pci.ThrowOnRead = !failWrite;
        Assert.Null(bus.ReadByteData(0x50, 0));
        Assert.Equal(SmbusStatus.IoUnavailable, bus.LastStatus);
        int reads = pci.ReadCount;
        pci.ThrowOnRead = pci.ThrowOnWrite = false;
        bus.ReleaseBus();
        Assert.Null(bus.ReadByteData(0x51, 0));
        Assert.Equal(reads, pci.ReadCount);
        Assert.Empty(pci.Writes);
    }

    // ---- 探測 ----

    [Fact]
    public void 找出兩組控制器()
    {
        var pci = new FakeImcPci();
        var found = ImcSmbusDiscovery.Find(pci, [Target], out var note);

        Assert.Equal(2, found.Count);
        Assert.All(found, f => Assert.Equal(FakeImcPci.Bus, f.Bus));
        Assert.All(found, f => Assert.Equal(FakeImcPci.Dev, f.Device));
        Assert.Equal([0, 1], found.Select(f => f.Segment));
        Assert.Contains("16:1E.5", note);
        Assert.Equal(7, pci.ReadCount); // 一次裝置 ID + 每組三個暫存器；不掃其他 BDF。

        foreach (var (address, bus, device, function) in new[]
        {
            (0x001E0005u, (byte)0x16, (byte)0x1E, (byte)5),
            (0x00000000u, (byte)0x00, (byte)0x00, (byte)0),
            (0x001F0007u, (byte)0xFF, (byte)0x1F, (byte)7),
        })
        {
            Assert.True(WindowsPciFunctionLocationSource.TryDecodeAddress(bus, address, out var decoded));
            Assert.Equal(new PciFunctionAddress(bus, device, function), decoded);
        }
        Assert.True(WindowsPciFunctionLocationSource.TryDecodeDword([0x05, 0x00, 0x1E, 0x00], out uint value));
        Assert.Equal(0x001E0005u, value);
        Assert.False(WindowsPciFunctionLocationSource.TryDecodeDword([0x05, 0x00, 0x1E], out _));
    }

    [Fact]
    public void 只有一組有效時就只回一組()
        => Assert.Single(ImcSmbusDiscovery.Find(new FakeImcPci { OnlyOneSegment = true }, [Target], out _));

    [Fact]
    public void 找不到裝置時回空清單並說明()
    {
        var pci = new FakeImcPci { DeviceMissing = true };
        Assert.Empty(ImcSmbusDiscovery.Find(pci, [Target], out var note));
        Assert.Contains("找不到有效", note);
        Assert.Equal(1, pci.ReadCount);

        foreach (uint address in new[] { 0x00200000u, 0x00000008u, 0xFFFF_FFFFu })
            Assert.False(WindowsPciFunctionLocationSource.TryDecodeAddress(0, address, out _));
        Assert.Empty(ImcSmbusDiscovery.Find(pci, [], out note));
        Assert.Contains("略過全 PCI 空間探測", note);
        Assert.Equal(1, pci.ReadCount);
    }

    // ---- 交易 ----

    [Fact]
    public void 正常讀取回傳DAT暫存器的低位元組()
    {
        var pci = new FakeImcPci { Modules = { [(0, 0x50)] = Image(0xA0) } };
        var bus = Make(pci);

        Assert.True(bus.TryAcquireBus(out var reason), reason);
        Assert.Equal((byte)(0xA0 ^ 0x0C), bus.ReadByteData(0x50, 0x0C));
        bus.ReleaseBus();
    }

    [Fact]
    public void 兩組控制器各自獨立()
    {
        var pci = new FakeImcPci { Modules = { [(0, 0x50)] = Image(0x10), [(1, 0x50)] = Image(0x20) } };

        var seg0 = Make(pci, 0);
        Assert.True(seg0.TryAcquireBus(out _));
        Assert.Equal((byte)(0x10 ^ 5), seg0.ReadByteData(0x50, 5));
        seg0.ReleaseBus();

        var seg1 = Make(pci, 1);
        Assert.True(seg1.TryAcquireBus(out _));
        Assert.Equal((byte)(0x20 ^ 5), seg1.ReadByteData(0x50, 5));
        seg1.ReleaseBus();
    }

    [Fact]
    public void 位址上沒有裝置時是讀不到_不是回0()
    {
        var pci = new FakeImcPci { Modules = { [(0, 0x50)] = Image(0x77) } };
        var bus = Make(pci);
        Assert.True(bus.TryAcquireBus(out _));

        Assert.Equal((byte)0x77, bus.ReadByteData(0x50, 0x00));
        Assert.Null(bus.ReadByteData(0x53, 0x00));
        Assert.Equal(SmbusStatus.TransactionFailed, bus.LastStatus);
    }

    [Fact]
    public void 切頁走的是SelPtr寫入_並且真的換了半頁()
    {
        var image = Image(0x33);
        var pci = new FakeImcPci { Modules = { [(0, 0x50)] = image } };
        var bus = Make(pci);
        Assert.True(bus.TryAcquireBus(out _));

        Assert.Equal(image[0x40], bus.ReadByteData(0x50, 0x40));
        Assert.True(bus.SendByte(SpdReader.PageSelect1, 0));
        Assert.Equal(1, pci.Page);
        Assert.Equal(image[0x140], bus.ReadByteData(0x50, 0x40));
        Assert.True(bus.SendByte(SpdReader.PageSelect0, 0));
        Assert.Equal(0, pci.Page);
    }

    // ---- 讓韌體讓路：TSOD 輪詢 ----

    [Fact]
    public void 韌體輪詢活動時拒絕取得且不寫入()
    {
        var pci = new FakeImcPci { TsodPollingActive = true, Modules = { [(0, 0x50)] = Image(0x44) } };
        uint before = pci.Read(FakeImcPci.Bus, FakeImcPci.Dev, FakeImcPci.Fn, 0x9C)!.Value;
        var bus = Make(pci);

        Assert.False(bus.TryAcquireBus(out var reason));
        Assert.Contains("firmware-owned", reason);
        bus.ReleaseBus();
        Assert.Empty(pci.Writes);
        Assert.Equal(before, pci.Read(FakeImcPci.Bus, FakeImcPci.Dev, FakeImcPci.Fn, 0x9C)!.Value);
    }

    [Fact]
    public void 沒在輪詢時也一樣把命令暫存器還原()
    {
        var pci = new FakeImcPci { Modules = { [(0, 0x50)] = Image(0x55) } };
        uint before = pci.Read(FakeImcPci.Bus, FakeImcPci.Dev, FakeImcPci.Fn, 0x9C)!.Value;
        var bus = Make(pci);

        Assert.True(bus.TryAcquireBus(out _));
        bus.ReadByteData(0x50, 0x00);
        bus.ReleaseBus();

        Assert.Equal(before, pci.Read(FakeImcPci.Bus, FakeImcPci.Dev, FakeImcPci.Fn, 0x9C)!.Value);
    }

    // ---- 失敗路徑 ----

    [Fact]
    public void 交易不完成就逾時()
    {
        var bus = Make(new FakeImcPci { NeverCompletes = true });
        Assert.True(bus.TryAcquireBus(out _));

        Assert.Null(bus.ReadByteData(0x50, 0x00));
        Assert.Equal(SmbusStatus.Timeout, bus.LastStatus);
        Assert.Contains("逾時", bus.LastError);
    }

    [Fact]
    public void 一直忙也算逾時()
    {
        var bus = Make(new FakeImcPci { BusyForever = true });
        Assert.True(bus.TryAcquireBus(out _));

        Assert.Null(bus.ReadByteData(0x50, 0x00));
        Assert.Equal(SmbusStatus.Timeout, bus.LastStatus);
    }

    [Fact]
    public void 控制器回報錯誤位時回null()
    {
        var bus = Make(new FakeImcPci { ErrorOnTransfer = true });
        Assert.True(bus.TryAcquireBus(out _));

        Assert.Null(bus.ReadByteData(0x50, 0x00));
        Assert.Equal(SmbusStatus.TransactionFailed, bus.LastStatus);
    }

    [Fact]
    public void 設定空間讀不到時連匯流排都取不到()
    {
        var bus = Make(new FakeImcPci { AccessGone = true });

        Assert.False(bus.TryAcquireBus(out var reason));
        Assert.Contains("Cannot verify", reason);
    }

    [Fact]
    public void AcquisitionDoesNotProbeWritePermissions()
    {
        var pci = new FakeImcPci { WritesRejected = true };
        var bus = Make(pci);
        Assert.True(bus.TryAcquireBus(out _));
        Assert.Null(bus.ReadByteData(0x50, 0));
        Assert.Equal(SmbusStatus.IoUnavailable, bus.LastStatus);
        Assert.Empty(pci.Writes);
    }

    [Fact]
    public void 沒取得匯流排就讀取是程式錯誤()
    {
        var bus = Make(new FakeImcPci());
        Assert.Throws<InvalidOperationException>(() => bus.ReadByteData(0x50, 0x00));
    }

    // ---- 白名單：與 PCH 那條共用同一份，這裡再釘一次 ----

    [Theory]
    [InlineData((byte)0x36)]
    [InlineData((byte)0x4F)]
    [InlineData((byte)0x58)]
    public void 其餘位址一律不准讀(byte slave)
    {
        var bus = Make(new FakeImcPci());
        Assert.True(bus.TryAcquireBus(out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => bus.ReadByteData(slave, 0x00));
    }

    /// <summary>0x31／0x33／0x34／0x35 是 SPD 的寫入保護指令，0x50–0x57 是 EEPROM 資料區。</summary>
    [Theory]
    [InlineData((byte)0x31)]
    [InlineData((byte)0x33)]
    [InlineData((byte)0x34)]
    [InlineData((byte)0x35)]
    [InlineData((byte)0x50)]
    [InlineData((byte)0x57)]
    public void 寫入保護指令與EEPROM資料區永遠不准寫(byte slave)
    {
        var pci = new FakeImcPci();
        var bus = Make(pci);
        Assert.True(bus.TryAcquireBus(out _));
        int writesBefore = pci.Writes.Count;

        Assert.Throws<ArgumentOutOfRangeException>(() => bus.SendByte(slave, 0x00));
        Assert.Equal(writesBefore, pci.Writes.Count);      // 連命令都沒送出去
    }

    // ---- 與 SpdReader 串起來 ----

    [Fact]
    public void 整條讀取在iMC路徑上也走得通()
    {
        var image = Image(0x9C);
        var pci = new FakeImcPci { Modules = { [(0, 0x50)] = image, [(0, 0x52)] = Image(0x6B) } };
        for (byte address = 0x50; address <= 0x57; address++)
            if (!pci.Modules.ContainsKey((0, address))) pci.Modules[(0, address)] = Image(address);
        var bus = Make(pci);
        Assert.True(bus.TryAcquireBus(out _));

        var scan = SpdReader.ReadAll(bus);
        bus.ReleaseBus();

        Assert.Equal(image, scan.Slots.Single(s => s.Address == 0x50).Raw);
        Assert.Equal(SpdKind.Ddr4, scan.Slots.Single(s => s.Address == 0x52).Kind);
        Assert.Equal(8, scan.Slots.Count(s => s.Kind == SpdKind.Ddr4));
        Assert.Equal(0, pci.Page);                          // 讀完頁要復位
    }
}
