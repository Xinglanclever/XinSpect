using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// SPD 讀取器：從 SMBus 上把每一條記憶體的 SPD 原始位元組取回來。
/// </summary>
/// <remarks>
/// <para>
/// 這裡最要緊的三件事，都是「不要生出假資料」：
/// </para>
/// <para>
/// ① <b>全 0 或全 0xFF 一律判讀不到。</b>設計文件裡那個「旋轉速率 0＝SSD」的陷阱，
/// 在 SPD 上的等價物是「製造週 0＝這條是 2000 年第 0 週做的」。不轉送 SMBus 指令的
/// USB 外接盒與某些多工匯流排會回一整片 0 或 F，而且是帶 ACK 回的。
/// </para>
/// <para>
/// ② <b>DDR5 明說不支援。</b>SPD5 hub 的協定與 DDR4 不同，而本機沒有 DDR5 硬體可以拿真實
/// 位元組驗證。照專案規矩（1.9.1-B1 的 NVMe 位移錯誤就是合成資料把 bug 鎖死），沒有真實
/// 位元組就不寫解碼器。
/// </para>
/// <para>
/// ③ <b>讀完必須把頁復位回 0。</b>DDR4 的 SPD 超過 256 位元組要切頁，而切頁是<i>模組上的
/// 狀態</i>：留在第 1 頁走人，下一個讀 SPD 的程式（CPU-Z、BIOS 的 POST）在偏移 0 讀到的
/// 會是後半頁的資料。這是本檔唯一會留下副作用的地方，所以有專門的測試盯著。
/// </para>
/// </remarks>
public class SpdReaderTests
{
    private static SmbusController Bus(FakeSmbusIo io)
    {
        var bus = new SmbusController(io, FakeSmbusIo.Base, transactionTimeoutMs: 30);
        Assert.True(bus.TryAcquireBus(out var reason), reason);
        return bus;
    }

    /// <summary>造一份可辨識上下半頁的合成 DDR4 映像。合成資料只用來造行為與邊界，不用來驗位移。</summary>
    private static byte[] Ddr4Image(byte seed = 0xA0)
    {
        var img = new byte[SpdReader.Ddr4Size];
        for (int i = 0; i < 256; i++) img[i] = (byte)(seed ^ i);
        for (int i = 0; i < 256; i++) img[256 + i] = (byte)(seed ^ 0x5A ^ i);
        img[2] = SpdReader.Ddr4TypeCode;
        return img;
    }

    [Fact]
    public void 讀滿512位元組_上下半頁都要對()
    {
        var image = Ddr4Image();
        var io = new FakeSmbusIo { Modules = { [0x50] = image } };
        var slots = SpdReader.ReadAll(Bus(io)).Slots;

        var slot = Assert.Single(slots, s => s.Address == 0x50);
        Assert.Equal(SpdKind.Ddr4, slot.Kind);
        Assert.Equal(image, slot.Raw);
    }

    [Fact]
    public void 讀完要把頁復位回0_否則下一個讀SPD的程式會拿到後半頁()
    {
        var io = new FakeSmbusIo { Modules = { [0x50] = Ddr4Image() } };
        _ = SpdReader.ReadAll(Bus(io)).Slots;

        Assert.Equal(0, io.Page);
    }

    [Fact]
    public void 四條模組逐條獨立讀出()
    {
        var io = new FakeSmbusIo
        {
            Modules =
            {
                [0x50] = Ddr4Image(0x10), [0x51] = Ddr4Image(0x20),
                [0x54] = Ddr4Image(0x30), [0x55] = Ddr4Image(0x40),
            },
        };
        var slots = SpdReader.ReadAll(Bus(io)).Slots;

        Assert.Equal(4, slots.Count(s => s.Kind == SpdKind.Ddr4));
        Assert.Equal(Ddr4Image(0x30), slots.Single(s => s.Address == 0x54).Raw);
    }

    [Fact]
    public void 空插槽不是錯誤_也不該留下警告噪音()
    {
        var io = new FakeSmbusIo { Modules = { [0x50] = Ddr4Image() } };
        var slots = SpdReader.ReadAll(Bus(io)).Slots;

        Assert.Equal(7, slots.Count(s => s.Kind == SpdKind.Empty));
        Assert.All(slots.Where(s => s.Kind == SpdKind.Empty), s => Assert.Equal("", s.Note));
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0xFF)]
    public void 型別碼是全0或全F時判讀不到_不是未知型別(byte code)
    {
        var image = Ddr4Image();
        image[2] = code;
        var io = new FakeSmbusIo { Modules = { [0x50] = image } };

        var slot = Assert.Single(SpdReader.ReadAll(Bus(io)).Slots, s => s.Address == 0x50);
        Assert.Equal(SpdKind.Unreadable, slot.Kind);
        Assert.Null(slot.Raw);
        Assert.Contains("讀不到", slot.Note);
    }

    [Fact]
    public void DDR5要明說不支援而不是硬解()
    {
        var image = Ddr4Image();
        image[2] = SpdReader.Ddr5TypeCode;
        var io = new FakeSmbusIo { Modules = { [0x50] = image } };

        var slot = Assert.Single(SpdReader.ReadAll(Bus(io)).Slots, s => s.Address == 0x50);
        Assert.Equal(SpdKind.Ddr5, slot.Kind);
        Assert.Null(slot.Raw);
        Assert.Contains("DDR5", slot.Note);
    }

    [Fact]
    public void 未知型別碼不解讀()
    {
        var image = Ddr4Image();
        image[2] = 0x0B;                     // DDR3
        var io = new FakeSmbusIo { Modules = { [0x50] = image } };

        var slot = Assert.Single(SpdReader.ReadAll(Bus(io)).Slots, s => s.Address == 0x50);
        Assert.Equal(SpdKind.Unknown, slot.Kind);
        Assert.Contains("0x0B", slot.Note);
    }

    [Fact]
    public void 讀到一半失敗時整條放棄_並指出是哪一個位移()
    {
        int calls = 0;
        var image = Ddr4Image();
        var io = new FakeSmbusIo
        {
            Respond = (slave, cmd) =>
            {
                if (slave != 0x50) return null;
                if (cmd == 2 && calls++ == 0) return SpdReader.Ddr4TypeCode;   // 型別碼先給
                return cmd < 0x40 ? image[cmd] : null;                          // 0x40 之後裝置不回應
            },
        };

        var slot = Assert.Single(SpdReader.ReadAll(Bus(io)).Slots, s => s.Address == 0x50);
        Assert.Equal(SpdKind.Unreadable, slot.Kind);
        Assert.Null(slot.Raw);
        Assert.Contains("0x40", slot.Note);
    }

    [Fact]
    public void 一條讀不到不得影響其餘各條()
    {
        var good = Ddr4Image(0x77);
        var io = new FakeSmbusIo
        {
            Respond = (slave, cmd) => slave switch
            {
                0x50 => cmd == 2 ? SpdReader.Ddr4TypeCode : (cmd < 0x10 ? good[cmd] : null),
                0x51 => good[cmd],
                _ => null,
            },
        };
        var slots = SpdReader.ReadAll(Bus(io)).Slots;

        Assert.Equal(SpdKind.Unreadable, slots.Single(s => s.Address == 0x50).Kind);
        Assert.Equal(SpdKind.Ddr4, slots.Single(s => s.Address == 0x51).Kind);
    }

    [Fact]
    public void 全部都是0xFF的映像判讀不到()
    {
        var io = new FakeSmbusIo { Respond = (slave, _) => slave == 0x50 ? (byte)0xFF : null };

        var slot = Assert.Single(SpdReader.ReadAll(Bus(io)).Slots, s => s.Address == 0x50);
        Assert.Equal(SpdKind.Unreadable, slot.Kind);
    }

    /// <summary>
    /// 這一條是實機跑出來的：本機（X299）的 DIMM SPD 不在 PCH 的 SMBus 上，於是八個位址
    /// 各自回了一句一模一樣的「無法選擇 SPD 頁」，並且都被歸成「有裝置但讀不到」——
    /// 那是三重錯誤：訊息重複八次、把空匯流排說成故障、還把它算成八筆發現。
    /// 切頁裝置沒回應是<b>匯流排層級</b>的結論，只該講一次。
    /// </summary>
    [Fact]
    public void 切頁裝置沒回應時是這條匯流排上沒有SPD_不是八筆讀不到()
    {
        var io = new FakeSmbusIo { NoPageSelectDevice = true };
        var scan = SpdReader.ReadAll(Bus(io));

        Assert.All(scan.Slots, s => Assert.Equal(SpdKind.Empty, s.Kind));
        Assert.All(scan.Slots, s => Assert.Equal("", s.Note));
        Assert.False(scan.AnyPresent);
        Assert.Contains("沒有任何 DDR4 SPD", scan.BusNote);
        Assert.Contains("HEDT", scan.BusNote);
    }

    [Fact]
    public void 讀得到的時候不留匯流排層級的雜訊()
    {
        var io = new FakeSmbusIo { Modules = { [0x50] = Ddr4Image() } };

        Assert.Equal("", SpdReader.ReadAll(Bus(io)).BusNote);
    }

    [Fact]
    public void 掃過的位址就是SPD的那八個()
    {
        var io = new FakeSmbusIo();
        var slots = SpdReader.ReadAll(Bus(io)).Slots;

        Assert.Equal(8, slots.Count);
        Assert.Equal(Enumerable.Range(0x50, 8).Select(a => (byte)a), slots.Select(s => s.Address));
    }
}
