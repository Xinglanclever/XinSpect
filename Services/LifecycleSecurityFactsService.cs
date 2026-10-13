namespace XinSpect;

/// <summary>
/// 生命週期與安全收口（Vol2 第 395–399 項）：PL-002 安裝器升級測試（<c>pl.upgrade</c>）、
/// PL-003 設定遷移測試（<c>pl.cfgmigrate</c>）、PL-004 多語安裝器（<c>pl.i18ninstaller</c>）、
/// SE-009 憑證漫遊（<c>se.credroam</c>，L2）、SE-011 ESP 簽章基線（<c>se.espbaseline</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>紅線：</b>① 安裝／遷移類是<b>測試結果</b>不是產品功能——本工具不執行安裝；② 憑證漫遊是 L2：
/// 只陳述政策存在性，<b>永不匯出憑證內容</b>；③ ESP 簽章基線與既有 ESP 掃描交叉，
/// 基線不符是<b>攻擊面事實</b>不是中毒判決；④ 讀不到一律三態。
/// </para>
/// </remarks>
public static class LifecycleSecurityFactsService
{
    private const string CatLife = "產品平台";
    private const string CatSec = "安全審計";

    public const string UpgradeKey = "pl.upgrade";
    public const string CfgMigrateKey = "pl.cfgmigrate";
    public const string I18nInstallerKey = "pl.i18ninstaller";
    public const string CredRoamKey = "se.credroam";
    public const string EspBaselineKey = "se.espbaseline";

    public delegate (bool UpgradeTested, int FromSchema, int ToSchema)? UpgradeProbe();
    public delegate (bool RoundTripOk, int SchemaVersion)? CfgMigrateProbe();
    public delegate (int Languages, bool InstallerReady)? I18nProbe();
    public delegate (bool PolicyPresent, bool Enabled)? CredRoamProbe();
    public delegate (int Files, int ChangedFromBaseline)? EspBaselineProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        UpgradeProbe? upgrade = null,
        CfgMigrateProbe? cfgMigrate = null,
        I18nProbe? i18n = null,
        CredRoamProbe? credRoam = null,
        EspBaselineProbe? espBaseline = null)
        =>
        [
            UpgradeFact(at, upgrade),
            CfgMigrateFact(at, cfgMigrate),
            I18nFact(at, i18n),
            CredRoamFact(at, credRoam),
            EspBaselineFact(at, espBaseline),
        ];

    // ── PL-002 安裝器升級測試 ──────────────────────────────────────────

    public static HardwareFact UpgradeFact(DateTimeOffset at, UpgradeProbe? probe = null)
    {
        (bool UpgradeTested, int FromSchema, int ToSchema)? s;
        try { s = (probe ?? (() => (false, 0, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, UpgradeKey, CatLife, "安裝器升級測試", FactAvailability.ReadError, "升級狀態不可讀");

        var (tested, from, to) = s.Value;
        if (!tested)
            return new HardwareFact(UpgradeKey, CatLife, "安裝器升級測試",
                "舊→新升級<b>未施測</b>。這是測試結果不是功能——本工具不執行安裝；" +
                "未施測不代表升級可用。",
                "", "升級測試（唯讀宣告）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable, "升級未施測");

        return new HardwareFact(UpgradeKey, CatLife, "安裝器升級測試",
            $"已施測 schema v{from}→v{to}。<b>遷移測試</b>驗證舊設定／舊資料能被新版讀取；" +
            "通過代表這條遷移路徑沒問題，不代表所有跳版組合都通過。不列路徑。",
            "", "升級測試（唯讀）", FactTrustLevel.Reported, false, at, to);
    }

    // ── PL-003 設定遷移測試 ────────────────────────────────────────────

    public static HardwareFact CfgMigrateFact(DateTimeOffset at, CfgMigrateProbe? probe = null)
    {
        (bool RoundTripOk, int SchemaVersion)? s;
        try { s = (probe ?? (() => (false, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, CfgMigrateKey, CatLife, "設定遷移測試", FactAvailability.ReadError, "設定 schema 不可讀");

        var (ok, ver) = s.Value;
        return new HardwareFact(CfgMigrateKey, CatLife, "設定遷移測試",
            $"設定 schema v{ver}、往返={(ok ? "通過" : "未通過")}。" +
            "<b>往返測試</b>：讀入→寫出→再讀入 要得到相同結果；不通過代表設定格式有相容性問題。" +
            "本項只陳述測試結果，不修改任何設定檔。",
            "", "設定 schema 往返（唯讀）", FactTrustLevel.Reported, false, at, ver);
    }

    // ── PL-004 多語安裝器 ──────────────────────────────────────────────

    public static HardwareFact I18nFact(DateTimeOffset at, I18nProbe? probe = null)
    {
        (int Languages, bool InstallerReady)? s;
        try { s = (probe ?? (() => (1, false)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, I18nInstallerKey, CatLife, "多語安裝器", FactAvailability.ReadError, "安裝器語言狀態不可讀");

        var (langs, ready) = s.Value;
        return new HardwareFact(I18nInstallerKey, CatLife, "多語安裝器",
            $"安裝器語言 {langs} 種、就緒={(ready ? "是" : "否")}。" +
            "多語安裝器是<b>產品方向</b>（便攜單一 exe 無安裝器，多語安裝器屬安裝包路線）——" +
            "與 PL-001 雙包裝決策連動，未定案前只陳述狀態。",
            "種", "安裝器語言（唯讀宣告）", FactTrustLevel.Reported, false, at, langs);
    }

    // ── SE-009 憑證漫遊（L2）──────────────────────────────────────────

    public static HardwareFact CredRoamFact(DateTimeOffset at, CredRoamProbe? probe = null)
    {
        (bool PolicyPresent, bool Enabled)? s;
        try { s = (probe ?? (() => ReadCredRoam()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, CredRoamKey, CatSec, "憑證漫遊", FactAvailability.ReadError,
                "憑證漫遊政策不可讀——讀不到就是不猜");

        var (present, enabled) = s.Value;
        return new HardwareFact(CredRoamKey, CatSec, "憑證漫遊",
            present
                ? $"企業憑證漫遊政策{(enabled ? "已啟用" : "未啟用")}（僅存在性）。" +
                  "<b>永不匯出憑證內容</b>：本項只陳述政策是否存在與啟用狀態，不讀憑證、不讀私鑰。" +
                  "憑證漫遊讓憑證跟隨使用者帳戶跨機器同步——是政策決定不是風險判決。"
                : "未設定憑證漫遊政策（Windows 預設不寫此登錄）。<b>未設定不等於關閉</b>——" +
                  "可能是用預設值，本項不代填預設。",
            "", "登錄政策（唯讀，L2）", FactTrustLevel.Reported, false, at, enabled ? 1 : 0);
    }

    private static (bool, bool) ReadCredRoam()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation");
            if (key is null) return (false, false);
            object? v = key.GetValue("AllowProtectedCreds");
            return (true, v is int i && i != 0);
        }
        catch { return (false, false); }
    }

    // ── SE-011 ESP 簽章基線 ────────────────────────────────────────────

    public static HardwareFact EspBaselineFact(DateTimeOffset at, EspBaselineProbe? probe = null)
    {
        (int Files, int ChangedFromBaseline)? s;
        try { s = (probe ?? (() => ReadEspBaseline()))(); }
        catch { s = null; }

        if (s is null || s.Value.Files == 0)
            return Unavailable(at, EspBaselineKey, CatSec, "ESP 簽章基線", FactAvailability.NotApplicable,
                "沒有 ESP 檔案或無基線可比較——缺一不判");

        var (files, changed) = s.Value;
        return new HardwareFact(EspBaselineKey, CatSec, "ESP 簽章基線",
            $"ESP 檔案 {files} 個、與基線不同 {changed} 個。" +
            "<b>攻擊面事實非中毒判決</b>：ESP 檔案變更（與既有 ESP 掃描交叉）可能來自系統更新、" +
            "開機管理員更換或攻擊——本項只陳述差異，不下結論。不列檔名或雜湊。",
            "個", "ESP 掃描↔基線 diff（唯讀）", FactTrustLevel.Reported, false, at, changed);
    }

    private static (int, int) ReadEspBaseline()
    {
        try { return (0, 0); }   // 無基線檔時回 0 檔案 → N/A，不冒充
        catch { return (0, 0); }
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
