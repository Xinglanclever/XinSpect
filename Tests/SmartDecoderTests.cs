using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>S.M.A.R.T. 解碼純函式：NVMe 健康紀錄與 ATA 屬性表（合成位元組）。</summary>
public class SmartDecoderTests
{
    private static byte[] BuildNvmeLog()
    {
        var log = new byte[512];
        void Put16(int o, ushort v) { log[o] = (byte)v; log[o + 1] = (byte)(v >> 8); }
        void Put64(int o, ulong v) { for (int i = 0; i < 8; i++) log[o + i] = (byte)(v >> (8 * i)); }
        Put16(0, 0);                      // 無關鍵警告
        Put16(2, 328);                    // 328 K = 55 °C
        log[4] = 100; log[5] = 10;        // 備用 100%，門檻 10%
        log[6] = 5;                       // Percentage Used 5%
        Put64(0x28, 1_000_000);           // 寫入 1,000,000 單位 = 512,000,000,000 B = 0.512 TB
        Put64(0x50, 123_456);             // 通電 123456 小時
        Put64(0x58, 3);                   // 不安全關機 3 次
        return log;
    }

    [Fact]
    public void NVMe健康紀錄解碼_溫度壽命與寫入量()
    {
        var rows = StorageSmartService.DecodeNvmeHealth(BuildNvmeLog());
        var dict = rows.ToDictionary(r => r.Name, r => r.ValueText);
        Assert.Equal("55 °C", dict["溫度（綜合）"]);
        Assert.Equal("5%", dict["已使用壽命（Percentage Used）"]);
        Assert.Equal("512.0 GB（1,000,000 單位）", dict["累計寫入（Data Units Written）"]);
        Assert.Equal("123,456 小時", dict["通電時間"]);
        Assert.Equal("3 次", dict["不安全關機"]);
        Assert.Equal("無", dict["關鍵警告"]);
    }

    [Fact]
    public void NVMe健康紀錄_長度不足丟例外()
    {
        Assert.Throws<InvalidOperationException>(() => StorageSmartService.DecodeNvmeHealth(new byte[100]));
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
