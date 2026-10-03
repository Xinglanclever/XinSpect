using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// A18 帶外管理的契約：SEL 事件金標（依 IPMI 2.0 §31 手算）、FRU 板卡區（依 §34 手算）、
/// SDR 標頭、以及「無 BMC → NotApplicable／有 BMC → NotSupported」的三態分離。
/// 解碼器只吃位元組緩衝——本測試與解碼器都不碰匯流排。
/// </summary>
public class IpmiDecoderTests
{
    [Fact]
    public void SEL事件_金標向量_依IPMI2點0第31節手算()
    {
        // 手算向量：Record ID=0x0001、Type=0x02、Timestamp=0（未指定）、
        // Generator ID=0x0020（u16）、Msg Rev=0x04、Sensor Type=0x01（溫度）、Sensor Number=0x05、
        // Dir|Type=0x6F（assertion＋sensor-specific）、ED1-3=FF（無事件資料）。合計 16 bytes。
        var record = new byte[]
        {
            0x01, 0x00,             // Record ID（LE）
            0x02,                   // System Event Record
            0x00, 0x00, 0x00, 0x00, // Timestamp＝未指定
            0x20, 0x00,             // Generator ID（u16 LE）
            0x04,                   // Event Message Revision
            0x01,                   // Sensor Type＝溫度
            0x05,                   // Sensor Number
            0x6F,                   // Assertion｜sensor-specific
            0xFF, 0xFF, 0xFF,       // Event Data 1-3
        };

        var (ev, error) = IpmiDecoder.DecodeSelEvent(record);
        Assert.Null(error);
        Assert.NotNull(ev);
        Assert.Equal((ushort)1, ev!.RecordId);
        Assert.Null(ev.TimestampUtc);          // 0＝未指定，如實 null
        Assert.Equal("溫度", ev.SensorTypeName);
        Assert.Equal(0x01, ev.SensorTypeRaw);
        Assert.Equal(0x05, ev.SensorNumber);
        Assert.True(ev.Assertion);
        Assert.Equal("Sensor-specific 離散事件", ev.EventTypeName);
        Assert.True(ev.OemEventData);          // ED1 bits[7:6]＝11
    }

    [Fact]
    public void SEL事件_時間戳非零_依Unix秒公式解碼()
    {
        var record = new byte[16];
        record[2] = 0x02;
        uint seconds = 1731486925; // 2024-11-13T08:35:25Z
        record[3] = (byte)seconds; record[4] = (byte)(seconds >> 8);
        record[5] = (byte)(seconds >> 16); record[6] = (byte)(seconds >> 24);
        record[10] = 0x04; // 風扇

        var (ev, error) = IpmiDecoder.DecodeSelEvent(record);
        Assert.Null(error);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime, ev!.TimestampUtc!.Value.UtcDateTime);
        Assert.Equal("風扇", ev.SensorTypeName);
    }

    [Fact]
    public void SEL事件_過短與型別錯誤都如實拒解()
    {
        var (shortEv, shortErr) = IpmiDecoder.DecodeSelEvent(new byte[10]);
        Assert.Null(shortEv);
        Assert.Contains("過短", shortErr);

        var badType = new byte[16];
        badType[2] = 0xC0; // OEM record
        var (ev, typeErr) = IpmiDecoder.DecodeSelEvent(badType);
        Assert.Null(ev);
        Assert.Contains("0x02", typeErr);
    }

    [Fact]
    public void FRU板卡區_依第34節手算_日期與ASCII字串()
    {
        // Common Header：版本 0x01、Board 區偏移＝1（×8=8）、檢查和＝sum(0..6) 的二補數
        byte checksum = 0;
        var header = new byte[8];
        header[0] = 0x01; header[3] = 0x01;
        foreach (var b in header[..7]) checksum += b;
        header[7] = (byte)(0 - checksum);

        // Board Area（48 bytes）：版本 1、長度 6（×8）、語言 0、Mfg Date＝7824 分鐘（1996-01-06T10:24Z）、
        // TLV：0xC8「ACME CO.」、0xCB「ROG OMEGA X」、0xC6「RV1234」、0xC5「PN001」＋補零＋檢查和
        var area = new byte[48];
        area[0] = 0x01; area[1] = 0x06; area[2] = 0x00;
        area[3] = 0x90; area[4] = 0x1E; area[5] = 0x00; // 7824 分鐘（LSB first）
        int off = 6;
        void WriteTlv(string s)
        {
            area[off++] = (byte)(0xC0 | s.Length); // 8-bit ASCII
            System.Text.Encoding.ASCII.GetBytes(s).CopyTo(area, off);
            off += s.Length;
        }
        WriteTlv("ACME CO.");
        WriteTlv("ROG OMEGA X");
        WriteTlv("RV1234");
        WriteTlv("PN001");
        // off=40，補零至 47，檢查和於 47
        byte sum = 0;
        foreach (var b in area[..47]) sum += b;
        area[47] = (byte)(0 - sum);

        var fru = header.Concat(area).ToArray();
        var board = IpmiDecoder.DecodeFruBoardArea(fru);
        Assert.NotNull(board);
        Assert.Equal(new DateTimeOffset(1996, 1, 6, 10, 24, 0, TimeSpan.Zero), board!.ManufacturingDate);
        Assert.Equal("ACME CO.", board.Manufacturer);
        Assert.Equal("ROG OMEGA X", board.ProductName);
        Assert.Equal("RV1234", board.SerialNumber);
        Assert.Equal("PN001", board.PartNumber);
    }

    [Fact]
    public void FRU_無板卡區或過短_如實回null()
    {
        var noBoard = new byte[8];
        noBoard[0] = 0x01; noBoard[3] = 0x00; // Board 偏移＝0（無）
        Assert.Null(IpmiDecoder.DecodeFruBoardArea(noBoard));
        Assert.Null(IpmiDecoder.DecodeFruBoardArea(new byte[5]));
    }

    [Fact]
    public void SDR標頭_五欄位解碼()
    {
        var sdr = new byte[] { 0x01, 0x00, 0x51, 0x01, 0x30 };
        var hdr = IpmiDecoder.DecodeSdrCommonHeader(sdr);
        Assert.NotNull(hdr);
        Assert.Equal((ushort)1, hdr!.Value.RecordId);
        Assert.Equal(0x51, hdr.Value.Version);
        Assert.Equal(0x01, hdr.Value.RecordType);
        Assert.Equal(0x30, hdr.Value.RecordLength);
        Assert.Null(IpmiDecoder.DecodeSdrCommonHeader(new byte[4]));
    }
}
