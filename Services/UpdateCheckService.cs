using System.Net.Http;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 檢查更新（v2.55，關於頁使用者主動點擊才觸發）：只向 GitHub Releases API 發一次 GET
/// 拿最新版本號——<b>不上傳本機任何資料</b>，硬體事實依然零網路（資料主權聲明不變）。
/// 判定三態誠實：有新版／已是最新／<b>查不到（網路不通、GitHub 限流或回應無法解析——
/// 如實顯示，不假裝已是最新）</b>。網路路徑登記在 DataSovereigntyTests 的 opt-in 允許清單；
/// 測試一律走純函數 Judge，不碰真網路。
/// </summary>
public static class UpdateCheckService
{
    public const string ReleasesApiUrl = "https://api.github.com/repos/Xinglanclever/XinSpect/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/Xinglanclever/XinSpect/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>判定結果：UpdateAvailable／UpToDate 至多一個為真；兩者皆假＝查不到（Detail 說明原因）。</summary>
    public sealed record Verdict(bool UpdateAvailable, bool UpToDate, string? LatestVersion, string Detail);

    /// <summary>
    /// 純函數判定：本地版本 vs releases/latest 的 JSON。查不到時 LatestVersion 為 null，
    /// <b>不得被讀成「已是最新」</b>——那是把「不知道」冒充成「沒問題」。
    /// </summary>
    public static Verdict Judge(string localVersion, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Verdict(false, false, null, "查不到（網路不通或 GitHub 限流）——程式不假裝已是最新。");

        string? tag;
        try
        {
            using var doc = JsonDocument.Parse(json);
            tag = doc.RootElement.GetProperty("tag_name").GetString();
        }
        catch
        {
            return new Verdict(false, false, null, "查不到（GitHub 回應無法解析）——程式不假裝已是最新。");
        }

        if (string.IsNullOrWhiteSpace(tag))
            return new Verdict(false, false, null, "查不到（GitHub 沒有回報版本標籤）——程式不假裝已是最新。");

        string latest = tag.TrimStart('v', 'V');
        if (CompareVersions(latest, localVersion) > 0)
            return new Verdict(true, false, latest, $"有新版 v{latest}（目前 v{localVersion}）——可到下載頁取得。");
        return new Verdict(false, true, latest, $"已是最新版（v{localVersion}）。");
    }

    /// <summary>逐段數字比較；缺段視為 0。2.10 &gt; 2.9——逐字串比較會把這個判反，所以這裡必須是 int。</summary>
    public static int CompareVersions(string a, string b)
    {
        int[] Parse(string v) => v.Split('.').Select(s => int.TryParse(s, out int n) ? n : 0).ToArray();
        var pa = Parse(a); var pb = Parse(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : 0;
            int y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>唯一會碰網路的方法：抓 releases/latest JSON 交給純函數判定；任何失敗都折回「查不到」。</summary>
    public static async Task<Verdict> CheckAsync(string localVersion)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            req.Headers.UserAgent.ParseAdd("XinSpect-UpdateCheck");
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Judge(localVersion, null);
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return Judge(localVersion, json);
        }
        catch
        {
            return Judge(localVersion, null);
        }
    }
}
