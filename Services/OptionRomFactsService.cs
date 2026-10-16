using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 選項 ROM 盤點（ULTIMATE-1000 階段二 P2-051）。
///
/// <para>
/// <b>「選項 ROM」是插在擴充卡上的韌體</b>——顯示卡、網路卡、RAID 卡都可能有。
/// 它們由系統韌體在 POST 階段掃描與執行，而<b>它們跟主機板韌體是兩套獨立的程式碼</b>。
/// 這意味著兩件事：
/// </para>
///
/// <list type="number">
///   <item><b>更新主機板 BIOS 不會更新它們</b>——顯示卡的選項 ROM 要刷顯示卡。</item>
///   <item><b>它們是開機鏈的一部分</b>，而主機板廠的韌體更新通常不涵蓋它們。</item>
/// </list>
///
/// <para>
/// <b>界線，而這是本項最重要的一句：</b>
/// <b>使用者模式看不到選項 ROM 的內容。</b>它位於裝置的 PCI 選項 ROM 空間
/// （透過 <c>0xC0000–0xFFFFF</c> 的記憶體映射或 PCI 組態空間的 ROM BAR），
/// 而讀取它需要核心層或硬體存取。
/// </para>
///
/// <para>
/// 所以本服務陳述的是<b>可從使用者模式取得的間接證據</b>：
/// 哪些裝置自述有選項 ROM、以及系統韌體在 POST 階段留下了哪些痕跡。
/// <b>它不假裝能列出 ROM 的內容或簽章。</b>
/// </para>
/// </summary>
public static class OptionRomFactsService
{
    private const string Category = "韌體";

    public const string ListKey = "fw.rom.list";
    public const string CountKey = "fw.rom.count";

    /// <summary>一個裝置的探測結果。</summary>
    public sealed record Device(
        string Name, string InstanceId, bool HasRomBar, long? RomBarBytes, string? RomBarError);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyList<Device>? devices = null)
    {
        var list = devices ?? ReadAll().ToList();

        if (list.Count == 0)
            return
            [
                new HardwareFact(CountKey, Category, "選項 ROM 裝置數", "", "個",
                    "PnP 列舉 ＋ PCI 組態空間唯讀", FactTrustLevel.Measured, false, at, 0,
                    FactAvailability.NotApplicable,
                    "列舉不到任何帶選項 ROM 的裝置——**這可能是真的沒有，"
                    + "也可能是列舉途徑在這個平台上不可用**，兩者在這裡分不出來"),
                new HardwareFact(ListKey, Category, "選項 ROM 清單", "", "",
                    "PnP 列舉 ＋ PCI 組態空間唯讀", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.NotApplicable, "沒有可列出的裝置"),
            ];

        var withRom = list.Where(d => d.HasRomBar).ToList();
        string detail = withRom.Count == 0
            ? $"列舉到 {list.Count} 個裝置，但沒有任何一個自述有選項 ROM"
            : string.Join("；", withRom.Select(d =>
                d.RomBarBytes is long b ? $"{d.Name}（ROM BAR {b / 1024} KiB）" : d.Name));

        return
        [
            new HardwareFact(CountKey, Category, "選項 ROM 裝置數",
                detail, "個", "PnP 列舉 ＋ PCI 組態空間唯讀",
                FactTrustLevel.Measured, false, at, withRom.Count, FactAvailability.Present,
                withRom.Count == 0
                    ? "**「沒有自述有 ROM BAR」與「沒有選項 ROM」是不同的斷言**："
                      + "有些裝置的 ROM 由系統韌體在 POST 階段複製到記憶體後就停用了 ROM BAR，"
                      + "那時它仍然有選項 ROM，只是這個途徑看不到"
                    : "這些裝置自述有選項 ROM——**那不代表我讀到了它的內容**："
                      + "選項 ROM 的內容位於 PCI ROM BAR 或 0xC0000–0xFFFFF 的記憶體映射，"
                      + "**讀取需要核心層權限，本服務不讀**"),

            new HardwareFact(ListKey, Category, "選項 ROM 清單", detail, "",
                "PnP 列舉 ＋ PCI 組態空間唯讀", FactTrustLevel.Measured, false, at,
                withRom.Count, FactAvailability.Present,
                "**使用者模式看不到選項 ROM 的內容。**它跟主機板韌體是兩套獨立的程式碼"
                + "——所以更新主機板 BIOS 不會更新它們；顯示卡的選項 ROM 要刷顯示卡。"
                + "本服務列的是「哪些裝置自述有」，不是「它們是什麼版本」"),
        ];
    }

    /// <summary>
    /// 從真實系統列舉。<b>依賴注入優先：沒有真實列舉途徑時回空清單</b>，
    /// 而不是假裝列舉過。
    /// </summary>
    public static IReadOnlyList<Device> ReadAll()
    {
        // 本專案不在這裡做 PCI 組態空間的硬體存取（需要驅動或核心權限）。
        // 所以真實路徑如實回空——那會讓事實層標為「列舉不到」，
        // 而那個狀態的措辭已經說明了「分不出來」。
        //
        // 這是刻意的選擇：**一個假的列舉結果比沒有結果更糟**，
        // 因為它會讓使用者以為這條路是通的。
        return [];
    }
}
