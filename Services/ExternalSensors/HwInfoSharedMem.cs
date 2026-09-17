// HWiNFO 共享記憶體讀取器
// 透過 Global\HWiNFO_SENS_SM2 讀取 HWiNFO64/32 的即時感測器資料

using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

/// <summary>
/// 從 HWiNFO 共享記憶體區段讀取感測器數值。
/// HWiNFO 必須在「僅感測器」模式下執行，並啟用「共享記憶體支援」。
/// </summary>
public static class HwInfoSharedMem
{
    // ── 共享記憶體名稱 ──────────────────────────────────────────
    private const string SharedMemName = "Global\\HWiNFO_SENS_SM2";

    // ── 標頭簽章 ──────────────────────────────────────────────────
    // SDK 定義 HWiNFO_SENSORS_SIGNATURE = 'SiWH'（以 DWORD 讀入的小端序結果）。
    // 'H'=0x48, 'W'=0x57, 'i'=0x69, 'S'=0x53；小端序 DWORD = 0x53695748。
    // 先前寫成 0x48576953 是把位元組順序反了——結果是永遠「簽章不符」，一筆都讀不到。
    private const uint ExpectedSignature = 0x53695748;

    // ── HWiNFO 讀數類型列舉 ─────────────────────────────────────
    private enum HwInfoReadingType : uint
    {
        None  = 0,
        Temp  = 1,
        Volt  = 2,
        Fan   = 3,
        Current = 4,
        Power = 5,
        Clock = 6,
        Usage = 7,
        Other = 8,
    }

    // ── 結構：共享記憶體標頭 ────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HwInfoHeader
    {
        public uint dwSignature;       // "HWiS"
        public uint dwVersion;         // 標頭版本
        public uint dwRevision;        // 標頭修訂
        public long dwPollTime;        // 上次輪詢時間（毫秒 tick）
        public uint dwSensorOffset;    // 感測器元素陣列的偏移量
        public uint dwSensorSize;      // 單一感測器元素大小
        public uint dwSensorElements;  // 感測器元素數量
        public uint dwReadingOffset;   // 讀數元素陣列的偏移量
        public uint dwReadingSize;     // 單一讀數元素大小
        public uint dwReadingElements; // 讀數元素數量
    }

    // ── 結構：讀數元素 ─────────────────────────────────────────
    // 此結構僅包含我們需要的欄位；實際結構可能更大，
    // 因此透過 dwReadingSize 做偏移量計算而非直接 Marshal 整塊。
    private const int LabelLength = 128;
    private const int UnitLength  = 16;

    /// <summary>
    /// 嘗試從 HWiNFO 共享記憶體讀取所有感測器讀數。
    /// </summary>
    /// <param name="readings">成功時回傳讀數清單。</param>
    /// <param name="error">失敗時的錯誤訊息。</param>
    /// <returns>是否成功開啟並解析共享記憶體。</returns>
    public static bool TryRead(out IReadOnlyList<ExternalReading> readings, out string? error)
    {
        readings = Array.Empty<ExternalReading>();
        error = null;

        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? accessor = null;
        try
        {
            mmf = MemoryMappedFile.OpenExisting(SharedMemName, MemoryMappedFileRights.Read);
            accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            // ── 讀取標頭 ──
            accessor.Read(0, out HwInfoHeader header);

            if (header.dwSignature != ExpectedSignature)
            {
                error = $"HWiNFO 共享記憶體簽章不符：預期 0x{ExpectedSignature:X8}，實際 0x{header.dwSignature:X8}";
                return false;
            }

            if (header.dwReadingElements == 0)
            {
                readings = Array.Empty<ExternalReading>();
                return true;
            }

            var result = new List<ExternalReading>((int)header.dwReadingElements);

            for (uint i = 0; i < header.dwReadingElements; i++)
            {
                long offset = header.dwReadingOffset + (long)i * header.dwReadingSize;

                // 讀數元素（HWiNFO_SENSOR_READING）的欄位順序：
                //   dwReadingType  @ 0
                //   dwSensorIndex  @ 4
                //   dwReadingID    @ 8   ← 先前漏掉這個，於是後面每一個欄位都少 4 位元組
                //   szLabelOrig    @ 12  (128 bytes)
                //   szLabelUser    @ 140 (128 bytes)
                //   szUnit         @ 268 (16 bytes)
                //   Value/ValueMin/ValueMax/ValueAvg @ 284/292/300/308
                //
                // 少了 dwReadingID 的後果不是拋例外，而是安靜的假數字：標籤從 ReadingID 的低位
                // 開始讀 → 第一個位元組常是 NUL → 標籤全空；Value 從 unit 尾端讀 → 垃圾 double。
                // 筆數與類型反而是對的（那些位移沒錯），所以畫面看起來「有一堆感測器但沒有名字」。
                const int ReadingHeaderSize = 12;

                uint readingTypeRaw = accessor.ReadUInt32(offset);          // +0

                byte[] labelOrigBytes = new byte[LabelLength];
                accessor.ReadArray(offset + ReadingHeaderSize, labelOrigBytes, 0, LabelLength);
                string labelOrig = ExtractString(labelOrigBytes);

                byte[] labelUserBytes = new byte[LabelLength];
                accessor.ReadArray(offset + ReadingHeaderSize + LabelLength, labelUserBytes, 0, LabelLength);
                string labelUser = ExtractString(labelUserBytes);

                byte[] unitBytes = new byte[UnitLength];
                accessor.ReadArray(offset + ReadingHeaderSize + LabelLength * 2, unitBytes, 0, UnitLength);
                string unit = ExtractString(unitBytes);

                long valOffset = offset + ReadingHeaderSize + LabelLength * 2 + UnitLength;
                double value    = accessor.ReadDouble(valOffset);
                double valueMin = accessor.ReadDouble(valOffset + 8);
                double valueMax = accessor.ReadDouble(valOffset + 16);
                double valueAvg = accessor.ReadDouble(valOffset + 24);

                // 優先使用使用者自訂標籤，若為空則用原始標籤
                string label = string.IsNullOrWhiteSpace(labelUser) ? labelOrig : labelUser;

                result.Add(new ExternalReading(
                    Source:     "HWiNFO",
                    SensorType: MapReadingType((HwInfoReadingType)readingTypeRaw),
                    Label:      label,
                    Value:      value,
                    Unit:       unit,
                    Min:        valueMin,
                    Max:        valueMax,
                    Avg:        valueAvg
                ));
            }

            readings = result;
            return true;
        }
        catch (System.IO.FileNotFoundException)
        {
            error = "HWiNFO 未執行或未啟用共享記憶體";
            return false;
        }
        catch (Exception ex)
        {
            error = $"讀取 HWiNFO 共享記憶體失敗：{ex.Message}";
            return false;
        }
        finally
        {
            accessor?.Dispose();
            mmf?.Dispose();
        }
    }

    /// <summary>將 HWiNFO 讀數類型對映到統一類型字串。</summary>
    private static string MapReadingType(HwInfoReadingType type) => type switch
    {
        HwInfoReadingType.Temp    => "Temperature",
        HwInfoReadingType.Volt    => "Voltage",
        HwInfoReadingType.Fan     => "Fan",
        HwInfoReadingType.Current => "Current",
        HwInfoReadingType.Power   => "Power",
        HwInfoReadingType.Clock   => "Clock",
        HwInfoReadingType.Usage   => "Usage",
        _                         => "Other",
    };

    /// <summary>從 null-terminated byte 陣列提取字串。</summary>
    private static string ExtractString(byte[] buffer)
    {
        int len = Array.IndexOf(buffer, (byte)0);
        if (len < 0) len = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, len);
    }
}
