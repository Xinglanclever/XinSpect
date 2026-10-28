using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 首次啟程儀式：藍→紅黑漸變 + 一次即焚音樂。
/// 僅在新裝置首次選擇「詳細進階」模式時觸發一次，之後永不重播。
///
/// <para>
/// <b>音樂檔不隨倉庫走</b>：<c>Assets/ceremony.mp3</c> 是 5,294,701 bytes（整個倉庫最大的一個檔），
/// 而單檔發佈只帶 exe、不帶側檔——正式版的使用者其實從來沒有聽到過它。2026-10-28 應作者要求
/// 把它從倉庫移除，機制原封不動：誰想把音樂放回來，擲一個 <c>ceremony.mp3</c> 到 exe 旁邊
/// （或 <c>Assets\</c> 下）就會播放，播完照樣一次即焚。<b>沒有音樂檔時不執行儀式</b>，
/// 也就是與移除檔案之前的發佈版行為一致。
/// </para>
/// </summary>
public static class CeremonyService
{
    private const string MusicFileName = "ceremony.mp3";
    private const double TotalDurationSec = 3.0;
    private const int TickMs = 16; // ≈60fps

    /// <summary>是否應該執行儀式（尚未播放過且音樂檔存在）。</summary>
    public static bool ShouldRun(SettingsService settings)
        => !settings.CeremonyPlayed && FindMusic() is not null;

    /// <summary>
    /// 執行完整的啟程儀式。在 UI 執行緒上呼叫。
    /// 完成後標記為已播放並持久化，音樂檔自動刪除。
    /// </summary>
    public static async Task RunAsync(SettingsService settings)
    {
        string? musicPath = FindMusic();

        // 播放音樂（如果檔案存在）
        MediaPlayer? player = null;
        if (musicPath is not null)
        {
            try
            {
                player = new MediaPlayer();
                player.Open(new Uri(musicPath, UriKind.Absolute));
                player.Play();
            }
            catch { player = null; }
        }

        // 記住起始色票
        var startAccent = ThemeService.FindAccent("blue");
        var endAccent = ThemeService.FindAccent("rog");

        // 漸變動畫：用 DispatcherTimer 驅動，每 16ms 一幀
        var tcs = new TaskCompletionSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        timer.Tick += (_, _) =>
        {
            double elapsed = sw.Elapsed.TotalSeconds;
            if (elapsed >= TotalDurationSec)
            {
                timer.Stop();
                // 最終狀態：完整切到 EE 主題
                ThemeService.Theme = AppTheme.ExtremeEdition;
                tcs.TrySetResult();
                return;
            }

            // 進度 0→1
            double t = Math.Clamp(elapsed / TotalDurationSec, 0, 1);

            // 1.5 秒內完成背景色漸變（透過直接設定中間色）
            if (t < 0.5)
            {
                // 前半段：維持藍色主題，微微變暗
            }
            else
            {
                // 後半段：切換到 EE，讓 ThemeService 處理
                if (ThemeService.Theme != AppTheme.ExtremeEdition)
                    ThemeService.Theme = AppTheme.ExtremeEdition;
            }
        };

        timer.Start();
        await tcs.Task;

        // 等音樂播完再刪除（或 5 分鐘上限）
        if (player is not null && musicPath is not null)
        {
            _ = Task.Run(async () =>
            {
                // 等待音樂播完（輪詢 NaturalDuration）
                await Task.Delay(TimeSpan.FromMinutes(5));
                try
                {
                    Shell.BeginOnUi(() => { try { player.Stop(); player.Close(); } catch { } });
                    await Task.Delay(500);
                    if (File.Exists(musicPath)) File.Delete(musicPath);
                }
                catch { /* 刪不掉就算了 */ }
            });
        }

        // 標記為已播放（setter 內部會自動存檔）
        settings.CeremonyPlayed = true;
    }

    /// <summary>找到音樂檔的完整路徑；不存在則回 null。</summary>
    private static string? FindMusic()
    {
        // 先找執行檔旁邊
        string? dir = Path.GetDirectoryName(Environment.ProcessPath);
        if (dir is not null)
        {
            string path = Path.Combine(dir, MusicFileName);
            if (File.Exists(path)) return path;
        }
        // 再找 Assets 子資料夾
        if (dir is not null)
        {
            string path = Path.Combine(dir, "Assets", MusicFileName);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
