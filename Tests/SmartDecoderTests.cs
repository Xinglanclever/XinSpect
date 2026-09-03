using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>S.M.A.R.T. 解碼純函式：NVMe 健康紀錄與 ATA 屬性表（合成位元組）。</summary>
public class SmartDecoderTests
{
    /// <summary>
    /// 依<b>規格</b>建一份 SMART／健康紀錄，不是依實作。
    /// </summary>
    /// <remarks>
    /// 這一點是這份測試最重要的地方。1.9.1 之前的版本把位移寫錯了兩處，而當時的測試是照著
    /// 實作的位移建資料的——於是測試綠燈、畫面上的溫度與壽命卻全是別的欄位。
    /// 現在改用 <see cref="NvmeLogDecoder"/> 的具名常數建資料：常數若跟規格不符，
    /// 這裡就會連帶錯，但至少「測試」與「實作」不再是同一個來源。
    /// </remarks>
    private static byte[] BuildNvmeLog(byte criticalWarning = 0)
    {
        var log = new byte[512];
        void Put16(int o, ushort v) { log[o] = (byte)v; log[o + 1] = (byte)(v >> 8); }
        // 128 位元計數器：低 64 位元寫值，高 64 位元留 0
        void Put128(int o, ulong v) { for (int i = 0; i < 8; i++) log[o + i] = (byte)(v >> (8 * i)); }

        log[NvmeLogDecoder.OffCriticalWarning] = criticalWarning;
        Put16(NvmeLogDecoder.OffCompositeTemp, 328);          // 328 K = 55 °C
        log[NvmeLogDecoder.OffAvailableSpare] = 100;
        log[NvmeLogDecoder.OffSpareThreshold] = 10;
        log[NvmeLogDecoder.OffPercentageUsed] = 5;
        Put128(NvmeLogDecoder.OffDataUnitsRead, 2_000_000);
        Put128(NvmeLogDecoder.OffDataUnitsWritten, 1_000_000);   // = 512,000,000,000 B
        Put128(NvmeLogDecoder.OffPowerCycles, 742);
        Put128(NvmeLogDecoder.OffPowerOnHours, 123_456);
        Put128(NvmeLogDecoder.OffUnsafeShutdowns, 3);
        Put128(NvmeLogDecoder.OffMediaErrors, 0);
        Put128(NvmeLogDecoder.OffErrorLogEntries, 11);
        return log;
    }

    [Fact]
    public void NVMe健康紀錄解碼_每一個欄位都對得上規格位移()
    {
        var rows = StorageSmartService.DecodeNvmeHealth(BuildNvmeLog());
        var dict = rows.ToDictionary(r => r.Name, r => r.ValueText);

        Assert.Equal("55 °C", dict["溫度（綜合）"]);
        Assert.Equal("100%（門檻 10%）", dict["可用備用空間"]);
        Assert.Equal("5%", dict["已使用壽命（Percentage Used）"]);
        Assert.Equal("1.024 TB（2,000,000 單位）", dict["累計讀取（Data Units Read）"]);
        Assert.Equal("512.0 GB（1,000,000 單位）", dict["累計寫入（Data Units Written）"]);
        Assert.Equal("742 次", dict["電源循環"]);
        Assert.Equal("123,456 小時", dict["通電時間"]);
        Assert.Equal("3 次", dict["不安全關機"]);
        Assert.Equal("0", dict["媒體與資料完整性錯誤"]);
        Assert.Equal("11", dict["錯誤資訊紀錄項目"]);
        Assert.Equal("無（0x00）", dict["關鍵警告"]);
    }

    [Fact]
    public void 規格位移本身要釘住_計數器是一百二十八位元而不是六十四位元()
    {
        // 這兩條是 1.9.1 之前那個缺陷的直接根因，各釘一條。
        Assert.Equal(0x01, NvmeLogDecoder.OffCompositeTemp);   // 關鍵警告只有 1 位元組，溫度緊接在後
        Assert.Equal(0x30, NvmeLogDecoder.OffDataUnitsWritten); // 讀取量在 0x20，佔 16 位元組
        Assert.Equal(0x80, NvmeLogDecoder.OffPowerOnHours);     // 與 TryReadPowerOnHours 用的是同一個值
        // 每個 128 位元計數器相隔 16 位元組
        Assert.Equal(16, NvmeLogDecoder.OffPowerCycles - NvmeLogDecoder.OffControllerBusyTime);
        Assert.Equal(16, NvmeLogDecoder.OffPowerOnHours - NvmeLogDecoder.OffPowerCycles);
    }

    [Fact]
    public void 關鍵警告要逐位元說出是什麼出問題而不是只給十六進位()
    {
        // 0b0000_1001 = 備用空間見底 ＋ 已進入唯讀模式
        var rows = StorageSmartService.DecodeNvmeHealth(BuildNvmeLog(0x09));
        var names = rows.Select(r => r.Name).ToList();
        Assert.Contains(names, n => n.Contains("備用空間低於門檻"));
        Assert.Contains(names, n => n.Contains("已進入唯讀模式"));
        Assert.Equal("2 項（0x09）", rows.First(r => r.Name == "關鍵警告").ValueText);
        // 沒亮的位元不該出現
        Assert.DoesNotContain(names, n => n.Contains("溫度超出臨界範圍"));
    }

    [Fact]
    public void 沒有警告時不列任何位元()
    {
        Assert.Empty(NvmeLogDecoder.CriticalWarnings(0));
        Assert.Equal(6, NvmeLogDecoder.CriticalWarnings(0x3F).Count);
        // 未定義的位元照實列出而不假裝認得
        var undef = NvmeLogDecoder.CriticalWarnings(0x40);
        Assert.Single(undef);
        Assert.Contains("未定義", undef[0].Name);
    }

    [Fact]
    public void NVMe健康紀錄_長度不足丟例外()
    {
        Assert.Throws<InvalidOperationException>(() => StorageSmartService.DecodeNvmeHealth(new byte[100]));
    }

    // ── 錯誤資訊紀錄（log page 0x01）────────────────────────────

    private static byte[] BuildErrorLog()
    {
        var log = new byte[NvmeLogDecoder.ErrorEntrySize * 4];
        void Entry(int slot, ulong count, int sq, int cid, int sct, int sc, ulong lba, uint ns)
        {
            int o = slot * NvmeLogDecoder.ErrorEntrySize;
            for (int i = 0; i < 8; i++) log[o + i] = (byte)(count >> (8 * i));
            log[o + 8] = (byte)sq; log[o + 9] = (byte)(sq >> 8);
            log[o + 10] = (byte)cid; log[o + 11] = (byte)(cid >> 8);
            int status = ((sct & 0x7) << 9) | ((sc & 0xFF) << 1);   // 位 0 是 Phase Tag
            log[o + 12] = (byte)status; log[o + 13] = (byte)(status >> 8);
            for (int i = 0; i < 8; i++) log[o + 16 + i] = (byte)(lba >> (8 * i));
            for (int i = 0; i < 4; i++) log[o + 24 + i] = (byte)(ns >> (8 * i));
        }
        Entry(0, 7, 3, 0x21, sct: 2, sc: 0x81, lba: 1_234_567, ns: 1);   // 讀取無法修正
        Entry(1, 9, 0, 0x05, sct: 0, sc: 0x02, lba: 0, ns: 1);           // 命令欄位無效（較新）
        // slot 2、3 保持全 0 ＝ 空槽位
        return log;
    }

    [Fact]
    public void 錯誤紀錄依錯誤計數由新到舊且跳過空槽位()
    {
        var entries = NvmeLogDecoder.ErrorEntries(BuildErrorLog());
        Assert.Equal(2, entries.Count);                 // 兩個空槽位不列
        Assert.Equal(9UL, entries[0].ErrorCount);       // 計數大的是較新的
        Assert.Equal(7UL, entries[1].ErrorCount);
    }

    [Fact]
    public void 錯誤紀錄的狀態碼要翻成人看得懂的一句話()
    {
        var entries = NvmeLogDecoder.ErrorEntries(BuildErrorLog());
        var media = entries.First(e => e.ErrorCount == 7);
        Assert.Equal(2, media.StatusCodeType);
        Assert.Equal(0x81, media.StatusCode);
        Assert.Contains("讀取無法修正", media.StatusText);
        Assert.Equal("1,234,567", media.LbaText);       // 媒體錯誤才有 LBA
        Assert.Equal("SQ 3 ／ CID 33", media.QueueText);

        var generic = entries.First(e => e.ErrorCount == 9);
        Assert.Equal("—", generic.LbaText);             // 非媒體錯誤的 LBA 不適用，不硬印數字
        Assert.Contains("命令欄位無效", generic.StatusText);
    }

    [Fact]
    public void 查不到的狀態碼回報原始SCT與SC而不編一個說法()
    {
        string s = NvmeLogDecoder.StatusText(1, 0x7E);
        Assert.Contains("沒有這個狀態", s);
        Assert.Contains("SCT 1", s);
        Assert.Contains("0x7E", s);
    }

    [Fact]
    public void 完全沒有錯誤時明說一筆都沒有而不是留白()
    {
        var rows = StorageSmartService.DecodeNvmeErrorLog(new byte[NvmeLogDecoder.ErrorEntrySize * 4]);
        Assert.Single(rows);
        Assert.Contains("沒有任何一筆", rows[0].ValueText);
    }

    private static byte[] BuildAtaSector()
    {
        var sector = new byte[512];
        sector[0] = 0x10; sector[1] = 0x80;   // 版本與狀態（不影響解碼）
        // 屬性 5：值 100、最差 99、raw 六位元組 00 00 00 00 00 00
        int o = 2;
        sector[o] = 5; sector[o + 3] = 100; sector[o + 4] = 99;
        // 屬性 194：值 30、raw LE = 35（byte0）
        o = 2 + 12;
        sector[o] = 194; sector[o + 3] = 30; sector[o + 4] = 30; sector[o + 5] = 35;
        // 屬性 12：值 99、raw LE = 0x0158 = 344 次循環
        o = 2 + 24;
        sector[o] = 12; sector[o + 3] = 99; sector[o + 4] = 99; sector[o + 5] = 0x58; sector[o + 6] = 0x01;
        return sector;
    }

    [Fact]
    public void ATA屬性表解碼_原始值照列且未使用槽位略過()
    {
        var rows = StorageSmartService.DecodeAtaAttributes(BuildAtaSector());
        Assert.Equal(3, rows.Count);
        Assert.Contains("5 重配置磁區數", rows[0].Name);
        Assert.Equal("100", rows[0].ValueText);
        Assert.Equal("99", rows[0].WorstText);
        Assert.Equal("000000000000（LE: 0）", rows[0].RawText);
        Assert.Contains("194 溫度", rows[1].Name);
        Assert.Contains("LE: 35", rows[1].RawText);
        Assert.Contains("12 電源循環", rows[2].Name);
        Assert.Contains("LE: 344", rows[2].RawText);
    }

    [Fact]
    public void ATA屬性表_長度不足丟例外()
    {
        Assert.Throws<InvalidOperationException>(() => StorageSmartService.DecodeAtaAttributes(new byte[100]));
    }

    [Fact]
    public void 屬性名稱_不認得的ID標廠商自訂()
    {
        Assert.Equal("重配置磁區數", StorageSmartService.AttributeName(5));
        Assert.Equal("廠商自訂", StorageSmartService.AttributeName(0x63));
    }

    // ── NVMe Identify Controller ────────────────────────────────────────────

    private static byte[] BuildNvmeIdentify()
    {
        var buf = new byte[4096];
        // AsciiString reads even byte (offset + i*2) as the char
        static void PutAscii(byte[] b, int offset, int byteLen, string s)
        {
            for (int i = 0; i < byteLen / 2; i++)
            {
                b[offset + i * 2] = (byte)(i < s.Length ? s[i] : 0x00);   // even byte = char
                b[offset + i * 2 + 1] = 0;                                // odd byte = 0
            }
        }
        // Vendor (offset 0, 2 words = 4 bytes = 2 chars): "NV"
        PutAscii(buf, 0, 4, "NV");
        // Model (offset 24, 20 words = 40 bytes)
        PutAscii(buf, 24, 40, "TestNVMe SSD");
        // Serial (offset 4, 10 words = 20 bytes)
        PutAscii(buf, 4, 20, "SER12345");
        // Firmware (offset 64, 4 words = 8 bytes)
        PutAscii(buf, 64, 8, "FW12");
        // NN = 1 (offset 513-514, LE)
        buf[513] = 1;
        // NCAP = 1000000 (offset 0x38, LE uint32) → 1000000 * 512 = 512000000
        buf[0x38] = 0x40; buf[0x39] = 0x42; buf[0x3A] = 0x0F; buf[0x3B] = 0x00;
        // MDTS: byte 77, low nibble = 7 → 2^7 = 128 pages
        buf[77] = 0x07;
        // CC.EN: offset 0x4C-0x4D, bit 0 = 1 → 就緒
        buf[0x4C] = 0x01;
        return buf;
    }

    [Fact]
    public void NVMeIdentify解碼_廠商型號韌體與容量()
    {
        var rows = StorageSmartService.DecodeNvmeIdentify(BuildNvmeIdentify());
        var dict = rows.ToDictionary(r => r.Name, r => r.ValueText);
        Assert.Contains("NV", dict["廠商（Vendor ID）"]);
        Assert.Equal("TestNVMe SSD", dict["型號（Model）"]);
        Assert.Equal("SER12345", dict["序號（Serial）"]);
        Assert.Equal("FW12", dict["韌體版本（Firmware）"]);
        Assert.Equal("1", dict["總命名空間數（NN）"]);
        Assert.Contains("512,000,000", dict["容量（NCAP）"]);
        Assert.Contains("128 頁", dict["最大資料傳輸大小（MDTS）"]);
    }

    [Fact]
    public void NVMeIdentify_長度不足4096丟例外()
    {
        Assert.Throws<InvalidOperationException>(() => StorageSmartService.DecodeNvmeIdentify(new byte[512]));
    }

    // ── ATA IDENTIFY DEVICE ─────────────────────────────────────────────────

    private static byte[] BuildAtaIdentify()
    {
        var buf = new byte[512];
        // ATA DecodeAtaIdentify.AsciiString reads (hi << 8) | lo with hi at odd offset, lo at even;
        // for ASCII chars, hi=0, lo=char. So chars live at EVEN bytes.
        static void PutAscii(byte[] b, int offset, int byteLen, string s)
        {
            for (int i = 0; i < byteLen / 2; i++)
            {
                b[offset + i * 2] = (byte)(i < s.Length ? s[i] : 0x00);   // lo = char
                b[offset + i * 2 + 1] = 0;                                // hi = 0
            }
        }
        // Word 0: bit 15 = 1 → ATA device (byte 1 high bit)
        buf[1] = 0x80;
        // Model (word 27-46, 40 bytes at offset 54)
        PutAscii(buf, 2 * 27, 40, "TestSATA");
        // Serial (word 10-19, 20 bytes at offset 20)
        PutAscii(buf, 2 * 10, 20, "SN12345678");
        // Firmware (word 23-26, 8 bytes at offset 46) — 4 words 只能裝 4 個字元
        PutAscii(buf, 2 * 23, 8, "FW20");
        // Word 100-101 (offset 200): total LBA = 2000000000 (0x77359400), LE
        buf[200] = 0x00; buf[201] = 0x94; buf[202] = 0x35; buf[203] = 0x77;
        // Word 106 (offset 212): bit 12 = 4K logical sector, LE (0x1000)
        buf[212] = 0x00; buf[213] = 0x10;
        // Word 69 (offset 138): bit 14 = TRIM support, LE (0x4000)
        buf[138] = 0x00; buf[139] = 0x40;
        // Word 78 (offset 156): bit 5 = DevSleep, LE (0x0020)
        buf[156] = 0x20; buf[157] = 0x00;
        // Word 217 (offset 434): 0x0001 = SSD, LE
        buf[434] = 0x01; buf[435] = 0x00;
        // Word 128 (offset 256): 0x0002 = enabled, LE
        buf[256] = 0x02; buf[257] = 0x00;
        // Word 80 (offset 160): 0x007E, LE
        buf[160] = 0x7E; buf[161] = 0x00;
        return buf;
    }

    [Fact]
    public void ATAIdentify解碼_型號序列號與磁區資訊()
    {
        var rows = StorageSmartService.DecodeAtaIdentify(BuildAtaIdentify());
        var dict = rows.ToDictionary(r => r.Name, r => r.ValueText);
        Assert.Equal("TestSATA", dict["型號（Model）"]);
        Assert.Equal("SN12345678", dict["序號（Serial）"]);
        Assert.Equal("FW20", dict["韌體（Firmware）"]);
        Assert.Equal("ATA", dict["裝置類型"]);
        Assert.Contains("2,000,000,000", dict["最大 LBA（48-bit）"]);
        Assert.Equal("4 KiB", dict["邏輯磁區大小"]);
        Assert.Contains("是", dict["支援 TRIM"]);
        Assert.Equal("SSD（無旋轉）", dict["媒體旋轉率（Word 217）"]);
    }

    [Fact]
    public void ATAIdentify_長度不足512丟例外()
    {
        Assert.Throws<InvalidOperationException>(() => StorageSmartService.DecodeAtaIdentify(new byte[100]));
    }

    // ── 序號回填：識別資料 → 儲存卡片 ────────────────────────────────────────

    [Fact]
    public void 序號取自識別列_NVMe與ATA都認得()
    {
        Assert.Equal("SER12345",
            StorageSmartService.SerialFromRows(StorageSmartService.DecodeNvmeIdentify(BuildNvmeIdentify())));
        Assert.Equal("SN12345678",
            StorageSmartService.SerialFromRows(StorageSmartService.DecodeAtaIdentify(BuildAtaIdentify())));
    }

    [Fact]
    public void 序號取自識別列_沒有或空白時回傳null()
    {
        // 匯流排預檢不通過時放進來的那種「不支援」列：沒有序號可用，就不能假裝有
        SmartRow[] unsupported = [new("NVMe Identify Controller", "不支援（儲存堆疊未回應協定查詢）", "", "")];
        Assert.Null(StorageSmartService.SerialFromRows(unsupported));

        // 韌體把序號欄位留空（合法但沒用）
        SmartRow[] blank = [new("序號（Serial）", "   ", "", "")];
        Assert.Null(StorageSmartService.SerialFromRows(blank));

        Assert.Null(StorageSmartService.SerialFromRows([]));
    }

    [Fact]
    public void 儲存卡片序號_直讀值優先於WMI值並標注來源()
    {
        var row = new StorageRow("INTEL SSDPELKX010T8") { SerialNumber = "0025_3852_31A2_D3AC." };

        // 還沒直讀過：顯示 WMI 的值，且標明是 WMI 說的
        Assert.Equal("0025_3852_31A2_D3AC.", row.SerialText);
        Assert.Equal("序號（WMI）", row.SerialLabel);

        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.IdentifySerial = StorageSmartService.SerialFromRows(
            StorageSmartService.DecodeNvmeIdentify(BuildNvmeIdentify()));

        Assert.Equal("SER12345", row.SerialText);
        Assert.Equal("序號（裝置直讀）", row.SerialLabel);
        Assert.Contains(nameof(StorageRow.SerialText), changed);
        Assert.Contains(nameof(StorageRow.SerialLabel), changed);
    }

    [Fact]
    public void 儲存卡片序號_直讀失敗不覆蓋WMI值()
    {
        var row = new StorageRow("ST1000LM024 HN-M101MBB") { SerialNumber = "S2ZZJ9CD123456" };
        row.IdentifySerial = null;
        Assert.Equal("S2ZZJ9CD123456", row.SerialText);
        Assert.Equal("序號（WMI）", row.SerialLabel);
    }
}
