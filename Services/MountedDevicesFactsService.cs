using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// MountedDevices 掛載對照事實服務（Vol 2 批次 B／FS-009）。
/// <para>
/// 列舉 <c>HKLM\SYSTEM\MountedDevices</c> 的全部值名：<c>\DosDevices\C:</c>（12 位元組＝4 位元組磁碟簽章
/// ＋8 位元組分割區起始位移）與 <c>\??\Volume{GUID}</c>（UTF-16 裝置路徑字串）。
/// </para>
/// <para>
/// <b>界線</b>：這是本機安裝以來<b>掛載過</b>的裝置對照（歷史紀錄）——拔掉的裝置條目會留下，
/// 不是「現在在線」清單；磁碟簽章與位移是分割區層事實，不解讀成裝置型號或健康狀態。
/// </para>
/// </summary>
public static class MountedDevicesFactsService
{
    public const string Category = "檔案鑑識";
    public const string CountKey = "fs.mount.count";
    public const string LettersKey = "fs.mount.letters";
    public const string VolumesKey = "fs.mount.volumes";

    /// <summary>登錄機碼路徑。</summary>
    public const string KeyPath = @"SYSTEM\MountedDevices";

    private const string Source = "登錄檔 MountedDevices（唯讀）";

    /// <summary>本機收集。probe 注入式：測試餵 (值名, 位元組) 清單，不碰真登錄檔。</summary>
    public static IReadOnlyList<HardwareFact> CollectForThisMachine(DateTimeOffset at,
        Func<IReadOnlyList<KeyValuePair<string, byte[]>>?>? probe = null) =>
        Collect(at, probe ?? ReadRegistryValues);

    /// <summary>從真登錄檔列舉值。機碼不存在回 null（＝NotApplicable）；機碼在回值清單（可能空）；讀取失敗丟例外（＝ReadError）。</summary>
    public static IReadOnlyList<KeyValuePair<string, byte[]>>? ReadRegistryValues()
    {
        var result = new List<KeyValuePair<string, byte[]>>();
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        if (key is null) return null;
        foreach (string name in key.GetValueNames())
        {
            if (key.GetValue(name) is byte[] b) result.Add(new KeyValuePair<string, byte[]>(name, b));
        }
        return result;
    }

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Func<IReadOnlyList<KeyValuePair<string, byte[]>>?> probe)
    {
        IReadOnlyList<KeyValuePair<string, byte[]>>? values;
        try
        {
            values = probe();
        }
        catch (Exception ex)
        {
            return
            [
                new HardwareFact(CountKey, Category, "MountedDevices 掛載對照", "", "筆", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    "登錄機碼讀取失敗（" + ex.GetType().Name + "）——讀不到不等於沒有掛載紀錄"),
            ];
        }

        if (values is null)
            return
            [
                new HardwareFact(CountKey, Category, "MountedDevices 掛載對照", "", "筆", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "登錄機碼不存在（" + KeyPath + " 缺席）——不冒充「沒有掛載紀錄」"),
            ];

        if (values.Count == 0)
            return
            [
                new HardwareFact(CountKey, Category, "MountedDevices 掛載對照", "機碼在、0 筆值", "筆", Source,
                    FactTrustLevel.Derived, false, at, 0.0, FactAvailability.NotApplicable,
                    "機碼存在但沒有任何掛載值——不適用（如實列，不冒充裝置在線）"),
            ];

        var letters = values.Where(v => v.Key.StartsWith(@"\DosDevices\", StringComparison.Ordinal)).ToList();
        var volumes = values.Where(v => v.Key.StartsWith(@"\??\Volume{", StringComparison.Ordinal)).ToList();
        string letterText = letters.Count == 0
            ? "沒有碟號指派"
            : string.Join(" ", letters.Select(l => l.Key.Substring(@"\DosDevices\".Length)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        string volumeText = string.Join("；", volumes.Take(12).Select(v =>
        {
            string guid = v.Key.Substring(@"\??\".Length);
            string? target = TryDecodeVolumeTarget(v.Value);
            return target is null ? guid + "（裝置路徑未解碼）" : guid + "→" + target;
        }))
            + (volumes.Count > 12 ? "；等 " + (volumes.Count - 12) + " 個" : "");
        string boundary = "這是安裝以來掛載過的裝置對照（歷史紀錄）——拔掉的裝置條目會留下，不是現在在線清單；"
            + "簽章與位移是分割區層事實，不解讀成裝置型號或健康狀態";

        return
        [
            new HardwareFact(CountKey, Category, "MountedDevices 掛載對照",
                $"碟號 {letters.Count} 筆＋磁碟區 GUID {volumes.Count} 筆＝{values.Count} 筆" + (values.Count != letters.Count + volumes.Count
                    ? $"（另有 {values.Count - letters.Count - volumes.Count} 筆其他形式值，未逐一解碼）" : "") + "・" + boundary,
                "筆", Source, FactTrustLevel.Derived, false, at, values.Count),
            new HardwareFact(LettersKey, Category, "MountedDevices 碟號指派", letterText, "", Source,
                FactTrustLevel.Derived, false, at),
            new HardwareFact(VolumesKey, Category, "MountedDevices 磁碟區 GUID 路徑",
                (volumeText.Length == 0 ? "沒有磁碟區 GUID 值" : volumeText) + "・原樣解碼 UTF-16（部分條目是 Windows 的跳脫名，不代譯）", "", Source,
                FactTrustLevel.Derived, false, at),
        ];
    }

    /// <summary>解碼 <c>\??\Volume{GUID}</c> 的目標裝置路徑（UTF-16LE 字串；解不出回 null 不猜）。</summary>
    public static string? TryDecodeVolumeTarget(byte[] data)
    {
        if (data.Length < 2 || data.Length % 2 != 0) return null;
        try
        {
            string s = System.Text.Encoding.Unicode.GetString(data);
            return s.EndsWith("\0", StringComparison.Ordinal) ? s[..^1] : s;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>解碼 <c>\DosDevices\X:</c> 的二進位（4 位元組磁碟簽章＋8 位元組分割區起始位移）。</summary>
    public static (uint Signature, long Offset)? TryDecodeDosDevice(byte[] data)
    {
        if (data.Length != 12) return null;
        uint sig = BitConverter.ToUInt32(data, 0);
        long offset = BitConverter.ToInt64(data, 4);
        return (sig, offset);
    }
}
