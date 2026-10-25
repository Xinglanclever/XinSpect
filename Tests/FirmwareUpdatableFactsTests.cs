using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段二 P2-098（<c>pdev.fw.updatable</c>：周邊韌體「可更新性」宣告）的守門。
///
/// <para>
/// 這一項最容易犯的錯是<b>把「裝置自述支援下載命令」講成「可以更新」</b>，
/// 或反過來把「讀不到」講成「不可更新」。所以測試分三層：
/// ①解碼器（純函式，含整片 0＝讀不到）；②事實的三態與界線（未回報不得壓成不支援、不得下判決）；
/// ③匯流排對照表只有一份且逐項對準規格列舉。
/// </para>
/// </summary>
public class FirmwareUpdatableFactsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Services")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ── 一、事實：三態、界線、不得下判決 ──────────────────────────────

    /// <summary>
    /// 造一份槽紀錄：AFI＋七個槽（每個 8 字元，其餘填 0）。
    /// 解碼器本身的契約（逐槽、整片 0＝讀不到、AFI 位元、OACS 三態）已由 <c>Tests/NvmeFwSlotTests.cs</c>
    /// 釘住，這一檔只測**事實層**——同一份契約不在兩處各抄一遍——兩份總有一天會漂開。
    /// </summary>
    private static byte[] SyntheticLog(byte afi, params string[] revisions)
    {
        var log = new byte[NvmeFwSlot.DataSize];
        log[0] = afi;
        for (int i = 0; i < revisions.Length && i < NvmeFwSlot.SlotCount; i++)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(revisions[i]);
            Array.Copy(bytes, 0, log, 8 + i * 8, Math.Min(bytes.Length, 8));
        }
        return log;
    }


    // ── 二、事實：三態、界線、不得下判決 ──────────────────────────────────

    private static FirmwareUpdatableFactsService.DeviceUpdatability Nvme(
        string model, byte[]? slotLog = null, bool? caps = null)
        => new("SSD", model, "ECV1", 1, 17, "NVMe",
            NvmeFwSlot.Decode(slotLog), caps, null);

    private static FirmwareUpdatableFactsService.DeviceUpdatability Ata(string model, bool? cap)
        => new("硬碟", model, "01.01A01", 0, 11, "SATA", null, null, cap);

    private static HardwareFact Fact(params FirmwareUpdatableFactsService.DeviceUpdatability[] devices)
        => FirmwareUpdatableFactsService.UpdatableFact(DateTimeOffset.UtcNow, () => devices);

    [Fact]
    public void 列舉失敗要標讀不到而不是沒有裝置()
    {
        var f = FirmwareUpdatableFactsService.UpdatableFact(DateTimeOffset.UtcNow, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("讀不到", f.UnavailableReason);
        Assert.Contains("沒有可宣告的裝置", f.UnavailableReason);   // 兩者的區分要寫在理由裡
    }

    [Fact]
    public void 列舉成功但回空是確定的答案不是讀不到()
    {
        var f = Fact([]);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Equal("沒有列舉到實體硬碟", f.Value);
        Assert.Contains("不是讀不到", f.UnavailableReason);   // 區分寫在理由欄，不是塞進值裡
        Assert.Null(f.NumericValue);
    }

    [Fact]
    public void 讀到槽資訊時要說出槽數與作用中槽()
    {
        var f = Fact(Nvme("INTEL SSDPELKX010T8", SyntheticLog(0x01, "ECV1", "", "ECV3")));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("NVMe 1 顆", f.Value);
        Assert.Contains("作用中槽 槽 1", f.Value);
        Assert.Contains("槽1＝ECV1、槽3＝ECV3", f.Value);
        Assert.Equal(1, f.NumericValue);   // 有證據的顆數
    }

    [Fact]
    public void 作用中槽未回報時不得編號成槽零()
    {
        var f = Fact(Nvme("X", SyntheticLog(0x00, "ECV1")));
        Assert.Contains("未回報", f.Value);
        Assert.DoesNotContain("作用中槽 槽 0", f.Value);
    }

    [Fact]
    public void ATA的三態要分開講_未回報不得併進不支援()
    {
        var f = Fact(Ata("ST1000LM024", true), Ata("INTEL SSDSC2BF180A5L", false), Ata("未知碟", null));
        Assert.Contains("1 顆自述支援 DOWNLOAD MICROCODE", f.Value);
        Assert.Contains("1 顆明確不支援", f.Value);
        Assert.Contains("1 顆未回報", f.Value);
        // 「明確不支援」也是讀到的證據（false ≠ 未回報），所以有證據的是 2 顆
        Assert.Equal(2, f.NumericValue);
    }

    [Fact]
    public void NVMe能力旗標讀不到時不得寫成不支援()
    {
        var f = Fact(Nvme("X", SyntheticLog(0x01, "ECV1"), caps: null));
        Assert.Contains("OACS 讀不到＝未回報", f.Value);
        Assert.DoesNotContain("明確不支援韌體命令", f.Value);
    }

    [Fact]
    public void 其他匯流排要寫成缺源而不是不可更新()
    {
        var usb = new FirmwareUpdatableFactsService.DeviceUpdatability(
            "硬碟", "USB 外接盒裡的碟", "1.00", 5, 7, "USB", null, null, null);
        var f = Fact(usb);
        Assert.Contains("缺源", f.Value);
        Assert.Contains("不是「不可更新」", f.Value);
        Assert.Equal(0, f.NumericValue);
    }

    [Fact]
    public void 事實本身要寫明界線_支援下載不等於可以更新也不等於需要更新()
    {
        var f = Fact(Nvme("X", SyntheticLog(0x01, "ECV1"), caps: true), Ata("Y", true));
        Assert.Contains("裝置自述的支援旗標", f.Value);
        Assert.Contains("不等於", f.Value);
        Assert.Contains("不燒寫", f.Value);
        Assert.Contains("不連網", f.Value);
        // 不得出現判決語氣（注意：界線句本身含「不等於需要更新」，那是**否定**，
        // 所以這裡釘的是「沒有肯定形」，不是「沒有這幾個字」。）
        // 值裡有粗體標記（不等於），所以用樣式比對而不是字面比對
        Assert.Matches(@"不等於\*{0,2}需要更新", f.Value);
        Assert.DoesNotMatch("(?<!不等於)(該|應該|建議|必須)更新", f.Value);
        Assert.DoesNotContain("最新版", f.Value);
    }

    [Fact]
    public void Present的事實不得帶讀不到的理由()
    {
        var f = Fact(Nvme("X", SyntheticLog(0x01, "ECV1")));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Null(f.UnavailableReason);
        Assert.NotNull(f.NumericValue);
    }

    [Fact]
    public void 有證據的顆數只算真的有讀到東西的()
    {
        var f = Fact(
            Nvme("A", SyntheticLog(0x01, "ECV1")),   // 有槽 ⇒ 有證據
            Nvme("B", null, caps: null),             // 兩樣都讀不到 ⇒ 無證據
            Ata("C", null),                          // IDENTIFY 讀不到 ⇒ 無證據
            Ata("D", false));                        // 明確不支援也是證據
        Assert.Equal(2, f.NumericValue);
        Assert.Contains("硬碟／SSD 4 顆", f.Value);
    }

    [Fact]
    public void 鍵與類別要固定()
    {
        var f = Fact(Ata("X", true));
        Assert.Equal("pdev.fw.updatable", f.Key);
        Assert.Equal("儲存裝置", f.Category);
        Assert.Equal(FactTrustLevel.Reported, f.Trust);   // 裝置自述
        Assert.False(f.Sensitive);
    }

    // ── 二、這條鍵不得自己長出讀取路徑 ───────────────────────────────

    [Fact]
    public void 可更新性事實不得自己開檔案或直接PInvoke()
    {
        // 讀取一律走 StorageSmartService（看門狗＋斷路器＋外層預算都在那裡）。
        // 這一條是原始碼掃描：單元測試驗不到「以後有人在這裡加一條 HttpClient」。
        string file = Path.Combine(RepoRoot(), "Services", "FirmwareUpdatableFactsService.cs");
        Assert.True(File.Exists(file), $"找不到 {file}");
        string text = File.ReadAllText(file);
        foreach (string pattern in new[]
                 {
                     @"\bDllImport\b", @"\bManagementObjectSearcher\b", @"\bFile\.", @"\bFileStream\b",
                     @"\bProcess\.", @"\bHttpClient\b", @"\bCreateFile\b", @"\bDeviceIoControl\b",
                 })
            Assert.DoesNotMatch(pattern, text);
        Assert.Contains("StorageSmartService.TryGetBusType", text);
    }

    [Fact]
    public void 裝置列舉沿用周邊韌體那一份不重查WMI()
    {
        string file = Path.Combine(RepoRoot(), "Services", "FirmwareUpdatableFactsService.cs");
        string text = File.ReadAllText(file);
        // 一次列舉、兩個事實共用：P2-096 的 ReadInventory 是唯一列舉處
        Assert.Contains("PeripheralFirmwareFactsService.ReadInventory()", text);
        Assert.DoesNotMatch(@"SELECT\s+Model", text);
    }

    // ── 三、匯流排對照表：唯一一份，且逐項對準規格列舉 ─────────────────

    [Theory]
    // 出處：Windows SDK 10.0.28000.0 um/winioctl.h 的 typedef enum _STORAGE_BUS_TYPE（0 起逐項加一）。
    // 2026-10-19 以本機 5 顆碟實測對照：CIM MSFT_PhysicalDisk.BusType 對 NVMe 給 17、對 SATA 給 11。
    [InlineData(1u, "SCSI")]
    [InlineData(2u, "ATAPI")]
    [InlineData(3u, "ATA")]
    [InlineData(4u, "IEEE 1394")]
    [InlineData(5u, "SSA")]
    [InlineData(6u, "Fibre Channel")]
    [InlineData(7u, "USB")]
    [InlineData(8u, "RAID")]
    [InlineData(9u, "iSCSI")]
    [InlineData(10u, "SAS")]
    [InlineData(11u, "SATA")]
    [InlineData(12u, "SD")]
    [InlineData(13u, "MMC")]
    [InlineData(17u, "NVMe")]
    [InlineData(18u, "SCM")]
    [InlineData(20u, "NVMe over Fabrics")]
    public void 匯流排名稱逐項對準規格列舉(uint bus, string name)
        => Assert.Equal(name, StorageSmartService.BusTypeName(bus));

    [Fact]
    public void 列舉外的值要如實標示不得猜()
    {
        Assert.Equal("未知", StorageSmartService.BusTypeName(0));
        Assert.Contains("不在已知列舉內", StorageSmartService.BusTypeName(200));
    }

    [Theory]
    [InlineData(17u, StorageSmartService.StorageBusChannel.Nvme)]
    [InlineData(16u, StorageSmartService.StorageBusChannel.Nvme)]
    [InlineData(3u, StorageSmartService.StorageBusChannel.Ata)]
    [InlineData(10u, StorageSmartService.StorageBusChannel.Ata)]
    [InlineData(11u, StorageSmartService.StorageBusChannel.Ata)]
    [InlineData(7u, StorageSmartService.StorageBusChannel.None)]
    [InlineData(0u, StorageSmartService.StorageBusChannel.None)]
    public void 通道分群與原本寫死的常數逐字相同(uint bus, StorageSmartService.StorageBusChannel expected)
        => Assert.Equal(expected, StorageSmartService.ChannelOf(bus));

    [Fact]
    public void 匯流排名稱只有一份_其他檔案不得再寫一張表()
    {
        string services = Path.Combine(RepoRoot(), "Services");
        var owners = new List<string>();
        foreach (string file in Directory.EnumerateFiles(services, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string text = File.ReadAllText(file);
            // 「某個數字 → 某個匯流排名」這種表，全專案只允許出現在 StorageSmartService
            // 只認「十進位數字 => 匯流排名」這種表（(?<![\w.]) 讓 0x14 這種十六進位不誤中）
            if (Regex.IsMatch(text,
                    @"(?<![\w.])[0-9]+\s*=>\s*""(SATA|SAS|iSCSI|Fibre Channel|Storage Spaces|NVMe|SD|MMC|SCSI|ATAPI|IEEE 1394|SSA|USB|RAID|SCM|UFS|NVMe over Fabrics)"""))
                owners.Add(Path.GetFileName(file));
        }
        Assert.Equal(new[] { "StorageSmartService.cs" }, owners.ToArray());
        // 另一份表要委派過來，而不是自己再抄一次
        Assert.Contains("StorageSmartService.BusTypeName",
            File.ReadAllText(Path.Combine(services, "DiskInfoService.cs")));
    }

    // ── 攻擊面彙整（U10-SE-0036，pdev.fw.surface）──────────────────────

    [Fact]
    public void 攻擊面彙整要把有無證據分兩邊且不得下判決()
    {
        var facts = FirmwareUpdatableFactsService.Collect(DateTimeOffset.UtcNow,
            () => [Nvme("X", SyntheticLog(0x01, "ECV1")), Ata("Y", null)]);
        var s = facts.Single(f => f.Key == "pdev.fw.surface");
        var u = facts.Single(f => f.Key == "pdev.fw.updatable");

        Assert.Equal(FactAvailability.Present, s.Availability);
        Assert.Equal(1, s.NumericValue);
        Assert.Contains("NVMe 1", s.Value);
        Assert.Contains("ATA 1", s.Value);
        Assert.Contains("無證據 1", s.Value);
        Assert.Contains("不是「已遭入侵」", s.Value);
        // 同一次列舉只探一次：兩把鍵的分母必須一致
        Assert.Equal(u.Source, s.Source);
    }

    [Fact]
    public void 攻擊面彙整列舉失敗要跟主事實同態()
    {
        var facts = FirmwareUpdatableFactsService.Collect(DateTimeOffset.UtcNow, () => null);
        var s = facts.Single(f => f.Key == "pdev.fw.surface");
        Assert.Equal(FactAvailability.ReadError, s.Availability);
        Assert.Contains("同一次列舉", s.UnavailableReason);
    }
}
