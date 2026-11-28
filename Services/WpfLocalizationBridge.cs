using System.ComponentModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>WPF 顯示屬性的生命週期橋接。只更新 target 的 current value，不取代 Binding 或 converter。</summary>
internal static class WpfLocalizationBridge
{
    private sealed class State
    {
        internal bool Simplified;
        internal bool WatchNonvisual;
        internal bool WindowCloseHooked;
        internal bool PopupLifecycleHooked;
        internal EventHandler? PanelLayoutUpdated;
        internal readonly HashSet<UIElement> PanelChildren = [];
        internal readonly Dictionary<FrameworkElement, RoutedEventHandler> PanelPendingLoads = [];
        internal readonly HashSet<DataGridColumn> Columns = [];
        internal NotifyCollectionChangedEventHandler? ColumnsChanged;
        internal readonly Dictionary<DependencyProperty, (DependencyPropertyDescriptor Descriptor, EventHandler Handler)> Watches = [];
    }

    private static readonly ConditionalWeakTable<DependencyObject, State> States = new();
    private static readonly List<WeakReference<DependencyObject>> Tracked = [];
    private static readonly object Gate = new();
    private static bool _initialized;
    [ThreadStatic] private static int _updating;

    internal static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(Loaded), true);
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.UnloadedEvent,
                new RoutedEventHandler(Unloaded), true);
            EventManager.RegisterClassHandler(typeof(FrameworkContentElement), FrameworkContentElement.LoadedEvent,
                new RoutedEventHandler(Loaded), true);
            EventManager.RegisterClassHandler(typeof(FrameworkContentElement), FrameworkContentElement.UnloadedEvent,
                new RoutedEventHandler(Unloaded), true);
            _initialized = true;
        }
    }

    private static void WindowClosed(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        window.Closed -= WindowClosed;
        if (States.TryGetValue(window, out var state)) state.WindowCloseHooked = false;
        CleanupTree(window);
    }

    private static void Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not DependencyObject routedTarget) return;
        DependencyObject? eventSource = args.OriginalSource as DependencyObject;
        DependencyObject target = routedTarget is Window window && eventSource is not null
            && !ReferenceEquals(eventSource, window)
                ? eventSource
                : routedTarget is ToolTip or ContextMenu ? routedTarget : eventSource ?? routedTarget;
        if (target is not DispatcherObject dispatcherObject) return;
        void ApplyWhenLoaded()
        {
            if (target is FrameworkElement { IsLoaded: false }
                or FrameworkContentElement { IsLoaded: false })
            {
                dispatcherObject.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                    new Action(() => ApplyIfStillLoaded(target)));
                return;
            }
            if (target is Window or ToolTip or ContextMenu) ConvertTree(target, LanguageService.IsSimplified);
            else Process(target, LanguageService.IsSimplified);
        }
        void ApplyIfStillLoaded(DependencyObject node)
        {
            if (node is FrameworkElement { IsLoaded: false }
                or FrameworkContentElement { IsLoaded: false }) return;
            if (node is Window or ToolTip or ContextMenu) ConvertTree(node, LanguageService.IsSimplified);
            else Process(node, LanguageService.IsSimplified);
        }
        dispatcherObject.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyWhenLoaded));
    }

    private static void Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not DependencyObject target || !ReferenceEquals(sender, args.OriginalSource)) return;
        // Descriptor 的事件表可能強持有 target；卸載時必須解除，重新 Loaded 再掛。
        CleanupTree(target);
    }

    private static void PopupOpened(object? sender, EventArgs args)
    {
        if (sender is Popup { Child: { } child } popup)
            popup.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => ConvertTree(child, LanguageService.IsSimplified)));
    }

    private static void CleanupTree(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.TryPop(out var target))
        {
            if (!visited.Add(target)) continue;
            foreach (var child in Children(target)) pending.Push(child);
            if (target is DataGrid grid && States.TryGetValue(grid, out var gridState))
            {
                if (gridState.ColumnsChanged is { } handler)
                {
                    grid.Columns.CollectionChanged -= handler;
                    gridState.ColumnsChanged = null;
                }
                foreach (var column in gridState.Columns.Concat(grid.Columns).Distinct().ToArray()) ReleaseColumn(column);
                gridState.Columns.Clear();
            }
            if (target is Panel panel && States.TryGetValue(panel, out var panelState))
            {
                if (panelState.PanelLayoutUpdated is { } handler)
                {
                    panel.LayoutUpdated -= handler;
                    panelState.PanelLayoutUpdated = null;
                }
                foreach (var (child, loadedHandler) in panelState.PanelPendingLoads.ToArray())
                    child.RemoveHandler(FrameworkElement.LoadedEvent, loadedHandler);
                panelState.PanelPendingLoads.Clear();
                panelState.PanelChildren.Clear();
            }
            if (target is Popup popup && States.TryGetValue(popup, out var popupState)
                && popupState.PopupLifecycleHooked)
            {
                popup.Opened -= PopupOpened;
                popupState.PopupLifecycleHooked = false;
            }
            Detach(target);
        }
    }

    private static void Detach(DependencyObject target)
    {
        if (!States.TryGetValue(target, out var state)) return;
        foreach (var (descriptor, handler) in state.Watches.Values) descriptor.RemoveValueChanged(target, handler);
        state.Watches.Clear();
    }

    internal static void RefreshAll()
    {
        Initialize();
        List<DependencyObject> targets = [];
        lock (Gate)
        {
            for (int i = Tracked.Count - 1; i >= 0; i--)
            {
                if (Tracked[i].TryGetTarget(out var target)) targets.Add(target);
                else Tracked.RemoveAt(i);
            }
        }
        foreach (var group in targets.GroupBy(t => t.Dispatcher))
        {
            var dispatcher = group.Key;
            if (dispatcher.HasShutdownStarted || !dispatcher.Thread.IsAlive) continue;
            var nodes = group.ToArray();
            void Refresh()
            {
                foreach (var node in nodes)
                {
                    if (IsEditable(node)) continue;
                    if (States.TryGetValue(node, out var state))
                    {
                        state.Simplified = LanguageService.IsSimplified;
                        foreach (var property in state.Watches.Keys.ToArray())
                            BindingOperations.GetBindingExpressionBase(node, property)?.UpdateTarget();
                    }
                    Process(node, LanguageService.IsSimplified);
                }
            }
            if (dispatcher.CheckAccess()) Refresh();
            else dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(Refresh));
        }
    }

    internal static void ConvertTree(DependencyObject? root, bool simplified)
    {
        if (root is null) return;
        Initialize();
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.TryPop(out var target))
        {
            if (!visited.Add(target) || IsEditable(target) || IsRawContentDescendant(target)) continue;
            Process(target, simplified);
            foreach (var child in Children(target)) pending.Push(child);
        }
    }

    private static State Track(DependencyObject target, bool simplified)
    {
        if (!States.TryGetValue(target, out var state))
        {
            state = new State();
            States.Add(target, state);
            lock (Gate) Tracked.Add(new WeakReference<DependencyObject>(target));
        }
        state.Simplified = simplified;
        if (target is Window window && window.IsLoaded && !state.WindowCloseHooked)
        {
            window.Closed += WindowClosed;
            state.WindowCloseHooked = true;
        }
        return state;
    }

    private static void Process(DependencyObject target, bool simplified)
    {
        if (IsEditable(target) || IsRawContentDescendant(target)) return;
        var state = Track(target, simplified);
        _updating++;
        try
        {
            switch (target)
            {
                case TextBox: Translate(target, TextBox.TextProperty, state); break;
                case Window: Translate(target, Window.TitleProperty, state); break;
                case AccessText: Translate(target, AccessText.TextProperty, state); break;
                case Run run when !IsBoundTextParent(run): Translate(run, Run.TextProperty, state); break;
                case TextBlock block:
                    if (BindingOperations.IsDataBound(block, TextBlock.TextProperty) || HasEmptyTextInline(block))
                        Translate(block, TextBlock.TextProperty, state);
                    else
                    {
                        Watch(block, TextBlock.TextProperty, state);
                        foreach (var inline in InlineTree(block.Inlines)) Process(inline, simplified);
                    }
                    break;
            }
            if (target is Panel panel)
                WatchPanelChildren(panel, state);
            if (target is DataGrid grid)
                SyncColumns(grid, state);
            if (target is Popup) Watch(target, Popup.ChildProperty, state);
            if (target is Popup popup && !state.PopupLifecycleHooked)
            {
                popup.Opened += PopupOpened;
                state.PopupLifecycleHooked = true;
            }
            if (target is ContentControl) Translate(target, ContentControl.ContentProperty, state);
            if (target is HeaderedContentControl) Translate(target, HeaderedContentControl.HeaderProperty, state);
            if (target is HeaderedItemsControl) Translate(target, HeaderedItemsControl.HeaderProperty, state);
            if (target is DataGridColumn) Translate(target, DataGridColumn.HeaderProperty, state);
            if (target is FrameworkElement)
            {
                Translate(target, FrameworkElement.ToolTipProperty, state);
                Watch(target, FrameworkElement.ContextMenuProperty, state);
            }
            switch (target)
            {
                case SectionHead: Translate(target, SectionHead.TextProperty, state); break;
                case FieldRow: Translate(target, FieldRow.LabelProperty, state); break;
                case RadialGauge:
                    Translate(target, RadialGauge.CaptionProperty, state);
                    Translate(target, RadialGauge.UnitProperty, state); break;
                case HistoryGraph: Translate(target, HistoryGraph.CaptionProperty, state); break;
                case Oscilloscope: Translate(target, Oscilloscope.CaptionProperty, state); break;
                case AnalogVoltMeter: Translate(target, AnalogVoltMeter.CaptionProperty, state); break;
                case DonutChart:
                    Translate(target, DonutChart.CaptionProperty, state);
                    Translate(target, DonutChart.Caption2Property, state);
                    Translate(target, DonutChart.SubTextProperty, state); break;
            }
        }
        finally { _updating--; }
    }

    private static bool HasEmptyTextInline(TextBlock block)
        => !InlineTree(block.Inlines).OfType<Run>().Any(run => run.Text.Length > 0)
           && block.Text.Length > 0;

    private static void Translate(DependencyObject target, DependencyProperty property, State state)
    {
        Watch(target, property, state);
        if (target.GetValue(property) is not string { Length: > 0 } text) return;
        if (LooksLikePath(text) && !(target is TextBox && text.Contains('\n'))) return;
        bool bound = BindingOperations.IsDataBound(target, property);
        var binding = BindingOperations.GetBindingBase(target, property);
        bool defaultWritesBack = property.GetMetadata(target.GetType()) is FrameworkPropertyMetadata { BindsTwoWayByDefault: true };
        if (CanWriteSource(binding, defaultWritesBack)) return;
        if (bound && ((!LanguageService.GetIsUiText(target) && !CanTranslateBinding(binding))
                      || !UiTextTranslation.ContainsChinese(text))) return;
        string localized = LanguageService.FromOriginal(target, property.Name, text, state.Simplified,
            allowEnglishReverse: !bound, knownOnly: target is TextBox);
        if (localized != text) target.SetCurrentValue(property, localized);
    }

    private static void Watch(DependencyObject target, DependencyProperty property, State state)
    {
        // 無 Loaded/Unloaded 的欄只在其 DataGrid 已載入時訂閱，並隨 DataGrid.Unloaded 解除。
        bool live = target switch
        {
            // Popup.Child is replaceable while the popup is closed; observe that property
            // even when the popup's separate presentation source is not currently loaded.
            Popup => true,
            FrameworkElement element => element.IsLoaded,
            FrameworkContentElement element => element.IsLoaded,
            _ => state.WatchNonvisual,
        };
        if (!live || state.Watches.ContainsKey(property)) return;
        var descriptor = DependencyPropertyDescriptor.FromProperty(property, target.GetType());
        if (descriptor is null) return;
        EventHandler handler = (_, _) =>
        {
            if (_updating != 0) return;
            Process(target, state.Simplified);
            if (property == FrameworkElement.ToolTipProperty || property == FrameworkElement.ContextMenuProperty
                || property == Popup.ChildProperty)
                foreach (var child in Children(target)) ConvertTree(child, state.Simplified);
        };
        descriptor.AddValueChanged(target, handler);
        state.Watches[property] = (descriptor, handler);
    }

    internal static int ObserverCountForTests(DependencyObject target)
        => States.TryGetValue(target, out var state) ? state.Watches.Count : 0;

    internal static bool WatchesForTests(DependencyObject target, DependencyProperty property)
        => States.TryGetValue(target, out var state) && state.Watches.ContainsKey(property);

    internal static int PendingPanelLoadsForTests(Panel panel)
        => States.TryGetValue(panel, out var state) ? state.PanelPendingLoads.Count : 0;

    private static void SyncColumns(DataGrid grid, State state)
    {
        foreach (var removed in state.Columns.Where(c => !grid.Columns.Contains(c)).ToArray())
        {
            ReleaseColumn(removed);
            state.Columns.Remove(removed);
        }
        if (grid.IsLoaded && state.ColumnsChanged is null)
        {
            state.ColumnsChanged = (_, _) => SyncColumns(grid, state);
            grid.Columns.CollectionChanged += state.ColumnsChanged;
        }
        foreach (var column in grid.Columns)
        {
            Track(column, state.Simplified).WatchNonvisual = grid.IsLoaded;
            if (grid.IsLoaded) state.Columns.Add(column);
            Process(column, state.Simplified);
        }
    }

    private static void WatchPanelChildren(Panel panel, State state)
    {
        if (state.PanelLayoutUpdated is not null) return;
        state.PanelChildren.Clear();
        foreach (UIElement child in panel.Children) state.PanelChildren.Add(child);
        state.PanelLayoutUpdated = (_, _) =>
        {
            if (!panel.IsLoaded) return;
            var current = new HashSet<UIElement>(panel.Children.Cast<UIElement>(), ReferenceEqualityComparer.Instance);
            foreach (var (pending, loadedHandler) in state.PanelPendingLoads.ToArray())
            {
                if (current.Contains(pending)) continue;
                pending.RemoveHandler(FrameworkElement.LoadedEvent, loadedHandler);
                state.PanelPendingLoads.Remove(pending);
                CleanupTree(pending);
            }
            foreach (var removed in state.PanelChildren.Where(child => !current.Contains(child)).ToArray())
            {
                if (removed is FrameworkElement element && state.PanelPendingLoads.Remove(element, out var loadedHandler))
                    element.RemoveHandler(FrameworkElement.LoadedEvent, loadedHandler);
                CleanupTree(removed);
            }
            state.PanelChildren.RemoveWhere(child => !current.Contains(child));
            foreach (var added in current.Where(child => !state.PanelChildren.Contains(child)))
            {
                if (added is FrameworkElement element && !element.IsLoaded)
                {
                    if (state.PanelPendingLoads.ContainsKey(element)) continue;
                    RoutedEventHandler? loadedHandler = null;
                    loadedHandler = (_, _) =>
                    {
                        element.RemoveHandler(FrameworkElement.LoadedEvent, loadedHandler!);
                        state.PanelPendingLoads.Remove(element);
                        element.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                        {
                            if (!panel.IsLoaded || !panel.Children.Contains(element)) return;
                            ConvertTree(element, state.Simplified);
                            state.PanelChildren.Add(element);
                        }));
                    };
                    state.PanelPendingLoads.Add(element, loadedHandler);
                    element.AddHandler(FrameworkElement.LoadedEvent, loadedHandler, true);
                    continue;
                }
                ConvertTree(added, state.Simplified);
                state.PanelChildren.Add(added);
            }
        };
        panel.LayoutUpdated += state.PanelLayoutUpdated;
    }

    private static void ReleaseColumn(DataGridColumn column)
    {
        Detach(column);
        if (States.TryGetValue(column, out var state)) state.WatchNonvisual = false;
    }

    private static bool CanTranslateBinding(BindingBase? binding)
    {
        if (CanWriteSource(binding, defaultWritesBack: false)) return false;
        if (binding?.StringFormat is { } format && UiTextTranslation.ContainsChinese(format)) return true;
        if (binding is MultiBinding multi) return multi.Bindings.All(CanTranslateBinding);
        if (binding is PriorityBinding priority) return priority.Bindings.All(CanTranslateBinding);
        if (binding is not Binding single) return true;
        if (single.Converter is ChineseConverter or LangFormatConverter) return true;
        // 只自動處理具名呈現欄位。硬體、Registry、對話或未知來源資料須保留，不能靠黑名單猜。
        // 其他產品文案可明確選用 Lang converter 或 IsUiText。
        string path = single.Path?.Path ?? "";
        string leaf = path.Split('.').Last().ToLowerInvariant();
        return leaf is "label" or "title" or "caption" or "caption2" or "subtext" or "unit"
            or "hint" or "tooltip" or "header" or "status" or "statustext" or "statusline"
            or "statusmessage" or "summary" or "summarytext" or "reason" or "reasontext"
            or "availabilitytext" or "severitytext" or "diagnostictext"
            or "number" or "count" or "index";
    }

    private static bool CanWriteSource(BindingBase? binding, bool defaultWritesBack)
        => binding switch
        {
            Binding single => single.Mode is BindingMode.TwoWay or BindingMode.OneWayToSource
                || single.Mode == BindingMode.Default && defaultWritesBack,
            MultiBinding multi => multi.Mode is BindingMode.TwoWay or BindingMode.OneWayToSource
                || multi.Mode == BindingMode.Default && defaultWritesBack
                || multi.Bindings.Any(b => CanWriteSource(b, false)),
            PriorityBinding priority => priority.Bindings.Any(b => CanWriteSource(b, defaultWritesBack)),
            _ => false,
        };

    internal static bool LooksLikePath(string text)
        => text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/'
           || text.StartsWith(@"\\", StringComparison.Ordinal)
           || text.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)
           || text.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)
           || text.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)
           || text.StartsWith("Registry::", StringComparison.OrdinalIgnoreCase)
           || Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file";

    private static bool IsBoundTextParent(FrameworkContentElement inline)
    {
        DependencyObject? parent = inline.Parent;
        while (parent is Span span) parent = span.Parent;
        return parent is TextBlock block && BindingOperations.IsDataBound(block, TextBlock.TextProperty);
    }

    private static bool IsRawContentDescendant(DependencyObject target)
    {
        DependencyObject? current = Parent(target);
        for (int i = 0; current is not null && i < 32; i++, current = Parent(current))
        {
            if (current is ContentControl { Content: string } content
                && HasRawBinding(content, ContentControl.ContentProperty)) return true;
            if (current is ComboBoxItem item && !LanguageService.GetIsUiText(item)
                && ItemsControl.ItemsControlFromItemContainer(item)?.ItemsSource is not null) return true;
            if (current is ContentPresenter { ContentSource: "Header", TemplatedParent: { } owner })
            {
                if (owner is HeaderedContentControl && HasRawBinding(owner, HeaderedContentControl.HeaderProperty)) return true;
                if (owner is HeaderedItemsControl && HasRawBinding(owner, HeaderedItemsControl.HeaderProperty)) return true;
            }
        }
        return false;
    }

    private static bool HasRawBinding(DependencyObject target, DependencyProperty property)
    {
        var binding = BindingOperations.GetBindingBase(target, property);
        return binding is not null && !LanguageService.GetIsUiText(target) && !CanTranslateBinding(binding);
    }

    private static DependencyObject? Parent(DependencyObject target) => target switch
    {
        FrameworkElement element => element.TemplatedParent ?? element.Parent
            ?? (element is Visual ? VisualTreeHelper.GetParent(element) : null),
        FrameworkContentElement element => element.Parent,
        _ => null,
    };

    private static bool IsEditable(DependencyObject target)
    {
        if (LanguageService.GetIsRawText(target)) return true;
        DependencyObject? current = target;
        for (int i = 0; current is not null && i < 32; i++)
        {
            if (current is PasswordBox) return true;
            if (current is TextBoxBase
                && !(current is TextBox { IsReadOnly: true } box && LanguageService.GetIsUiText(box))) return true;
            current = current switch
            {
                FrameworkElement element => element.TemplatedParent ?? element.Parent,
                FrameworkContentElement element => element.Parent,
                _ => null,
            };
        }
        return false;
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject target)
    {
        if (target is TextBlock text)
        {
            if (!BindingOperations.IsDataBound(text, TextBlock.TextProperty))
                foreach (var inline in InlineTree(text.Inlines)) yield return inline;
            yield break; // 不得碰 Binding.Text 的隱式 Run。
        }
        if (target is Visual or Visual3D)
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(target); i++)
                yield return VisualTreeHelper.GetChild(target, i);
        foreach (object child in LogicalTreeHelper.GetChildren(target))
            if (child is DependencyObject node) yield return node;
        if (target is Popup { Child: { } popup }) yield return popup;
        if (target is FrameworkElement element)
        {
            if (element.ContextMenu is { } menu) yield return menu;
            if (element.ToolTip is DependencyObject tip) yield return tip;
        }
        if (target is ContentControl { Content: DependencyObject content }) yield return content;
        if (target is HeaderedContentControl { Header: DependencyObject header }) yield return header;
        if (target is HeaderedItemsControl { Header: DependencyObject itemHeader }) yield return itemHeader;
        if (target is ItemsControl items)
            foreach (var item in items.Items.OfType<DependencyObject>().ToArray()) yield return item;
        if (target is DataGrid grid)
            foreach (var column in grid.Columns) yield return column;
    }

    private static IEnumerable<Inline> InlineTree(InlineCollection inlines)
    {
        foreach (var inline in inlines.ToArray())
        {
            yield return inline;
            if (inline is Span span)
                foreach (var nested in InlineTree(span.Inlines)) yield return nested;
        }
    }
}
