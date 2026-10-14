using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 媒體保護與空間音訊深化（Vol2 第 MU-003、MU-006 項）：
/// <c>mu.drm.components</c>（DRM 元件盤點）、<c>mu.spatial.formats</c>（空間音訊格式）。
/// </summary>
/// <remarks>
/// <para>
/// <b>與既有能力的界線：</b><c>mu.drm.summary</c> 只回答「PlayReady 有沒有登錄」、
/// <c>mu.spatial.summary</c> 只回答「有幾個端點宣告啟用空間音訊」。
/// 兩個問題都停在<b>有／沒有</b>；本項問的是<b>「哪幾個元件、哪一種格式」</b>——
/// 「PlayReady 存在」與「Widevine 存在」對「這個網站能不能播」是兩件事，
/// 「空間音訊開了」與「開的是 Windows Sonic 還是 Dolby Atmos」也是兩件事。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① <b>元件存在不等於可以播</b>——DRM 要能播還需要授權伺服器可達、金鑰可取、
/// 內容與裝置的保護層級相符；本項只陳述元件在不在；
/// ② <b>不讀授權、不讀金鑰、不讀內容識別碼</b>——那些是權利資訊不是能力資訊；
/// ③ 空間音訊<b>「支援格式」與「目前使用中」是兩件事</b>：登錄列的是這台機器
/// 認得的格式，不代表現在播放的那一條音軌正在用它；
/// ④ <b>格式名稱不在地化</b>——註冊名稱是產品識別，翻譯它只會讓兩台機器看起來不同。
/// </para>
/// </remarks>
public static class MediaProtectionFactsService
{
    private const string Cat = "多媒體";
    private const string Source = "本機唯讀登錄列舉（PlayReady／Widevine／空間音訊元件）";

    /// <summary>MU-003 DRM 元件盤點。</summary>
    public const string DrmComponentsKey = "mu.drm.components";
    /// <summary>MU-006 空間音訊格式。</summary>
    public const string SpatialFormatsKey = "mu.spatial.formats";

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>一個 DRM 元件：名稱（產品識別，不翻譯）與可讀版本（null＝登錄沒給版本）。</summary>
    public sealed record DrmComponent(string Name, string? Version);

    /// <summary>（已註冊的空間音訊格式名稱清單, 端點總數, 有任一格式可用的端點數）。</summary>
    public sealed record SpatialFormatsState(IReadOnlyList<string> Formats, int Endpoints,
        int EndpointsWithFormat);

    public delegate IReadOnlyList<DrmComponent>? DrmProbe();
    public delegate SpatialFormatsState? SpatialProbe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        DrmProbe? drm = null,
        SpatialProbe? spatial = null)
        =>
        [
            DrmComponentsFact(at, drm),
            SpatialFormatsFact(at, spatial),
        ];

    // ── MU-003 DRM 元件 ───────────────────────────────────────────────────

    public static HardwareFact DrmComponentsFact(DateTimeOffset at, DrmProbe? probe = null)
    {
        IReadOnlyList<DrmComponent>? rows;
        try { rows = (probe ?? ReadDrmComponents)(); }
        catch { rows = null; }

        if (rows is null)
            return Unavailable(at, DrmComponentsKey, "DRM 元件盤點", FactAvailability.ReadError,
                "DRM 元件登錄列舉失敗——讀不到就是不猜（不把讀不到讀成沒有元件）");

        if (rows.Count == 0)
            return Unavailable(at, DrmComponentsKey, "DRM 元件盤點", FactAvailability.NotApplicable,
                "本機沒有回報任何已知的 DRM 元件——不適用。" +
                "注意：沒有元件不等於不能播串流——部分服務走瀏覽器內建或硬體路徑，" +
                "那些不登記在這幾個機碼下");

        string detail = string.Join("、", rows.Select(r =>
            r.Version is { Length: > 0 } v && v != "0.0.0.0" ? $"{r.Name}（{v}）" : r.Name));

        return new HardwareFact(DrmComponentsKey, Cat, "DRM 元件盤點",
            $"{rows.Count} 個元件：{detail}。" +
            "<b>元件存在不等於可以播</b>：要真的播放還需要授權伺服器可達、金鑰取得成功、" +
            "而且內容要求的保護層級與這台機器的輸出鏈相符——本項只陳述元件在不在。" +
            "<b>不讀授權、不讀金鑰、不讀內容識別碼</b>：那些是權利資訊，不是能力資訊。" +
            "元件名稱刻意保留原文不翻譯——那是產品識別，翻過來只會讓兩台機器看起來不一樣。",
            "個", Source, FactTrustLevel.Reported, false, at, rows.Count);
    }

    /// <summary>
    /// 登錄通路：已知 DRM 元件的機碼存在性。<b>只取名稱與版本</b>，
    /// 刻意不下鑽任何授權或金鑰子機碼。
    /// </summary>
    internal static IReadOnlyList<DrmComponent>? ReadDrmComponents()
    {
        // （顯示名, 機碼路徑）——清單固定且可稽核：不掃描全登錄猜測哪些是 DRM。
        (string Name, string Path)[] candidates =
        [
            ("PlayReady", @"SOFTWARE\Microsoft\PlayReady"),
            ("PlayReady (WOW64)", @"SOFTWARE\WOW6432Node\Microsoft\PlayReady"),
            ("Widevine", @"SOFTWARE\Google\WidevineCdm"),
            ("Widevine (WOW64)", @"SOFTWARE\WOW6432Node\Google\WidevineCdm"),
            ("Primetime", @"SOFTWARE\Adobe\Primetime"),
        ];

        var list = new List<DrmComponent>();
        try
        {
            foreach (var (name, path) in candidates)
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key is null) continue;
                string? version = key.GetValue("CurrentVersion")?.ToString()
                    ?? key.GetValue("Version")?.ToString();
                list.Add(new DrmComponent(name, version));
            }
        }
        catch { return null; }
        return list;
    }

    // ── MU-006 空間音訊格式 ───────────────────────────────────────────────

    public static HardwareFact SpatialFormatsFact(DateTimeOffset at, SpatialProbe? probe = null)
    {
        SpatialFormatsState? s;
        try { s = (probe ?? ReadSpatialFormats)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, SpatialFormatsKey, "空間音訊格式", FactAvailability.ReadError,
                "空間音訊格式列舉失敗——讀不到就是不猜");

        var v = s;
        if (v.Endpoints == 0)
            return Unavailable(at, SpatialFormatsKey, "空間音訊格式", FactAvailability.NotApplicable,
                "沒有渲染音訊端點——不適用（沒有端點不等於格式不支援；接上耳機或螢幕後再讀）");

        string formats = v.Formats.Count == 0
            ? "登錄中<b>沒有回報任何空間音訊格式</b>——不代表不支援，只代表這個登錄面沒有列出來"
            : string.Join("、", v.Formats);

        return new HardwareFact(SpatialFormatsKey, Cat, "空間音訊格式",
            $"已註冊格式 {v.Formats.Count} 種：{formats}・端點 {v.Endpoints} 個，" +
            $"其中 {v.EndpointsWithFormat} 個至少有一種格式可用。" +
            "<b>「支援格式」與「目前使用中」是兩件事</b>：這裡列的是這台機器認得的格式，" +
            "不代表現在播放的音軌正在用它——那是播放器與音軌自己的事。" +
            "<b>格式名稱不在地化</b>：那是產品識別（Windows Sonic／Dolby Atmos…），" +
            "翻譯它只會讓兩台機器看起來不同，不會增加任何資訊。" +
            "本項不開音訊裝置、不測試空間效果、不判斷音質。",
            "種", Source, FactTrustLevel.Reported, false, at, v.Formats.Count);
    }

    /// <summary>
    /// 登錄通路：空間音訊格式清單與端點可用性。<b>只取格式註冊名稱</b>，
    /// 不下鑽端點屬性（那是 <c>mu.spatial.summary</c> 的事）。
    /// </summary>
    internal static SpatialFormatsState? ReadSpatialFormats()
    {
        try
        {
            // 格式註冊：SpatialAudio 的 Metadata 子機碼下每個格式一個子機碼。
            var formats = new List<string>();
            using (var meta = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\SpatialAudio\Metadata"))
            {
                if (meta is not null)
                    formats.AddRange(meta.GetSubKeyNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            }

            int endpoints = 0, withFormat = 0;
            using (var root = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"))
            {
                if (root is not null)
                    foreach (string endpoint in root.GetSubKeyNames())
                    {
                        endpoints++;
                        using var sub = root.OpenSubKey(endpoint);
                        if (sub is null) continue;
                        // 端點有 Properties 子機碼且宣告空間音訊能力時算「有格式可用」。
                        using var props = sub.OpenSubKey("Properties");
                        if (props is null) continue;
                        if (props.GetValueNames().Any(n =>
                                n.Contains("Spatial", StringComparison.OrdinalIgnoreCase)))
                            withFormat++;
                    }
            }
            return new SpatialFormatsState(formats, endpoints, withFormat);
        }
        catch { return null; }
    }

    // ── helper ────────────────────────────────────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
