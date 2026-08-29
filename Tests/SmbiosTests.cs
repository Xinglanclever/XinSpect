using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>SMBIOS 表解析器與解碼：以合成位元組驗證，位移以 dmidecode 3.x 為準。</summary>
public class SmbiosTests
{
    /// <summary>組一段 Type 17 結構（長度 0x1C）＋字串區，接一個 Type 127 結尾。</summary>
    private static byte[] BuildType17Table()
    {
        var data = new byte[0x1C];
        data[0] = 17; data[1] = 0x1C; data[2] = 0x00; data[3] = 0x11;   // handle 0x1100
        data[0x08] = 64; data[0x09] = 0;                                 // Total Width 64 bit
        data[0x0A] = 64; data[0x0B] = 0;                                 // Data Width 64 bit
        data[0x0C] = 0x00; data[0x0D] = 0xC0;                            // Size = 0xC000（bit15=1 → 0x4000 = 16384 MB）
        data[0x0E] = 0x08;                                               // Form Factor DIMM
        data[0x10] = 1; data[0x11] = 2;                                  // Locator / Bank 字串索引
        data[0x12] = 0x1A;                                               // DDR4
        data[0x15] = 0xA0; data[0x16] = 0x0C;                            // Speed = 0x0CA0 = 3232 MT/s
        data[0x17] = 3; data[0x18] = 4; data[0x1A] = 5;                  // 製造商／序號／型號字串索引
        data[0x1B] = 0x02;                                               // Rank 2

        var table = new List<byte>(data);
        void AddString(string s) { table.AddRange(System.Text.Encoding.ASCII.GetBytes(s)); table.Add(0); }
        AddString("ChannelA-DIMM0");
        AddString("BANK 0");
        AddString("Kingston");
        AddString("ABC123456");
        AddString("F4-3200C16-8GIS");
        table.Add(0);                       // 字串區以雙 NULL 結束
        table.Add(127); table.Add(4); table.Add(0xFF); table.Add(0xFE);   // End-of-Table
        return table.ToArray();
    }

    [Fact]
    public void 解析器切出結構並還原字串區()
    {
        var structs = SmbiosParser.Parse(BuildType17Table());
        Assert.Equal(1, structs.Count);    // Type 127 為表尾，不列入
        var s = structs[0];
        Assert.Equal(17, s.Type);
        Assert.Equal(0x1100, s.Handle);
        Assert.Equal(5, s.Strings.Length);
        Assert.Equal("ChannelA-DIMM0", s.Strings[0]);
    }

    [Fact]
    public void 解析器_無字串結構以雙NULL立即結束()
    {
        // Type 2（主機板）len=0x08、無字串 → 格式區後直接兩個 0
        var table = new byte[] { 2, 0x08, 0x01, 0x00, 1, 2, 3, 4, 0, 0, 127, 4, 0xFF, 0xFE };
        var structs = SmbiosParser.Parse(table);
        Assert.Single(structs);
        Assert.Equal(2, structs[0].Type);
        Assert.Empty(structs[0].Strings);
    }

    [Fact]
    public void 字串索引0與越界代表無字串()
    {
        var s = new SmbiosStruct(17, 1, new byte[0x1C], new[] { "Only" });
        Assert.Null(s.GetString(0));
        Assert.Null(s.GetString(2));
        Assert.Equal("Only", s.GetString(1));
    }

    [Fact]
    public void Type17解碼_容量類型速度與識別資訊()
    {
        var structs = SmbiosParser.Parse(BuildType17Table());
        var row = SmbiosService.DecodeMemoryDeviceStruct(structs[0]);
        Assert.NotNull(row);
        Assert.Equal("ChannelA-DIMM0", row.Locator);
        Assert.Equal("BANK 0", row.Bank);
        Assert.Equal("16384 MB", row.Size);
        Assert.Equal("DDR4", row.Type);
        Assert.Equal("3232 MT/s", row.Speed);
        Assert.Equal("Kingston", row.Manufacturer);
        Assert.Equal("ABC123456", row.Serial);
        Assert.Equal("F4-3200C16-8GIS", row.Part);
        Assert.Equal("2", row.Rank);
        Assert.Equal("—", row.Configured);   // 結構長度未達 0x22 → 不猜
    }

    [Fact]
    public void Type17解碼_未安裝插槽回報未安裝而非空白()
    {
        var data = new byte[0x1C];
        data[0] = 17; data[1] = 0x1C;
        data[0x10] = 1;
        // Size = 0 → 未安裝
        var table = new List<byte>(data) { 0, 0 };   // 無字串
        var structs = SmbiosParser.Parse(table.ToArray());
        var row = SmbiosService.DecodeMemoryDeviceStruct(structs[0]);
        Assert.NotNull(row);
        Assert.Equal("未安裝", row.Size);
    }

    [Fact]
    public void Type9解碼_插槽類型寬度與使用狀態()
    {
        var data = new byte[0x0C];
        data[0] = 9; data[1] = 0x0C;
        data[0x04] = 1;              // Designation 字串 1
        data[0x05] = 0xAA;           // PCI Express x16
        data[0x06] = 0x0D;           // x16
        data[0x07] = 0x04;           // 使用中
        var table = new List<byte>(data);
        table.AddRange(System.Text.Encoding.ASCII.GetBytes("PCIE_1"));
        table.Add(0); table.Add(0);
        var structs = SmbiosParser.Parse(table.ToArray());
        var row = SmbiosService.DecodeSlotStruct(structs[0]);
        Assert.NotNull(row);
        Assert.Equal("PCIE_1", row.Designation);
        Assert.Equal("PCI Express x16", row.Type);
        Assert.Equal("x16", row.Width);
        Assert.Equal("使用中", row.Usage);
    }

    [Fact]
    public void 列舉解碼_不認得的值顯示原始位元組()
    {
        Assert.Equal("0x99", SmbiosService.SlotTypeName(0x99));
        Assert.Equal("0x30", SmbiosService.MemoryTypeName(0x30));
        Assert.Equal("DDR4", SmbiosService.MemoryTypeName(0x1A));
        Assert.Equal("DDR5", SmbiosService.MemoryTypeName(0x22));
        Assert.Equal("PCI Express x16", SmbiosService.SlotTypeName(0xAA));
        Assert.Equal("PCI Express 3 x16", SmbiosService.SlotTypeName(0xB6));
        Assert.Equal("可用", SmbiosService.SlotUsageName(0x03));
    }
}
