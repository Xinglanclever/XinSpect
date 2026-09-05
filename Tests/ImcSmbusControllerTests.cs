using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 處理器記憶體控制器（iMC）自己那條 SMBus 的探測與交易狀態機。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要這條路徑：HEDT 與伺服器平台（X299、C621、LGA3647、Threadripper）的 DIMM SPD
/// <b>不掛在 PCH 的 SMBus 上</b>。本機（i9-7980XE／X299）實測過——PCH 那條上八個 SPD 位址
/// 全部 NAK，連 DDR4 的切頁裝置都不回應，而整台機器只有一個 PCI 類別碼 0x0C05 的裝置。
/// SPD 在處理器 uncore 的 SMBus 區段上，共兩組（所以 CPU-Z 會把兩條不同的模組都標成「0x50」）。
/// </para>
/// <para>
/// 這條路徑比 PCH 那條危險一級，理由要寫清楚：控制器的命令暫存器<b>在 PCI 設定空間裡</b>，
/// 而設定空間裡有一大堆一寫就讓機器當場停住的東西。因此這組測試完全跑在假的設定空間上，
/// 而且實作只寫探測到的那三個位移、裝置位址仍受 <see cref="SpdBusAddresses"/> 白名單約束。
/// </para>
/// <para>
/// 還有一件必須靠韌體讓路的事：韌體會週期性輪詢 DIMM 上的溫度感測器（TSOD）。交易前要把
/// 那個輪詢關掉、交易後要原樣還原——沒還原的話主機板從此讀不到記憶體溫度，風扇曲線會跟著失準。
/// </para>
/// </remarks>
public class ImcSmbusControllerTests
{
    private static ImcSmbusController Make(FakeImcPci pci, int segment = 0)
        => new(pci, new ImcSmbusLocation(FakeImcPci.Bus, FakeImcPci.Dev, FakeImcPci.Fn, segment),
               transactionTimeoutMs: 30);

    private static byte[] Image(byte seed)
    {
        var img = new byte[SpdReader.Ddr4Size];
        for (int i = 0; i < 256; i++) img[i] = (byte)(seed ^ i);
        for (int i = 0; i < 256; i++) img[256 + i] = (byte)(seed ^ 0x5A ^ i);
        img[2] = SpdReader.Ddr4TypeCode;
        return img;
    }

    // ---- 探測 ----

    [Fact]
    public void 找出兩組控制器()
    {
        var found = ImcSmbusDiscovery.Find(new FakeImcPci(), out var note);

        Assert.Equal(2, found.Count);
        Assert.All(found, f => Assert.Equal(FakeImcPci.Bus, f.Bus));
        Assert.All(found, f => Assert.Equal(FakeImcPci.Dev, f.Device));
        Assert.Equal([0, 1], found.Select(f => f.Segment));
        Assert.Contains("16:1E.5", note);
    }

    [Fact]
    public void 只有一組有效時就只回一組()
        => Assert.Single(ImcSmbusDiscovery.Find(new FakeImcPci { OnlyOneSegment = true }, out _));

    [Fact]
    public void 找不到裝置時回空清單並說明()
    {
        Assert.Empty(ImcSmbusDiscovery.Find(new FakeImcPci { DeviceMissing = true }, out var note));
        Assert.Contains("找不到", note);
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
        Assert.Equal(SmbusStatus.NoDevice, bus.LastStatus);
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

    /// <summary>
    /// 韌體正在輪詢 DIMM 溫度感測器時，必須先讓它停下來才能發自己的交易；
    /// <b>而且結束時要原樣還原</b>——沒還原的話主機板從此讀不到記憶體溫度，風扇曲線跟著失準。
    /// </summary>
    [Fact]
    public void 韌體在輪詢溫度時先讓它停下_結束時原樣還原()
    {
        var pci = new FakeImcPci { TsodPollingActive = true, Modules = { [(0, 0x50)] = Image(0x44) } };
        uint before = pci.Read(FakeImcPci.Bus, FakeImcPci.Dev, FakeImcPci.Fn, 0x9C)!.Value;
        var bus = Make(pci);

        Assert.True(bus.TryAcquireBus(out var reason), reason);
        Assert.Equal((byte)0x44, bus.ReadByteData(0x50, 0x00));
        bus.ReleaseBus();

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
        Assert.Equal(SmbusStatus.NoDevice, bus.LastStatus);
    }

    [Fact]
    public void 設定空間讀不到時連匯流排都取不到()
    {
        var bus = Make(new FakeImcPci { AccessGone = true });

        Assert.False(bus.TryAcquireBus(out var reason));
        Assert.Contains("讀不到", reason);
    }

    [Fact]
    public void 設定空間不給寫時取不到匯流排_而不是硬幹()
    {
        var bus = Make(new FakeImcPci { WritesRejected = true });

        Assert.False(bus.TryAcquireBus(out var reason));
        Assert.Contains("寫", reason);
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
        var bus = Make(pci);
        Assert.True(bus.TryAcquireBus(out _));

        var scan = SpdReader.ReadAll(bus);
        bus.ReleaseBus();

        Assert.Equal(image, scan.Slots.Single(s => s.Address == 0x50).Raw);
        Assert.Equal(SpdKind.Ddr4, scan.Slots.Single(s => s.Address == 0x52).Kind);
        Assert.Equal(6, scan.Slots.Count(s => s.Kind == SpdKind.Empty));
        Assert.Equal(0, pci.Page);                          // 讀完頁要復位
    }
}
