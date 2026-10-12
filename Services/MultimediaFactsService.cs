using System.Diagnostics;
using System.IO;

namespace XinSpect;

/// <summary>
/// 多媒體事實（Vol2 推進第 99–101 項）：MU-001 編解碼器清單、MU-005 音訊端點（麥克風陣列面向）、
/// MU-007 媒體基礎診斷。
/// </summary>
/// <remarks>
/// <para><b>全部唯讀且只報數量與版本</b>：編解碼器清單只讀 Drivers32 登錄的註冊項，不測播放；
/// 音訊端點只數 MMDevices 的端點數與使用中數量，<b>不錄音、不讀任何音訊內容</b>；
/// Media Foundation 只報平台鍵與 mfplat.dll 版本，不做播放測試。</para>
/// <para><b>界線</b>：登錄清單不等於「所有可用編解碼器」（Store 版與 MF transform 不在 Drivers32）；
/// 端點存在不等於有麥克風陣列硬體；版本不代表功能完整。</para>
/// </remarks>
public static class MultimediaFactsService
{
    private const string Cat = "多媒體";

    public const string CodecCountKey = "mu.codec.count";
    public const string CodecSummaryKey = "mu.codec.summary";
    public const string MicCountKey = "mu.mic.count";
    public const string MicSummaryKey = "mu.mic.summary";
    public const string MfKey = "mu.mf.summary";

    /// <summary>登錄值讀取（值名→字串值）。</summary>
    public delegate IReadOnlyDictionary<string, string>? RegistryMapProbe(string path);

    /// <summary>端點列舉（回 (總數, 使用中數量)）。</summary>
    public delegate (int Total, int Active)? EndpointProbe();

    public delegate string? FileVersionProbe(string path);
    public delegate bool KeyExistsProbe(string path);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        RegistryMapProbe? drivers32 = null,
        EndpointProbe? captureEndpoints = null,
        FileVersionProbe? fileVersion = null,
        KeyExistsProbe? keyExists = null)
    {
        var facts = new List<HardwareFact>(5);
        facts.AddRange(CodecFacts(at, drivers32 ?? ReadDrivers32));
        facts.AddRange(MicFacts(at, captureEndpoints ?? ReadCaptureEndpoints));
        facts.Add(MfFact(at, fileVersion ?? ReadFileVersion, keyExists ?? ReadKeyExists));
        return facts;
    }

    // ── MU-001 編解碼器清單 ─────────────────────────────────────────────

    private static IReadOnlyList<HardwareFact> CodecFacts(DateTimeOffset at, RegistryMapProbe probe)
    {
        IReadOnlyDictionary<string, string>? map;
        try { map = probe(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32"); }
        catch { map = null; }
        if (map is null)
        {
            const string why = "Drivers32 登錄讀取失敗——讀不到就是不猜";
            return
            [
                new HardwareFact(CodecCountKey, Cat, "編解碼器註冊數", "", "", @"HKLM\...\Drivers32",
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, why),
                new HardwareFact(CodecSummaryKey, Cat, "編解碼器清單", "", "", @"HKLM\...\Drivers32",
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, why),
            ];
        }

        var byPrefix = map.Keys
            .Select(k =>
            {
                int cut = k.IndexOfAny(['.', ' ']);
                return (cut > 0 ? k[..cut] : k).ToUpperInvariant();
            })
            .GroupBy(p => p, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        string breakdown = string.Join("、", byPrefix.Take(8).Select(g => $"{g.Key}×{g.Count()}"));
        return
        [
            new HardwareFact(CodecCountKey, Cat, "編解碼器註冊數",
                map.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "項",
                @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32",
                FactTrustLevel.Measured, false, at, map.Count),
            new HardwareFact(CodecSummaryKey, Cat, "編解碼器清單",
                $"Drivers32 註冊 {map.Count} 項：{breakdown}。" +
                "界線：登錄清單不等於「所有可用編解碼器」（Store 版與 MF transform 不在此），也不測能不能播——只陳述註冊事實。",
                "項", @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32",
                FactTrustLevel.Measured, false, at),
        ];
    }

    private static IReadOnlyDictionary<string, string>? ReadDrivers32(string path)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            if (key is null) return new Dictionary<string, string>();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in key.GetValueNames())
                map[name] = key.GetValue(name)?.ToString() ?? "";
            return map;
        }
        catch { return null; }
    }

    // ── MU-005 音訊端點（麥克風陣列面向；不錄音）─────────────────────

    private static IReadOnlyList<HardwareFact> MicFacts(DateTimeOffset at, EndpointProbe probe)
    {
        (int Total, int Active)? snap;
        try { snap = probe(); }
        catch { snap = null; }
        if (snap is null)
        {
            const string why = "音訊端點列舉失敗——讀不到就是不猜";
            return
            [
                new HardwareFact(MicCountKey, Cat, "擷取端點數", "", "", @"HKLM\...\MMDevices\Audio\Capture",
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, why),
                new HardwareFact(MicSummaryKey, Cat, "擷取端點摘要", "", "", @"HKLM\...\MMDevices\Audio\Capture",
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, why),
            ];
        }

        var (total, active) = snap.Value;
        return
        [
            new HardwareFact(MicCountKey, Cat, "擷取端點數",
                total.ToString(System.Globalization.CultureInfo.InvariantCulture), "個",
                @"HKLM\...\MMDevices\Audio\Capture（端點計數）", FactTrustLevel.Measured, false, at, total),
            new HardwareFact(MicSummaryKey, Cat, "擷取端點摘要",
                $"擷取端點 {total} 個（使用中 {active} 個）。" +
                "界線：<b>只數端點，不錄音、不讀任何音訊內容</b>；端點存在也不等於有麥克風陣列硬體（陣列是裝置的事實，不是端點數）。",
                "個", @"HKLM\...\MMDevices\Audio\Capture（只讀 DeviceState）", FactTrustLevel.Measured, false, at, total),
        ];
    }

    private static (int Total, int Active)? ReadCaptureEndpoints()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture");
            if (root is null) return (0, 0);
            int total = 0, active = 0;
            foreach (string name in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(name);
                if (sub is null) continue;
                total++;
                if (sub.GetValue("DeviceState") is int st && st == 1) active++;
            }
            return (total, active);
        }
        catch { return null; }
    }

    // ── MU-007 媒體基礎診斷 ─────────────────────────────────────────────

    private static HardwareFact MfFact(DateTimeOffset at, FileVersionProbe version, KeyExistsProbe keyExists)
    {
        bool platform;
        string? ver;
        try
        {
            platform = keyExists(@"SOFTWARE\Microsoft\Windows Media Foundation\Platform");
            ver = version(Path.Combine(Environment.SystemDirectory, "mfplat.dll"));
        }
        catch
        {
            return new HardwareFact(MfKey, Cat, "媒體基礎診斷", "", "", "mfplat.dll＋MF Platform",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "Media Foundation 資訊讀取失敗——不推測");
        }

        return new HardwareFact(MfKey, Cat, "媒體基礎診斷",
            $"MF Platform 登錄鍵 {(platform ? "存在" : "不存在")}・mfplat.dll 版本 {ver ?? "（讀不到）"}。" +
            "界線：版本與鍵存在只說明平台元件在；能不能播某種格式要看對應解碼器，本項不測播放。",
            "", @"HKLM\SOFTWARE\Microsoft\Windows Media Foundation\Platform＋System32\mfplat.dll",
            FactTrustLevel.Reported, false, at);
    }

    private static string? ReadFileVersion(string path)
    {
        try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null; }
        catch { return null; }
    }

    private static bool ReadKeyExists(string path)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
        return key is not null;
    }
}
