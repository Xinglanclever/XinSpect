using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 初始開機塊（IBB）與金鑰模組（KM）判定（ULTIMATE-1000 階段二 P2-054／P2-055）。
///
/// <para>
/// <b>這一項復用既有的 <see cref="FitDecoder"/>，不新增解碼器。</b>
/// FIT 目錄是 Boot Guard 信任鏈的<b>索引</b>：它記錄了 IBB 的啟動模組、
/// Key Manifest、Boot Policy Manifest 各自在哪。
/// </para>
///
/// <para>
/// <b>界線，而這是本項最重要的一句：</b>
/// <b>FIT 目錄裡「有」那些條目，不代表 Boot Guard 已啟用、更不代表驗證通過。</b>
/// 目錄是<b>索引</b>，不是<b>結論</b>。要確認是否啟用需要 flash descriptor 的
/// Boot Guard 設定檔（FLMAP／FMLF），而那個出處本版未對準
/// （既有的 <c>fw.bg.profile</c> 就是這樣如實標示的）。
/// </para>
///
/// <para>
/// <b>所以本服務陳述的是「目錄上宣告了什麼」</b>——包括每個條目的位移與長度，
/// 因為那正是「IBB 範圍」與「KM 存在性」在目錄層唯一能說的事。
/// <b>它不宣告「IBB 從哪裡到哪裡」以外的事</b>：真正的 IBB 範圍由 Boot Policy
/// Manifest 決定，而那是目錄條目<b>指向</b>的內容，需要另一層解析。
/// </para>
///
/// <para>
/// <b>本服務不自己去讀快閃</b>——它接收已經解出的 FIT 表，
/// 與 <see cref="MeasuredBootFactsService"/>／<see cref="TrustRootFactsService"/>
/// 是同一個模式。
/// </para>
/// </summary>
public static class BootGuardFactsService
{
    private const string Category = "信賴根";
    private const string Source = "FIT 目錄的條目分類（既有的 FitDecoder）";

    public const string IbbKey = "bt.ibb";
    public const string KmKey = "bt.km";
    public const string ManifestsKey = "bt.manifests";

    /// <summary>
    /// FIT 型別編號（依 Intel FWI 公開目錄）。
    /// <b>與 <see cref="FitDecoder.TypeNames"/> 相同的來源</b>——
    /// 這裡只挑出與信任鏈有關的幾個，其餘留給既有的目錄顯示。
    /// </summary>
    private const ushort TypeAcm = 0x02;            // Co-Security／ACM
    private const ushort TypeBootPolicy = 0x05;     // Platform Boot Policy
    private const ushort TypeStartupModule = 0x07;  // BIOS Startup Module（IBB 的起點）

    // ═══ 這裡「沒有」一個常數，而那是刻意的 ═══
    //
    // Key Manifest 的 FIT 型別編號**不在本專案任何地方**——
    // 既有的 FitDecoder.TypeNames 收錄了 8 個型別，而 KM 不在裡面。
    //
    // 我的第一版在這裡寫了 `private const ushort TypeKeyManifest = 0x0B;`，
    // 並在註解裡承認「我在本專案裡找不到那個編號的出處」——
    // **然後我還是把它填進去了**，並在事實的措辭裡小心地說它沒有出處。
    //
    // **那是錯的。** 一旦寫下 0x0B，無論措辭多小心，那個數字就會被當成事實讀。
    // 而「填錯的編號會讓一個不存在的條目看起來存在」正是我自己在同一段寫下的警告
    // ——**我把「我知道這有問題」寫成了註解，卻仍然產出了一個值。**
    //
    // 這與撤回 P2-052（FIT）／P2-008（HMAT）／P2-024（Type 19）／
    // Type 44 那半是同一件事，而我差點在同一天又犯一次。
    //
    // 所以 bt.km 永遠 NotSupported，理由誠實：**沒有出處就不給編號。**

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, FitTable? fit)
    {
        if (fit is null)
            return Na(at, "沒有 FIT 表——**「讀不到 FIT」與「FIT 裡沒有信任鏈條目」是不同的斷言**："
                + "前者代表那條讀取路徑不通（快閃尾端不可讀、驅動未載），"
                + "後者代表這台機器沒有 Boot Guard 目錄。本服務不自己去讀快閃",
                FactAvailability.NotSupported);

        if (fit.Entries.Count == 0)
            return Na(at, "FIT 表存在但沒有任何條目——本服務不猜沒有目錄內容的信任鏈",
                FactAvailability.NotApplicable);

        var acm = fit.Entries.Where(e => e.Type == TypeAcm).ToList();
        var startup = fit.Entries.Where(e => e.Type == TypeStartupModule).ToList();
        var policy = fit.Entries.Where(e => e.Type == TypeBootPolicy).ToList();

        return
        [
            Ibb(at, startup, policy),
            Km(at),
            Manifests(at, acm, startup, policy, fit),
        ];
    }

    private static HardwareFact Ibb(DateTimeOffset at,
        List<FitEntry> startup, List<FitEntry> policy)
    {
        if (startup.Count == 0)
            return new HardwareFact(IbbKey, Category, "初始開機塊（IBB）",
                $"FIT 目錄裡沒有啟動模組條目（型別 0x{TypeStartupModule:X2}）", "", Source,
                FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                "**「目錄裡沒有啟動模組條目」與「這台機器沒有 IBB」是不同的斷言**："
                + "IBB 是 Boot Guard 的概念，而沒有 Boot Guard 目錄的機器"
                + "仍然有它自己的第一個執行的東西。所以這裡說的是"
                + "「**Boot Guard 目錄沒有描述 IBB**」，不是「沒有 IBB」");

        string detail = string.Join("；", startup.Select(e =>
            $"位移 0x{e.Offset:X}、長度 {e.Size} bytes"));

        return new HardwareFact(IbbKey, Category, "初始開機塊（IBB）",
            $"啟動模組 {startup.Count} 條：{detail}"
            + (policy.Count > 0 ? $"；另有 Boot Policy {policy.Count} 條" : ""),
            "條", Source, FactTrustLevel.Measured, false, at, startup.Count,
            FactAvailability.Present,
            "**目錄條目給的是「宣告的位置與長度」，不是「IBB 的範圍」。**"
            + "真正的 IBB 範圍由 Boot Policy Manifest 決定，而那是條目**指向**的內容，"
            + "需要另一層解析——**本版只到目錄層**。"
            + "而**目錄裡有啟動模組條目不代表 Boot Guard 已啟用**："
            + "目錄是索引，不是結論");
    }

    private static HardwareFact Km(DateTimeOffset at)
    {
        return new HardwareFact(KmKey, Category, "金鑰模組（KM）", "", "", Source,
            FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
            "**本版不判定 KM。**既有的 FIT 型別表（FitDecoder.TypeNames）沒有收錄 "
            + "Key Manifest 的編號，而本服務**不憑印象填一個進去**——"
            + "**填錯的編號會讓一個不存在的條目看起來存在**，那比「未收錄」更糟。"
            + "要補上這一項，需要先對準 Intel FWI 的型別編號出處，"
            + "再把它加進 FitDecoder.TypeNames（**那是一處改、兩處受益**）");
    }

    private static HardwareFact Manifests(DateTimeOffset at, List<FitEntry> acm,
        List<FitEntry> startup, List<FitEntry> policy, FitTable fit)
    {
        var known = new[] { TypeAcm, TypeBootPolicy, TypeStartupModule }
            .SelectMany(t => fit.Entries.Where(e => e.Type == t))
            .ToList();
        var unknownTypes = fit.Entries
            .Where(e => e.TypeName == "未收錄")
            .Select(e => e.Type)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        string detail = $"ACM {acm.Count}、Boot Policy {policy.Count}、啟動模組 {startup.Count}"
            + (unknownTypes.Count > 0
                ? $"；另有未收錄型別 {unknownTypes.Count} 種（{string.Join("、", unknownTypes.Select(t => $"0x{t:X2}"))}）"
                : "");

        return new HardwareFact(ManifestsKey, Category, "信任鏈目錄條目",
            detail, "條", Source, FactTrustLevel.Measured, false, at, known.Count,
            unknownTypes.Count > 0 ? FactAvailability.Unknown : FactAvailability.Present,
            "**目錄是索引，不是結論。**它記錄了 IBB 的啟動模組、Boot Policy、ACM"
            + "各自在哪，而**「有這些條目」與「Boot Guard 已啟用」是兩件事**——"
            + "後者需要 flash descriptor 的設定檔（FLMAP／FMLF），而那個出處本版未對準"
            + "（見既有的 fw.bg.profile，它如實標示這件事）"
            + (unknownTypes.Count > 0
                ? "。**另有未收錄型別的條目**——它們如實列出編號而不命名，"
                  + "因為猜一個名字會讓它們看起來像我們知道的東西"
                  + "（**Key Manifest 很可能就在其中，但本版不指名**）"
                : ""));
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        string[] keys = [IbbKey, KmKey, ManifestsKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
