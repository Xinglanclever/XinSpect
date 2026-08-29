using System.Collections.ObjectModel;
using System.Text;

namespace XinSpect;

/// <summary>
/// 總覽儀表板版面：哪些磁貼要顯示、依什麼順序排，並持久化於使用者設定。
/// </summary>
/// <remarks>
/// 只存「順序 + 是否顯示」這一件事，格式是一行以逗號分隔的識別碼，隱藏者前面加減號，例如
/// <c>gauges,trends,-ai,brands</c>。刻意用這種鬆散格式而非結構化 JSON：
/// <list type="bullet">
/// <item>設定檔裡出現的是未知識別碼（改版移除的磁貼）就直接忽略，不會讓整份版面讀不進來；</item>
/// <item>設定檔裡<b>沒提到</b>的磁貼（改版新增的）會依內建預設補到最後，舊使用者升級後仍看得到新東西，
/// 而不是因為「存檔裡沒有它」被永久藏起來。</item>
/// </list>
/// </remarks>
public sealed class DashboardLayout : ObservableObject
{
    /// <summary>一塊磁貼的內建定義。<c>Default</c> 為預設是否顯示。</summary>
    internal readonly record struct TileDef(string Id, string Title, string Hint, bool Default);

    /// <summary>
    /// 內建磁貼目錄。<b>順序即預設版面順序，識別碼一經發佈不得更名</b>（它會寫進設定檔）。
    /// 新增磁貼請往後面加，並在 <c>OverviewView.xaml</c> 補上對應的 <c>Tile.{Id}</c> 樣板。
    /// </summary>
    internal static readonly TileDef[] Catalog =
    {
        new("gauges",  "即時儀表",          "CPU 使用率／CPU 溫度／記憶體／GPU 使用率四環儀表", true),
        new("trends",  "即時走勢",          "近 90 秒的 CPU 使用率、CPU 溫度、記憶體與 GPU 使用率", true),
        new("trends2", "更多走勢",          "近 90 秒的 CPU 頻率、GPU 溫度與顯示記憶體用量", false),
        new("ai",      "AI 評價",           "一鍵請 AI 依本機真實數據評價，或開啟完整 AI 助手", true),
        new("brands",  "硬體識別",          "處理器／顯示卡／主機板／記憶體的廠牌徽章", true),
        new("sysinfo", "作業系統與主機板",  "系統版本、開機時間、機型與 BIOS 兩欄資訊", true),
        new("specs",   "核心規格",          "處理器、核心數、記憶體與顯示卡摘要", true),
    };

    /// <summary>
    /// 把存檔字串解析成「目錄驗證過」的版面計畫：未知識別碼丟棄、重複只取第一次、
    /// 存檔未提及的磁貼依目錄順序補在最後（採其內建預設顯示狀態）。
    /// </summary>
    internal static List<(string Id, bool Visible)> Plan(string? saved)
    {
        var plan = new List<(string Id, bool Visible)>(Catalog.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in (saved ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;

            bool visible = token[0] != '-';
            var id = visible ? token : token[1..].Trim();

            if (!seen.Add(id)) continue;                                  // 存檔重複：只認第一次出現的順位
            if (!Catalog.Any(c => c.Id == id)) { seen.Remove(id); continue; }   // 已移除的磁貼：忽略
            plan.Add((id, visible));
        }

        foreach (var c in Catalog)
            if (seen.Add(c.Id)) plan.Add((c.Id, c.Default));               // 本版新增的磁貼：補到最後

        return plan;
    }

    /// <summary>把版面計畫寫回存檔字串（隱藏者加減號前綴）。</summary>
    internal static string Serialize(IEnumerable<(string Id, bool Visible)> plan)
    {
        var sb = new StringBuilder();
        foreach (var (id, visible) in plan)
        {
            if (sb.Length > 0) sb.Append(',');
            if (!visible) sb.Append('-');
            sb.Append(id);
        }
        return sb.ToString();
    }

    private readonly SettingsService _cfg;

    /// <summary>依使用者順序排好的全部磁貼（含隱藏者；隱藏者由版面以 Visibility 收起）。</summary>
    public ObservableCollection<DashboardTile> Tiles { get; } = new();

    public DashboardLayout(MainViewModel vm, SettingsService cfg)
    {
        _cfg = cfg;
        foreach (var (id, visible) in Plan(cfg.DashboardTiles))
        {
            var def = Catalog.First(c => c.Id == id);
            var tile = new DashboardTile(id, def.Title, def.Hint, visible, vm) { VisibleChanged = OnChanged };
            Tiles.Add(tile);
        }
        Restamp();
    }

    private bool _editing;
    /// <summary>是否展開「自訂磁貼」面板。純介面狀態，不持久化——下次進來還是先看資料。</summary>
    public bool Editing { get => _editing; set => SetProperty(ref _editing, value); }

    /// <summary>往前挪一位（已在最前則無事發生）。</summary>
    public void MoveUp(DashboardTile tile)
    {
        int i = Tiles.IndexOf(tile);
        if (i <= 0) return;
        Tiles.Move(i, i - 1);
        OnChanged();
    }

    /// <summary>往後挪一位（已在最後則無事發生）。</summary>
    public void MoveDown(DashboardTile tile)
    {
        int i = Tiles.IndexOf(tile);
        if (i < 0 || i >= Tiles.Count - 1) return;
        Tiles.Move(i, i + 1);
        OnChanged();
    }

    /// <summary>回復內建版面（順序與顯示狀態都還原）。</summary>
    public void Reset()
    {
        // 就地重排既有磁貼物件，不重建：樣板已經掛在畫面上，重建等於整頁重繪一次
        for (int target = 0; target < Catalog.Length; target++)
        {
            var def = Catalog[target];
            int at = -1;
            for (int j = 0; j < Tiles.Count; j++) if (Tiles[j].Id == def.Id) { at = j; break; }
            if (at < 0) continue;
            if (at != target) Tiles.Move(at, target);
            Tiles[target].Visible = def.Default;
        }
        OnChanged();
    }

    /// <summary>順序或顯示狀態變更：重算交錯延遲與端點旗標，並存檔。</summary>
    private void OnChanged()
    {
        Restamp();
        _cfg.DashboardTiles = Serialize(Tiles.Select(t => (t.Id, t.Visible)));
    }

    /// <summary>重算每塊磁貼的進場延遲與是否位於端點。</summary>
    private void Restamp()
    {
        int shown = 0;
        for (int i = 0; i < Tiles.Count; i++)
        {
            var t = Tiles[i];
            t.CanMoveUp = i > 0;
            t.CanMoveDown = i < Tiles.Count - 1;
            // 隱藏的磁貼不佔延遲位次，否則把前幾塊藏起來後，第一塊可見磁貼要等半秒才浮出來
            t.RevealDelay = t.Visible ? Math.Min(shown++ * 45, 270) : double.NaN;
        }
    }
}
