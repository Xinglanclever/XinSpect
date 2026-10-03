namespace XinSpect;

/// <summary>IPMI SEL 系統事件記錄的解碼結果（純資料，不含任何 BMC 通訊）。</summary>
public sealed record IpmiSelEvent(
    ushort RecordId,
    byte RecordType,
    DateTimeOffset? TimestampUtc,   // 0x00000000＝未指定（如實 null）；其餘＝Unix 秒
    byte SensorTypeRaw,
    string SensorTypeName,          // 未收錄＝「Sensor Type 0x..（未收錄）」
    byte SensorNumber,
    bool Assertion,                 // true＝assertion（發生），false＝deassertion（解除）
    byte EventTypeRaw,
    string EventTypeName,           // 未收錄＝「Event Type 0x..（未收錄）」
    byte EventData1,
    byte EventData2,
    byte EventData3,
    bool OemEventData);             // EventData1 bits[7:6]＝11（OEM 自訂，內容不解碼）

/// <summary>IPMI SEL 解碼結果：事件或「無法解碼」的原因（截斷／型別未知）。</summary>
public sealed record IpmiSelDecode(IpmiSelEvent? Event, string? Error);

/// <summary>FRU 板卡區解碼結果。字串來自 FRU 的 ASCII 區；日期＝1996-01-01 起的分鐘數。</summary>
public sealed record IpmiFruBoard(
    DateTimeOffset? ManufacturingDate,
    string Manufacturer,
    string ProductName,
    string SerialNumber,
    string PartNumber);

/// <summary>IPMI/FRU/SDR 的純解碼器（V7 WP18／A18）。</summary>
public static class IpmiDecoder
{
    /// <summary>
    /// 解 SEL 系統事件記錄（16 位元組，Record Type 0x02）。
    /// <b>本解碼器只吃位元組緩衝，不碰匯流排</b>——KCS／SSIF／NCSI 通路不在本工具範圍（無 BMC 可驗證）。
    /// </summary>
    [SpecRef("IPMI 2.0 Specification §31（SEL Record Formats）：System Event Record 16 bytes——Record ID u16 LE、Record Type 0x02、Timestamp u32 LE（Unix 秒，0＝未指定）、Generator ID u16（slave addr／LUN）、Event Msg Rev、Sensor Type、Sensor Number、Event Dir|Type（bit7 方向）、Event Data 1-3")]
    public static IpmiSelDecode DecodeSelEvent(byte[] record)
    {
        if (record.Length < 16) return new(null, $"記錄過短（{record.Length} 位元組，SEL 系統事件記錄為 16）");
        byte type = record[2];
        if (type != 0x02) return new(null, $"Record Type 0x{type:X2} 不是系統事件記錄（0x02）——不解碼");

        uint ts = (uint)(record[3] | (record[4] << 8) | (record[5] << 16) | (record[6] << 24));
        DateTimeOffset? timestamp = ts == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(ts);
        byte sensorType = record[10];
        byte dirType = record[12];
        byte ed1 = record[13];
        return new(new IpmiSelEvent(
            (ushort)(record[0] | (record[1] << 8)),
            type,
            timestamp,
            sensorType,
            SensorTypeName(sensorType),
            record[11],
            (dirType & 0x80) == 0, // bit7＝0 assertion／1 deassertion
            (byte)(dirType & 0x7F),
            EventTypeName((byte)(dirType & 0x7F)),
            ed1,
            record[14],
            record[15],
            (ed1 & 0xC0) == 0xC0), null);
    }

    /// <summary>Sensor Type 名稱（IPMI 2.0 §42.2，只收錄有把握子集；未收錄如實標）。</summary>
    [SpecRef("IPMI 2.0 Specification §42.2（Sensor Type Codes）")]
    public static string SensorTypeName(byte code) => code switch
    {
        0x01 => "溫度",
        0x02 => "電壓",
        0x03 => "電流",
        0x04 => "風扇",
        0x07 => "處理器",
        0x08 => "電源供應器",
        0x0C => "記憶體",
        0x0F => "系統板",
        0x21 => "電池",
        _ => $"Sensor Type 0x{code:X2}（未收錄）",
    };

    /// <summary>Event/Reading Type 名稱（IPMI 2.0 §42.1，只收錄門檻轉換與 sensor-specific）。</summary>
    [SpecRef("IPMI 2.0 Specification §42.1（Event/Reading Type Codes）")]
    public static string EventTypeName(byte code) => code switch
    {
        0x00 => "未指定",
        0x01 => "門檻：轉入 Lower Non-critical",
        0x02 => "門檻：轉入 Lower Critical",
        0x03 => "門檻：轉入 Lower Non-recoverable",
        0x04 => "門檻：轉入 Upper Non-critical",
        0x05 => "門檻：轉入 Upper Critical",
        0x06 => "門檻：轉入 Upper Non-recoverable",
        0x6F => "Sensor-specific 離散事件",
        _ => $"Event Type 0x{code:X2}（未收錄）",
    };

    /// <summary>
    /// 解 FRU 資訊的板卡區（Board Info Area）：製造日期（1996-01-01 起的分鐘數，LSB first）
    /// 與 ASCII 型別長度欄（bits[7:6]=11 為 8-bit ASCII）。
    /// </summary>
    [SpecRef("IPMI 2.0 Specification §34（FRU Information Storage Format）：Common Header 8 bytes（版本／各區偏移×8／檢查和）、Board Area（版本／長度×8／語言／Mfg Date 3 bytes LSB first／Type-Length 欄位）")]
    public static IpmiFruBoard? DecodeFruBoardArea(byte[] fru)
    {
        if (fru.Length < 8) return null;
        byte boardOffsetUnits = fru[3];
        if (boardOffsetUnits == 0) return null; // 無板卡區
        int boardStart = boardOffsetUnits * 8;
        if (boardStart + 6 > fru.Length) return null;
        byte areaLenUnits = fru[boardStart + 1];
        if (areaLenUnits == 0) return null;
        int areaLen = areaLenUnits * 8;
        if (boardStart + areaLen > fru.Length) return null;

        uint minutes = (uint)(fru[boardStart + 3] | (fru[boardStart + 4] << 8) | (fru[boardStart + 5] << 16));
        DateTimeOffset? mfgDate = minutes == 0
            ? null
            : new DateTimeOffset(1996, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        string ReadString(ref int off)
        {
            if (off >= boardStart + areaLen) return "";
            byte tl = fru[off++];
            if (((tl >> 6) & 0x3) != 0x3) return ""; // 只解 8-bit ASCII（11b）；binary/BCD/6-bit 如實回空
            int len = tl & 0x3F;
            if (off + len > boardStart + areaLen) return "";
            var s = System.Text.Encoding.ASCII.GetString(fru, off, len);
            off += len;
            return s.TrimEnd('\0').Trim();
        }

        int o = boardStart + 6;
        var manufacturer = ReadString(ref o);
        var product = ReadString(ref o);
        var serial = ReadString(ref o);
        var part = ReadString(ref o);
        return new IpmiFruBoard(mfgDate, manufacturer, product, serial, part);
    }

    /// <summary>解 SDR 共同標頭（5 bytes）：Record ID、SDR 版本（0x51）、Record Type、Record Length。</summary>
    [SpecRef("IPMI 2.0 Specification §33（SDR）：Common Header——Record ID u16 LE、SDR Version 0x51、Record Type、Record Length")]
    public static (ushort RecordId, byte Version, byte RecordType, byte RecordLength)? DecodeSdrCommonHeader(byte[] sdr)
    {
        if (sdr.Length < 5) return null;
        return ((ushort)(sdr[0] | (sdr[1] << 8)), sdr[2], sdr[3], sdr[4]);
    }
}
