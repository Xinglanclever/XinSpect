using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Xunit;

namespace XinSpect.Tests;

[Collection(WpfCollection.Name)]
public sealed class DriverStoreLanguageTests
{
    private static DriverStorePackage Package() => new()
    {
        PublishedName = "oem42.inf", OriginalFileName = @"C:\使用者\記憶體\裝置.inf",
        Provider = "裝置提供者記憶體", ClassName = "自訂類別", CatalogFile = "原始記憶體.cat",
        BindingReason = "尚未查詢裝置綁定", Bindings = [new("oem42.inf", @"PCI\記憶體", "原始記憶體裝置")],
    };

    [Fact]
    public void DisplayTranslationsDoNotChangeRawPackageOrSerializedExport() => OnSta(() =>
    {
        const string detailTemplate = "{0} | {1} / {2} | {3}\n原始 INF：{4}\nCAT：{5}；Class GUID：{6}；BootCritical：{7}\n{8}；{9}\n{10}：{11}\n";
        var package = Package();
        var manifest = new DriverStoreExportManifest { Detail = "原始診斷", Packages = [new() { Package = package }] };
        string raw = JsonSerializer.Serialize(manifest);
        Switch(AppLanguage.English);
        Assert.Equal("Third-party package", package.OriginText);
        Assert.Equal("Unknown (not verified by DISM)", package.SignatureText);
        Assert.Equal("Bindings unknown: Unknown — Device bindings have not been queried", package.BindingText);
        AssertFormatParameters(LanguageService.T(detailTemplate), Package().OriginalFileName);
        Assert.Equal("尚未查詢裝置綁定", package.BindingReason);
        Assert.Equal(raw, JsonSerializer.Serialize(manifest));
        const string diagnostic = @"unmapped-driver-診斷 C:\使用者\記憶體\原始.inf";
        var failed = package with { BindingReason = "WMI 裝置綁定失敗：" + diagnostic };
        Assert.Equal("WMI device binding query failed: " + diagnostic, failed.BindingReasonText);
        Switch(AppLanguage.Simplified);
        Assert.Contains("绑定未知", package.BindingText);
        Assert.Contains("尚未查询", package.BindingReasonText);
        AssertFormatParameters(LanguageService.T(detailTemplate), Package().OriginalFileName);
        Assert.Equal(raw, JsonSerializer.Serialize(manifest));
        Assert.Equal(diagnostic, (package with { BindingReason = diagnostic }).BindingReasonText);
        Assert.EndsWith(diagnostic, failed.BindingReasonText);
        Switch(AppLanguage.Traditional);
        Assert.Equal("第三方套件", package.OriginText);
        Assert.Equal(raw, JsonSerializer.Serialize(manifest));
    });

    private static void AssertFormatParameters(string template, string rawPath)
    {
        for (int i = 0; i < 12; i++) Assert.Contains("{" + i + "}", template);
        object?[] values = Enumerable.Range(0, 12).Select(i => (object?)(i == 4 ? rawPath : $"value-{i}")).ToArray();
        Assert.Contains(rawPath, string.Format(template, values));
    }

    [Fact]
    public void ExistingWindowRefreshesRowsAndCurrentDetailsWithoutReadingOrExportingAgain() => OnSta(() =>
    {
        WpfEnv.Ensure();
        var backend = new InventoryBackend();
        var window = new DriverStoreWindow(new DriverStoreService(backend));
        try
        {
            window.Left = -5000; window.Top = -5000; window.ShowActivated = false; window.ShowInTaskbar = false;
            window.Show();
            PumpUntil(() => window.IsLoaded);
            Assert.Equal(0, backend.Reads);
            var refresh = (Button)window.FindName("RefreshButton");
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => refresh.IsEnabled);
            Assert.Equal(1, backend.Reads);
            var row = Assert.Single(window.Rows);
            var details = (TextBox)window.FindName("DetailsBox");
            Assert.False(WpfLocalizationBridge.WatchesForTests(details, TextBox.TextProperty));
            var destination = (TextBox)window.FindName("DestinationBox");
            var filter = (TextBox)window.FindName("FilterBox");
            var export = (Button)window.FindName("ExportButton");
            destination.Text = @"C:\使用者\記憶體\備份";
            filter.Text = "裝置提供者記憶體";
            Switch(AppLanguage.English);
            Assert.Contains("DISM package enumeration complete.", details.Text);
            Assert.DoesNotContain("DISM 套件列舉完成", details.Text);
            Assert.Equal(Package().OriginalFileName, row.Package.OriginalFileName);
            var packagesGrid = (DataGrid)window.FindName("PackagesGrid");
            packagesGrid.SelectedItem = row;
            row.IsSelected = true;
            typeof(DriverStoreWindow).GetMethod("Package_SelectionChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [packagesGrid, new SelectionChangedEventArgs(Selector.SelectionChangedEvent,
                    new List<object>(), new List<object> { row })]);
            Assert.Contains(row.Package.OriginalFileName, details.Text);
            int rowRefreshes = 0;
            row.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(row.Package)) rowRefreshes++; };
            foreach (var language in new[] { AppLanguage.Simplified, AppLanguage.Traditional, AppLanguage.English })
            {
                Switch(language);
                Assert.Same(row, Assert.Single(window.Rows));
                Assert.Same(row, packagesGrid.SelectedItem);
                Assert.Equal("oem42.inf", row.Package.PublishedName);
                Assert.Equal(Package().OriginalFileName, row.Package.OriginalFileName);
                Assert.True(row.IsSelected);
                Assert.Equal(@"C:\使用者\記憶體\備份", destination.Text);
                Assert.Equal("裝置提供者記憶體", filter.Text);
                Assert.Contains(row.Package.OriginalFileName, details.Text);
                Assert.Contains("原始記憶體.cat", details.Text);
                Assert.Contains(@"原始記憶體裝置 | PCI\記憶體", details.Text);
            }
            Assert.Equal(3, rowRefreshes);
            Assert.Contains("Original INF:", details.Text);
            Assert.Contains("Device bindings have not been queried", details.Text);
            Assert.Equal("Export 1 selected packages", export.Content);
            Assert.Equal(1, backend.Reads);
            window.Close();
            int before = rowRefreshes;
            Switch(AppLanguage.Traditional);
            Assert.Equal(before, rowRefreshes);
        }
        finally { window.Close(); }
    });

    private sealed class InventoryBackend : IDriverStoreBackend
    {
        public int Reads;
        public DriverStoreInventory ReadInventory(CancellationToken token)
        {
            Interlocked.Increment(ref Reads);
            return new() { Availability = FactAvailability.Present, Reason = "DISM 套件列舉完成。",
                BindingReason = "尚未查詢裝置綁定", Packages = [Package()] };
        }
        public Task<DriverStoreProcessResult> ExportPackageAsync(string inf, string directory, CancellationToken token)
            => throw new InvalidOperationException("This language test must never export a driver.");
    }

    // Exercise the production Changed subscriptions while keeping the test completely outside settings persistence.
    private static void Switch(AppLanguage language)
    {
        LanguageService.Initialize(language == AppLanguage.Simplified, language == AppLanguage.English, false);
        var changed = typeof(LanguageService).GetField("Changed", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(changed);
        (changed.GetValue(null) as Action)?.Invoke();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void PumpUntil(Func<bool> ready)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            // The event handler is async void. Keep its owning STA dispatcher running while
            // awaiting the injected backend, so the continuation and final UI updates execute
            // on the same thread as the controls.
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(20),
            };
            EventHandler? tick = null;
            tick = (_, _) =>
            {
                timer.Stop();
                timer.Tick -= tick;
                frame.Continue = false;
            };
            timer.Tick += tick;
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        Assert.True(ready(), "Injected Driver Store inventory did not complete.");
    }
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                LanguageService.Initialize(false, false, false);
                action();
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                LanguageService.Initialize(false, false, false);
                SynchronizationContext.SetSynchronizationContext(null);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(1)), "Driver Store language test timed out.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
