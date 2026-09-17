namespace BlueSquadron;

/// <summary>
/// 基線雜湊存儲：保存首次偵測到的安全狀態，後續比對時若有差異即產生威脅事件。
/// 儲存在 %LOCALAPPDATA%\XinSpect\bluesquadron\baseline.json。
/// </summary>
internal sealed class BaselineStore
{
    private readonly string _path;
    private Dictionary<string, string> _baseline = [];

    public BaselineStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XinSpect", "bluesquadron");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "baseline.json");
    }

    /// <summary>載入已存基線。</summary>
    public void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                _baseline = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            }
        }
        catch { _baseline = []; }
    }

    /// <summary>儲存目前基線。</summary>
    public void Save()
    {
        try
        {
            string json = System.Text.Json.JsonSerializer.Serialize(_baseline,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch { }
    }

    /// <summary>
    /// 設定或更新一個基線值。若是首次設定回傳 null；若值變更回傳舊值。
    /// </summary>
    public string? Set(string key, string value)
    {
        if (_baseline.TryGetValue(key, out var old))
        {
            if (old == value) return null;
            _baseline[key] = value;
            return old;  // changed!
        }
        _baseline[key] = value;
        return null;  // first set
    }

    /// <summary>取得基線值。</summary>
    public string? Get(string key)
        => _baseline.GetValueOrDefault(key);

    /// <summary>所有基線項目。</summary>
    public IReadOnlyDictionary<string, string> All => _baseline;

    /// <summary>
    /// 快照目前事實到基線。回傳有變更的項目清單（key, oldValue, newValue）。
    /// </summary>
    public List<(string Key, string Old, string New)> Snapshot(SecurityFacts facts)
    {
        var changes = new List<(string, string, string)>();

        void Check(string key, string? value)
        {
            if (value is null) return;
            string? old = Set(key, value);
            if (old is not null)
                changes.Add((key, old, value));
        }

        Check("hvci", facts.HvciRunning?.ToString());
        Check("vbs", facts.VbsRunning?.ToString());
        Check("secureboot", facts.SecureBootEnabled?.ToString());
        Check("testsigning", facts.TestSigningEnabled?.ToString());
        Check("iommu", facts.IommuAvailable?.ToString());
        Check("dma_protection", facts.DmaProtection?.ToString());
        Check("credential_guard", facts.CredentialGuardRunning?.ToString());
        Check("total_drivers", facts.TotalDriverCount.ToString());
        Check("unsigned_drivers", facts.UnsignedDriverCount.ToString());
        Check("driver_blocklist", facts.VulnerableDriverBlocklistPresent?.ToString());

        return changes;
    }
}
