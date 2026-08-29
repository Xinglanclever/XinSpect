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
}
