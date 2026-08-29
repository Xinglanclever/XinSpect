using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 超頻模組的持久化：風險對話框「不再顯示」旗標、看門狗 / 開機還原偏好，
/// 以及 .ocp 設定檔與「最後穩定設定」的存取。全部存於 %APPDATA%\XinSpect\Overclock\。
/// </summary>
public sealed class OcSettings
{
    public static string RootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect", "Overclock");

    public static string ProfilesDir => Path.Combine(RootDir, "Profiles");
    private static string SettingsPath => Path.Combine(RootDir, "settings.json");
    private static string LastStablePath => Path.Combine(RootDir, "last-stable.ocp");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 保留中文，不轉義為 \uXXXX
    };

    // ── 持久旗標 ───────────────────────────────────────────────────────────
    public bool DontShowRisk { get; set; }
    public bool BootRestore { get; set; } = true;    // 預設開啟開機還原（斷電/當機後回穩）
    public bool WatchdogEnabled { get; set; }

    public static OcSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<OcSettings>(File.ReadAllText(SettingsPath), Json);
                if (s is not null) return s;
            }
        }
        catch { /* 設定毀損時退回預設 */ }
        return new OcSettings();
    }

    public void Save()
    {
        try
        {
            EnsureDir(RootDir);
            AtomicWrite.AllText(SettingsPath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* 無法寫入設定不影響本次操作 */ }
    }

    // ── .ocp 設定檔 ────────────────────────────────────────────────────────

    /// <summary>存到內建設定檔資料夾（檔名依 Profile 名稱）。回傳實際路徑。</summary>
    public static string SaveProfile(OcProfile p)
    {
        EnsureDir(ProfilesDir);
        string path = Path.Combine(ProfilesDir, Sanitize(p.Name) + ".ocp");
        AtomicWrite.AllText(path, JsonSerializer.Serialize(p, Json));
        return path;
    }

    /// <summary>匯出到任意路徑（供「另存 / 匯出」用）。</summary>
    public static void ExportProfile(OcProfile p, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(p, Json));

    public static OcProfile? LoadProfile(string path)
    {
        try { return JsonSerializer.Deserialize<OcProfile>(File.ReadAllText(path), Json); }
        catch { return null; }
    }

    public static IReadOnlyList<(string Name, string Path)> ListProfiles()
    {
        var result = new List<(string, string)>();
        try
        {
            if (Directory.Exists(ProfilesDir))
                foreach (var f in Directory.EnumerateFiles(ProfilesDir, "*.ocp"))
                    result.Add((Path.GetFileNameWithoutExtension(f), f));
        }
        catch { }
        return result;
    }

    // ── 最後穩定設定（當機重開後自動回復用）─────────────────────────────────

    public static void SaveLastStable(OcProfile p)
    {
        try { EnsureDir(RootDir); AtomicWrite.AllText(LastStablePath, JsonSerializer.Serialize(p, Json)); }
        catch { }
    }

    public static OcProfile? LoadLastStable()
    {
        try { return File.Exists(LastStablePath) ? JsonSerializer.Deserialize<OcProfile>(File.ReadAllText(LastStablePath), Json) : null; }
        catch { return null; }
    }

    // ── 工具 ───────────────────────────────────────────────────────────────

    private static void EnsureDir(string dir)
    {
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "未命名";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }
}
