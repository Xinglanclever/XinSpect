using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace XinSpect;

/// <summary>
/// 「拜神」視窗：一包綠色乖乖、一爐香，與一個誠實的笑話。
/// </summary>
/// <remarks>
/// 這個視窗是娛樂功能，但它守同一條誠實線：乖度的輸入（近 7 天藍屏筆數、開機時長）是真的，
/// 讀不到就由 <see cref="ShrineMath.Obedience"/> 回 null 並顯示「—」；祈福語只許願、
/// 不下診斷（「你該清灰」那類句子永遠不會出現在這裡，測試 <c>ShrineTests</c> 守著）。
/// 香的煙只在使用者按「拜」時動一下，並尊重全站動態效果總開關（<see cref="Motion"/>）——
/// 沒有常駐裝飾動畫，量測期間也不會分心。
/// </remarks>
public partial class ShrineWindow : Window
{
    public ShrineWindow(int? bsod7d, long uptimeSeconds)
    {
        InitializeComponent();

        var (score, verdict, detail) = ShrineMath.Obedience(bsod7d, uptimeSeconds);
        ScoreText.Text = score?.ToString() ?? "—";
        VerdictText.Text = LanguageService.T(verdict);
        DetailText.Text = LanguageService.T(detail);

        // 簡體模式：視窗不在主視覺樹裡，開啟時自己轉換一次
        if (LanguageService.IsSimplified)
            LanguageService.ConvertVisualTree(this, true);
    }

    // 無邊框視窗：允許拖曳標題區移動。
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            try { DragMove(); } catch { /* 非滑鼠拖曳狀態呼叫會丟例外，忽略 */ }
    }

    // 拜：上香（若動態效果開著）＋抽一句不與上次重複的祈福語。
    private void Pray_Click(object sender, RoutedEventArgs e)
    {
        int index;
        do { index = Random.Shared.Next(ShrineMath.Blessings.Length); }
        while (index == _lastBlessingIndex && ShrineMath.Blessings.Length > 1);
        _lastBlessingIndex = index;

        _prayCount++;
        PrayCount.Text = $"×{_prayCount}";
        BlessingText.Text = LanguageService.T(ShrineMath.Blessings[index]);
        BlessingCard.Visibility = Visibility.Visible;

        if (Motion.Enabled)
        {
            ResetSmoke();
            BeginSmoke(longBurn: false);
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.45)) { EasingFunction = new QuadraticEase() };
            BlessingCard.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        else
        {
            // 動態效果關閉時不播任何動畫，只把句子放上去——誠實比熱鬧重要
            BlessingCard.Opacity = 1;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private bool _incenseCooldown;
    private int _lastBlessingIndex = -1;
    private int _prayCount;
    private int _incenseCount;

    // 燒香：播較長的煙霧動畫＋提示「已上香」＋按鈕變灰 5 秒。
    private async void Incense_Click(object sender, RoutedEventArgs e)
    {
        if (_incenseCooldown) return;
        _incenseCooldown = true;
        IncenseButton.IsEnabled = false;

        _incenseCount++;
        IncenseCount.Text = $"×{_incenseCount}";
        ResetSmoke();
        BlessingText.Text = LanguageService.T("已上香，心誠則靈");
        BlessingCard.Visibility = Visibility.Visible;
        BlessingCard.Opacity = 1;

        if (Motion.Enabled)
        {
            BeginSmoke(longBurn: true);
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.45)) { EasingFunction = new QuadraticEase() };
            BlessingCard.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        await Task.Delay(5000);
        _incenseCooldown = false;
        IncenseButton.IsEnabled = true;
    }

    /// <summary>清除煙霧的殘留動畫，確保下次播放從頭開始。</summary>
    private void ResetSmoke()
    {
        foreach (var smoke in (Ellipse[])[Smoke1, Smoke2, Smoke3])
        {
            smoke.BeginAnimation(UIElement.OpacityProperty, null);
            smoke.Opacity = 0;
            smoke.RenderTransform.BeginAnimation(TranslateTransform.YProperty, null);
            ((TranslateTransform)smoke.RenderTransform).Y = 0;
        }
    }

    /// <summary>
    /// 三縷煙往上飄：淡入淡出＋上移，一秒半內結束。只在使用者按下「拜」的那一下播，
    /// 不是常駐動畫，所以不必進量測暫停清單（<see cref="Motion.Suspend"/> 管的是持續性繪圖）。
    /// </summary>
    private void BeginSmoke(bool longBurn = false)
    {
        double fadeSeconds = longBurn ? 1.2 : 0.5;
        double riseSeconds = longBurn ? 3.5 : 1.5;
        double peakOpacity = longBurn ? 0.85 : 0.65;
        double riseDist    = longBurn ? -65 : -42;

        foreach (var smoke in (Ellipse[])[Smoke1, Smoke2, Smoke3])
        {
            var fade = new DoubleAnimation(0, peakOpacity, TimeSpan.FromSeconds(fadeSeconds))
            {
                AutoReverse = true,
                BeginTime = TimeSpan.FromSeconds(Random.Shared.NextDouble() * 0.2),
            };
            smoke.BeginAnimation(UIElement.OpacityProperty, fade);

            var rise = new DoubleAnimation(0, riseDist, TimeSpan.FromSeconds(riseSeconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                BeginTime = fade.BeginTime,
            };
            smoke.RenderTransform.BeginAnimation(TranslateTransform.YProperty, rise);
        }
    }
}