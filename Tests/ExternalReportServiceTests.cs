using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 外部報告解析的守門：GPU-Z txt、AIDA64 XML、HWiNFO CSV 三種格式，
/// 餵入已知的迷你 fixture，確認解析不丟例外、數值正確、狀態誠實。
/// </summary>
public class ExternalReportServiceTests
{
    [Fact]
    public void Gpuz解析兩個感測器欄位()
    {
        string text = "GPU Core Clock, GPU Memory Clock, GPU Temperature\n" +
                      "[MHz], [MHz], [C]\n" +
                      "2026/1/15 14:30:00,1500.0,2000.0,45.0\n" +
                      "2026/1/15 14:30:01,1501.0,2001.0,45.5\n";
        var r = ExternalReportService.ParseGpuz("test.txt", text);
        Assert.Equal("GPU-Z", r.Format);
        Assert.True(r.Count >= 6, $"預期 >=6 列（3欄×2行），實際 {r.Count}");
        Assert.Contains(r.Rows, x => x.Field == "GPU Core Clock" && x.NumericValue == 1500.0);
        Assert.Contains(r.Rows, x => x.Field == "GPU Temperature" && x.Unit == "C" && x.NumericValue == 45.0);
    }

    [Fact]
    public void Aida64解析XML感測器()
    {
        string text = @"<?xml version=""1.0"" encoding=""utf-8""?><aida64><page id=""Sensor"">
            <item id=""CPU Package"">45 °C</item>
            <item id=""GPU Diode"">52 °C</item>
            <item id=""CPU Vcore"">1.231 V</item>
            </page></aida64>";
        var r = ExternalReportService.ParseAida64("test.xml", text);
        Assert.Equal("AIDA64 XML", r.Format);
        Assert.True(r.Count >= 3);
        Assert.Contains(r.Rows, x => x.Field == "CPU Package" && x.NumericValue == 45.0 && x.Unit == "°C");
        Assert.Contains(r.Rows, x => x.Field == "CPU Vcore" && x.NumericValue == 1.231);
    }

    [Fact]
    public void HwinfoCsv解析感測器()
    {
        string text = "Date,Time,\"CPU Package [C]\",\"GPU Core [C]\",\"Vcore [V]\"\n" +
                      ",,\"C\",\"C\",\"V\"\n" +
                      "\"2026-01-15\",\"14:30:00\",\"45.0\",\"52.0\",\"1.231\"\n" +
                      "\"2026-01-15\",\"14:30:01\",\"45.5\",\"52.5\",\"1.230\"\n";
        var r = ExternalReportService.ParseHwinfoCsv("test.csv", text);
        Assert.Equal("HWiNFO CSV", r.Format);
        Assert.True(r.Count >= 6, $"預期 >=6 列（3欄×2行），實際 {r.Count}");
        Assert.Contains(r.Rows, x => x.Field.Contains("CPU Package") && x.NumericValue == 45.0);
    }

    [Fact]
    public void ToFacts產生穩定Key()
    {
        string text = "GPU Core Clock\n[MHz]\n2026/1/15 14:30:00,1500.0\n";
        var r = ExternalReportService.ParseGpuz("test.txt", text);
        var facts = ExternalReportService.ToFacts(r);
        Assert.Single(facts);
        Assert.Equal("外部報告・GPU-Z", facts[0].Category);
        Assert.Equal("GPU-Z", facts[0].Source);
        Assert.Equal(FactTrustLevel.Reported, facts[0].Trust);
        Assert.NotEmpty(facts[0].Key);
    }

    [Fact]
    public void 自動偵測Aida64XML()
    {
        string text = @"<?xml version=""1.0"" encoding=""utf-8""?><aida64><page id=""Summary"">
            <item id=""Motherboard"">ASUS TUF Gaming X670E</item></page></aida64>";
        // 直接用 ParseFile 會嘗試偵測格式——寫暫存檔測
        string tmp = Path.GetTempFileName() + ".xml";
        try
        {
            File.WriteAllText(tmp, text);
            var r = ExternalReportService.ParseFile(tmp);
            Assert.Equal("AIDA64 XML", r.Format);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void 空檔案回誠實狀態()
    {
        var r = ExternalReportService.ParseGpuz("empty.txt", "");
        Assert.Equal("內容不足，無法解析", r.Status);
        Assert.Empty(r.Rows);
    }
}
