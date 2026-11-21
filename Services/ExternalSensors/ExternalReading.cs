// 外部感測器讀數資料模型
// 統一表達來自 HWiNFO、AIDA64、Core Temp 的共享記憶體讀數

namespace XinSpect;

/// <summary>
/// 代表一筆來自第三方硬體監控工具的感測器讀數。
/// </summary>
public sealed record ExternalReading(
    /// <summary>資料來源 ("HWiNFO" / "AIDA64" / "Core Temp")</summary>
    string Source,
    /// <summary>感測器類型 ("Temperature" / "Voltage" / "Fan" / "Power" / "Clock" / "Usage" / "Current" / "Other")</summary>
    string SensorType,
    /// <summary>感測器標籤</summary>
    string Label,
    /// <summary>當前數值</summary>
    double Value,
    /// <summary>單位</summary>
    string Unit,
    /// <summary>最小值 (僅 HWiNFO 提供)</summary>
    double? Min = null,
    /// <summary>最大值 (僅 HWiNFO 提供)</summary>
    double? Max = null,
    /// <summary>平均值 (僅 HWiNFO 提供)</summary>
    double? Avg = null,
    /// <summary>讀取器識別到的裝置型號；不代表硬體感測元件 ID。</summary>
    string? DeviceIdentity = null,
    /// <summary>逐核心來源的零起始索引。</summary>
    int? ChannelIndex = null,
    /// <summary>本程式取得該共享記憶體快照的時間，不是來源工具最後更新時間。</summary>
    DateTimeOffset? CapturedAtUtc = null,
    /// <summary>本程式單調鐘上的擷取區間；來源更新時間仍未知。</summary>
    SensorAcquisition? Acquisition = null
);
