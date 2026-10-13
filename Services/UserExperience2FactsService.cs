namespace XinSpect;

/// <summary>
/// 使用體驗第二批（Vol2 第 430–439 項）：UX-001 逐值溯源（<c>ux.provenance</c>）、UX-003 命令面板（<c>ux.palette</c>）、
/// UX-004 Help 檢索（<c>ux.help.search</c>）、UX-005 UIA 守門（<c>ux.uia</c>）、UX-006 雙解析度截圖守門（<c>ux.dpi</c>）、
/// UX-007 高對比/自動主題（<c>ux.theme</c>，跟隨系統）、UX-008 節能取樣（<c>ux.eco</c>）、UX-010 錯誤聚合頁（<c>ux.errors</c>）、
/// UX-011 事實鍵瀏覽器（<c>ux.keybrowser</c>）、UX-013 全域右鍵複製（<c>ux.copy</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>紅線：</b>① 溯源<b>不等於正確性背書</b>（只說「值從哪來」不說「值一定對」）；
/// ② 錯誤聚合頁的<b>重試＝唯讀重掃</b>，不修復任何東西；③ UIA／鍵盤／DPI 守門是<b>自動化測試結果</b>不是功能；
/// ④ 節能取樣降頻率要受效能預算約束；⑤ 不列路徑或使用者資料。
/// </para>
/// </remarks>
public static class UserExperience2FactsService
{
    private const string Cat = "使用體驗";

    public const string ProvenanceKey = "ux.provenance";
    public const string PaletteKey = "ux.palette";
    public const string HelpSearchKey = "ux.help.search";
    public const string UiaKey = "ux.uia";
    public const string DpiKey = "ux.dpi";
    public const string ThemeKey = "ux.theme";
    public const string EcoKey = "ux.eco";
    public const string ErrorsKey = "ux.errors";
    public const string KeyBrowserKey = "ux.keybrowser";
    public const string CopyKey = "ux.copy";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    public delegate (int Anchors, int Unresolved)? ProvenanceProbe();
    public delegate (int Commands, bool CtrlK)? PaletteProbe();
    public delegate (int Entries, bool Offline)? HelpSearchProbe();
    public delegate (int Elements, int Unnamed)? UiaProbe();
    public delegate (int Baselines, bool AllMatch)? DpiProbe();
    public delegate (bool FollowsSystem, bool HighContrast)? ThemeProbe();
    public delegate (double IdleIntervalSeconds, bool WithinBudget)? EcoProbe();
    public delegate (int ReadErrors, bool RetryIsReadOnly)? ErrorsProbe();
    public delegate (int Keys, bool ShowsSemver)? KeyBrowserProbe();
    public delegate (bool IncludesKeyName, bool IncludesTimestamp)? CopyProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        ProvenanceProbe? provenance = null, PaletteProbe? palette = null, HelpSearchProbe? helpSearch = null,
        UiaProbe? uia = null, DpiProbe? dpi = null, ThemeProbe? theme = null, EcoProbe? eco = null,
        ErrorsProbe? errors = null, KeyBrowserProbe? keyBrowser = null, CopyProbe? copy = null)
        =>
        [
            ProvenanceFact(at, provenance), PaletteFact(at, palette), HelpSearchFact(at, helpSearch),
            UiaFact(at, uia), DpiFact(at, dpi), ThemeFact(at, theme), EcoFact(at, eco),
            ErrorsFact(at, errors), KeyBrowserFact(at, keyBrowser), CopyFact(at, copy),
        ];

    // ── UX-001 逐值溯源 ────────────────────────────────────────────────

    public static HardwareFact ProvenanceFact(DateTimeOffset at, ProvenanceProbe? probe = null)
    {
        (int Anchors, int Unresolved)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, ProvenanceKey, Cat, "逐值溯源", FactAvailability.ReadError, "溯源錨點不可讀");

        var (anchors, unresolved) = s.Value;
        return new HardwareFact(ProvenanceKey, Cat, "逐值溯源",
            $"溯源錨點 {anchors} 個、無法解析 {unresolved} 個。" +
            "<b>溯源≠正確性背書</b>：卡片說明「這個值從哪來」，不保證值一定對——" +
            "來源正確也可能讀到過期值。無法解析的錨點<b>顯示為未知</b>，不猜來源。",
            "錨點", "FactProvenance（唯讀）", FactTrustLevel.Reported, false, at, anchors);
    }

    // ── UX-003 命令面板 ────────────────────────────────────────────────

    public static HardwareFact PaletteFact(DateTimeOffset at, PaletteProbe? probe = null)
    {
        (int Commands, bool CtrlK)? s;
        try { s = (probe ?? (() => (0, false)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, PaletteKey, Cat, "命令面板", FactAvailability.ReadError, "命令面板狀態不可讀");

        var (commands, ctrlK) = s.Value;
        return new HardwareFact(PaletteKey, Cat, "命令面板",
            $"可跳轉命令 {commands} 個、快捷鍵 Ctrl+K{(ctrlK ? "已綁定" : "未綁定")}。" +
            "命令面板只做<b>導覽</b>（跳頁／聚焦），不執行會改變狀態的操作。",
            "個", "命令面板（唯讀）", FactTrustLevel.Reported, false, at, commands);
    }

    // ── UX-004 Help 檢索 ───────────────────────────────────────────────

    public static HardwareFact HelpSearchFact(DateTimeOffset at, HelpSearchProbe? probe = null)
    {
        (int Entries, bool Offline)? s;
        try { s = (probe ?? (() => (0, true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, HelpSearchKey, Cat, "Help 檢索", FactAvailability.ReadError, "Help 檢索不可讀");

        var (entries, offline) = s.Value;
        return new HardwareFact(HelpSearchKey, Cat, "Help 檢索",
            $"檢索條目 {entries} 條、離線全文搜尋={(offline ? "是" : "否")}。" +
            "Help 檢索是<b>本機離線</b>的——說明文字隨程式出貨，不出網查詢。",
            "條", "HelpCatalog（唯讀）", FactTrustLevel.Reported, false, at, entries);
    }

    // ── UX-005 UIA 守門 ────────────────────────────────────────────────

    public static HardwareFact UiaFact(DateTimeOffset at, UiaProbe? probe = null)
    {
        (int Elements, int Unnamed)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, UiaKey, Cat, "UIA 守門", FactAvailability.ReadError, "UIA 掃描不可讀");

        var (elements, unnamed) = s.Value;
        return new HardwareFact(UiaKey, Cat, "UIA 守門",
            $"UIA 元素 {elements} 個、無名稱 {unnamed} 個。" +
            "<b>樹名稱完整性</b>是給螢幕朗讀者的無障礙守門——無名稱的元素朗讀不出內容；" +
            "這是<b>自動化測試結果</b>不是產品功能。",
            "個", "UIA 掃描（唯讀）", FactTrustLevel.Reported, false, at, unnamed);
    }

    // ── UX-006 雙解析度截圖守門 ────────────────────────────────────────

    public static HardwareFact DpiFact(DateTimeOffset at, DpiProbe? probe = null)
    {
        (int Baselines, bool AllMatch)? s;
        try { s = (probe ?? (() => (0, true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, DpiKey, Cat, "雙解析度截圖守門", FactAvailability.ReadError, "DPI 基線不可讀");

        var (baselines, match) = s.Value;
        return new HardwareFact(DpiKey, Cat, "雙解析度截圖守門",
            $"DPI 基線 {baselines} 份、全部相符={(match ? "是" : "否")}。" +
            "DPI 基線擴充抓的是<b>縮放下的版面漂移</b>（100% 正常、150% 溢出）——" +
            "不符是訊號，需人工確認再更新基線。",
            "份", "DPI 算繪基線（唯讀）", FactTrustLevel.Reported, false, at, baselines);
    }

    // ── UX-007 高對比/自動主題 ────────────────────────────────────────

    public static HardwareFact ThemeFact(DateTimeOffset at, ThemeProbe? probe = null)
    {
        (bool FollowsSystem, bool HighContrast)? s;
        try { s = (probe ?? (() => (true, false)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, ThemeKey, Cat, "高對比／自動主題", FactAvailability.ReadError, "主題狀態不可讀");

        var (follows, hc) = s.Value;
        return new HardwareFact(ThemeKey, Cat, "高對比／自動主題",
            $"跟隨系統={(follows ? "是" : "否")}、高對比模式={(hc ? "開" : "關")}。" +
            "跟隨系統主題讓工具融入使用者環境；高對比模式由系統設定驅動，" +
            "本工具只回應不覆寫。",
            "", "主題設定（唯讀）", FactTrustLevel.Reported, false, at, hc ? 1 : 0);
    }

    // ── UX-008 節能取樣 ────────────────────────────────────────────────

    public static HardwareFact EcoFact(DateTimeOffset at, EcoProbe? probe = null)
    {
        (double IdleIntervalSeconds, bool WithinBudget)? s;
        try { s = (probe ?? (() => (0, true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, EcoKey, Cat, "節能取樣", FactAvailability.ReadError, "節能取樣狀態不可讀");

        var (interval, within) = s.Value;
        return new HardwareFact(EcoKey, Cat, "節能取樣",
            $"閒置取樣間隔 {interval:0.#} 秒、在效能預算內={(within ? "是" : "否")}。" +
            "<b>背景降頻率</b>降低待機耗電——代價是資料新鮮度下降；" +
            "間隔受效能預算約束（復用 A54 預算），不能無限拉長也不能無限縮短。",
            "秒", "取樣排程（唯讀）", FactTrustLevel.Reported, false, at, interval);
    }

    // ── UX-010 錯誤聚合頁 ──────────────────────────────────────────────

    public static HardwareFact ErrorsFact(DateTimeOffset at, ErrorsProbe? probe = null)
    {
        (int ReadErrors, bool RetryIsReadOnly)? s;
        try { s = (probe ?? (() => ReadErrors()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, ErrorsKey, Cat, "錯誤聚合頁", FactAvailability.ReadError, "錯誤聚合不可讀");

        var (readErrors, ro) = s.Value;
        return new HardwareFact(ErrorsKey, Cat, "錯誤聚合頁",
            $"目前 ReadError {readErrors} 筆、重試為唯讀重掃={(ro ? "是" : "否")}。" +
            "<b>重試＝唯讀重掃</b>：重試只是再讀一次，不修復、不重設裝置、不寫入。" +
            "錯誤集中的頁面讓「讀不到」不再散落各處。",
            "筆", "事實三態聚合（唯讀）", FactTrustLevel.Reported, false, at, readErrors);
    }

    private static (int, bool) ReadErrors() => (0, true);

    // ── UX-011 事實鍵瀏覽器 ────────────────────────────────────────────

    public static HardwareFact KeyBrowserFact(DateTimeOffset at, KeyBrowserProbe? probe = null)
    {
        (int Keys, bool ShowsSemver)? s;
        try { s = (probe ?? (() => (FactKeyCatalog.Count, true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, KeyBrowserKey, Cat, "事實鍵瀏覽器", FactAvailability.ReadError, "鍵瀏覽器不可讀");

        var (keys, semver) = s.Value;
        return new HardwareFact(KeyBrowserKey, Cat, "事實鍵瀏覽器",
            $"可瀏覽鍵 {keys} 把、顯示 semver={(semver ? "是" : "否")}。" +
            "鍵樹＋搜尋讓使用者找到「有哪些事實」；顯示鍵 semver 呼應「鍵是公開契約」的穩定性承諾。",
            "把", "FactKeyCatalog（唯讀）", FactTrustLevel.Reported, false, at, keys);
    }

    // ── UX-013 全域右鍵複製 ────────────────────────────────────────────

    public static HardwareFact CopyFact(DateTimeOffset at, CopyProbe? probe = null)
    {
        (bool IncludesKeyName, bool IncludesTimestamp)? s;
        try { s = (probe ?? (() => (true, true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, CopyKey, Cat, "全域右鍵複製", FactAvailability.ReadError, "複製狀態不可讀");

        var (keyName, ts) = s.Value;
        return new HardwareFact(CopyKey, Cat, "全域右鍵複製",
            $"複製內容含鍵名={(keyName ? "是" : "否")}、含時間戳={(ts ? "是" : "否")}。" +
            "含鍵名與時間戳讓貼出的值<b>可追溯</b>（哪個鍵、什麼時候量的）——" +
            "單獨一個數字貼出去沒有意義。",
            "", "複製格式（唯讀）", FactTrustLevel.Reported, false, at, (keyName && ts) ? 1 : 0);
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
