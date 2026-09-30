using System.Windows.Controls;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 實用工具分頁：內建小工具的容器頁。左側子導覽由 <see cref="PageRegistry.Utilities"/> 資料繫結產生，
/// 工具檢視於首次選取時才建立（延遲實體化），並支援以鍵值程式化跳轉（命令面板用）。
/// 新增一個工具＝在 <see cref="PageRegistry.Utilities"/> 加一筆，不需改動本檔或 XAML。
/// </summary>
/// <summary>子導覽項目：包住 <see cref="PageDef"/> 並附加可用性檢測狀態（✓ 可載入／⚠ 建構失敗）。</summary>
public class UtilityNavItem
{
    public UtilityNavItem(PageDef def) => Def = def;
    public PageDef Def { get; }
    public string Title => Def.Title;
    public string Icon => Def.IconData;
    public string Hint => Def.Hint ?? "";

    private string _status = "";
    /// <summary>空字串＝正常（含尚未檢測與建構成功，不顯示任何記號）；「⚠」＝建構失敗才標出。</summary>
    public string Status { get => _status; set { _status = value; StatusChanged?.Invoke(); } }
    public event Action? StatusChanged;
}

public partial class UtilitiesView : UserControl, IPageLifecycle
{
    private readonly Dictionary<string, UserControl> _cache = new(StringComparer.OrdinalIgnoreCase);
    private UserControl? _current;
    private List<UtilityNavItem>? _items;

    public UtilitiesView()
    {
        InitializeComponent();
        _items = PageRegistry.Utilities.Select(d => new UtilityNavItem(d)).ToList();
        SubNav.ItemsSource = _items;
        SubNav.SelectedIndex = 0;
        Loaded += (_, _) => ProbeAll();
    }

    /// <summary>
    /// 可用性自我檢測：載入後逐台在背景優先序把每個子工具實際建構一次。
    /// 建構要 WPF STA，不能丟背景執行緒；一次排一行，滑起來不卡。
    /// 順帶把建好的檢視留在快取裡，之後第一次點它不用再等。
    /// </summary>
    private void ProbeAll()
    {
        if (_items is null) return;
        var queue = new Queue<UtilityNavItem>(_items.Where(i => i.Status.Length == 0));
        void Next()
        {
            if (queue.Count == 0) return;
            var item = queue.Dequeue();
            try
            {
                if (!_cache.TryGetValue(item.Def.Key, out var view))
                {
                    view = item.Def.Factory();
                    _cache[item.Def.Key] = view;
                }
                item.Status = "";   // 正常不標記，維持清單乾淨
            }
            catch (Exception ex)
            {
                Diag.Swallow("子工具自我檢測", ex, $"「{item.Def.Title}」建構失敗，清單會標 ⚠");
                item.Status = "⚠";
            }
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, Next);
        }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, Next);
    }

    private void SubNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SubNav.SelectedItem is not UtilityNavItem item) return;
        var def = item.Def;

        if (!_cache.TryGetValue(def.Key, out var view))
        {
            try
            {
                view = def.Factory();
                _cache[def.Key] = view;
            }
            catch (Exception ex)
            {
                // 單一子工具建構失敗（缺執行階段元件等）不得讓整個實用工具容器倒下
                Diag.Swallow("子工具載入", ex, $"「{def.Title}」無法建立，維持目前工具");
                return;
            }
        }
        if (ReferenceEquals(_current, view)) return;

        (_current as IPageLifecycle)?.OnDeactivated();
        _current = view;
        ToolHost.Content = view;
        (view as IPageLifecycle)?.OnActivated();
    }

    /// <summary>切換到指定鍵值的子工具（供命令面板深層跳轉）。找不到則不動作。</summary>
    public void SelectTool(string key)
    {
        int i = 0;
        foreach (var d in PageRegistry.Utilities)
        {
            if (string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase))
            { if (_items is not null && i < _items.Count) SubNav.SelectedItem = _items[i]; return; }
            i++;
        }
    }

    // 容器頁的生命週期向下轉交給目前顯示的子工具
    public void OnActivated() => (_current as IPageLifecycle)?.OnActivated();
    public void OnDeactivated() => (_current as IPageLifecycle)?.OnDeactivated();
}
