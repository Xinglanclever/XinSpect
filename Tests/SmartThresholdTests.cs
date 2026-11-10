using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// SMART 門檻與 failing-now 的契約：ATA READ THRESHOLDS（0xD1）的門檻表解碼、
/// 「現值 ≤ 門檻＝現正低於門檻」判定（門檻 0＝無門檻不評比，SMART 規範）、
/// NVMe WCTEMP 對照實際合成溫度。純函式釘值；通路以注入探測替代。
/// </summary>
public class SmartThresholdTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);

    private static byte[] ThresholdSector(params (byte Id, byte Threshold)[] entries)
    {
        var sector = new byte[512];
        sector[0] = 0x10; sector[1] = 0x01;   // version
        int off = 2;
        foreach (var (id, th) in entries)
        {
            sector[off] = id;
            sector[off + 1] = th;
            off += 12;
        }
        return sector;
    }

    [Fact]
    public void 門檻表解碼_逐條釘值()
    {
        var map = XinSpect.StorageSmartService.DecodeAtaThresholds(
            ThresholdSector((0x05, 20), (0xC5, 0), (0xC6, 5)));
        Assert.Equal((byte)20, map[0x05]);
        Assert.Equal((byte)0, map[0xC5]);
        Assert.Equal((byte)5, map[0xC6]);
        Assert.False(map.ContainsKey(0x09));
    }

    [Fact]
    public void FailingNow_現值低於門檻即標_門檻零不評比()
    {
        // 現值 10／門檻 20 → failing；門檻 0 不評比；超過 byte 範圍的 500 是無效 normalized。
        var attrs = new List<XinSpect.SmartRow>
        {
            new("重新配置磁區數", "10", "", "raw", id: 0x05),
            new("待對映磁區數", "30", "", "raw", id: 0xC5),
            new("無法修正磁區數", "500", "", "raw", id: 0xC6),
        };
        var thresholds = XinSpect.StorageSmartService.DecodeAtaThresholds(
            ThresholdSector((0x05, 20), (0xC5, 0), (0xC6, 15)));

        var failing = XinSpect.StorageSmartService.EvaluateFailingNow(attrs, thresholds);
        var a5 = Assert.Single(failing, f => f.Id == 0x05);
        Assert.Equal(10, a5.Value);
        Assert.Equal(20, a5.Threshold);
        Assert.DoesNotContain(failing, f => f.Id == 0xC5);
        Assert.DoesNotContain(failing, f => f.Id == 0xC6);
    }

    [Fact]
    public void NVMe_WCTEMP對照_超過警告_未提供如實標()
    {
        // WCTEMP 在 Identify Controller 偏移 0x10A（u16 LE，Kelvin；0＝未提供）
        var idc = new byte[4096];
        BitConverter.GetBytes((ushort)343).CopyTo(idc, 0x10A);
        var ok = XinSpect.StorageSmartService.EvaluateWctemp(compositeTempC: 65, identifyController: idc);
        Assert.Equal(XinSpect.StorageSmartService.WctempState.Normal, ok.State);
        Assert.Equal(70, ok.ThresholdC);

        var warn = XinSpect.StorageSmartService.EvaluateWctemp(75, idc);
        Assert.Equal(XinSpect.StorageSmartService.WctempState.Warning, warn.State);
        Assert.Equal(XinSpect.StorageSmartService.WctempState.Warning,
            XinSpect.StorageSmartService.EvaluateWctemp(70, idc).State);
        Assert.Equal(XinSpect.StorageSmartService.WctempState.NotProvided,
            XinSpect.StorageSmartService.EvaluateWctemp(null, idc).State);

        var none = new byte[4096];   // WCTEMP＝0＝未提供
        var na = XinSpect.StorageSmartService.EvaluateWctemp(75, none);
        Assert.Equal(XinSpect.StorageSmartService.WctempState.NotProvided, na.State);
    }

    [Fact]
    public void FailingNow事實_有失敗逐條_全正常也成列()
    {
        var facts = XinSpect.SmartFailingNowFactsService.Collect(At,
            probe: () => (
                new List<XinSpect.SmartRow>
                {
                    new("重新配置磁區數", "10", "", "raw", id: 0x05),
                },
                XinSpect.StorageSmartService.DecodeAtaThresholds(ThresholdSector((0x05, 20))),
                (XinSpect.StorageSmartService.WctempState.Normal, 70, 65)));
        var count = Assert.Single(facts, f => f.Key == "smart.failing_now");
        Assert.Equal(1u, count.NumericValue);
        var item = Assert.Single(facts, f => f.Key == "smart.failing_now.0");
        Assert.Contains("現正低於門檻", item.Value);

        var clean = XinSpect.SmartFailingNowFactsService.Collect(At, probe: () => (
            new List<XinSpect.SmartRow> { new("重新配置磁區數", "100", "", "raw", id: 0x05) },
            XinSpect.StorageSmartService.DecodeAtaThresholds(ThresholdSector((0x05, 20))),
            (XinSpect.StorageSmartService.WctempState.Normal, 70, 65)));
        Assert.Contains("沒有", Assert.Single(clean, f => f.Key == "smart.failing_now").Value);
        Assert.Equal(0.0, Assert.Single(clean, f => f.Key == "smart.failing_now").NumericValue);

        foreach (var thresholds in new Dictionary<byte, byte>?[]
            { null, new(), new() { [5] = 0 }, new() { [6] = 20 } })
        {
            var missing = XinSpect.SmartFailingNowFactsService.Collect(At, () => (
                new List<XinSpect.SmartRow> { new("Attribute", "100", "", "0", id: 5) },
                thresholds, (XinSpect.StorageSmartService.WctempState.NotProvided, 0, 0)));
            var unknown = Assert.Single(missing, f => f.Key == "smart.failing_now");
            Assert.Equal(XinSpect.FactAvailability.Unknown, unknown.Availability);
            Assert.Null(unknown.NumericValue);
        }
        foreach (string normalized in new[] { "999", "-1", "invalid", "" })
        {
            var invalid = XinSpect.SmartFailingNowFactsService.Collect(At, () => (
                new List<XinSpect.SmartRow> { new("Attribute", normalized, "", "0", id: 5) },
                new Dictionary<byte, byte> { [5] = 20 },
                (XinSpect.StorageSmartService.WctempState.NotProvided, 0, 0)));
            var unknown = Assert.Single(invalid, f => f.Key == "smart.failing_now");
            Assert.Equal(XinSpect.FactAvailability.Unknown, unknown.Availability);
            Assert.Null(unknown.NumericValue);
        }
    }
}
