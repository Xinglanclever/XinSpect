using System.ComponentModel;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 一格核心熱區圖的資料形狀。<see cref="CoreRow"/>（感測器逐核列）與
/// <see cref="CoreTempEntry"/>（逐實體核心摘要）都實作它——熱區圖因此只有
/// <b>一份</b>畫格子的方式，兩種來源不可能各自漂移。
/// </summary>
/// <remarks>
/// 這裡刻意只放三個「兩邊本來就有」的成員，不加任何一邊才有的東西：介面一胖，
/// 第二個實作者就會開始為了通過編譯而長出假的屬性。
/// </remarks>
public interface ICoreHeatCell
{
    /// <summary>顯示用名字（例如「核心 #7」）。</summary>
    string Name { get; }

    /// <summary>該核溫度（°C）。<c>null</c>＝這個來源沒有讀值——<b>不要用 0 代替</b>。</summary>
    double? TempC { get; }

    /// <summary>該核使用率（0–100）。</summary>
    double LoadPercent { get; }
}

/// <summary>
/// 熱區圖上的一格位置。要畫幾格由「這台機器有幾顆實體核心」決定，不是由「感測器回了幾筆」決定——
/// 所以每個位置都被保留下來，沒有讀值的那幾格如實顯示「—」與中性底色，而不是從畫面上消失。
/// </summary>
/// <remarks>
/// 這一層同時是<b>活轉接</b>：來源物件（<see cref="ObservableObject"/>）每秒改值時，
/// 它把通知原樣轉出來，所以格子的顏色真的會每秒跟著溫度走。轉接而不直接繫結來源，
/// 是因為兩種來源的屬性名不同（<c>TempC</c>／<c>Temperature</c>），而畫格子只有一套樣板；
/// 與其讓樣板去認兩種名字，不如在這裡把名字統一。
/// </remarks>
public sealed class CoreHeatCell : ObservableObject, IDisposable
{
    private readonly ICoreHeatCell? _source;
    private readonly PropertyChangedEventHandler? _relay;

    public CoreHeatCell(int position, ICoreHeatCell? source)
    {
        Position = position;
        _source = source;
        if (source is INotifyPropertyChanged n)
        {
            _relay = OnSourceChanged;
            n.PropertyChanged += _relay;
        }
    }

    /// <summary>在圖上的位置（0 起算，與實體核心列舉順序一致）。</summary>
    public int Position { get; }

    /// <summary>這個位置有對應到感測器列嗎？（有列 ≠ 有溫度讀值）</summary>
    public bool HasSource => _source is not null;

    /// <summary>有溫度讀值嗎？沒有的話格子畫成中性色，不假裝涼。</summary>
    public bool HasReading => TempC.HasValue;

    public string Name => _source?.Name ?? $"核心 #{Position + 1}";

    /// <summary>格內角落的短標籤（「核心 #13」→「#13」），擠不下時可以整條收掉。</summary>
    public string ShortName
    {
        get
        {
            string name = Name;
            int hash = name.LastIndexOf('#');
            if (hash >= 0 && hash + 1 < name.Length)
            {
                int end = hash + 1;
                while (end < name.Length && char.IsAsciiDigit(name[end])) end++;
                if (end > hash + 1) return name[hash..end];
            }
            return name;
        }
    }

    public double? TempC => _source?.TempC;
    public double LoadPercent => _source?.LoadPercent ?? 0;

    public string TempText => TempC is { } t ? $"{t:0} °C" : "—";

    /// <summary>格子底色（＝溫度）；無讀值時為 <c>null</c>，由版面以主題中性色表示。</summary>
    public Brush? Fill => HeatScale.BrushFor(TempC);

    /// <summary>格內文字在該底色上的墨色。</summary>
    public Brush Ink => HeatScale.InkFor(HeatScale.ColorFor(TempC));

    /// <summary>負載條底槽色（跟著墨色走）。</summary>
    public Brush LoadTrack => HeatScale.TrackFor(TempC);

    /// <summary>
    /// 滑過去看得到的完整說明；「沒有讀值」也要說清楚是哪一種沒有。
    /// 走 <see cref="LanguageService.TComposite"/>：這是程式拼出來的字串，整串查表一定查不到，
    /// 得按「 ・ 」分段查，否則英語模式下這一格會夾著中文。
    /// </summary>
    public string Tooltip => _source is null
        ? LanguageService.TComposite($"位置 #{Position + 1} ・ 這個位置沒有對應的逐核感測器")
        : LanguageService.TComposite($"{Name} ・ {(TempC is { } t ? $"{t:0} °C" : "沒有溫度讀值")} ・ 使用率 {LoadPercent:0} %");

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ICoreHeatCell.TempC):
                RaiseTemperature();
                break;
            case nameof(ICoreHeatCell.LoadPercent):
                OnPropertyChanged(nameof(LoadPercent));
                OnPropertyChanged(nameof(Tooltip));
                break;
            case nameof(ICoreHeatCell.Name):
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(ShortName));
                OnPropertyChanged(nameof(Tooltip));
                break;
            default:
                // 來源用了別的通知名（例如 CoreTempEntry 的 Temperature）：寧可多轉一次，不要漏轉。
                RaiseTemperature();
                OnPropertyChanged(nameof(LoadPercent));
                break;
        }
    }

    private void RaiseTemperature()
    {
        OnPropertyChanged(nameof(TempC));
        OnPropertyChanged(nameof(TempText));
        OnPropertyChanged(nameof(HasReading));
        OnPropertyChanged(nameof(Fill));
        OnPropertyChanged(nameof(Ink));
        OnPropertyChanged(nameof(LoadTrack));
        OnPropertyChanged(nameof(Tooltip));
    }

    public void Dispose()
    {
        if (_source is INotifyPropertyChanged n && _relay is not null)
            n.PropertyChanged -= _relay;
    }
}

/// <summary>
/// 熱區圖的版面算術：幾顆核心排成幾欄。抽出來是為了<b>可測</b>——
/// 「18 核排成 6×3、32 核排成 8×4」這種事寫在版面事件裡就只能靠眼睛驗。
/// </summary>
public static class CoreHeatLayout
{
    /// <summary>一列最多幾欄：再多就變成細長條，看不出「一塊晶片」的樣子。</summary>
    public const int MaxColumns = 16;

    /// <summary>目標列數：接近方形、略微橫向，像一塊晶片而不是一條橫幅。</summary>
    private const int PreferredRows = 3;

    /// <summary>核心數 → 欄數。整除優先，其次少留空格，再其次別太高。</summary>
    public static int Columns(int count)
    {
        if (count <= 1) return 1;

        // 小核心數的平台（4、6 核）用「每列最多 8 欄」會排出很扁的一條，
        // 所以欄數上限隨核心數放寬：32 核以內最多 8 欄，之後才允許到 16 欄。
        int maxColumns = count <= 32 ? 8 : MaxColumns;
        int best = Math.Min(count, 2);
        int bestCost = int.MaxValue;

        for (int c = 2; c <= Math.Min(maxColumns, count); c++)
        {
            int rows = (count + c - 1) / c;
            int empty = rows * c - count;                 // 最後一列會空幾格
            int cost = empty * 4                           // 空格難看
                     + Math.Abs(rows - PreferredRows) * 2  // 離目標列數越遠越差
                     + Math.Max(0, rows - 4) * 3;          // 太高的話再罰一次
            if (cost <= bestCost) { bestCost = cost; best = c; }   // 同分取較寬的（橫向晶片）
        }
        return best;
    }

    /// <summary>核心數與欄數 → 列數。</summary>
    public static int Rows(int count, int columns)
        => count <= 0 || columns <= 0 ? 0 : (count + columns - 1) / columns;
}
