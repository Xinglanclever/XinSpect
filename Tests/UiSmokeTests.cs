using System.Diagnostics;
using System.Text;
using System.Windows;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// UI 煙霧測試（ROADMAP 第一部第 1 項）：逐一「真的建構」所有導覽頁、實用工具子頁與五個全螢幕檢測視窗，
/// 並以 Measure／Arrange 逼繫結求值，再由 <see cref="PresentationTraceSources"/>.DataBindingSource
/// 攔下繫結路徑錯誤與轉換失敗——從根上斷掉「XAML 繫結寫錯、進頁面才閃退」這個反覆發生的失敗模式。
/// </summary>
/// <remarks>
/// ⚠ 本類是「測試不建構 MainViewModel」慣例的唯一例外：繫結路徑必須對著真的屬性形狀求值才有意義，
/// 手工假物件維護成本高且會隨介面演進爛掉。安全性論證——這裡建構的是<b>未呼叫 Initialize()</b> 的
/// MainViewModel：StartupSequence／MetricsPump 不會啟動，因此 <b>SensorService／LibreHardwareMonitor
/// 不會被建立、不碰任何硬體</b>；GpuOcService 的 NVML／NVAPI 亦為延遲初始化。副作用僅限讀取
/// %APPDATA% 的既有狀態檔，與 WingetService 開機時同款的背景 winget 偵測（唯讀）。
///
/// WPF 需要 STA 執行緒與 Application 執行個體（StaticResource 解析用），故全部工作排入一條專屬
/// STA 執行緒，其餘純函式測試不受影響。視窗類別只建構不 Show()，Loaded 事件在測試中不會觸發。
/// </remarks>
[Collection(WpfCollection.Name)]
public class UiSmokeTests
{
    /// <summary>把 DataBindingSource 的輸出原樣收集成字串的監聽器。</summary>
    private sealed class CollectingListener : TraceListener
    {
        private readonly StringBuilder _sb = new();
        public override void Write(string? message) => _sb.Append(message);
        public override void WriteLine(string? message) => _sb.AppendLine(message);
        public string Drain() { var s = _sb.ToString(); _sb.Clear(); return s; }
    }

    [Fact]
    public void 所有導覽頁_實用工具與全螢幕視窗皆可建構且無繫結錯誤()
    {
        var failures = new List<string>();
        var thread = new Thread(() => Run(failures));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        // 逾時保護：任何檢視在建構時卡死（如等待網路）都不得讓整個測試回合無限等下去
        if (!thread.Join(TimeSpan.FromMinutes(3)))
            failures.Add("煙霧測試逾時（3 分鐘未完成），可能有檢視在建構時阻塞。");

        Assert.True(failures.Count == 0,
            $"UI 煙霧測試發現 {failures.Count} 項問題：\n" + string.Join("\n", failures));
    }

    private static void Run(List<string> failures)
    {
        // WPF 環境：StaticResource 解析需要 Application 與佈景資源字典（合併內容與 App.xaml 相同）。
        // 建立動作集中在 WpfEnv：一個 AppDomain 只能有一個 Application，平行跑的測試類別必須共用同一把鎖。
        var app = WpfEnv.Ensure();

        PresentationTraceSources.Refresh();
        var listener = new CollectingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        try
        {
            var vm = new MainViewModel();   // 唯一例外（見類別註解）；絕不呼叫 Initialize()

            // ── 自我驗證 ──────────────────────────────────────────────
            // 刻意綁一個不存在的路徑，確認監聽器真的攔得到路徑錯誤——
            // 否則這套煙霧測試可能靜默失效（抓不到東西以為一切正常），那比沒有測試更糟。
            var probe = new System.Windows.Controls.TextBlock { DataContext = vm };
            probe.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
                new System.Windows.Data.Binding("絕對不存在的屬性路徑"));
            probe.Measure(new Size(10, 10));
            probe.Arrange(new Rect(0, 0, 10, 10));
            if (!listener.Drain().Contains("BindingExpression path error"))
            {
                failures.Add("監聽器自我驗證失敗：刻意製造的繫結路徑錯誤未被攔到，煙霧測試形同失效。");
                return;
            }

            var targets = new List<(string Name, Func<FrameworkElement> Make)>();
            foreach (var d in PageRegistry.Pages)
                targets.Add(($"頁面「{d.Title}」", d.Factory));
            foreach (var d in PageRegistry.Utilities)
                targets.Add(($"工具「{d.Title}」", d.Factory));
            targets.Add(("視窗「ScreenTestWindow」", () => new ScreenTestWindow()));
            targets.Add(("視窗「MouseTestWindow」", () => new MouseTestWindow()));
            targets.Add(("視窗「KeyboardTestWindow」", () => new KeyboardTestWindow()));
            targets.Add(("視窗「SpeakerTestWindow」", () => new SpeakerTestWindow()));
            targets.Add(("視窗「MotionTestWindow」", () => new MotionTestWindow()));
            // 徽章一覽：陳列了對照表裡每一枚專屬徽章，建構它等於把所有向量字形（Geometry.Parse）
            // 與疊加層走一遍。新增徽章若把路徑資料寫壞，只有擁有那顆晶片的人看得到——放進煙霧測試才攔得住。
            targets.Add(("視窗「IconGalleryWindow」", () => new IconGalleryWindow()));
            // 這兩個對話框在正常使用中很少被建出來，卻是最不能壞的：首次啟動的版面選擇只有
            // 「第一次打開程式」那一刻會出現，超頻風險確認則是進超頻頁的唯一入口。
            // XAML 寫錯的話，兩者都會在最糟的時機才炸出來，所以一併列進煙霧測試。
            targets.Add(("對話框「FirstRunWindow」", () => new FirstRunWindow()));
            targets.Add(("對話框「OcRiskWindow」", () => new OcRiskWindow()));

            foreach (var (name, make) in targets)
            {
                try
                {
                    var fe = make();
                    fe.DataContext = vm;
                    // 多數繫結要等版面配置才求值；給一個常見桌面尺寸逼它們真的跑一次
                    fe.Measure(new Size(1280, 800));
                    fe.Arrange(new Rect(0, 0, 1280, 800));
                }
                catch (Exception ex)
                {
                    failures.Add($"{name} 建構／量測失敗：{Describe(ex)}");
                }
            }

            // 三類致訊號（ROADMAP 指定）：路徑錯誤、轉換失敗、找不到宿主元素
            foreach (var line in listener.Drain().Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t.Contains("BindingExpression path error") ||
                    t.Contains("Cannot convert") ||
                    t.Contains("Cannot find governing FrameworkElement"))
                    failures.Add("繫結錯誤：" + t);
            }
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }

    /// <summary>
    /// 有資料才會用到的清單樣板。
    /// <para>
    /// 上面那支煙霧測試建構的每一頁都是<b>空的</b>——沒有按過任何「重新掃描」，
    /// 所以 <c>ItemsControl.ItemTemplate</c> 一次也不會被套用，樣板裡寫錯的繫結路徑與
    /// 找不到的 <c>StaticResource</c>（例如轉換器沒登記進佈景資源）就這樣躲過整套測試，
    /// 直到使用者掃出第一個裝置才當場閃退。這裡把 USB 鏈路頁塞兩列真的資料進去，逼樣板套用一次。
    /// </para>
    /// </summary>
    [Fact]
    public void USB鏈路頁的清單樣板_有資料時也沒有繫結錯誤()
    {
        var failures = new List<string>();
        var thread = new Thread(() => RunUsbTemplate(failures));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromMinutes(1)))
            failures.Add("USB 樣板煙霧測試逾時（1 分鐘未完成）。");

        Assert.True(failures.Count == 0,
            $"USB 鏈路頁樣板發現 {failures.Count} 項問題：\n" + string.Join("\n", failures));
    }

    /// <summary>只提供 UsbLink 一個屬性的最小宿主：樣板要驗的是路徑與資源，不必動用整個 MainViewModel。</summary>
    private sealed class UsbOnly { public UsbLinkService UsbLink { get; } = new(); }
    private static void RunUsbTemplate(List<string> failures)
    {
        WpfEnv.Ensure();

        PresentationTraceSources.Refresh();
        var listener = new CollectingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        try
        {
            var host = new UsbOnly();
            // 兩列：一列直接掛在根集線器上，一列掛在外接集線器底下——縮排轉換器只有後者會走到
            host.UsbLink.Rows.Add(SampleUsbRow(0, "控制器 1／埠 3"));
            host.UsbLink.Rows.Add(SampleUsbRow(1, "控制器 1／埠 3／埠 2"));

            var view = new UsbLinkView { DataContext = host };
            view.Measure(new Size(1280, 800));
            view.Arrange(new Rect(0, 0, 1280, 800));
            view.UpdateLayout();   // 項目容器是排版時才產生的，逼它把樣板真的套上去

            foreach (var line in listener.Drain().Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t.Contains("BindingExpression path error") ||
                    t.Contains("Cannot convert") ||
                    t.Contains("Cannot find governing FrameworkElement"))
                    failures.Add("繫結錯誤：" + t);
            }
        }
        catch (Exception ex)
        {
            failures.Add("套用樣板失敗：" + Describe(ex));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }

    /// <summary>
    /// 同一個道理，NVMe 電源狀態頁有三張清單（電源狀態表、APST 表、實測樣本），
    /// 三個樣板都只有在有資料時才會被套用。
    /// </summary>
    [Fact]
    public void NVMe電源狀態頁的清單樣板_有資料時也沒有繫結錯誤()
    {
        var failures = new List<string>();
        var thread = new Thread(() => RunNvmePowerTemplate(failures));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromMinutes(1)))
            failures.Add("NVMe 電源狀態樣板煙霧測試逾時（1 分鐘未完成）。");

        Assert.True(failures.Count == 0,
            $"NVMe 電源狀態頁樣板發現 {failures.Count} 項問題：\n" + string.Join("\n", failures));
    }

    /// <summary>只提供 NvmePower 一個屬性的最小宿主。</summary>
    private sealed class NvmeOnly { public NvmePowerService NvmePower { get; } = new(); }

    private static void RunNvmePowerTemplate(List<string> failures)
    {
        WpfEnv.Ensure();

        PresentationTraceSources.Refresh();
        var listener = new CollectingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        try
        {
            var host = new NvmeOnly();
            host.NvmePower.States.Add(new NvmePowerStateRow
            {
                State = 0, NonOperational = false, MaxPowerW = 9,
                RelRead = 0, RelReadLatency = 0, RelWrite = 0, RelWriteLatency = 0,
            });
            host.NvmePower.States.Add(new NvmePowerStateRow
            {
                State = 3, NonOperational = true, MaxPowerW = 0.05,
                EntryLatencyUs = 5_000, ExitLatencyUs = 8_000,
                RelRead = 0, RelReadLatency = 0, RelWrite = 0, RelWriteLatency = 0,
            });
            host.NvmePower.Apst.Add(new NvmeApstRow { State = 0, IdleMs = 100, TargetState = 3 });
            host.NvmePower.Samples.Add(new IdleLatencySample(0, 120));
            host.NvmePower.Samples.Add(new IdleLatencySample(2000, 8_100));

            var view = new NvmePowerView { DataContext = host };
            view.Measure(new Size(1280, 900));
            view.Arrange(new Rect(0, 0, 1280, 900));
            view.UpdateLayout();

            foreach (var line in listener.Drain().Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t.Contains("BindingExpression path error") ||
                    t.Contains("Cannot convert") ||
                    t.Contains("Cannot find governing FrameworkElement"))
                    failures.Add("繫結錯誤：" + t);
            }
        }
        catch (Exception ex)
        {
            failures.Add("套用樣板失敗：" + Describe(ex));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }

    /// <summary>顯示鏈路頁的卡片樣板（含嚴重度顏色轉換器）同樣只有在有資料時才會套用。</summary>
    [Fact]
    public void 顯示鏈路頁的清單樣板_有資料時也沒有繫結錯誤()
    {
        var failures = new List<string>();
        var thread = new Thread(() => RunTemplate(failures, "顯示鏈路", () =>
        {
            var host = new DisplayOnly();
            host.DisplayLink.Rows.Add(new DisplayLinkRow
            {
                Name = "測試螢幕", ConnectionText = "DisplayPort（外接）",
                ModeText = "3840 × 2160 ・ 143.98 Hz", PixelClockText = "1188.00 MHz",
                EncodingText = "YCbCr 4:2:2", DepthText = "每通道 8 位元", HdrText = "支援但未啟用",
                RequiredText = "19.01 Gb/s", Verdict = "色度被降到 YCbCr 4:2:2。", Severity = Severity.Warning,
            });
            return new DisplayLinkView { DataContext = host };
        }));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromMinutes(1)))
            failures.Add("顯示鏈路樣板煙霧測試逾時（1 分鐘未完成）。");

        Assert.True(failures.Count == 0,
            $"顯示鏈路頁樣板發現 {failures.Count} 項問題：\n" + string.Join("\n", failures));
    }

    /// <summary>顯示鏈路頁的假 VM。螢幕組成一區綁 EvidenceLab.MonitorFacts，所以也要有它。</summary>
    private sealed class DisplayOnly
    {
        public DisplayLinkService DisplayLink { get; } = new();
        public EvidenceLabService EvidenceLab { get; } = new();
    }

    /// <summary>
    /// PCIe 鏈路頁的 Resizable BAR 卡片：外層是裝置、內層是每一段已指派的位址範圍，
    /// <b>兩層都是 ItemsControl</b>，兩層的樣板都只有在有資料時才會套用。
    /// </summary>
    [Fact]
    public void ResizableBAR卡片的兩層樣板_有資料時也沒有繫結錯誤()
    {
        var failures = new List<string>();
        var thread = new Thread(() => RunTemplate(failures, "Resizable BAR", () =>
        {
            var host = new PcieOnly();
            host.ResizableBar.Rows.Add(new BarDeviceRow
            {
                Name = "測試顯示卡", Location = "PCI 匯流排 1，裝置 0，函數 0",
                Ranges = [new BarRange(0x40_0000_0000, 0x41_FFFF_FFFF), new BarRange(0xD000_0000, 0xD1FF_FFFF)],
                VramBytes = 12UL * 1024 * 1024 * 1024,
                IsPci = true,
            });
            // 第二列刻意是虛擬顯示裝置：判定與顏色走的是另一條分支，欄位也全是「—」
            host.ResizableBar.Rows.Add(new BarDeviceRow
            {
                Name = "虛擬顯示卡", Location = "—", Ranges = [], VramBytes = 0, IsPci = false,
            });
            return new PcieLinkView { DataContext = host };
        }));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromMinutes(1)))
            failures.Add("Resizable BAR 樣板煙霧測試逾時（1 分鐘未完成）。");

        Assert.True(failures.Count == 0,
            $"Resizable BAR 卡片樣板發現 {failures.Count} 項問題：\n" + string.Join("\n", failures));
    }

    /// <summary>PCIe 頁需要的兩個服務；不動用整個 MainViewModel。</summary>
    private sealed class PcieOnly
    {
        public PcieLinkService PcieLink { get; } = new();
        public ResizableBarService ResizableBar { get; } = new();
    }

    /// <summary>共用的樣板套用檢查：建好帶資料的檢視，逼它排版，攔下繫結錯誤。</summary>
    private static void RunTemplate(List<string> failures, string what, Func<FrameworkElement> make)
    {
        WpfEnv.Ensure();

        PresentationTraceSources.Refresh();
        var listener = new CollectingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        try
        {
            var view = make();
            view.Measure(new Size(1280, 900));
            view.Arrange(new Rect(0, 0, 1280, 900));
            view.UpdateLayout();

            foreach (var line in listener.Drain().Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t.Contains("BindingExpression path error") ||
                    t.Contains("Cannot convert") ||
                    t.Contains("Cannot find governing FrameworkElement"))
                    failures.Add("繫結錯誤：" + t);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{what}套用樣板失敗：" + Describe(ex));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }

    private static UsbPortRow SampleUsbRow(int depth, string location)
    {
        var (verdict, severity) = UsbLinkDecoder.Judge(2, 2, 4, true);
        return new UsbPortRow(location, depth, "測試裝置", "VID 1234 ・ PID 5678", "USB 3.2", "大量儲存",
                              UsbLinkDecoder.OperatingText(2, 2, true),
                              UsbLinkDecoder.CapableText(2, true),
                              UsbLinkDecoder.PortText(7),
                              UsbLinkDecoder.PowerText(250, 0x80, false),
                              verdict, severity);
    }

    /// <summary>
    /// 證據頁（韌體安全）在<b>真的有資料</b>時的渲染檢查。
    /// </summary>
    /// <remarks>
    /// 上面那支大煙霧測試建構每一頁時 MainViewModel 是<b>空的</b>——<c>ItemsControl.ItemTemplate</c>
    /// 一次也沒被套用。而事實列的樣板（<c>Name</c>／<c>ValueText</c>／<c>Trust</c>／分組）
    /// 只有在有資料時才會求值，所以「證據頁畫不畫得出儲存可靠性事實」完全沒有人驗過。
    /// <para>
    /// 這一條把<b>共用組裝點</b>（<c>EvidenceCollection.LoadUsermodeFacts</c>）灌進真的 MainViewModel，
    /// 再建構真的 <see cref="FirmwareSecurityView"/>、逼它排版，然後檢查兩件事：
    /// 一是 <c>FirmwareRowsSource</c> 這個 CollectionViewSource 真的生出視圖（代表資料到位、
    /// 分組描述能吃這種類別），二是樣板套用過程沒有繫結錯誤。
    /// </para>
    /// </remarks>
    [Fact]
    public void 證據頁有事實時樣板套用沒有繫結錯誤且真的長出列()
    {
        var failures = new List<string>();
        var thread = new Thread(() => RunFirmwareRows(failures));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromMinutes(1)))
            failures.Add("證據頁事實列煙霧測試逾時（1 分鐘未完成）。");

        Assert.True(failures.Count == 0,
            $"證據頁事實列發現 {failures.Count} 項問題：" + Environment.NewLine
            + string.Join(Environment.NewLine, failures));
    }

    /// <summary>注入用的可靠性計數器來源：兩顆碟，其中一顆只回兩個欄位——用來逼出「不適用」那一列。</summary>
    private sealed class TwoDiskStorageSource : IStorageReliabilitySource
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<StorageReliabilityRaw> Read() =>
        [
            new StorageReliabilityRaw(0, 12, 29, 40, 1424, 5, 100, 7, 200, 0, 0, 0, 0, 0, 0, 3, 4, 0, "2023-08"),
            new StorageReliabilityRaw(1, 33, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null),
        ];
    }

    private static void RunFirmwareRows(List<string> failures)
    {
        WpfEnv.Ensure();

        PresentationTraceSources.Refresh();
        var listener = new CollectingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        try
        {
            var vm = new MainViewModel();   // 與煙霧測試同一個慣例：絕不呼叫 Initialize()

            // 這一筆是本條測試的重點：走的是 UI 與 CLI 共用的那一條組裝點，
            // 所以它同時驗證了「接線真的接上了」。
            EvidenceCollection.LoadUsermodeFacts(vm.EvidenceLab, new TwoDiskStorageSource());

            if (!vm.EvidenceLab.FirmwareSecurityRows.Any(r => r.Name.Contains("磨損程度")))
                failures.Add("資料沒到達頁面：FirmwareSecurityRows 找不到儲存可靠性的列——接線斷了。");

            var view = new FirmwareSecurityView { DataContext = vm };
            view.Measure(new Size(1280, 800));
            view.Arrange(new Rect(0, 0, 1280, 800));
            view.UpdateLayout();

            if (view.Resources["FirmwareRowsSource"] is not System.Windows.Data.CollectionViewSource cv
                || cv.View is null || !cv.View.Cast<object>().Any())
            {
                failures.Add("FirmwareRowsSource 沒有生出視圖：CollectionViewSource 沒綁到事實列，畫面上會是空的。");
            }
            else if (!cv.View.Cast<object>().OfType<EvidenceFactRow>()
                         .Any(r => r.Category == "儲存裝置"))
            {
                failures.Add("FirmwareRowsSource 生出視圖了，但裡面沒有「儲存裝置」的列——資料被濾掉或分組吃掉了。");
            }

            using var sr = new System.IO.StringReader(listener.Drain());
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t.Contains("BindingExpression path error") ||
                    t.Contains("Cannot convert") ||
                    t.Contains("Cannot find governing FrameworkElement"))
                    failures.Add("繫結錯誤：" + t);
            }
        }
        catch (Exception ex)
        {
            failures.Add("證據頁事實列套用失敗：" + Describe(ex));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }

    /// <summary>
    /// 把例外連同<b>全部內層例外</b>攤成一行。XamlParseException 的訊息只說「設定屬性時擲回例外狀況」，
    /// 真正的原因永遠在 InnerException 裡——只印外層等於把診斷資訊丟掉。
    /// </summary>
    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (sb.Length > 0) sb.Append(" ← ");
            sb.Append(e.GetType().Name).Append('：').Append(e.Message);
        }
        var deepest = ex;
        while (deepest.InnerException is not null) deepest = deepest.InnerException;
        if (deepest.StackTrace is { Length: > 0 } st)
            sb.Append("\n    最內層堆疊：").Append(st.Trim().Replace("\n", "\n    "));
        return sb.ToString();
    }
}
