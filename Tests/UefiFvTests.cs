using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// UEFI FV／FFS 解碼器的金標與契約：結構解析逐欄對得上規格、校驗和機制正確（FV 表頭 u16 總和為 0、
/// FFS 表頭扣 File/State 兩欄總和為 0）、抹除極性的狀態位元反相、損壞如實計數而非假裝沒看見、
/// 找不到 _FVH 時如實回空。服務層另測三態與 BIOS 區選取。
/// </summary>
public class UefiFvTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    private const string Fs2Guid = "8C8CE578-8A3D-4F1C-9935-896185C32DD3"; // EFI_FIRMWARE_FILE_SYSTEM2_GUID

    // ── 金標建構器：按 UEFI PI Spec 逐欄編碼 ────────────────────────────────

    /// <summary>造一個 USER_INTERFACE 區段（4 位元組頭 + UTF-16LE 名，含補零終止）。</summary>
    private static byte[] UiSection(string name)
    {
        var payload = Encoding.Unicode.GetBytes(name + "\0");
        return Section(0x15, payload);
    }

    private static byte[] Section(byte type, byte[] payload)
    {
        int size = 4 + payload.Length;
        var s = new byte[size];
        s[0] = (byte)size; s[1] = (byte)(size >> 8); s[2] = (byte)(size >> 16);
        s[3] = type;
        Array.Copy(payload, 0, s, 4, payload.Length);
        return s;
    }

    /// <summary>造一個 FFS 檔（FFS2，0x18 表頭 + 區段列，區段間按規格 4 位元組對齊）。state 預設 0x07（三個有效位元）。</summary>
    private static byte[] FfsFile(string guid, byte type, byte[][] sections, byte? state = null, byte? attributes = null)
    {
        int payloadLen = sections.Sum(s => (s.Length + 3) & ~3); // 區段起始 4 位元組對齊，尾段補齊也計入檔案大小
        int size = 0x18 + payloadLen;
        var f = new byte[size];
        new Guid(guid).ToByteArray().CopyTo(f, 0);
        f[0x12] = type;
        f[0x13] = attributes ?? 0;
        f[0x14] = (byte)size; f[0x15] = (byte)(size >> 8); f[0x16] = (byte)(size >> 16);
        f[0x17] = state ?? 0x07;
        // 表頭校驗和：File(0x11) 與 State(0x17) 視為 0 後總和為 0
        f[0x10] = ComputeFileHeaderChecksum(f);
        f[0x11] = 0xAA; // FFS_FIXED_CHECKSUM（未設 FFS_ATTRIB_CHECKSUM 時）
        int p = 0x18;
        foreach (var s in sections)
        {
            Array.Copy(s, 0, f, p, s.Length);
            p = (p + s.Length + 3) & ~3; // 區段之間 4 位元組對齊（PI Spec）
        }
        return f;
    }

    private static byte ComputeFileHeaderChecksum(byte[] header)
    {
        int sum = 0;
        for (int i = 0; i < header.Length; i++)
        {
            if (i == 0x10 || i == 0x11 || i == 0x17) continue;
            sum += header[i];
        }
        return (byte)(-(byte)sum & 0xFF);
    }

    /// <summary>把若干 FFS 檔包進一個 FV（FFS2 檔案系統 GUID、修訂 2、無抹除極性、含正確的 FV 表頭校驗和）。</summary>
    private static byte[] BuildFv(IReadOnlyList<byte[]> files, int revision = 2, uint extraAttributes = 0, bool padFile = false)
    {
        var body = new List<byte[]>();
        if (padFile)
        {
            // FFS_PAD：State 用 0x07（同有效位元）、型別 0xF0、無區段
            var pad = new byte[0x18];
            new Guid("00000000-0000-0000-0000-000000000000").ToByteArray().CopyTo(pad, 0);
            pad[0x12] = 0xF0;
            pad[0x14] = 0x18;
            pad[0x17] = 0x07;
            body.Add(pad);
        }
        foreach (var f in files) body.Add(f);
        int bodyLen = body.Sum(b => (b.Length + 7) & ~7);
        ulong fvLength = 0x48 + (ulong)bodyLen;

        var fv = new byte[fvLength];
        // ZeroVector 16 bytes = 0（保留預設）
        new Guid(Fs2Guid).ToByteArray().CopyTo(fv, 0x10);
        BitConverter.GetBytes(fvLength).CopyTo(fv, 0x20);
        BitConverter.GetBytes(UefiFv.FvhSignature).CopyTo(fv, 0x28);
        BitConverter.GetBytes(0x0004_0400u | extraAttributes).CopyTo(fv, 0x2C);
        // HeaderLength = 0x48：0x38 起一組 block map 項目（0x38–0x3F）＋終止項 (0,0)（0x40–0x47）
        BitConverter.GetBytes((ushort)0x48).CopyTo(fv, 0x30);
        BitConverter.GetBytes((ushort)0x100).CopyTo(fv, 0x38); // block map：0x100 塊 × 0x1000
        BitConverter.GetBytes(0x1000u).CopyTo(fv, 0x3C);
        fv[0x36] = 0;           // Reserved
        fv[0x37] = (byte)revision;
        // 0x40–0x47 已是預設的 0＝block map 終止項 (0,0)

        // FV 表頭校驗和：0x00–HeaderLength 內所有 u16 總和為 0
        ushort sum = 0;
        for (int i = 0; i < 0x48; i += 2) sum += BitConverter.ToUInt16(fv, i);
        BitConverter.GetBytes((ushort)(0 - sum)).CopyTo(fv, 0x32);

        int p = 0x48;
        foreach (var b in body)
        {
            Array.Copy(b, 0, fv, p, b.Length);
            p = (p + b.Length + 7) & ~7;
        }
        return fv;
    }

    // ── 解碼器契約 ──────────────────────────────────────────────────────────

    [Fact]
    public void 金標FV_逐欄解出_檔案與UI名()
    {
        var f1 = FfsFile("9B680FCE-AD6B-4F3A-B60B-F59899003443", 0x05, [UiSection("DxeCore")]); // DXE_CORE
        var f2 = FfsFile("3C1FE63E-8EED-41A8-9D93-9D7E4B1B1A10", 0x07, [UiSection("TestDriver"), Section(0x19, [1, 2, 3, 4])]);
        var fv = BuildFv([f1, f2]);

        var fvs = UefiFv.DecodeFvs(fv);

        var info = Assert.Single(fvs);
        Assert.Equal(Fs2Guid, info.FileSystemGuid);
        Assert.Equal((ulong)fv.Length, info.Length);
        Assert.Equal(2, info.Revision);
        Assert.Equal(2, info.FileCount);
        Assert.Equal(0, info.SkippedCount);
        Assert.True(info.HeaderChecksumOk);

        Assert.Equal("9B680FCE-AD6B-4F3A-B60B-F59899003443", info.Files[0].Guid);
        Assert.Equal("DXE_CORE", info.Files[0].TypeName);
        Assert.Equal("DxeCore", info.Files[0].UiName);
        Assert.True(info.Files[0].HeaderChecksumOk);
        Assert.Equal("DRIVER", info.Files[1].TypeName);
        Assert.Equal("TestDriver", info.Files[1].UiName);
        Assert.Equal(2, info.Files[1].Sections.Count);
        Assert.Equal("RAW", info.Files[1].Sections[1].TypeName);
    }

    [Fact]
    public void FV表頭校驗和弄壞_如實標示且結構照常解析()
    {
        var fv = BuildFv([FfsFile("9B680FCE-AD6B-4F3A-B60B-F59899003443", 0x05, [UiSection("A")])]);
        fv[0x15] ^= 0xFF; // FileSystemGuid 內一位元組：不影響任何結構欄位，但 u16 總和不再為 0
        var info = Assert.Single(UefiFv.DecodeFvs(fv));
        Assert.False(info.HeaderChecksumOk);
        Assert.Equal(1, info.FileCount); // 結構照常解析——校驗和不符如實標示，不拒收
    }

    [Fact]
    public void FFS表頭校驗和弄壞_逐檔如實計數()
    {
        var f1 = FfsFile("9B680FCE-AD6B-4F3A-B60B-F59899003443", 0x05, [UiSection("A")]);
        f1[4] ^= 0xFF; // 弄壞 Name GUID 內一位元組——表頭總和不為 0
        var fv = BuildFv([f1]);

        var info = Assert.Single(UefiFv.DecodeFvs(fv));
        Assert.Equal(1, info.FileCount);
        Assert.Equal(1, info.ChecksumMismatchCount);
    }

    [Fact]
    public void FFS3大檔_ExtendedSize要讀對()
    {
        // FFS3：Attributes bit0 = 1、Size=0xFFFFFF、ExtendedSize u64 在 0x18、表頭 0x20
        var sections = UiSection("BigDriver");
        int size = 0x20 + sections.Length;
        var f = new byte[size];
        new Guid("11111111-2222-3333-4444-555555555555").ToByteArray().CopyTo(f, 0);
        f[0x12] = 0x07;
        f[0x13] = 0x01; // FFS_ATTRIB_LARGE_FILE
        f[0x14] = 0xFF; f[0x15] = 0xFF; f[0x16] = 0xFF;
        f[0x17] = 0x07;
        BitConverter.GetBytes((ulong)size).CopyTo(f, 0x18);
        int sum = 0;
        for (int i = 0; i < 0x20; i++) if (i != 0x11 && i != 0x17) sum += f[i];
        f[0x10] = (byte)(-(byte)sum & 0xFF);
        f[0x11] = 0xAA;
        Array.Copy(sections, 0, f, 0x20, sections.Length);

        var info = Assert.Single(UefiFv.DecodeFvs(BuildFv([f])));
        var file = Assert.Single(info.Files);
        Assert.Equal((ulong)size, file.Size);
        Assert.Equal("BigDriver", file.UiName);
    }

    [Fact]
    public void 抹除極性1_狀態位元反相後仍要判出有效()
    {
        // NOR 快閃的常態：erased = 全 1，狀態位元以「清除」表示成立 → 有效檔的 State 是 ~0x07
        var f1 = FfsFile("9B680FCE-AD6B-4F3A-B60B-F59899003443", 0x05, [UiSection("A")], state: unchecked((byte)~0x07));
        var fv = BuildFv([f1], extraAttributes: 0x0000_0800); // EFI_FVB2_ERASE_POLARITY

        var info = Assert.Single(UefiFv.DecodeFvs(fv));
        Assert.Equal(1, info.FileCount);
        Assert.Equal(0, info.SkippedCount);
    }

    [Fact]
    public void Pad檔_不計為檔案()
    {
        var f1 = FfsFile("9B680FCE-AD6B-4F3A-B60B-F59899003443", 0x05, [UiSection("A")]);
        var info = Assert.Single(UefiFv.DecodeFvs(BuildFv([f1], padFile: true)));
        Assert.Equal(1, info.FileCount);
    }

    [Fact]
    public void 檔案大小越界_如實計數略過並停止()
    {
        var f1 = FfsFile("9B680FCE-AD6B-4F3A-B60B-F59899003443", 0x05, [UiSection("A")]);
        f1[0x14] = 0xFF; f1[0x15] = 0xFF; f1[0x16] = 0xFF; // Size 超出 FV（且無 LARGE_FILE 屬性）
        var fv = BuildFv([f1]);

        var info = Assert.Single(UefiFv.DecodeFvs(fv));
        Assert.Equal(0, info.FileCount);
        Assert.Equal(1, info.SkippedCount);
    }

    [Fact]
    public void 找不到FVH_回空清單_不是錯誤()
    {
        Assert.Empty(UefiFv.DecodeFvs(new byte[0x1000]));
    }

    [Fact]
    public void 兩個FV_依序解出索引正確()
    {
        var f1 = FfsFile("9B680FCE-AD6B-4F3A-B60B-F59899003443", 0x05, [UiSection("A")]);
        var fvA = BuildFv([f1]);
        var fvB = BuildFv([f1]);
        // FV 之後補 8 位元組對齊再放第二個 FV
        var gap = (8 - fvA.Length % 8) % 8;
        var both = new byte[fvA.Length + gap + fvB.Length];
        Array.Copy(fvA, 0, both, 0, fvA.Length);
        Array.Copy(fvB, 0, both, fvA.Length + gap, fvB.Length);

        var fvs = UefiFv.DecodeFvs(both);
        Assert.Equal(2, fvs.Count);
        Assert.Equal(0, fvs[0].Index);
        Assert.Equal((ulong)0, fvs[0].Offset);
        Assert.Equal(1, fvs[1].Index);
        Assert.Equal((ulong)(fvA.Length + gap), fvs[1].Offset);
    }
    [Fact]
    public void GUID_DEFINED區段_標出引擎名與資料位移_不解壓()
    {
        // GUID_DEFINED 區段：4 位元組頭 + GUID(16) + DataOffset(u16) + Attributes(u16) + 資料
        var guid = UefiFv.GuidLzmaCustomDecompress.ToByteArray();
        var payload = new byte[0x18 + 3];
        Array.Copy(guid, payload, 16);
        BitConverter.GetBytes((ushort)0x18).CopyTo(payload, 16); // DataOffset
        BitConverter.GetBytes((ushort)0).CopyTo(payload, 18);
        payload[0x18] = 0x5D; payload[0x19] = 0x00; payload[0x1A] = 0x00; // LZMA 屬性前幾位（不解壓，僅示意）
        var sect = Section(0x02, payload);
        var f1 = FfsFile("9B680FCE-AD6B-4F3A-B60B-F59899003443", 0x07, [sect]);

        var info = Assert.Single(UefiFv.DecodeFvs(BuildFv([f1])));
        var guidSect = Assert.Single(info.Files[0].Sections);
        Assert.Equal("GUID_DEFINED", guidSect.TypeName);
        Assert.NotNull(guidSect.Extra);
        Assert.Contains("LZMA", guidSect.Extra);
        Assert.Contains("不解壓", guidSect.Extra);
    }

    [Fact]
    public void 未收錄的型別代碼_回原始代碼不猜名()
    {
        Assert.Equal("未收錄 (0x77)", UefiFv.FileTypeName(0x77));
        Assert.Equal("未收錄 (0x99)", UefiFv.SectionTypeName(0x99));
        Assert.Equal("DRIVER", UefiFv.FileTypeName(0x07));
    }

    // ── 服務層：BIOS 區選取與三態 ──────────────────────────────────────────

    private const ulong FlashBase = 0xFFF00000;
    private const ulong SpiBar = 0xFED10000;

    // 描述符 0x000-0x00F、BIOS 0x010-0x02F（128 KiB）→ 快閃 192 KiB
    private static readonly uint[] Fregs =
    [
        SyntheticFixtures.EncodeFreg(0x000, 0x00F),
        SyntheticFixtures.EncodeFreg(0x010, 0x02F),
        0, 0, 0, 0,
    ];

    private static byte[] MakeFlash(byte[] biosFv)
    {
        var flash = new byte[0x30000]; // (0x02F + 1) × 4096
        Array.Copy(biosFv, 0, flash, 0x10000, biosFv.Length);
        return flash;
    }

    [Fact]
    public void BIOS區有FV_給出總數與逐FV摘要()
    {
        var fv = BuildFv([FfsFile("9B680FCE-AD6B-4F3A-B60B-F59899003443", 0x05, [UiSection("DxeCore")])]);
        var facts = UefiFvFactsService.Collect(new FakeSpiPci(), new FakeSpiFlashMmio(Fregs, MakeFlash(fv)), At);

        var count = facts.Single(x => x.Key == UefiFvFactsService.CountKey);
        Assert.Equal(FactAvailability.Present, count.Availability);
        Assert.Equal(1, count.NumericValue);
        Assert.Contains("1 個韌體磁碟區", count.Value);
        Assert.Contains("不構成對韌體真偽的判決", count.Value);

        var summary = Assert.Single(facts, x => x.Key == "ufv.fv.0.summary");
        Assert.Contains("DxeCore", summary.Value);
        Assert.Contains("DXE_CORE", summary.Value);
        Assert.Contains("GUID " + Fs2Guid, summary.Value);
    }

    [Fact]
    public void BIOS區沒有FV_如實回0並說明不猜()
    {
        var facts = UefiFvFactsService.Collect(new FakeSpiPci(), new FakeSpiFlashMmio(Fregs, new byte[0x30000]), At);
        var count = facts.Single(x => x.Key == UefiFvFactsService.CountKey);
        Assert.Equal(FactAvailability.Present, count.Availability);
        Assert.Equal(0, count.NumericValue);
        Assert.Contains("未找到", count.Value);
        Assert.Contains("不猜", count.Value);
    }

    [Fact]
    public void 全空FREG_如實標不適用()
    {
        uint[] empty = [0, 0, 0, 0, 0, 0];
        var fact = Assert.Single(UefiFvFactsService.Collect(new FakeSpiPci(), new FakeSpiFlashMmio(empty, new byte[0x1000]), At));
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
    }

    [Fact]
    public void 讀取中斷_如實標讀取失敗()
    {
        var fv = BuildFv([FfsFile("9B680FCE-AD6B-4F3A-B60B-F59899003443", 0x05, [UiSection("A")])]);
        var fact = Assert.Single(UefiFvFactsService.Collect(
            new FakeSpiPci(),
            new FakeSpiFlashMmio(Fregs, MakeFlash(fv), failAt: FlashBase + 0x18000), At));
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("讀取失敗", fact.UnavailableReason);
    }

    // 以下假件與 SpiEntropyServiceTests 同形（每檔私有、不共用）——IPciConfigReader／IMmioReader 的假後端。
    private sealed class FakeSpiPci : IPciConfigReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;

        public uint? ReadDword(byte bus, byte device, byte function, uint register) =>
            (device, function, register) switch
            {
                (0x1F, 5, 0x00) => 0x06C0_8086,
                (0x1F, 5, 0x10) => (uint)SpiBar,
                _ => 0xFFFF_FFFF,
            };
    }

    private sealed class FakeSpiFlashMmio(uint[] fregs, byte[] flash, ulong? failAt = null) : IMmioReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public string? LastFailReason => "測試假件：讀取被拒";

        public byte[]? ReadBlock(ulong address, int length)
        {
            if (failAt is { } at && address >= at) return null;

            if (address == SpiBar)
            {
                var block = new byte[0x88];
                for (int i = 0; i < fregs.Length; i++)
                    BitConverter.GetBytes(fregs[i]).CopyTo(block, 0x54 + i * 4);
                return block;
            }
            ulong mappedBase = 0x1_0000_0000UL - (ulong)flash.Length;
            if (address >= mappedBase && address - mappedBase < (ulong)flash.Length)
            {
                ulong off = address - mappedBase;
                if (off + (ulong)length > (ulong)flash.Length) return null;
                return flash[(int)off..(int)(off + (ulong)length)];
            }
            return null;
        }
    }
}
