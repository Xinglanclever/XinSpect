using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 實用工具分頁：內建小工具的容器頁。左側子導覽由 <see cref="PageRegistry.Utilities"/> 資料繫結產生，
/// 工具檢視於首次選取時才建立（延遲實體化），並支援以鍵值程式化跳轉（命令面板用）。
/// 新增一個工具＝在 <see cref="PageRegistry.Utilities"/> 加一筆，不需改動本檔或 XAML。
/// </summary>
public partial class UtilitiesView : UserControl, IPageLifecycle
{
    private readonly Dictionary<string, UserControl> _cache = new(StringComparer.OrdinalIgnoreCase);
    private UserControl? _current;

    public UtilitiesView()
    {
        InitializeComponent();
        SubNav.ItemsSource = PageRegistry.Utilities;
        SubNav.SelectedIndex = 0;
    }

    private void SubNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SubNav.SelectedItem is not PageDef def) return;

        if (!_cache.TryGetValue(def.Key, out var view))
        {
            view = def.Factory();
            _cache[def.Key] = view;
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
            if (string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)) { SubNav.SelectedIndex = i; return; }
            i++;
        }
    }

    // 容器頁的生命週期向下轉交給目前顯示的子工具
    public void OnActivated() => (_current as IPageLifecycle)?.OnActivated();
    public void OnDeactivated() => (_current as IPageLifecycle)?.OnDeactivated();
}
