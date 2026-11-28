using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Xunit;

namespace XinSpect.Tests;

[Collection(WpfCollection.Name)]
public sealed class DiskSpaceLanguageTests
{
    private const string RawRoot = @"C:\使用者\記憶體";
    private const string RawPayload = @"無法讀取 C:\使用者\記憶體\原始.資料 — 列舉完成";

    [Theory]
    [InlineData(DiskSpaceScanState.Complete, "Enumeration complete")]
    [InlineData(DiskSpaceScanState.Partial, "Partial results")]
    [InlineData(DiskSpaceScanState.Cancelled, "Cancelled; partial results retained")]
    public void SummaryTranslatesAppOwnedStateAndUnknownAllocationBeforeFormatting(DiskSpaceScanState state, string expected) => OnSta(() =>
    {
        var result = Fixture() with { State = state };
        Switch(AppLanguage.English);
        Assert.StartsWith(expected + " · ", result.Summary);
        Assert.Contains(" + unknown", result.Summary);
        Assert.DoesNotContain("個檔案", result.Summary);
        Assert.DoesNotContain("部分結果", result.Summary);
        Switch(AppLanguage.Simplified);
        Assert.Contains("逻辑大小", result.Summary);
        Switch(AppLanguage.Traditional);
        Assert.Contains("邏輯大小", result.Summary);
        Assert.DoesNotContain("unknown", result.Summary);
    });

    [Fact]
    public void SyntheticAggregateLabelKeepsItsTemplateAndDoesNotGainAUnitSuffix() => OnSta(() =>
    {
        Switch(AppLanguage.Simplified);
        Assert.Equal("其他 3 项（合并显示）", DiskSpaceUiText.Format("其他 {0:N0} 項（合併顯示）", 3));
    });

    [Fact]
    public void DisplayLocalizationPreservesRawNamesReasonsAndReportBytes() => OnSta(() =>
    {
        var result = Fixture();
        var file = result.BiggestFiles[0];
        string report = DiskSpaceScanService.CreateJsonReport(result);
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Simplified, AppLanguage.Traditional, AppLanguage.English })
        {
            Switch(language);
            Assert.StartsWith(file.Path + "\n", file.Detail);
            Assert.Contains(RawPayload, file.Detail);
            Assert.StartsWith(file.Name, file.DisplayText);
            Assert.Equal("讀取失敗：" + RawPayload, file.Note);
            Assert.Equal(RawRoot, result.RootPath);
            Assert.Equal(report, DiskSpaceScanService.CreateJsonReport(result));
        }
        using var json = JsonDocument.Parse(report);
        var type = json.RootElement.GetProperty("FileTypes")[0];
        Assert.False(type.TryGetProperty("UiExtension", out _));
        Assert.False(type.TryGetProperty("UiAllocatedDisplay", out _));
        Assert.Equal("（無副檔名）", type.GetProperty("Extension").GetString());
        Assert.Equal("記憶體", DiskSpaceUiText.Reason("記憶體")); // A catalog match alone is not producer provenance.
        Assert.Equal(".資料", new DiskSpaceTypeRow(".資料", 1, 1, 0, 1).UiExtension);
        Assert.NotEqual(result.FileTypes[0].Extension, result.FileTypes[0].UiExtension);
    });

    [Fact]
    public void KnownDiagnosticsTranslateButSystemPayloadAndParameterSuffixStayExact() => OnSta(() =>
    {
        var invalid = new ArgumentException("請指定完整的目錄路徑。", "directory");
        string suffix = invalid.Message["請指定完整的目錄路徑。".Length..];
        Switch(AppLanguage.English);
        Assert.Equal(LanguageService.T("請指定完整的目錄路徑。") + suffix, DiskSpaceUiText.ExceptionMessage(invalid));
        Assert.Equal(LanguageService.T("讀取失敗：") + RawPayload, DiskSpaceUiText.Reason("讀取失敗：" + RawPayload));
        Assert.Equal("FileIdInfo failed (Win32 50): " + RawPayload, DiskSpaceUiText.Reason("FileIdInfo 失敗（Win32 50）：" + RawPayload));
        Assert.Equal(LanguageService.T("已達深度上限，未列舉此目錄內容。"), DiskSpaceUiText.Reason("已達深度上限，未列舉此目錄內容。"));
        Assert.NotEqual("已達深度上限，未列舉此目錄內容。", DiskSpaceUiText.Reason("已達深度上限，未列舉此目錄內容。"));
        string bounded = ("讀取失敗：" + new string('記', 1014) + " [截斷]");
        Assert.Equal(1024, bounded.Length);
        Assert.Equal(LanguageService.T("讀取失敗：") + new string('記', 1014) + LanguageService.T(" [截斷]"), DiskSpaceUiText.Reason(bounded));
        foreach (var kind in Enum.GetValues<DiskSpaceIssueKind>())
            Assert.NotEqual(kind.ToString(), DiskSpaceUiText.Kind(kind));
    });

    [Fact]
    public void ExistingWindowRebuildsCurrentDisplaysAndAggregateWithoutChangingRawDataOrSelection() => OnSta(() =>
    {
        WpfEnv.Ensure();
        var result = Fixture();
        var window = new DiskSpaceWindow(RawRoot);
        try
        {
            window.Show(); Pump();
            var tree = (TreeView)window.FindName("FolderTree");
            var types = (DataGrid)window.FindName("TypesGrid");
            var issues = (DataGrid)window.FindName("IssuesGrid");
            var detail = (TextBox)window.FindName("DetailBox");
            var status = (TextBlock)window.FindName("StatusText");
            var canvas = (Canvas)window.FindName("MapCanvas");
            Field("_result").SetValue(window, result); Field("_mapNode").SetValue(window, result.Root);
            tree.ItemsSource = new[] { result.Root }; types.ItemsSource = result.FileTypes;
            ((DataGrid)window.FindName("BigFilesGrid")).ItemsSource = result.BiggestFiles;
            Method("SetStatus").Invoke(window, new object[] { (Func<string>)(() => result.Summary) });
            Method("RefreshLanguage").Invoke(window, null); window.UpdateLayout(); Pump();
            var rootItem = Assert.IsType<TreeViewItem>(tree.ItemContainerGenerator.ContainerFromItem(result.Root));
            rootItem.IsExpanded = true; rootItem.IsSelected = true; window.UpdateLayout(); Pump();
            Method("SetDetail").Invoke(window, new object[] { result.BiggestFiles[0] });
            ((ComboBox)window.FindName("BasisBox")).SelectedIndex = 1;
            string report = DiskSpaceScanService.CreateJsonReport(result);
            foreach (var language in new[] { AppLanguage.English, AppLanguage.Simplified, AppLanguage.Traditional, AppLanguage.English })
            {
                Switch(language); window.UpdateLayout(); Pump(); // Include Loaded/localization of newly drawn map labels.
                Assert.Equal(RawRoot, ((TextBox)window.FindName("DirectoryBox")).Text);
                Assert.StartsWith(result.BiggestFiles[0].Path + "\n", detail.Text);
                Assert.Contains(RawPayload, detail.Text);
                Assert.Equal(result.BiggestFiles[0].Detail, detail.Text);
                Assert.Equal(result.Summary, status.Text);
                Assert.Equal(report, DiskSpaceScanService.CreateJsonReport(result));
                Assert.Same(result.Root, tree.SelectedItem); Assert.True(rootItem.IsExpanded);
                Assert.Equal(80, canvas.Children.Count);
                Assert.Contains(canvas.Children.OfType<Button>(), button => DisplayedText((TextBlock)button.Content).StartsWith("記憶體.資料\n", StringComparison.Ordinal));
                Assert.Equal(result.Issues[0].Path, issues.Items[0].GetType().GetProperty("Path")!.GetValue(issues.Items[0]));
                Assert.Equal(DiskSpaceUiText.Reason(result.Issues[0].Reason), issues.Items[0].GetType().GetProperty("UiReason")!.GetValue(issues.Items[0]));
            }
            Assert.Contains("Confirmed unique allocation", ((TextBlock)window.FindName("MapNoteText")).Text);
            var aggregate = canvas.Children.OfType<Button>().Single(button => ((TextBlock)button.Content).Text.StartsWith("Other 3 entries", StringComparison.Ordinal));
            aggregate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.StartsWith("Other 3 entries (combined display)\n", detail.Text);
            Switch(AppLanguage.Simplified); Assert.StartsWith("其他 3 项（合并显示）\n", detail.Text);
            Switch(AppLanguage.Traditional); Assert.StartsWith("其他 3 項（合併顯示）\n", detail.Text);
            Assert.Null(Field("_scanCancellation").GetValue(window));
            window.Close();
            var changed = typeof(LanguageService).GetField("Changed", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.DoesNotContain((changed!.GetValue(null) as Action)?.GetInvocationList() ?? [], handler => ReferenceEquals(handler.Target, window));
        }
        finally { window.Close(); }
    });

    private static DiskSpaceScanResult Fixture()
    {
        var root = new DiskSpaceNode(RawRoot, true) { LogicalBytes = 8600, UniqueAllocatedBytes = 332000, FileCount = 82, UnknownAllocationFiles = 1, IsPartial = true };
        var first = new DiskSpaceNode(RawRoot + @"\記憶體.資料", false)
        { LogicalBytes = 500, FileCount = 1, Parent = root, Note = "讀取失敗：" + RawPayload, UnknownAllocationFiles = 1, IsPartial = true, UniqueAllocatedBytes = 8000 };
        root.Children.Add(first);
        for (int i = 0; i < 81; i++) root.Children.Add(new DiskSpaceNode(RawRoot + @"\項目" + i, false)
        { LogicalBytes = 100, UniqueAllocatedBytes = 4000, FileCount = 1, Parent = root });
        return new(RawRoot, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1.25), DiskSpaceScanState.Partial, new(), root, 83, 0,
            [new("（無副檔名）", 81, 8100, 324000, 1)], [first],
            [new(first.Path, DiskSpaceIssueKind.ReadError, first.Note), new(RawRoot, DiskSpaceIssueKind.DepthLimit, "已達深度上限，未列舉此目錄內容。")], 2, 500000);
    }
    private static FieldInfo Field(string name) => typeof(DiskSpaceWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(DiskSpaceWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static string DisplayedText(TextBlock text) => new TextRange(text.ContentStart, text.ContentEnd).Text.Replace("\r\n", "\n");
    private static void Switch(AppLanguage language)
    {
        LanguageService.Initialize(language == AppLanguage.Simplified, language == AppLanguage.English, false);
        typeof(LanguageService).GetMethod("RefreshPresentation", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        (typeof(LanguageService).GetField("Changed", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as Action)?.Invoke();
        Pump();
    }
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { LanguageService.Initialize(false, false, false); action(); }
            catch (Exception ex) { error = ex; }
            finally { LanguageService.Initialize(false, false, false); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(1)), "Disk space language test timed out.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
