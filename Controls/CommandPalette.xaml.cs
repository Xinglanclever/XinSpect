using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace XinSpect;

/// <summary>命令面板的一筆可執行項目。<see cref="Invoke"/> 於使用者確認後在 UI 執行緒呼叫。</summary>
public sealed class PaletteItem
{
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    /// <summary>右側種類徽章文字（頁面／動作／設定…）。</summary>
    public required string Kind { get; init; }
    public Geometry? Icon { get; init; }
    public string[] Keywords { get; init; } = [];
    public required Action Invoke { get; init; }
}

/// <summary>
/// 命令面板（Ctrl+K）：對所有頁面、實用工具與全域動作做模糊搜尋並直接執行。
/// 21 個主分頁 + 12 個子工具之後，這是導覽的必要品而非裝飾。
/// </summary>
public partial class CommandPalette : UserControl
{
    private readonly List<PaletteItem> _all = new();
    private readonly List<PaletteItem> _shown = new();

    public CommandPalette()
    {
        InitializeComponent();
        Results.ItemsSource = _shown;
    }

    /// <summary>以指定項目集開啟面板：清空查詢、重建結果、聚焦輸入框。</summary>
    public void Open(IEnumerable<PaletteItem> items)
    {
        _all.Clear();
        _all.AddRange(items);
        Query.Text = "";
        Refresh();
        Visibility = Visibility.Visible;
        // 版面尚未套用時 Focus() 會失效，排到 Loaded 之後的排程階段再聚焦
        Dispatcher.BeginInvoke(new Action(() => { Query.Focus(); Keyboard.Focus(Query); }),
                               System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        _all.Clear();
        _shown.Clear();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    // 依查詢重排結果：標題權重最高，關鍵字次之，說明最低。空查詢時維持註冊順序。
    private void Refresh()
    {
        string q = Query.Text.Trim();
        _shown.Clear();

        if (q.Length == 0)
        {
            _shown.AddRange(_all);
        }
        else
        {
            var scored = new List<(int Score, int Order, PaletteItem Item)>();
            for (int i = 0; i < _all.Count; i++)
            {
                var it = _all[i];
                var fields = new List<(string?, int)>(2 + it.Keywords.Length)
                {
                    (it.Title, 100),
                    (it.Subtitle, 55),
                };
                foreach (var k in it.Keywords) fields.Add((k, 80));

                int s = FuzzyMatch.Best(q, fields);
                if (s > 0) scored.Add((s, i, it));
            }
            // 同分時以註冊順序穩定排序，避免每次輸入都跳動
            scored.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Order.CompareTo(b.Order));
            foreach (var t in scored) _shown.Add(t.Item);
        }

        Results.Items.Refresh();
        bool any = _shown.Count > 0;
        Results.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (any) Results.SelectedIndex = 0;
    }

    /// <summary>由外殼在面板開啟時轉交按鍵；已處理回傳 true。</summary>
    public bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Escape:
                Close();
                return true;
            case Key.Down:
                Move(1);
                return true;
            case Key.Up:
                Move(-1);
                return true;
            case Key.Enter:
                InvokeSelected();
                return true;
            default:
                return false;
        }
    }

    private void Move(int delta)
    {
        if (_shown.Count == 0) return;
        int i = Results.SelectedIndex + delta;
        if (i < 0) i = _shown.Count - 1;
        else if (i >= _shown.Count) i = 0;
        Results.SelectedIndex = i;
        Results.ScrollIntoView(_shown[i]);
    }

    private void InvokeSelected()
    {
        if (Results.SelectedItem is not PaletteItem it) return;
        Close();                        // 先關閉：被呼叫的動作可能開對話框或切頁
        try { it.Invoke(); } catch { /* 單一項目失敗不得使面板或主程式崩潰 */ }
    }

    private void Results_Click(object sender, MouseButtonEventArgs e) => InvokeSelected();

    private void Backdrop_Click(object sender, MouseButtonEventArgs e) => Close();
}
