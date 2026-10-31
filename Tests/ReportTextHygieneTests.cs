using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 報告「字面衛生」：不重複印廠商名、不把驅動堆疊的介面名當成實體介面、
/// 不照抄 OEM 的佔位機型。這三件都是 04:15 那份匯出報告上真的印出來過的字。
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ReportTextHygieneTests
{
    [Fact]
    public void 廠商名已在型號裡就不再印一次()
    {
        Assert.Equal("NVIDIA TITAN Xp", ReportService.GpuCaption("NVIDIA", "NVIDIA TITAN Xp"));
        Assert.Equal("NVIDIA GeForce RTX 4090", ReportService.GpuCaption("NVIDIA", "NVIDIA GeForce RTX 4090"));
        Assert.Equal("AMD　Radeon RX 7900 XTX", ReportService.GpuCaption("AMD", "Radeon RX 7900 XTX"));
        Assert.Equal("Radeon RX 7900 XTX", ReportService.GpuCaption("", "Radeon RX 7900 XTX"));
        Assert.DoesNotContain("NVIDIA　NVIDIA", ReportService.GpuCaption("NVIDIA", "NVIDIA TITAN Xp"), StringComparison.Ordinal);
    }

    [Fact]
    public void 介面欄補上真實匯流排_不讓系統碟看起來插在IDE上()
    {
        // Windows 對 NVMe 回報 SCSI、對 SATA 回報 IDE：兩者都是驅動堆疊的名字，不是排線。
        Assert.Equal("SCSI（NVMe）", ReportService.InterfaceText("SCSI", DiskKind.NvmeSsd));
        Assert.Equal("IDE（SATA）", ReportService.InterfaceText("IDE", DiskKind.SataSsd));
        Assert.Equal("IDE（SATA）", ReportService.InterfaceText("IDE", DiskKind.Hdd));
        // 判不出來就照 Windows 的話寫，不加推測。
        Assert.Equal("USB", ReportService.InterfaceText("USB", DiskKind.Unknown));
        // 已經是匯流排名就不重複加括號。
        Assert.Equal("SATA", ReportService.InterfaceText("SATA", DiskKind.SataSsd));
        Assert.Equal("NVMe", ReportService.InterfaceText("—", DiskKind.NvmeSsd));
        Assert.Equal("—", ReportService.InterfaceText("—", DiskKind.Unknown));

        // 再走一次整份 Markdown：幫手函式對不代表欄位真的接上了。
        var vm = new MainViewModel
        {
            PhysicalDisks = new[]
            {
                new PhysicalDiskInfo
                {
                    Index = 3, Model = "INTEL SSDPE2ME012T4D", Kind = DiskKind.NvmeSsd,
                    InterfaceType = "SCSI", Firmware = "8DV10110", SerialNumber = "S-1",
                },
                new PhysicalDiskInfo
                {
                    Index = 0, Model = "ST1000LM024 HN-M101MBB", Kind = DiskKind.Hdd,
                    InterfaceType = "IDE", Firmware = "2BA30001", SerialNumber = "S-2",
                },
            },
        };
        string md = ReportService.BuildMarkdownForTests(vm);

        Assert.Contains("SCSI（NVMe）", md, StringComparison.Ordinal);
        Assert.Contains("IDE（SATA）", md, StringComparison.Ordinal);
        Assert.Contains("韌體", md, StringComparison.Ordinal);          // 表頭用「韌體」，不是「固件」
    }

    [Fact]
    public void 韌體沒填機型時改印主機板並說明原因()
    {
        var vm = new MainViewModel
        {
            System = new SystemSummary
            {
                SystemManufacturer = "System manufacturer",
                SystemModel = "System Product Name",
                BoardVendor = "ASUSTeK COMPUTER INC.",
                BoardModel = "ROG RAMPAGE VI EXTREME OMEGA",
            },
        };

        string text = ReportService.MachineText(vm);

        Assert.DoesNotContain("System manufacturer", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System Product Name", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROG RAMPAGE VI EXTREME OMEGA", text, StringComparison.Ordinal);
        Assert.Contains("韌體未填機型", text, StringComparison.Ordinal);

        // 首屏膠囊只存在於 HTML：兩個佔位字不該被黏在一起當成機型，改印主機板並說明原因。
        string html = ReportService.BuildHtmlForTests(vm);
        Assert.Contains("韌體未填機型", html, StringComparison.Ordinal);
        Assert.Contains("ROG RAMPAGE VI EXTREME OMEGA", html, StringComparison.Ordinal);
        Assert.DoesNotContain("System manufacturer System Product Name", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 韌體真的填了機型就照韌體印()
    {
        var vm = new MainViewModel
        {
            System = new SystemSummary
            {
                SystemManufacturer = "LENOVO",
                SystemModel = "20Y3001DTW",
                BoardVendor = "LENOVO",
                BoardModel = "20Y3001DTW",
            },
        };

        Assert.Equal("LENOVO 20Y3001DTW", ReportService.MachineText(vm));
    }

    [Fact]
    public void 固件這個詞不再出現在報告程式裡()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Services"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string report = File.ReadAllText(Path.Combine(dir!.FullName, "Services", "ReportService.cs"));

        Assert.Contains("韌體", report, StringComparison.Ordinal);
        Assert.DoesNotContain("固件", report, StringComparison.Ordinal);
    }
}
