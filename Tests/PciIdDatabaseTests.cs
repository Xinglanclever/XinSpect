using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// PCI 廠商／裝置 ID 的離線完整對照表（第 281 輪，知識包第二批）。
///
/// <para>
/// 這一組要釘住三件事：<b>資料真的載進來了</b>（不是空的卻回「未收錄」）、
/// <b>兩層擔保分得開</b>（手工表 vs 上游登錄檔）、以及<b>查不到時講的話是誠實的</b>
/// （「這份快照沒有那一格」不是「裝置有問題」）。
/// </para>
/// </summary>
public class PciIdDatabaseTests
{
    // ── 載入 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 內嵌資料檔要載得進來且規模符合宣告()
    {
        var db = PciIdDatabase.Database;
        Assert.Null(db.Failure);
        Assert.True(db.VendorCount > 2400, $"廠商只有 {db.VendorCount} 筆——資料檔可能沒被內嵌");
        Assert.True(db.DeviceCount > 20000, $"裝置只有 {db.DeviceCount} 筆——資料檔可能被截斷");
    }

    [Fact]
    public void 快照版本與授權要寫在常數裡()
    {
        Assert.Equal("2026.10.01", PciIdDatabase.SnapshotVersion);
        Assert.Contains("BSD-3-Clause", PciIdDatabase.License);
        Assert.Contains("GPL-2.0-or-later", PciIdDatabase.License);   // 上游自述的雙授權，要如實轉述
        Assert.Contains("pci-ids.ucw.cz", PciIdDatabase.SourceUrl);
    }

    // ── 查得到 ────────────────────────────────────────────────────────────

    [Fact]
    public void 常見廠商ID查得到名字()
    {
        Assert.Contains("Intel", PciIdDatabase.VendorName(0x8086)!);
        Assert.Contains("NVIDIA", PciIdDatabase.VendorName(0x10DE)!);
        Assert.Contains("AMD", PciIdDatabase.VendorName(0x1022)!);
        Assert.Contains("Realtek", PciIdDatabase.VendorName(0x10EC)!);
    }

    [Fact]
    public void 常見裝置ID查得到名字()
    {
        // 這幾個是本機實際會遇到的：ASMedia USB 控制器、Intel 晶片組、Samsung NVMe
        Assert.NotNull(PciIdDatabase.DeviceName(0x1B21, 0x2142));
        Assert.NotNull(PciIdDatabase.DeviceName(0x8086, 0xA36D));
        Assert.NotNull(PciIdDatabase.DeviceName(0x144D, 0xA808));
    }

    [Fact]
    public void 未收錄的ID回null而不是空字串()
    {
        // ⚠ 不能用 0xFFFF 當「不存在」的例子：pci.ids 裡 0xFFFF 是 **Illegal Vendor ID**
        // （登錄檔刻意登記的哨兵值），0xFFFE 也真的登記了東西。用 0x1234——實測不在表裡。
        Assert.Null(PciIdDatabase.VendorName(0x1234));
        Assert.Null(PciIdDatabase.DeviceName(0x1234, 0x5678));
        // 廠商有、但該裝置不在快照裡
        Assert.Null(PciIdDatabase.DeviceName(0x8086, 0xFFFF));
    }

    [Fact]
    public void 登錄檔的哨兵值要如實轉述_不當成未收錄()
    {
        // 這一條是上一條的教訓：0xFFFF 在 pci.ids 裡有名字（Illegal Vendor ID）。
        // 「未收錄」與「登錄檔說這是非法 ID」是兩種完全不同的陳述，不能混。
        var hit = PciIdLookup.Vendor(0xFFFF);
        Assert.Equal(PciIdLookup.NameOrigin.Registry, hit.Origin);
        Assert.Contains("Illegal", hit.Name!);
    }

    // ── 兩層擔保 ──────────────────────────────────────────────────────────

    [Fact]
    public void 手工表優先_值裡不標登錄檔版本()
    {
        var hit = PciIdLookup.Vendor(0x8086);
        Assert.Equal(PciIdLookup.NameOrigin.Curated, hit.Origin);
        Assert.Equal("Intel", hit.Name);          // 手工表的中文語境短名
        Assert.Null(hit.Snapshot);                // 手工層沒有快照概念
    }

    [Fact]
    public void 手工表沒收的廠商走登錄檔層_並標出快照版本()
    {
        // 0x1B21（ASMedia）不在手工的 15 條裡
        var hit = PciIdLookup.Vendor(0x1B21);
        Assert.Equal(PciIdLookup.NameOrigin.Registry, hit.Origin);
        Assert.Contains("ASMedia", hit.Name!);
        Assert.Equal(PciIdDatabase.SnapshotVersion, hit.Snapshot);
    }

    [Fact]
    public void 兩層都沒有時回未收錄()
    {
        var hit = PciIdLookup.Vendor(0x1234);
        Assert.Equal(PciIdLookup.NameOrigin.Unlisted, hit.Origin);
        Assert.Null(hit.Name);
        Assert.False(hit.Found);
    }

    [Fact]
    public void 裝置名只有登錄檔層_手工表從來沒有收錄型號()
    {
        var hit = PciIdLookup.Device(0x8086, 0xA36D);
        Assert.Equal(PciIdLookup.NameOrigin.Registry, hit.Origin);
        Assert.NotNull(hit.Name);
    }

    // ── 描述行 ────────────────────────────────────────────────────────────

    [Fact]
    public void 描述行要標明每一段的來源層()
    {
        var ids = PciKnowledge.DecodeVendorDevice(0x15B8_8086);   // Intel 廠商、裝置 0x15B8
        var cls = PciKnowledge.DecodeClassCode(0x02000000u);      // 網路／Ethernet
        string text = PciIdLookup.Describe(ids, cls);

        Assert.Contains("網路控制器", text);
        Assert.Contains("Ethernet", text);
        Assert.Contains("Intel", text);                    // 手工層：不加版本標記
        Assert.Contains("0x8086:0x15B8", text);            // 原始 ID 永遠保留（稽核面）
    }

    [Fact]
    public void 登錄檔層的名字要附快照版本_讓讀者知道資料多新()
    {
        var ids = (VendorId: (ushort)0x1B21, DeviceId: (ushort)0x2142);   // ASMedia USB
        var cls = PciKnowledge.DecodeClassCode(0x0C033000u);              // 序列匯流排／USB
        string text = PciIdLookup.Describe(ids, cls);

        Assert.Contains("登錄檔 2026.10.01", text);
        Assert.Contains("0x1B21:0x2142", text);
    }

    [Fact]
    public void 未收錄的廠商與裝置都如實標_不猜()
    {
        var ids = (VendorId: (ushort)0x1234, DeviceId: (ushort)0x5678);
        var cls = PciKnowledge.DecodeClassCode(0xFF000000u);
        string text = PciIdLookup.Describe(ids, cls);

        Assert.Contains("Vendor 0x1234（未收錄）", text);
        Assert.Contains("Device 0x5678（未收錄）", text);
        // 0xFF 是規格定義的「未分類」而不是未收錄——兩者不同，這一條順帶釘住
        Assert.Contains("未分類", text);

        // 真正未收錄的 base class（0x12 不在 PCI-SIG 的定義表裡）
        var unknownClass = PciKnowledge.DecodeClassCode(0x12000000u);
        Assert.Contains("類別碼未收錄", PciIdLookup.Describe(ids, unknownClass));
    }

    [Fact]
    public void 覆蓋率申報要寫出兩層各自的規模與快照日期()
    {
        string text = PciIdLookup.Coverage();
        Assert.Contains("手工表 15 個廠商", text);
        Assert.Contains(PciIdDatabase.SnapshotVersion, text);
        Assert.Contains("不代表裝置有問題", text);
    }

    // ── 效能界線 ──────────────────────────────────────────────────────────

    [Fact]
    public void 首次載入要在合理時間內完成_之後走快取()
    {
        // 2 萬多筆字典的建置成本要能被「開一次程式」接受；之後每次查詢都是字典命中。
        var sw = Stopwatch.StartNew();
        _ = PciIdDatabase.VendorName(0x8086);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 3000, $"首次載入 {sw.ElapsedMilliseconds} ms——太慢");

        var again = Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++) _ = PciIdDatabase.DeviceName(0x8086, 0xA36D);
        again.Stop();
        Assert.True(again.ElapsedMilliseconds < 500,
            $"一萬次查詢 {again.ElapsedMilliseconds} ms——快取沒生效");
    }

    // ── 資料檔本身 ────────────────────────────────────────────────────────

    [Fact]
    public void 資料檔要隨組件內嵌且不被當成內容檔複製()
    {
        var asm = typeof(PciIdDatabase).Assembly;
        Assert.Contains("XinSpect.Decoders.Data.pci-ids.tsv.gz", asm.GetManifestResourceNames());
    }
}
