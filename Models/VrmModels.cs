namespace XinSpect;

/// <summary>VRM 供電控制器晶片的廠商家族。</summary>
public enum VrmVendorFamily
{
    Unknown,
    Renesas,
    Mps,
    Infineon,
}

/// <summary>一顆已辨識的 VRM PMBus 控制器晶片。</summary>
/// <param name="ChipName">晶片型號名稱（如 ISL69269）。</param>
/// <param name="Address">SMBus 7-bit 裝置位址（0x40–0x4F）。</param>
/// <param name="LlcRegister">LLC 設定所在的 PMBus 命令暫存器。</param>
/// <param name="MaxLlcLevel">此晶片支援的最大 LLC 等級（0-based：0 到 MaxLlcLevel 皆合法）。</param>
/// <param name="VendorFamily">廠商家族。</param>
public sealed record VrmChipInfo(string ChipName, byte Address, byte LlcRegister, int MaxLlcLevel, VrmVendorFamily VendorFamily);

/// <summary>VRM 自動偵測的結果。</summary>
/// <param name="Found">是否找到至少一顆已知晶片。</param>
/// <param name="Chips">偵測到的所有晶片清單。</param>
/// <param name="ScanLog">掃描過程的紀錄（供診斷用）。</param>
public sealed record VrmDetectionResult(bool Found, IReadOnlyList<VrmChipInfo> Chips, string ScanLog);

/// <summary>一次 LLC 讀取的結果。</summary>
/// <param name="Level">當前 LLC 等級。</param>
/// <param name="RawByte">暫存器原始值。</param>
/// <param name="ChipName">來源晶片型號。</param>
public sealed record VrmLlcReading(int Level, byte RawByte, string ChipName);
