using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace XinSpect.Tests;

[Collection(WpfCollection.Name)]
public sealed class LanguageLifecycleTests
{
    [Fact]
    public void 舊bool入口必須退出英文並同步設定() => OnSta((settings, windows) =>
    {
        LanguageService.SetLanguage(AppLanguage.English, settings);
        LanguageService.SetLanguage(true, settings);
        Assert.False(LanguageService.IsEnglish);
        Assert.False(settings.IsEnglish);
        Assert.True(settings.SimplifiedChinese);
        Assert.Equal("内存", LanguageService.ToSimplified("記憶體"));
        Assert.Null(EnglishStrings.Reverse("記憶體"));
        Assert.Null(UiTextTranslation.OriginalFromEnglishTemplate("記憶體"));
        Assert.Null(UiTextTranslation.TranslateTemplate("記憶體", simplified: true));
        Assert.Equal("已附加 3 個檔案", UiTextTranslation.OriginalFromEnglishTemplate("3 files attached"));
        Assert.Equal("内存", LanguageService.T("記憶體"));
        LanguageService.SetLanguage(false, settings);
        Assert.Equal(AppLanguage.Traditional, LanguageService.Language);
        Assert.Equal("記憶體", LanguageService.T("記憶體"));
    });

    [Fact]
    public void 獨立視窗和之後才載入的內容共用語言生命週期() => OnSta((settings, windows) =>
    {
        var first = Host(windows, new Button { Content = "記憶體" });
        var second = Host(windows, new Button { Content = "總覽" });
        first.Title = "記憶體";
        second.Title = "總覽";
        LanguageService.SetLanguage(AppLanguage.English, settings);
        Assert.Equal("Memory", first.Title);
        Assert.Equal("Overview", second.Title);

        var late = new Button { Content = "顯示主視窗" };
        ((StackPanel)first.Content).Children.Add(late);
        Pump();
        Assert.True(late.IsLoaded);
        Assert.True(WpfLocalizationBridge.WatchesForTests(late, ContentControl.ContentProperty));
        var third = Host(windows, new Button { Content = "記憶體" });
        third.Title = "記憶體";
        Pump();
        Assert.Equal("Show main window", late.Content);
        Assert.Equal("Memory", third.Title);
        LanguageService.SetLanguage(AppLanguage.Simplified, settings);
        Assert.Equal("内存", first.Title);
        Assert.Equal("显示主窗口", late.Content);
        LanguageService.SetLanguage(AppLanguage.Traditional, settings);
        Assert.Equal("記憶體", first.Title);
        Assert.Equal("顯示主視窗", late.Content);
    });

    [Fact]
    public void 動態內容要更新原文槽而不是重播首次內容() => OnSta((settings, windows) =>
    {
        var button = new Button { Content = "總覽" };
        Host(windows, button);
        LanguageService.SetLanguage(AppLanguage.English, settings);
        button.Content = "記憶體";
        Assert.Equal("Memory", button.Content);
        for (int i = 0; i < 3; i++)
        {
            LanguageService.SetLanguage(AppLanguage.Simplified, settings);
            Assert.Equal("内存", button.Content);
            LanguageService.SetLanguage(AppLanguage.Traditional, settings);
            Assert.Equal("記憶體", button.Content);
            LanguageService.SetLanguage(AppLanguage.English, settings);
            Assert.Equal("Memory", button.Content);
        }
    });

    [Fact]
    public void Panel移除尚未Loaded的新增子項會解除延後事件() => OnSta((settings, windows) =>
    {
        var host = Host(windows, new Button { Content = "總覽" });
        var panel = (StackPanel)host.Content;
        var late = new Button { Content = "記憶體" };
        panel.Children.Add(late);
        panel.UpdateLayout();
        Assert.False(late.IsLoaded);
        Assert.Equal(1, WpfLocalizationBridge.PendingPanelLoadsForTests(panel));

        panel.Children.Remove(late);
        panel.UpdateLayout();
        Pump();

        Assert.Equal(0, WpfLocalizationBridge.PendingPanelLoadsForTests(panel));
        Assert.Equal(0, WpfLocalizationBridge.ObserverCountForTests(late));
    });

    [Fact]
    public void 保留原Binding與converter和StringFormat並持續接收來源變動() => OnSta((settings, windows) =>
    {
        var source = new Probe();
        var label = new TextBlock();
        var binding = new Binding(nameof(Probe.Label)) { Source = source };
        label.SetBinding(TextBlock.TextProperty, binding);
        var count = new TextBlock();
        var converter = new AddOneConverter();
        var formattedBinding = new Binding(nameof(Probe.Number))
        {
            Source = source, Converter = converter, StringFormat = "{0} 執行緒",
        };
        count.SetBinding(TextBlock.TextProperty, formattedBinding);
        Host(windows, label, count);
        var expression = label.GetBindingExpression(TextBlock.TextProperty);
        var formattedExpression = count.GetBindingExpression(TextBlock.TextProperty);
        LanguageService.SetLanguage(AppLanguage.English, settings);
        Assert.Equal("Memory", label.Text);
        Assert.Equal("9 threads", count.Text);
        Assert.Equal("記憶體", source.Label);
        source.Label = "總覽";
        source.Number = 11;
        Pump();
        Assert.Equal("Overview", label.Text);
        Assert.Equal("12 threads", count.Text);
        LanguageService.SetLanguage(AppLanguage.Simplified, settings);
        Assert.Equal("总览", label.Text);
        Assert.Equal("12 线程", count.Text);
        LanguageService.SetLanguage(AppLanguage.Traditional, settings);
        Assert.Equal("總覽", label.Text);
        Assert.Equal("12 執行緒", count.Text);
        Assert.Same(expression, label.GetBindingExpression(TextBlock.TextProperty));
        Assert.Same(formattedExpression, count.GetBindingExpression(TextBlock.TextProperty));
        Assert.Same(binding, expression!.ParentBinding);
        Assert.Same(converter, formattedExpression!.ParentBinding.Converter);
        Assert.Equal("{0} 執行緒", formattedExpression.ParentBinding.StringFormat);
        Assert.Equal(11, source.Number);
    });

    [Fact]
    public void MultiBinding和既有LangConverter切語言會刷新且不替換expression() => OnSta((settings, windows) =>
    {
        var source = new Probe();
        var multiText = new TextBlock();
        var multi = new MultiBinding { Converter = new ThreadSummaryConverter() };
        multi.Bindings.Add(new Binding(nameof(Probe.Number)) { Source = source });
        multi.Bindings.Add(new Binding(nameof(Probe.Number)) { Source = source });
        multiText.SetBinding(TextBlock.TextProperty, multi);
        var langText = new TextBlock();
        langText.SetBinding(TextBlock.TextProperty, new Binding(nameof(Probe.Label))
        { Source = source, Converter = ChineseConverter.Instance });
        Host(windows, multiText, langText);
        var expression = BindingOperations.GetMultiBindingExpression(multiText, TextBlock.TextProperty);
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Simplified, AppLanguage.Traditional })
        {
            LanguageService.SetLanguage(language, settings);
            Assert.Equal(language == AppLanguage.English ? "All 16 threads saturated"
                : language == AppLanguage.Simplified ? "全部 16 线程满载" : "全部 16 執行緒滿載", multiText.Text);
            Assert.Equal(language == AppLanguage.English ? "Memory"
                : language == AppLanguage.Simplified ? "内存" : "記憶體", langText.Text);
        }
        Assert.Same(expression, BindingOperations.GetMultiBindingExpression(multiText, TextBlock.TextProperty));
    });

    [Fact]
    public void 巢狀Run選單工具提示和表格欄標頭都可往返且保留結構() => OnSta((settings, windows) =>
    {
        var source = new Probe();
        var bound = new Run();
        bound.SetBinding(Run.TextProperty, new Binding(nameof(Probe.Label)) { Source = source, Mode = BindingMode.OneWay });
        var bold = new Bold(bound);
        var span = new Span(bold);
        var text = new TextBlock(span);
        var nested = new MenuItem { Header = "記憶體" };
        var menu = new ContextMenu();
        var top = new MenuItem { Header = "總覽" };
        top.Items.Add(nested);
        menu.Items.Add(top);
        var tipText = new TextBlock { Text = "記憶體" };
        var button = new Button { Content = "總覽", ContextMenu = menu, ToolTip = new ToolTip { Content = tipText } };
        var grid = new DataGrid { AutoGenerateColumns = false, Height = 45 };
        var column = new DataGridTextColumn { Header = "記憶體" };
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new Binding());
        column.HeaderTemplate = new DataTemplate { VisualTree = factory };
        grid.Columns.Add(column);
        Host(windows, text, button, grid);
        Assert.True(WpfLocalizationBridge.WatchesForTests(bound, Run.TextProperty));
        LanguageService.SetLanguage(AppLanguage.English, settings);
        Pump();
        Assert.Equal("Memory", bound.Text);
        Assert.Equal("Overview", top.Header);
        Assert.Equal("Memory", nested.Header);
        Assert.Equal("Memory", RenderedText(tipText));
        Assert.Equal("Memory", column.Header);
        column.Header = "總覽";
        Assert.Equal("Overview", column.Header);
        LanguageService.SetLanguage(AppLanguage.Simplified, settings);
        Pump();
        Assert.Equal("内存", bound.Text);
        Assert.Equal("总览", column.Header);
        LanguageService.SetLanguage(AppLanguage.Traditional, settings);
        Pump();
        Assert.Equal("記憶體", bound.Text);
        Assert.Equal("總覽", column.Header);
        Assert.Same(span, text.Inlines.FirstInline);
        Assert.Same(bold, span.Inlines.FirstInline);
        Assert.NotNull(bound.GetBindingExpression(Run.TextProperty));
        Assert.Equal("記憶體", source.Label);
        source.Label = "總覽";
        Pump();
        Assert.Equal("總覽", bound.Text);
        LanguageService.SetLanguage(AppLanguage.English, settings);
        Pump();
        Assert.Equal("Overview", bound.Text);
        Assert.Equal("總覽", source.Label);
        Assert.NotNull(bound.GetBindingExpression(Run.TextProperty));
    });

    [Fact]
    public void Popup更換子內容和下拉項目開啟後仍套用目前語言() => OnSta((settings, windows) =>
    {
        var item = new ComboBoxItem { Content = "記憶體" };
        var combo = new ComboBox();
        combo.Items.Add(item);
        var popup = new Popup { Child = new TextBlock { Text = "總覽" }, StaysOpen = true, PlacementTarget = combo };
        Host(windows, combo, popup);
        Assert.True(WpfLocalizationBridge.WatchesForTests(popup, Popup.ChildProperty));
        LanguageService.SetLanguage(AppLanguage.English, settings);
        Assert.Equal("Memory", item.Content);
        bool childChanged = false;
        var descriptor = DependencyPropertyDescriptor.FromProperty(Popup.ChildProperty, typeof(Popup))!;
        EventHandler changed = (_, _) => childChanged = true;
        descriptor.AddValueChanged(popup, changed);
        popup.Child = new TextBlock { Text = "記憶體" };
        descriptor.RemoveValueChanged(popup, changed);
        Assert.True(childChanged);
        Pump();
        Assert.Equal("Memory", RenderedText((DependencyObject)popup.Child));
        popup.IsOpen = true;
        Pump();
        Assert.Equal("Memory", RenderedText((DependencyObject)popup.Child));
        popup.IsOpen = false;
        combo.IsDropDownOpen = true;
        Pump();
        Assert.Equal("Memory", item.Content);
        combo.IsDropDownOpen = false;
        LanguageService.SetLanguage(AppLanguage.Simplified, settings);
        Assert.Equal("内存", ((TextBlock)popup.Child).Text);
        LanguageService.SetLanguage(AppLanguage.Traditional, settings);
        Assert.Equal("記憶體", item.Content);
        Assert.Equal("記憶體", ((TextBlock)popup.Child).Text);
    });

    [Fact]
    public void TwoWay含中文StringFormat和MultiBinding也不可回寫翻譯到來源() => OnSta((settings, windows) =>
    {
        var source = new Probe { Label = "payload" };
        var formatted = new TextBlock();
        formatted.SetBinding(TextBlock.TextProperty, new Binding(nameof(Probe.Label))
        {
            Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            StringFormat = "{0} 記憶體",
        });
        var multiText = new TextBlock();
        var multi = new MultiBinding
        {
            Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            Converter = new ReversibleSummaryConverter(), StringFormat = "{0} 記憶體",
        };
        multi.Bindings.Add(new Binding(nameof(Probe.Label)) { Source = source });
        multiText.SetBinding(TextBlock.TextProperty, multi);
        Host(windows, formatted, multiText);
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Simplified, AppLanguage.Traditional })
        {
            LanguageService.SetLanguage(language, settings);
            Pump();
            Assert.Equal("payload", source.Label);
            Assert.Equal("payload 記憶體", formatted.Text);
            Assert.Equal("payload 記憶體", multiText.Text);
        }
        Assert.NotNull(formatted.GetBindingExpression(TextBlock.TextProperty));
        Assert.NotNull(BindingOperations.GetMultiBindingExpression(multiText, TextBlock.TextProperty));
    });

    [Fact]
    public void 不改動硬體名稱原始讀值路徑使用者輸入或雙向來源() => OnSta((settings, windows) =>
    {
        var source = new Probe();
        var name = Bound(nameof(Probe.Name));
        var data = Bound(nameof(Probe.ValueText));
        var conversation = Bound(nameof(Probe.Text));
        var path = new TextBlock { Text = source.Path };
        var registry = Bound(nameof(Probe.RegistryPath));
        var externalName = Bound(nameof(Probe.DisplayName));
        var rawButton = new Button();
        rawButton.SetBinding(ContentControl.ContentProperty, new Binding(nameof(Probe.ValueText)) { Source = source });
        var input = new TextBox { Text = "這是使用者的記憶體資料" };
        var twoWay = new TextBlock();
        twoWay.SetBinding(TextBlock.TextProperty, new Binding(nameof(Probe.Label))
        { Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Host(windows, name, data, conversation, path, input, twoWay, registry, externalName, rawButton);
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Simplified, AppLanguage.Traditional })
        {
            LanguageService.SetLanguage(language, settings);
            Assert.Equal("Memory", name.Text);
            Assert.Equal("記憶體", data.Text);
            Assert.Equal("使用者說記憶體壞了", conversation.Text);
            Assert.Equal(source.Path, path.Text);
            Assert.Equal(source.RegistryPath, registry.Text);
            Assert.Equal(source.DisplayName, externalName.Text);
            Assert.Equal("記憶體", rawButton.Content);
            Assert.DoesNotContain("Memory", RenderedText(rawButton));
            Assert.Equal("這是使用者的記憶體資料", input.Text);
            Assert.Equal("記憶體", source.Label);
            Assert.Equal("記憶體", twoWay.Text);
            Assert.Equal("Memory", ChineseConverter.Instance.Convert("Memory", typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal(source.Path, ChineseConverter.Instance.Convert(source.Path, typeof(string), null, CultureInfo.InvariantCulture));
        }

        TextBlock Bound(string property)
        {
            var block = new TextBlock();
            block.SetBinding(TextBlock.TextProperty, new Binding(property) { Source = source });
            return block;
        }
    });

    [Fact]
    public void 唯讀產品說明可明確選用翻譯且原始紀錄不被泛轉() => OnSta((settings, windows) =>
    {
        const string payload = @"C:\使用者\記憶體\report.txt";
        const string raw = "raw-device-記憶體-identifier喵";
        var report = new TextBox { IsReadOnly = true, Text = "記憶體\r\n來源：" + payload + "\r\n" + raw };
        LanguageService.SetIsUiText(report, true);
        var log = new TextBox { IsReadOnly = true, Text = "記憶體" };
        Host(windows, report, log);
        LanguageService.SetLanguage(AppLanguage.English, settings);
        Assert.Equal("Memory\r\nSource: " + payload + "\r\n" + raw, report.Text);
        Assert.Equal("記憶體", log.Text);
        LanguageService.SetLanguage(AppLanguage.Simplified, settings);
        Assert.Equal("内存\r\n来源：" + payload + "\r\n" + raw, report.Text);
        LanguageService.SetLanguage(AppLanguage.Traditional, settings);
        Assert.Equal("記憶體\r\n來源：" + payload + "\r\n" + raw, report.Text);
        LanguageService.ApplyCatLanguage(true, settings);
        Assert.Contains(raw, report.Text);
        Assert.Contains(payload, report.Text);
    });

    [Fact]
    public void 英文格式化內容先建立後切繁簡要保留參數原值() => OnSta((settings, windows) =>
    {
        LanguageService.SetLanguage(AppLanguage.English, settings);
        const string payload = @"C:\使用者\記憶體\report.txt";
        var button = new Button { Content = LanguageService.T("來源：" + payload) };
        Host(windows, button);
        Assert.Equal("Source: " + payload, button.Content);
        LanguageService.SetLanguage(AppLanguage.Simplified, settings);
        Assert.Equal("来源：" + payload, button.Content);
        LanguageService.SetLanguage(AppLanguage.Traditional, settings);
        Assert.Equal("來源：" + payload, button.Content);
        LanguageService.SetLanguage(AppLanguage.English, settings);
        Assert.Equal("Source: " + payload, button.Content);
        Assert.Equal("1,234 items", LanguageService.T("1,234 筆"));
        Assert.Equal("Memory", LanguageService.T("内存"));
    });

    [Fact]
    public void 關閉視窗解除observer且後續切語言不得重新掛到離線節點() => OnSta((settings, windows) =>
    {
        var text = new TextBlock { Text = "記憶體" };
        var source = new Probe();
        var boundRun = new Run();
        boundRun.SetBinding(Run.TextProperty, new Binding(nameof(Probe.Label)) { Source = source });
        var boundRunHost = new TextBlock(new Span(boundRun));
        var column = new DataGridTextColumn { Header = "記憶體" };
        var grid = new DataGrid();
        grid.Columns.Add(column);
        var popupAnchor = new Button();
        var popupChild = new TextBlock { Text = "記憶體" };
        var popup = new Popup { Child = popupChild, PlacementTarget = popupAnchor, StaysOpen = true };
        var window = Host(windows, text, boundRunHost, grid, popupAnchor, popup);
        popup.IsOpen = true;
        Pump();
        Assert.True(WpfLocalizationBridge.ObserverCountForTests(text) > 0);
        Assert.True(WpfLocalizationBridge.ObserverCountForTests(boundRun) > 0);
        Assert.True(WpfLocalizationBridge.ObserverCountForTests(popup) > 0);
        Assert.True(WpfLocalizationBridge.ObserverCountForTests(popupChild) > 0);
        Assert.True(WpfLocalizationBridge.ObserverCountForTests(column) > 0);
        grid.Columns.Remove(column);
        Assert.Equal(0, WpfLocalizationBridge.ObserverCountForTests(column));
        var addedColumn = new DataGridTextColumn { Header = "記憶體" };
        grid.Columns.Add(addedColumn);
        Assert.True(WpfLocalizationBridge.ObserverCountForTests(addedColumn) > 0);
        window.Close();
        Pump();
        Assert.Equal(0, WpfLocalizationBridge.ObserverCountForTests(text));
        Assert.Equal(0, WpfLocalizationBridge.ObserverCountForTests(boundRun));
        Assert.Equal(0, WpfLocalizationBridge.ObserverCountForTests(popup));
        Assert.Equal(0, WpfLocalizationBridge.ObserverCountForTests(popupChild));
        Assert.Equal(0, WpfLocalizationBridge.ObserverCountForTests(column));
        Assert.Equal(0, WpfLocalizationBridge.ObserverCountForTests(addedColumn));
        LanguageService.SetLanguage(AppLanguage.English, settings);
        Assert.Equal(0, WpfLocalizationBridge.ObserverCountForTests(text));
        Assert.Equal(0, WpfLocalizationBridge.ObserverCountForTests(boundRun));
        Assert.Equal(0, WpfLocalizationBridge.ObserverCountForTests(popupChild));
        Assert.Equal(0, WpfLocalizationBridge.ObserverCountForTests(column));
        Assert.Equal(0, WpfLocalizationBridge.ObserverCountForTests(addedColumn));
        var offscreen = new TextBlock { Text = "記憶體" };
        LanguageService.ConvertVisualTree(offscreen, false);
        Assert.Equal("Memory", RenderedText(offscreen));
        Assert.Equal(0, WpfLocalizationBridge.ObserverCountForTests(offscreen));
    });

    private sealed class Probe : INotifyPropertyChanged
    {
        private string _label = "記憶體";
        private int _number = 8;
        public string Label { get => _label; set { _label = value; Changed(nameof(Label)); } }
        public int Number { get => _number; set { _number = value; Changed(nameof(Number)); } }
        public string Name => "Memory";
        public string ValueText => "記憶體";
        public string Text => "使用者說記憶體壞了";
        public string Path => @"C:\使用者\記憶體\report.txt";
        public string RegistryPath => @"HKEY_CLASSES_ROOT\*\shell\開啟自訂";
        public string DisplayName => "使用者的開啟自訂";
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private sealed class AddOneConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (int)value + 1;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private sealed class ThreadSummaryConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => $"全部 {(int)values[0] + (int)values[1]} 執行緒滿載";
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private sealed class ReversibleSummaryConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values[0];
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [value];
    }

    private static string RenderedText(DependencyObject root)
    {
        if (root is AccessText access) return access.Text;
        if (root is TextBlock text)
            return BindingOperations.IsDataBound(text, TextBlock.TextProperty)
                ? text.Text : string.Concat(text.Inlines.Select(RenderInline));
        return string.Join("", Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(root))
            .Select(i => RenderedText(VisualTreeHelper.GetChild(root, i))));
    }

    private static string RenderInline(Inline inline) => inline switch
    {
        Run run => run.Text,
        LineBreak => "\n",
        Span span => string.Concat(span.Inlines.Select(RenderInline)),
        _ => "",
    };

    private static Window Host(List<Window> windows, params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children) panel.Children.Add(child);
        var window = new Window
        {
            Content = panel, Width = 340, Height = 260, Left = -5000, Top = -5000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        windows.Add(window);
        window.Show();
        Pump();
        return window;
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void OnSta(Action<SettingsService, List<Window>> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var windows = new List<Window>();
            try
            {
                WpfEnv.Ensure();
                LanguageService.Initialize(false, false, false);
                action(new SettingsService(loadFromDisk: false), windows);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                foreach (var window in windows) window.Close();
                LanguageService.Initialize(false, false, false);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "語言生命週期測試逾時");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
