using System.Collections.ObjectModel;

namespace XinSpect;

/// <summary>
/// M.2 / U.2 介面分析服務。
/// 從 SMBIOS Type 9 偵測 M.2 與 U.2 插槽，並提供 Key 定義、尺寸對照與通道規格參考。
/// </summary>
public sealed class M2AnalysisService : ObservableObject
{
    private string _detectedSlots = "（偵測中…）";
    public string DetectedSlots { get => _detectedSlots; private set => SetProperty(ref _detectedSlots, value); }

    public ObservableCollection<SmbiosSlotRow> Slots { get; } = [];
    public List<M2FormFactor> FormFactors { get; } = BuildFormFactors();
    public List<M2KeySpec> KeySpecs { get; } = BuildKeySpecs();

    public M2AnalysisService()
    {
        DetectSlots();
    }

    void DetectSlots()
    {
        try
        {
            var smbios = new SmbiosService();
            int count = 0;
            foreach (var slot in smbios.Slots)
            {
                var typeLower = slot.Type.ToLowerInvariant();
                // 只認實際會出現的字串。原本還列了 0x14/0x15/0x17/0x1f… 等十六進位碼，
                // 但 SmbiosService.SlotTypeName 對已知碼一律回中文名（0x17→「M.2 Socket 3」），
                // 那些條件永遠為假——留著只會讓後人以為它們在起作用。
                if (typeLower.Contains("m.2") || typeLower.Contains("sff-8639") ||
                    typeLower.Contains("u.2"))
                {
                    Slots.Add(slot);
                    count++;
                }
            }
            DetectedSlots = count > 0
                ? $"偵測到 {count} 個 M.2 / U.2 插槽"
                : "SMBIOS 未回報 M.2 / U.2 插槽（不代表沒有——部分主機板不在 Type 9 裡列出 M.2）";
        }
        catch
        {
            DetectedSlots = "（SMBIOS 讀取失敗）";
        }
    }

    static List<M2FormFactor> BuildFormFactors() =>
    [
        new("2230", "22 x 30 mm", "Wi-Fi 卡、部分 OEM SSD（Steam Deck、Surface）"),
        new("2242", "22 x 42 mm", "短規格 SSD、部分筆電 WWAN 模組"),
        new("2260", "22 x 60 mm", "少見，部分舊款 SATA M.2 SSD"),
        new("2280", "22 x 80 mm", "最常見的 NVMe / SATA SSD 尺寸"),
        new("22110", "22 x 110 mm", "企業級 NVMe SSD、部分高容量消費級"),
    ];

    static List<M2KeySpec> BuildKeySpecs() =>
    [
        new("Key B", "缺口在右側（pin 12–19）",
            "PCIe x2 + SATA + USB 3.0",
            "SATA SSD、WWAN 模組",
            [
                "最多 PCIe x2（Gen 3 約 2 GB/s）",
                "同時支援 SATA 與 PCIe 協定",
                "常見於早期 M.2 SATA SSD",
            ]),
        new("Key M", "缺口在左側（pin 59–66）",
            "PCIe x4 + SATA",
            "NVMe SSD（主流高速）",
            [
                "PCIe x4（Gen 3 約 4 GB/s、Gen 4 約 8 GB/s、Gen 5 約 16 GB/s）",
                "NVMe SSD 幾乎都走這個 Key",
                "部分 Key M 插槽也接受 Key B+M 的 SATA SSD",
            ]),
        new("Key B+M", "左右兩側皆有缺口",
            "PCIe x2 + SATA",
            "相容性最廣的 SATA / PCIe x2 SSD",
            [
                "可以插入 Key B 或 Key M 的插槽",
                "但只用得到 PCIe x2（即使插在 Key M x4 插槽）",
                "多數 M.2 SATA SSD 採此設計",
            ]),
        new("Key A", "缺口在右側（pin 8–15）",
            "PCIe x2 + USB 2.0 + I2C / DP",
            "Wi-Fi / 藍牙模組",
            [
                "Intel AX200/AX210 等無線網卡使用",
                "不用於儲存裝置",
            ]),
        new("Key E", "缺口在右側（pin 24–31）",
            "PCIe x2 + USB 2.0 + SDIO / UART",
            "Wi-Fi / 藍牙模組、部分 AI 加速卡",
            [
                "與 Key A 功能類似，部分平台混用",
                "Coral Edge TPU 等 AI 模組採此 Key",
            ]),
    ];

    public sealed record M2FormFactor(string Name, string Dimensions, string Note);
    public sealed record M2KeySpec(string KeyName, string Notch, string Lanes, string Usage, List<string> Facts);
}
