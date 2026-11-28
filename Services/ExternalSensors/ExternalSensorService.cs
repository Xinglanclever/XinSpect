// 外部感測器服務協調器
// 統一管理 HWiNFO、AIDA64、Core Temp 三種共享記憶體來源

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace XinSpect;

/// <summary>
/// 資料來源模式：指定要查詢哪些第三方硬體監控工具。
/// </summary>
public enum SourceMode
{
    /// <summary>不查詢任何外部來源。</summary>
    None,
    /// <summary>僅 HWiNFO。</summary>
    HwInfo,
    /// <summary>僅 AIDA64。</summary>
    Aida64,
    /// <summary>僅 Core Temp。</summary>
    CoreTemp,
    /// <summary>自動偵測並合併所有可用來源。</summary>
    All,
}

/// <summary>
/// 代表單一外部來源的偵測狀態。
/// </summary>
public sealed record SourceStatus(
    /// <summary>來源名稱</summary>
    string Name,
    /// <summary>是否可用</summary>
    bool IsAvailable,
    /// <summary>狀態描述</summary>
    string Detail,
    int? ReadingCount = null,
    string? ProductDetail = null)
{
    /// <summary>Catalogued status prose is translated; provider diagnostics remain unchanged.</summary>
    public string DisplayDetail => ProductDetail is not null
        ? ProductTextProjection.LocalizeKnown(ProductDetail)
        : ReadingCount is int count
            ? ProductTextProjection.LocalizeKnown($"{count} 筆讀數")
            : Detail;
}

/// <summary>
/// 外部感測器服務：偵測並讀取第三方硬體監控工具的共享記憶體資料。
/// 與 <see cref="SensorService"/> 獨立運作，不修改原有 LHM 感測器邏輯。
/// </summary>
public sealed class ExternalSensorService : ObservableObject
{
    // ── 可繫結屬性 ─────────────────────────────────────────────

    private bool _isLoading;
    /// <summary>是否正在讀取中。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    private string _status = "尚未查詢";
    /// <summary>目前狀態描述（供 UI 顯示）。</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(DisplayStatus));
        }
    }

    /// <summary>Localized product status; exception content remains an anchored template parameter.</summary>
    public string DisplayStatus => ProductTextProjection.LocalizeKnown(_status);

    private SourceMode _mode = SourceMode.All;
    /// <summary>資料來源模式。</summary>
    public SourceMode Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, value);
    }

    /// <summary>最近一次查詢取得的所有讀數。</summary>
    public ObservableCollection<ExternalReading> Readings { get; } = new();

    public ObservableCollection<CoreTemperatureComparison> CoreTemperatureComparisons { get; } = new();

    private string _comparisonStatus = "按重新偵測後核對 Core Temp 與 LHM 的逐核心溫度。";
    public string ComparisonStatus
    {
        get => _comparisonStatus;
        private set => SetProperty(ref _comparisonStatus, value);
    }

    /// <summary>各來源偵測狀態。</summary>
    public ObservableCollection<SourceStatus> Sources { get; } = new();

    // ── 核心方法 ──────────────────────────────────────────────

    /// <summary>
    /// 重新整理：依 <see cref="Mode"/> 嘗試各來源，合併結果。
    /// </summary>
    public void Refresh(IReadOnlyList<CoreTemperatureSample>? primarySamples = null)
    {
        IsLoading = true;
        Readings.Clear();
        Sources.Clear();
        CoreTemperatureComparisons.Clear();
        ComparisonStatus = "交叉比對尚未完成。";

        var errors = new List<string>();
        int totalCount = 0;

        try
        {
            // ── HWiNFO ──
            if (_mode is SourceMode.All or SourceMode.HwInfo)
            {
                bool ok = HwInfoSharedMem.TryRead(out var hwReadings, out string? hwErr);
                Sources.Add(Source("HWiNFO", ok, hwReadings.Count, hwErr));
                if (ok)
                {
                    foreach (var r in hwReadings) Readings.Add(r);
                    totalCount += hwReadings.Count;
                }
                else if (hwErr is not null)
                {
                    errors.Add(hwErr);
                }
            }

            // ── AIDA64 ──
            if (_mode is SourceMode.All or SourceMode.Aida64)
            {
                bool ok = Aida64SharedMem.TryRead(out var aidaReadings, out string? aidaErr);
                Sources.Add(Source("AIDA64", ok, aidaReadings.Count, aidaErr));
                if (ok)
                {
                    foreach (var r in aidaReadings) Readings.Add(r);
                    totalCount += aidaReadings.Count;
                }
                else if (aidaErr is not null)
                {
                    errors.Add(aidaErr);
                }
            }

            // ── Core Temp ──
            if (_mode is SourceMode.All or SourceMode.CoreTemp)
            {
                bool ok = CoreTempSharedMem.TryRead(out var ctReadings, out string? ctErr);
                Sources.Add(Source("Core Temp", ok, ctReadings.Count, ctErr));
                if (ok)
                {
                    foreach (var r in ctReadings) Readings.Add(r);
                    totalCount += ctReadings.Count;
                }
                else if (ctErr is not null)
                {
                    errors.Add(ctErr);
                }
            }

            // ── 狀態彙總 ──
            if (_mode == SourceMode.None)
            {
                Status = "已停用外部感測器";
            }
            else if (totalCount > 0 && errors.Count == 0)
            {
                Status = $"已取得 {totalCount} 筆讀數";
            }
            else if (totalCount > 0)
            {
                Status = $"已取得 {totalCount} 筆讀數（部分來源無法連線）";
            }
            else
            {
                Status = "所有外部來源均無法連線";
            }

            if (primarySamples is null)
            {
                ComparisonStatus = "LHM 感測快照不可用，未做交叉核對。";
            }
            else
            {
                var comparisons = SensorCrossCheckService.CompareCoreTemperatures(primarySamples, Readings);
                foreach (var comparison in comparisons) CoreTemperatureComparisons.Add(comparison);
                ComparisonStatus = comparisons.Count == 0
                    ? "沒有可比對的逐核心溫度（限單 CPU、非異質 Intel，需名稱與正規化序號相同、讀取時間差 ≤3 秒）。"
                    : $"已按序號比對 {comparisons.Count} 個核心候選；來源更新時間與核心對應未經實機驗證，差值不代表精度。";
            }
        }
        catch (Exception ex)
        {
            Status = $"查詢失敗：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 快速偵測哪些第三方工具目前正在執行（不讀取完整資料）。
    /// </summary>
    /// <returns>各來源的偵測結果。</returns>
    public static IReadOnlyList<SourceStatus> DetectAvailableSources()
    {
        var results = new List<SourceStatus>(3);

        {
            bool ok = HwInfoSharedMem.TryRead(out var r, out string? err);
            results.Add(ok ? Source("HWiNFO", true, r.Count, null)
                : err is not null ? Source("HWiNFO", false, 0, err)
                : new SourceStatus("HWiNFO", false, "", ProductDetail: "未偵測到"));
        }

        {
            bool ok = Aida64SharedMem.TryRead(out var r, out string? err);
            results.Add(ok ? Source("AIDA64", true, r.Count, null)
                : err is not null ? Source("AIDA64", false, 0, err)
                : new SourceStatus("AIDA64", false, "", ProductDetail: "未偵測到"));
        }

        {
            bool ok = CoreTempSharedMem.TryRead(out var r, out string? err);
            results.Add(ok ? Source("Core Temp", true, r.Count, null)
                : err is not null ? Source("Core Temp", false, 0, err)
                : new SourceStatus("Core Temp", false, "", ProductDetail: "未偵測到"));
        }

        return results;
    }

    private static SourceStatus Source(string name, bool available, int count, string? error)
        => available ? new SourceStatus(name, true, "", ReadingCount: count)
            : error is not null ? new SourceStatus(name, false, error)
            : new SourceStatus(name, false, "", ProductDetail: "未知錯誤");
}
