using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// NTFS 開機磁區金標（U10 第三圈 FS-0001／FS-0002）：合成 512 位元組磁區＋已知答案逐欄比對，
/// 外加「驅動端與磁碟端不一致時要看得出來」的四種對帳情形。
/// </summary>
public class NtfsBootSectorFactsTests
{
    // ── 合成磁區 ────────────────────────────────────────────────────────

    private static byte[] Sector(
        string oem = "NTFS    ",
        ushort bytesPerSector = 512,
        byte sectorsPerCluster = 8,
        ulong totalSectors = 2_000_000,
        ulong mftLcn = 786_432,
        ulong mftMirrLcn = 125_000,
        sbyte clustersPerMftRecord = -10,
        sbyte clustersPerIndexBuffer = 0,
        ulong serial = 0x1122334455667788,
        bool endMarker = true)
    {
        var b = new byte[NtfsBootSectorDecoder.SectorBytes];
        b[0] = 0xEB; b[1] = 0x52; b[2] = 0x90; // x86 跳躍指令（不解）
        for (int i = 0; i < 8; i++) b[0x03 + i] = (byte)oem[i];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x0B, 2), bytesPerSector);
        b[0x0D] = sectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x0E, 2), 0);
        b[0x15] = 0xF8;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x18, 2), 63);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x1A, 2), 255);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x1C, 4), 63);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x24, 4), 0x00800080);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x28, 8), totalSectors);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x30, 8), mftLcn);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x38, 8), mftMirrLcn);
        b[0x40] = unchecked((byte)clustersPerMftRecord);
        b[0x41] = unchecked((byte)clustersPerIndexBuffer);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x48, 8), serial);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x1FE, 2), endMarker ? (ushort)0xAA55 : (ushort)0);
        return b;
    }

    private static NtfsVolumeData Driver(
        ulong serial = 0x1122334455667788,
        long mftStart = 786_432,
        long mft2Start = 125_000,
        uint bytesPerSector = 512,
        uint bytesPerCluster = 4096,
        long numberSectors = 2_000_000)
        => new(serial, numberSectors, 250_000, 62_500, bytesPerSector, bytesPerCluster, 1024, 0,
            1024 * 50, mftStart, mft2Start, 0x100, 0x200);

    private static BootSectorReadResult Read(byte[]? data, uint error = 0, int bytesRead = 512)
        => new(data, error, bytesRead);

    private static HardwareFact Bpb(IReadOnlyList<HardwareFact> facts) =>
        facts.Single(f => f.Key == NtfsBootSectorFactsService.BpbKey);

    private static HardwareFact Layout(IReadOnlyList<HardwareFact> facts) =>
        facts.Single(f => f.Key == NtfsBootSectorFactsService.MftLayoutKey);

    // ── 解碼器：逐欄 ────────────────────────────────────────────────────

    [Fact]
    public void 開機磁區_逐欄比對()
    {
        var b = NtfsBootSectorDecoder.Parse(Sector());
        Assert.NotNull(b);
        Assert.Equal("NTFS    ", b.OemId);
        Assert.True(b.OemIdValid);
        Assert.Equal(512, b.BytesPerSector);
        Assert.Equal(8, b.SectorsPerCluster);
        Assert.Equal(0xF8, b.MediaDescriptor);
        Assert.Equal(63, b.SectorsPerTrack);
        Assert.Equal(255, b.Heads);
        Assert.Equal(63u, b.HiddenSectors);
        Assert.Equal(2_000_000UL, b.TotalSectors);
        Assert.Equal(786_432UL, b.MftLcn);
        Assert.Equal(125_000UL, b.MftMirrLcn);
        Assert.Equal(0x1122334455667788UL, b.VolumeSerialNumber);
        Assert.True(b.EndMarkerValid);
    }

    [Fact]
    public void 開機磁區_不足512位元組回null不猜()
    {
        Assert.Null(NtfsBootSectorDecoder.Parse(new byte[511]));
    }

    [Fact]
    public void 結尾標記不對_要看得出來()
    {
        var b = NtfsBootSectorDecoder.Parse(Sector(endMarker: false))!;
        Assert.False(b.EndMarkerValid);
    }

    [Fact]
    public void OEM不是NTFS_辨識為非NTFS()
    {
        var b = NtfsBootSectorDecoder.Parse(Sector(oem: "EXFAT   "))!;
        Assert.False(b.OemIdValid);
    }

    [Fact]
    public void 每叢位元組_合法幾何才相乘()
    {
        Assert.Equal(4096u, NtfsBootSectorDecoder.Parse(Sector())!.BytesPerCluster);
        // 每叢 3 扇區不是 2 的冪 → 不乘
        Assert.Null(NtfsBootSectorDecoder.Parse(Sector(sectorsPerCluster: 3))!.BytesPerCluster);
        // 每扇區 1000 不是 2 的冪 → 不乘
        Assert.Null(NtfsBootSectorDecoder.Parse(Sector(bytesPerSector: 1000))!.BytesPerCluster);
    }

    [Fact]
    public void MFT記錄大小_負值是二位冪_正值是叢數()
    {
        // NTFS 號誌約定：-10 → 2^10 = 1024 B（本機實測值）
        Assert.Equal(1024L, NtfsBootSectorDecoder.Parse(Sector(clustersPerMftRecord: -10))!.BytesPerMftRecord);
        // 正值 → 叢數 × 每叢位元組（2 × 4096）
        Assert.Equal(8192L, NtfsBootSectorDecoder.Parse(Sector(clustersPerMftRecord: 2))!.BytesPerMftRecord);
        // 0 不宣告
        Assert.Null(NtfsBootSectorDecoder.Parse(Sector(clustersPerMftRecord: 0))!.BytesPerMftRecord);
        // 指數超出 9–16 → 不硬算
        Assert.Null(NtfsBootSectorDecoder.Parse(Sector(clustersPerMftRecord: -3))!.BytesPerMftRecord);
        Assert.Null(NtfsBootSectorDecoder.Parse(Sector(clustersPerMftRecord: -20))!.BytesPerMftRecord);
    }

    [Fact]
    public void 索引緩衝區_未宣告就是未宣告()
    {
        Assert.Null(NtfsBootSectorDecoder.Parse(Sector(clustersPerIndexBuffer: 0))!.BytesPerIndexBuffer);
        Assert.Equal(4096L, NtfsBootSectorDecoder.Parse(Sector(clustersPerIndexBuffer: 1))!.BytesPerIndexBuffer);
    }

    // ── 服務：正常路徑 ──────────────────────────────────────────────────

    [Fact]
    public void BPB事實_值裡要有逐欄與界線_數字是總扇區()
    {
        var f = Bpb(NtfsBootSectorFactsService.Collect(DateTimeOffset.UnixEpoch, Read(Sector()), Driver()));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Null(f.UnavailableReason);
        Assert.Equal(2_000_000d, f.NumericValue!.Value);
        Assert.Contains("OEM `NTFS    `", f.Value);
        Assert.Contains("每叢 8 扇區＝4096 B", f.Value);
        Assert.Contains("每 MFT 記錄 1024 B（號誌欄 -10）", f.Value);
        Assert.Contains("結尾標記 0xAA55（在）", f.Value);
        // 界線：備份開機磁區未讀、卷序號不列、不宣稱校驗規則
        Assert.Contains("備份開機磁區在卷尾，本版未讀", f.Value);
        Assert.Contains("卷序號刻意不列", f.Value);
        Assert.Contains("不宣稱", f.Value);
    }

    [Fact]
    public void BPB事實_幾何不合法時不乘只說算不出來()
    {
        var f = Bpb(NtfsBootSectorFactsService.Collect(DateTimeOffset.UnixEpoch,
            Read(Sector(sectorsPerCluster: 3)), null));
        Assert.Contains("不是合法幾何", f.Value);
        Assert.DoesNotContain("＝12 B", f.Value);
    }

    [Fact]
    public void MFT位置_兩來源一致時說一致()
    {
        var f = Layout(NtfsBootSectorFactsService.Collect(DateTimeOffset.UnixEpoch, Read(Sector()), Driver()));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("786,432／驅動 786,432（一致）", f.Value);
        Assert.Contains("125,000／驅動 125,000（一致）", f.Value);
        Assert.Contains("卷序號兩來源：一致（值不列）", f.Value);
    }

    [Fact]
    public void MFT位置_不一致要指名哪一邊差在哪()
    {
        var f = Layout(NtfsBootSectorFactsService.Collect(DateTimeOffset.UnixEpoch,
            Read(Sector()), Driver(mftStart: 800_000, mft2Start: 125_001)));
        Assert.Contains("**不一致**", f.Value);
        Assert.Contains("786,432／驅動 800,000", f.Value);
        Assert.Contains("125,000／驅動 125,001", f.Value);
    }

    [Fact]
    public void MFT位置_卷序號不同要說不一致但不印值()
    {
        var f = Layout(NtfsBootSectorFactsService.Collect(DateTimeOffset.UnixEpoch,
            Read(Sector(serial: 1)), Driver(serial: 2)));
        Assert.Contains("卷序號兩來源：**不一致**（值不列）", f.Value);
        Assert.DoesNotContain("0x0000000000000001", f.Value);
        Assert.DoesNotContain("1122334455667788", f.Value);
    }

    [Fact]
    public void MFT位置_起點在卷外要看得出來()
    {
        // 總扇區 100 × 512 B ÷ 4096 B/叢 = 12 叢；兩個起點都遠超
        var f = Layout(NtfsBootSectorFactsService.Collect(DateTimeOffset.UnixEpoch,
            Read(Sector(totalSectors: 100)), null));
        Assert.Contains("**在卷外**", f.Value);
    }

    [Fact]
    public void MFT位置_驅動端讀不到時不編一致也不編不一致()
    {
        var f = Layout(NtfsBootSectorFactsService.Collect(DateTimeOffset.UnixEpoch, Read(Sector()), null));
        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("單邊不叫對帳", f.UnavailableReason);
        Assert.DoesNotContain("（一致）", f.Value);
    }

    // ── 服務：非 Present ────────────────────────────────────────────────

    [Fact]
    public void 開機磁區打不開_兩把鍵都要有理由且不帶數字()
    {
        var facts = NtfsBootSectorFactsService.Collect(DateTimeOffset.UnixEpoch, Read(null, 5), Driver());
        Assert.Equal(2, facts.Count);
        Assert.All(facts, f =>
        {
            Assert.NotEqual(FactAvailability.Present, f.Availability);
            Assert.Null(f.NumericValue);
            Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason));
        });
        Assert.All(facts, f => Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability));
    }

    [Fact]
    public void 短讀與打不開要分開講()
    {
        var facts = NtfsBootSectorFactsService.Collect(DateTimeOffset.UnixEpoch, Read(null, 0, bytesRead: 200), null);
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.All(facts, f => Assert.Contains("短讀", f.UnavailableReason));
        Assert.All(facts, f => Assert.Contains("200", f.UnavailableReason));
    }

    [Fact]
    public void OEM不是NTFS_標不適用而不是讀取錯誤()
    {
        var facts = NtfsBootSectorFactsService.Collect(DateTimeOffset.UnixEpoch,
            Read(Sector(oem: "EXFAT   ")), Driver());
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.All(facts, f => Assert.Contains("EXFAT", f.UnavailableReason));
        Assert.All(facts, f => Assert.Contains("不是 `NTFS    `", f.UnavailableReason));
    }

    [Fact]
    public void 結尾標記不對_事實仍然是Present但值要說打折()
    {
        var f = Bpb(NtfsBootSectorFactsService.Collect(DateTimeOffset.UnixEpoch,
            Read(Sector(endMarker: false)), Driver()));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("**不是 0xAA55**", f.Value);
    }
}