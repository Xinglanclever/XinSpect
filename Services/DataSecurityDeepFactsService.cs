namespace XinSpect;

/// <summary>
/// 資料安全深化第二批（Vol2 第 DS-006、DS-007、DS-010、DS-011、DS-012 項）：
/// 憑證自動更新（<c>ds.cert.update</c>）、自簽憑證清單（<c>ds.selfsigned.list</c>）、
/// Smart App Control（<c>ds.sac.deep</c>）、裝置安裝限制政策（<c>ds.devpol.deep</c>）、
/// 資料安全摘要卡（<c>ds.dashboard</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一波的價值在「摘要卡」那一項。</b>其餘四項是把既有的計數展開成可判讀的形式，
/// 而摘要卡做的是本專案最難也最該做的一件事：<b>把多個訊號聚合成一句人話，同時說清楚
/// 它為什麼不是判決。</b>
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① 自動更新只陳述<b>設定狀態</b>，不宣稱「憑證一定會更新成功」（那要網路與 CA 配合）；
/// ② 自簽清單<b>不列主體名稱</b>——自簽憑證的 CN 常含機器名或使用者名；
/// ③ Smart App Control 的「評估中」不是「已啟用」，三態要分明；
/// ④ 裝置安裝限制只讀政策，不判「這台機器安不安全」；
/// ⑤ 摘要卡<b>聚合的是訊號不是分數</b>——不出一個 0–100 的分數，那只會讓人問「幾分算好」。
/// </para>
/// </remarks>
public static class DataSecurityDeepFactsService
{
    private const string Cat = "資料安全深化";

    // ── 鍵 ────────────────────────────────────────────────────────────────

    /// <summary>DS-006 憑證自動更新。</summary>
    public const string CertUpdateKey = "ds.cert.update";
    /// <summary>DS-007 自簽憑證清單。</summary>
    public const string SelfSignedKey = "ds.selfsigned.list";
    /// <summary>DS-010 Smart App Control。</summary>
    public const string SacKey = "ds.sac.deep";
    /// <summary>DS-011 裝置安裝限制政策。</summary>
    public const string DevPolKey = "ds.devpol.deep";
    /// <summary>DS-012 資料安全摘要卡。</summary>
    public const string DashboardKey = "ds.dashboard";

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>（已設定的自動更新項目, 停用的項目, 待更新項目, 上次檢查可讀）。</summary>
    public sealed record CertUpdateState(int Enabled, int Disabled, int Pending, bool LastCheckReadable);

    /// <summary>（自簽憑證數, 存放區分布數, 有到期日的數, 已過期數）。</summary>
    public sealed record SelfSignedState(int Count, int Stores, int WithExpiry, int Expired);

    /// <summary>（狀態值, 狀態名稱）——0＝關閉、1＝強制、2＝評估中、其他＝未知。</summary>
    public sealed record SacState(int Raw, string Name);

    /// <summary>（政策項數, 已設定項數, 阻擋安裝項數, 僅警告項數）。</summary>
    public sealed record DevPolState(int Total, int Configured, int Blocking, int WarnOnly);

    public delegate CertUpdateState? CertUpdateProbe();
    public delegate SelfSignedState? SelfSignedProbe();
    public delegate SacState? SacProbe();
    public delegate DevPolState? DevPolProbe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        CertUpdateProbe? certUpdate = null,
        SelfSignedProbe? selfSigned = null,
        SacProbe? sac = null,
        DevPolProbe? devPol = null)
    {
        var facts = new List<HardwareFact>(5)
        {
            CertUpdateFact(at, certUpdate),
            SelfSignedFact(at, selfSigned),
            SacFact(at, sac),
            DevPolFact(at, devPol),
        };
        facts.Add(DashboardFact(at, facts));
        return facts;
    }

    // ── DS-006 憑證自動更新 ───────────────────────────────────────────────

    public static HardwareFact CertUpdateFact(DateTimeOffset at, CertUpdateProbe? probe = null)
    {
        CertUpdateState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, CertUpdateKey, "憑證自動更新", FactAvailability.ReadError,
                "憑證自動更新設定讀不到——讀不到就是不猜（不把讀不到當成「已停用」）");

        var v = s;
        string check = v.LastCheckReadable
            ? "上次檢查時間可讀"
            : "<b>上次檢查時間讀不到</b>——沒有這個時間就無法判斷更新機制最近有沒有動過";
        return new HardwareFact(CertUpdateKey, Cat, "憑證自動更新",
            $"已設定 {v.Enabled} 項・停用 {v.Disabled} 項・待更新 {v.Pending} 項・{check}。" +
            "<b>設定狀態不是結果</b>：這裡回答的是「自動更新有沒有被設定」，" +
            "不是「憑證一定都更新成功」——後者要網路、要 CA 回應、要根憑證信任鏈配合，" +
            "任何一環不通都不會更新，而那些不會反映在這張表上。",
            "項", @"登錄（憑證自動更新政策，唯讀）", FactTrustLevel.Reported, false, at, v.Enabled);
    }

    // ── DS-007 自簽憑證清單 ───────────────────────────────────────────────

    public static HardwareFact SelfSignedFact(DateTimeOffset at, SelfSignedProbe? probe = null)
    {
        SelfSignedState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, SelfSignedKey, "自簽憑證清單", FactAvailability.ReadError,
                "憑證存放區列舉失敗——讀不到就是不猜");

        var v = s;
        if (v.Count == 0)
            return new HardwareFact(SelfSignedKey, Cat, "自簽憑證清單",
                "存放區裡沒有自簽憑證（0 是值，不是「沒檢查」）。",
                "張", "CertOpenStore 列舉（唯讀，不列主體名稱）", FactTrustLevel.Reported, false, at, 0);

        string expiry = v.WithExpiry == 0
            ? "沒有可讀的到期日"
            : $"其中 {v.WithExpiry} 張有到期日、{v.Expired} 張已過期";
        return new HardwareFact(SelfSignedKey, Cat, "自簽憑證清單",
            $"自簽憑證 {v.Count} 張・分布於 {v.Stores} 個存放區・{expiry}。" +
            "<b>不列主體名稱</b>：自簽憑證的 CN 常含機器名或使用者名（那正是它自簽的原因——" +
            "沒有公開 CA 可以代簽），列出來等於洩漏識別資訊。" +
            "自簽本身不是問題：開發、內部服務、測試環境大量使用；本項只陳述數量與存放位置分布。",
            "張", "CertOpenStore 列舉（唯讀，不列主體名稱）", FactTrustLevel.Reported, false, at, v.Count);
    }

    // ── DS-010 Smart App Control ─────────────────────────────────────────

    public static HardwareFact SacFact(DateTimeOffset at, SacProbe? probe = null)
    {
        SacState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, SacKey, "Smart App Control", FactAvailability.NotApplicable,
                "本機沒有 Smart App Control 政策鍵——不適用（此功能僅 Windows 11 全新安裝才有）");

        var v = s;
        // 0＝關閉、1＝強制、2＝評估中
        string meaning = v.Raw switch
        {
            0 => "關閉——不受 Smart App Control 限制",
            1 => "強制執行——未受信任的應用程式會被阻擋",
            2 => "<b>評估中</b>——系統還在收集判斷資料，<b>這不是「已啟用」</b>；" +
                 "評估期結束前它不會阻擋任何東西，把它讀成已保護是錯的",
            _ => $"未知狀態值 {v.Raw}（{v.Name}）——不猜語意",
        };
        return new HardwareFact(SacKey, Cat, "Smart App Control",
            $"狀態 {v.Raw}（{v.Name}）：{meaning}。" +
            "<b>三態分明</b>：關閉、強制、評估中是三件不同的事；" +
            "評估中是暫時狀態且不提供保護——本項如實區分，不合併成「有開」。",
            "", @"HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy（唯讀）",
            FactTrustLevel.Reported, false, at, v.Raw);
    }

    // ── DS-011 裝置安裝限制政策 ───────────────────────────────────────────

    public static HardwareFact DevPolFact(DateTimeOffset at, DevPolProbe? probe = null)
    {
        DevPolState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, DevPolKey, "裝置安裝限制政策", FactAvailability.ReadError,
                "裝置安裝限制政策讀不到——讀不到就是不猜");

        var v = s;
        if (v.Total == 0)
            return Unavailable(at, DevPolKey, "裝置安裝限制政策", FactAvailability.NotApplicable,
                "沒有可檢查的政策項——不適用");

        string verdict = v.Configured == 0
            ? "沒有任何政策項被設定——這台機器對裝置安裝沒有額外限制"
            : $"已設定 {v.Configured} 項（阻擋安裝 {v.Blocking}、僅警告 {v.WarnOnly}）";
        return new HardwareFact(DevPolKey, Cat, "裝置安裝限制政策",
            $"政策 {v.Total} 項・{verdict}。" +
            "<b>政策存在不等於安全，也不等於不安全</b>：企業機常靠它擋未授權周邊，" +
            "開發機則刻意全開；本項只陳述「有哪些限制被設定了」，" +
            "不判這台機器安不安全——那取決於它的用途。",
            "項", @"裝置安裝限制政策（唯讀）", FactTrustLevel.Reported, false, at, v.Configured);
    }

    // ── DS-012 資料安全摘要卡 ─────────────────────────────────────────────

    private static HardwareFact DashboardFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
    {
        int n = facts.Count;
        int present = facts.Count(f => f.Availability == FactAvailability.Present);
        int unavailable = n - present;
        var names = facts.Where(f => f.Availability != FactAvailability.Present)
            .Select(f => f.Name).ToList();

        string signal = unavailable == 0
            ? $"四項訊號全部可讀（{present}/{n}）"
            : $"可讀 {present}/{n} 項；不可讀：{string.Join("、", names)}" +
              "（<b>不可讀本身就是要看的訊號</b>——不是「沒問題」）";

        return new HardwareFact(DashboardKey, Cat, "資料安全摘要",
            $"{signal}。" +
            "<b>摘要卡聚合的是訊號不是分數</b>：本項刻意不產出「資料安全 87 分」這種數字——" +
            "分數會讓人問「幾分算好」，而那個答案不存在；有意義的是「哪幾項沒讀到、" +
            "哪幾項的狀態值得看一眼」。逐項明細在各自的鍵裡，這一張只負責讓人知道往哪裡看。",
            "項", "本批事實聚合（衍生）", FactTrustLevel.Derived, false, at, present);
    }

    // ── helper ────────────────────────────────────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
