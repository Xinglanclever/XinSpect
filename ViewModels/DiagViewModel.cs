using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace XinSpect;

/// <summary>
/// 把 <see cref="Diag"/> 的內容搬上畫面：被接住、功能降級、程式照樣跑的那些例外。
/// </summary>
/// <remarks>
/// <see cref="Diag"/> 從一開始就在記錄，但在 1.5.x 之前<b>沒有任何介面看得到它</b>——
/// 那等於白記：使用者看到某一欄是「—」時，仍然無法分辨是這台機器沒有這個項目、
/// 是驅動不給讀，還是程式自己有缺陷。這個檢視模型就是補上那條路。
///
/// 刻意不做的事：不訂閱 <see cref="Diag"/> 的靜態事件。<see cref="Diag.Swallow"/> 會從
/// ETW 泵、量測執行緒、執行緒集區等任何地方被呼叫，靜態事件等於把 UI 物件掛在
/// 一個永生的根上、還得每次自己切回 UI 執行緒。改由每秒心跳呼叫
/// <see cref="RefreshIfChanged"/>：判斷只比一個整數，沒變就什麼都不做。
/// </remarks>
public sealed class DiagViewModel : ObservableObject
{
    /// <summary>畫面上最多列幾筆（記憶體裡可能更多，完整內容在檔案）。</summary>
    public const int ShowMax = 60;

    private int _seen = -1;   // 上次重建清單時的累計筆數；−1 迫使首次必定重建

    /// <summary>最近的降級紀錄，<b>新的在上</b>（畫面由上往下讀）。</summary>
    public ObservableCollection<DiagEntry> Recent { get; } = [];

    /// <summary>累計筆數（含已被環形緩衝丟棄的）。</summary>
    public int Count => Diag.Count;

    public string FilePath => Diag.FilePath;

    public bool HasEntries => Recent.Count > 0;

    /// <summary>一句話交代這份清單目前的狀態，包含「沒有紀錄」該怎麼解讀。</summary>
    public string SummaryText => Diag.Count == 0
        ? "目前沒有降級紀錄。可能是一切正常，也可能是相關功能這次還沒被用到——這個欄位不是「保證無誤」的意思。"
        : $"累計 {Diag.Count} 筆，下方列出最近 {Recent.Count} 筆（記憶體保留最多 {Diag.MaxEntries} 筆，更早的只在紀錄檔裡）。";

    private string _hint = "";
    /// <summary>操作結果提示（開檔失敗、已清空之類）。</summary>
    public string HintText { get => _hint; private set => SetProperty(ref _hint, value); }

    public DiagViewModel() => Refresh();

    /// <summary>
    /// 筆數有變才重建清單。供每秒心跳呼叫——判斷成本是一次整數比較。
    /// </summary>
    public void RefreshIfChanged()
    {
        if (_seen != Diag.Count) Refresh();
    }

    /// <summary>重建清單並發佈所有衍生屬性。須在 UI 執行緒上呼叫。</summary>
    public void Refresh()
    {
        _seen = Diag.Count;
        var snapshot = Diag.Entries;

        Recent.Clear();
        for (int i = snapshot.Count - 1, n = 0; i >= 0 && n < ShowMax; i--, n++)
            Recent.Add(snapshot[i]);

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(FilePath));
    }

    /// <summary>清空記憶體中的紀錄。<b>不刪紀錄檔</b>——檔案才是回報問題時要附的東西。</summary>
    public void Clear()
    {
        Diag.Reset();
        Refresh();
        HintText = "已清空畫面上的紀錄；diag.log 檔案本身保留不動。";
    }

    /// <summary>以系統預設程式開啟 diag.log；還沒有檔案時如實說明而不是靜靜沒反應。</summary>
    public void OpenFile()
    {
        string path = Diag.FilePath;
        if (!File.Exists(path))
        {
            HintText = "還沒有 diag.log——這台機器上目前沒有任何被接住的例外寫進檔案。";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            HintText = "已以系統預設程式開啟 " + path;
        }
        catch (Exception ex)
        {
            Diag.Swallow("開啟診斷紀錄", ex, "改為請使用者自行前往該路徑");
            HintText = "開不起來（" + ex.Message + "）。路徑：" + path;
        }
    }

    /// <summary>開啟紀錄所在資料夾（與當機紀錄同一個）。</summary>
    public void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(CrashLog.Folder);
            Process.Start(new ProcessStartInfo(CrashLog.Folder) { UseShellExecute = true });
            HintText = "已開啟 " + CrashLog.Folder;
        }
        catch (Exception ex)
        {
            Diag.Swallow("開啟診斷資料夾", ex, "改為請使用者自行前往該路徑");
            HintText = "開不起來（" + ex.Message + "）。路徑：" + CrashLog.Folder;
        }
    }
}
