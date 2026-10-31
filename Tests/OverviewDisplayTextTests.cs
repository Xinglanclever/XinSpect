using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 總覽「詳細資訊」每一列會顯示的那幾段字串。
///
/// <para>
/// 這些字串是<strong>顯示層的最後一哩</strong>：資料本身已經有測試（記憶體模組、實體磁碟、網卡各有一組），
/// 但拼起來的那一行沒有人守著。斷線的方式很典型——某個欄位讀不到時留下「—」，於是畫面上出現
/// 「—・16 GB・—」這種看起來像亂碼的句子；使用者無法分辨那是「沒有這項」還是「程式壞了」。
/// 這裡的要求是：<b>缺的段落整段不出現</b>，而不是留一個佔位符。
/// </para>
/// </summary>
public sealed class OverviewDisplayTextTests
{
    [Fact]
    public void 處理器摘要列出核心與快取_缺的段落不出現()
    {
        var cpu = new CpuStatic { Cores = 16, Threads = 32, L2Cache = "8 MB", L3Cache = "32 MB" };
        Assert.Equal("16 核心・32 執行緒・L2 8 MB・L3 32 MB", cpu.SummaryText);

        // 快取讀不到（WMI 在某些虛擬機上不給）：只留核心與執行緒，不留「L2 —」
        var noCache = new CpuStatic { Cores = 8, Threads = 16 };
        Assert.Equal("8 核心・16 執行緒", noCache.SummaryText);

        // 什麼都沒有時是「—」而不是空字串：空白會讓那一列看起來像版面壞掉
        Assert.Equal("—", new CpuStatic().SummaryText);
    }

    [Fact]
    public void 模組_磁碟_網卡摘要缺段落時不留佔位符()
    {
        var module = new MemoryModuleInfo
        {
            Manufacturer = "Acer", PartNumber = "BL.9BWWR.119", CapacityGB = 16, ConfiguredSpeedMHz = 6000,
        };
        Assert.Equal("Acer・BL.9BWWR.119・16 GB・6000 MHz", module.DisplayText);

        var unknownModule = new MemoryModuleInfo { CapacityGB = 8 };
        Assert.Equal("8 GB", unknownModule.DisplayText);   // 料號與廠商讀不到就不寫，不是「—・8 GB」

        var disk = new PhysicalDiskInfo { Model = "ZHITAI TiPlus5000 2TB", SizeBytes = 2_000_000_000_000, Kind = DiskKind.NvmeSsd };
        Assert.Equal("ZHITAI TiPlus5000 2TB（2 TB・NVMe SSD）", disk.DisplayText);
        Assert.Equal("—", new PhysicalDiskInfo().DisplayText);

        var nic = new NicInfo { Name = "Realtek PCIe 2.5GbE", SpeedText = "2.5 Gbps" };
        Assert.Equal("Realtek PCIe 2.5GbE・2.5 Gbps", nic.DisplayText);
        Assert.Equal("Realtek PCIe 2.5GbE", new NicInfo { Name = "Realtek PCIe 2.5GbE" }.DisplayText);
        Assert.Equal("—", new NicInfo().DisplayText);
    }
}
