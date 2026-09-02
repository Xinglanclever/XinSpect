using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;

namespace XinSpect;

/// <summary>
/// 開機耗時分解：最近一次開機花了多久，以及 Windows 記下來是誰比平常慢。
/// </summary>
/// <remarks>
/// 資料全部取自 Windows 自己的 <c>Microsoft-Windows-Diagnostics-Performance/Operational</c> 頻道
/// （唯讀讀取事件記錄，零特權）：
/// <list type="bullet">
/// <item>事件 100：總開機時間、主路徑時間、登入後時間。</item>
/// <item>事件 101／102／103／106／109／110：分別是應用程式、驅動、服務、背景最佳化、裝置、
/// 啟動服務比平常慢，每一筆都帶名稱、總時間與「比平常多花多久」。</item>
/// </list>
/// <para>
/// 為什麼值得單獨一張卡：可靠性歷史只給了一個總時間，而「開機很慢」這件事沒有名單就無法處理。
/// Windows 其實早就把名單寫下來了，只是沒人去看。
/// </para>
/// </remarks>
public sealed class BootBreakdownService : ObservableObject
{
    private const string Channel = "Microsoft-Windows-Diagnostics-Performance/Operational";

    /// <summary>事件編號 → 類別名稱。沒收錄的編號不列入，不猜它是什麼。</summary>
    private static readonly Dictionary<int, string> Kinds = new()
    {
        [101] = "應用程式",
        [102] = "驅動程式",
        [103] = "服務",
        [106] = "背景最佳化",
        [109] = "裝置",
        [110] = "啟動服務",
    };

    public ObservableCollection<BootCulprit> Culprits { get; } = [];

    private BootVerdict _verdict = new()
    {
        Headline = "尚未讀取", Severity = Severity.Neutral,
        Detail = "第一次進入本頁時會讀一次 Windows 的開機效能紀錄（唯讀）。",
    };
    public BootVerdict Verdict { get => _verdict; private set => SetProperty(ref _verdict, value); }

    private string _bootText = "—";
    public string BootText { get => _bootText; private set => SetProperty(ref _bootText, value); }

    private string _mainPathText = "—";
    public string MainPathText { get => _mainPathText; private set => SetProperty(ref _mainPathText, value); }

    private string _postBootText = "—";
    public string PostBootText { get => _postBootText; private set => SetProperty(ref _postBootText, value); }

    private string _when = "—";
    public string WhenText { get => _when; private set => SetProperty(ref _when, value); }

    private bool _busy;
    public bool IsBusy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanRefresh)); }
    }

    public bool CanRefresh => !_busy;

    private bool _loaded;

    /// <summary>第一次進頁時讀一次。</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Refresh();
    }

    public void Refresh()
    {
        if (_busy) return;
        IsBusy = true;
        _ = Task.Run(Collect).ContinueWith(t =>
        {
            var r = t.Result;
            Culprits.Clear();
            foreach (var c in r.Culprits) Culprits.Add(c);
            BootText = BootBreakdownDecoder.MsText(r.BootMs);
            MainPathText = BootBreakdownDecoder.MsText(r.MainPathMs);
            PostBootText = BootBreakdownDecoder.MsText(r.PostBootMs);
            WhenText = r.When;
            Verdict = BootBreakdownDecoder.Judge(r.BootMs, r.MainPathMs, r.PostBootMs, r.Culprits, r.ChannelMissing);
            IsBusy = false;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private readonly record struct Snapshot(long BootMs, long MainPathMs, long PostBootMs,
                                            string When, List<BootCulprit> Culprits, bool ChannelMissing);

    private static Snapshot Collect()
    {
        long boot = 0, mainPath = 0, postBoot = 0;
        string when = "—";
        var culprits = new List<BootCulprit>();
        int? bootId = null;
        bool channelMissing = false;

        try
        {
            // 事件 100 的最近一筆：屬性順序依 Windows 的資訊清單（1=BootTsVersion…3=BootTime）
            using var reader = new EventLogReader(new EventLogQuery(
                Channel, PathType.LogName, "*[System[(EventID=100)]]") { ReverseDirection = true });
            using var rec = reader.ReadEvent();
            if (rec is not null)
            {
                boot = LongOf(rec, "BootTime");
                mainPath = LongOf(rec, "MainPathBootTime");
                postBoot = LongOf(rec, "BootPostBootTime");
                when = rec.TimeCreated?.ToString("yyyy-MM-dd HH:mm") ?? "—";
                bootId = IntOf(rec, "BootInstance");
            }
        }
        catch (EventLogNotFoundException)
        {
            // 頻道根本不存在（Windows Server 不隨附）——這不是缺陷，也不該記成例外
            channelMissing = true;
        }
        catch (Exception ex)
        {
            Diag.Swallow("BootBreakdownService.Boot100", ex, "讀不到開機總時間，卡片顯示為「讀不到」。");
        }

        // 拖慢項目：同一次開機（BootInstance 相同）的 101/102/103/106/109/110
        foreach (int id in channelMissing ? Array.Empty<int>() : Kinds.Keys.ToArray())
        {
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(
                    Channel, PathType.LogName, $"*[System[(EventID={id})]]") { ReverseDirection = true });
                for (int taken = 0; taken < 40; taken++)
                {
                    EventRecord? rec;
                    try { rec = reader.ReadEvent(); } catch (EventLogException) { break; }
                    if (rec is null) break;
                    using (rec)
                    {
                        // 只收最近那一次開機的紀錄；讀不到 BootInstance 就一律收（寧可多列，不要漏）
                        int? inst = IntOf(rec, "BootInstance");
                        if (bootId is { } b && inst is { } i && i != b) continue;

                        string name = StringOf(rec, "Name");
                        if (string.IsNullOrWhiteSpace(name)) name = StringOf(rec, "FriendlyName");
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        culprits.Add(new BootCulprit
                        {
                            Name = name.Trim(),
                            Kind = Kinds[id],
                            TotalMs = LongOf(rec, "TotalTime"),
                            DegradationMs = LongOf(rec, "DegradationTime"),
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Swallow($"BootBreakdownService.Event{id}", ex, $"開機拖慢項目（事件 {id}）讀不到，該類別不列入。");
            }
        }

        return new Snapshot(boot, mainPath, postBoot, when, BootBreakdownDecoder.Rank(culprits), channelMissing);
    }

    // ── 事件屬性取值：依名稱而不是位置，Windows 各版本的順序不保證一致 ──────────

    private static string StringOf(EventRecord rec, string name)
    {
        try
        {
            var xml = System.Xml.Linq.XDocument.Parse(rec.ToXml());
            var ns = xml.Root!.Name.Namespace;
            return xml.Descendants(ns + "Data")
                      .FirstOrDefault(d => (string?)d.Attribute("Name") == name)?.Value ?? "";
        }
        catch { return ""; }
    }

    private static long LongOf(EventRecord rec, string name)
        => long.TryParse(StringOf(rec, name), out long v) ? v : 0;

    private static int? IntOf(EventRecord rec, string name)
        => int.TryParse(StringOf(rec, name), out int v) ? v : null;
}
