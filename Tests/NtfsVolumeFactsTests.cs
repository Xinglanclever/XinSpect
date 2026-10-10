using System.Buffers.Binary;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// NTFS 卷冊解碼器金標（FS-006／FS-014）：合成 FSCTL 輸出＋已知答案逐欄比對，
/// 版面對準 winioctl.h 的 NTFS_VOLUME_DATA_BUFFER（96B）與 USN_JOURNAL_DATA_V0/V1。
/// </summary>
public class NtfsVolumeDecoderTests
{
    private static byte[] BuildVolumeData(long totalClusters = 1000, long freeClusters = 250,
        uint bytesPerFr = 1024, long mftLen = 1024 * 50)
    {
        byte[] b = new byte[96];
        int i = 0;
        void I64(long v) { BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(i, 8), v); i += 8; }
        void U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i, 4), v); i += 4; }
        I64(0x1122334455667788);  // VolumeSerialNumber
        I64(500_000_000);         // NumberSectors
        I64(totalClusters);
        I64(freeClusters);
        I64(0);                   // TotalReserved
        U32(512);                 // BytesPerSector
        U32(4096);                // BytesPerCluster
        U32(bytesPerFr);          // BytesPerFileRecordSegment
        U32(4);                   // ClustersPerFileRecordSegment
        I64(mftLen);              // MftValidDataLength
        I64(0x300000);            // MftStartLcn
        I64(0x400000);            // Mft2StartLcn
        I64(0x500000);            // MftZoneStart
        I64(0x600000);            // MftZoneEnd
        return b;
    }

    [Fact]
    public void 卷冊資料_96位元組逐欄比對()
    {
        var v = NtfsVolumeDecoder.ParseVolumeData(BuildVolumeData());
        Assert.NotNull(v);
        Assert.Equal(0x1122334455667788UL, v.VolumeSerialNumber);
        Assert.Equal(1000, v.TotalClusters);
        Assert.Equal(250, v.FreeClusters);
        Assert.Equal(512u, v.BytesPerSector);
        Assert.Equal(4096u, v.BytesPerCluster);
        Assert.Equal(1024u, v.BytesPerFileRecordSegment);
        Assert.Equal(1024 * 50, v.MftValidDataLength);
        Assert.Equal(0x600000, v.MftZoneEnd);
    }

    [Fact]
    public void 卷冊資料_不足96位元組_回null不猜()
    {
        Assert.Null(NtfsVolumeDecoder.ParseVolumeData(new byte[95]));
    }

    [Fact]
    public void 記錄數估算_長除以記錄大小()
    {
        var v = NtfsVolumeDecoder.ParseVolumeData(BuildVolumeData())!;
        Assert.Equal(50, NtfsVolumeDecoder.EstimateRecords(v));
        Assert.Null(NtfsVolumeDecoder.EstimateRecords(v with { BytesPerFileRecordSegment = 0 }));
    }

    internal static byte[] BuildUsn(int version)
    {
        int len = version == 0 ? 48 : 56;
        byte[] b = new byte[len];
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0, 8), 0xAABBCCDD11223344UL);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8, 8), 0x10000);   // FirstUsn
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16, 8), 0x90000);  // NextUsn
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24, 8), 0x08000);  // LowestValidUsn
        if (version == 1)
        {
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(32, 8), 0x95000); // MaxUsn（V1）
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(40, 8), 0x2000000); // MaximumSize
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(48, 8), 0x100000);  // AllocationDelta
        }
        else
        {
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(32, 8), 0x2000000); // MaximumSize（V0）
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(40, 8), 0x100000);  // AllocationDelta
        }
        return b;
    }

    [Fact]
    public void Usn_V0_48位元組逐欄比對()
    {
        var u = NtfsVolumeDecoder.ParseUsnJournal(BuildUsn(0));
        Assert.NotNull(u);
        Assert.Equal("V0", u.Version);
        Assert.Equal(0xAABBCCDD11223344UL, u.UsnJournalId);
        Assert.Equal(0x10000, u.FirstUsn);
        Assert.Equal(0x90000, u.NextUsn);
        Assert.Equal(0x2000000, u.MaximumSize);
        Assert.Equal(0x100000, u.AllocationDelta);
        Assert.Null(u.MaxUsn);
    }

    [Fact]
    public void Usn_V1_56位元組_欄位移正確()
    {
        var u = NtfsVolumeDecoder.ParseUsnJournal(BuildUsn(1));
        Assert.NotNull(u);
        Assert.Equal("V1", u.Version);
        Assert.Equal(0x95000, u.MaxUsn);
        Assert.Equal(0x2000000, u.MaximumSize);
        Assert.Equal(0x100000, u.AllocationDelta);
        Assert.Equal(0x10000, u.FirstUsn);
    }

    [Fact]
    public void Usn_不足48位元組_回null()
    {
        Assert.Null(NtfsVolumeDecoder.ParseUsnJournal(new byte[47]));
    }
}

/// <summary>假卷冊查詢接縫：固定回傳佇列或錯誤碼。</summary>
internal sealed class FakeVolumeProbe(params (byte[]? Data, uint Error)[] replies) : IVolumeProbe
{
    private int _i;
    public VolumeProbeResult Query(uint fsctl, uint minOutputBytes)
    {
        var (data, error) = replies[_i++];
        return new VolumeProbeResult(data, error);
    }
}

/// <summary>
/// NTFS 卷冊事實服務（FS-006／FS-014）三態契約：開檔失敗＝權限、日誌未啟用＝NotApplicable（如實）、
/// 刪除進行中＝ReadError、短緩衝＝不猜——「估算非解析」「存在性非內容」「卷序號不列」界線要寫進值裡。
/// </summary>
public class NtfsVolumeFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    private static byte[] BuildVolumeData()
    {
        byte[] b = new byte[96];
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16, 8), 1000);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24, 8), 250);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(48, 4), 1024); // BytesPerFileRecordSegment 在位移 48
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(56, 8), 1024 * 50); // MftValidDataLength
        return b;
    }

    private static byte[] BuildUsn() => NtfsVolumeDecoderTests.BuildUsn(1);

    [Fact]
    public void 正常_兩件事實_界線與估算標註()
    {
        var probe = new FakeVolumeProbe((BuildVolumeData(), 0u), (BuildUsn(), 0u));
        var facts = NtfsVolumeFactsService.Collect(At, probe);

        var mft = facts.Single(f => f.Key == "fs.mft.stat");
        Assert.Equal(50, mft.NumericValue);
        Assert.Contains("估算未逐條解析", mft.Value);
        Assert.Contains("卷序號刻意不列", mft.Value);
        Assert.Contains("系統碟", mft.Value);

        var usn = facts.Single(f => f.Key == "fs.usn.state");
        Assert.Contains("啟用（V1）", usn.Value);
        Assert.Contains("不是內容", usn.Value);
        Assert.Contains("FS-005", usn.Value);
    }

    [Fact]
    public void Mft開檔被拒_標權限不足()
    {
        var probe = new FakeVolumeProbe((null, 5u), (BuildUsn(), 0u));
        var facts = NtfsVolumeFactsService.Collect(At, probe);
        var mft = facts.Single(f => f.Key == "fs.mft.stat");
        Assert.Equal(FactAvailability.InsufficientPrivilege, mft.Availability);
        Assert.Contains("讀不到不等於沒有", mft.UnavailableReason);
    }

    [Fact]
    public void Mft短緩衝_如實不猜()
    {
        var probe = new FakeVolumeProbe((new byte[64], 0u), (BuildUsn(), 0u));
        var facts = NtfsVolumeFactsService.Collect(At, probe);
        var mft = facts.Single(f => f.Key == "fs.mft.stat");
        Assert.Equal(FactAvailability.ReadError, mft.Availability);
        Assert.Contains("不猜", mft.UnavailableReason);
    }

    [Fact]
    public void Usn未啟用_如實標不適用不是沒有變更()
    {
        var probe = new FakeVolumeProbe((BuildVolumeData(), 0u), (null, 1179u));
        var facts = NtfsVolumeFactsService.Collect(At, probe);
        var usn = facts.Single(f => f.Key == "fs.usn.state");
        Assert.Equal(FactAvailability.NotApplicable, usn.Availability);
        Assert.Contains("未啟用", usn.UnavailableReason);
        Assert.Contains("不是「沒有檔案變更」", usn.UnavailableReason);
    }

    [Fact]
    public void Usn刪除進行中_標過渡狀態()
    {
        var probe = new FakeVolumeProbe((BuildVolumeData(), 0u), (null, 1178u));
        var facts = NtfsVolumeFactsService.Collect(At, probe);
        var usn = facts.Single(f => f.Key == "fs.usn.state");
        Assert.Equal(FactAvailability.ReadError, usn.Availability);
        Assert.Contains("過渡狀態", usn.UnavailableReason);
    }

    [Fact]
    public void Usn被拒_標權限不足()
    {
        var probe = new FakeVolumeProbe((BuildVolumeData(), 0u), (null, 5u));
        var facts = NtfsVolumeFactsService.Collect(At, probe);
        var usn = facts.Single(f => f.Key == "fs.usn.state");
        Assert.Equal(FactAvailability.InsufficientPrivilege, usn.Availability);
    }
}
