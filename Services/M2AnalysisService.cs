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

    /// <summary>BIOS 明講是 M.2／U.2 的插槽（量到的事實）。</summary>
    public ObservableCollection<SmbiosSlotRow> Slots { get; } = [];

    /// <summary>BIOS 只標成 PCI Express x4、沒有說它是 M.2 的插槽（<b>推論</b>，顯示時要標明）。</summary>
    public ObservableCollection<SmbiosSlotRow> PossibleSlots { get; } = [];

    /// <summary>本機每一顆磁碟的外型說明（有第一手證據才說外型，其餘如實說未回報）。</summary>
    public ObservableCollection<string> DriveNotes { get; } = [];

    public List<M2FormFactor> FormFactors { get; } = BuildFormFactors();
    public List<M2KeySpec> KeySpecs { get; } = BuildKeySpecs();

    public M2AnalysisService()
    {
        // 與 CpuPinoutService／DimmReferenceService 同一套規矩：本服務在 MainViewModel 的欄位初始化式
        // 就 eager 建立（主視窗還沒顯示），而這裡要做兩件可能不回應的事——讀 SMBIOS 韌體表、
        // 連兩個 WMI 命名空間（root\Microsoft\Windows\Storage、root\WMI）。卡在任何一件上，
        // 主視窗就永遠不出現。所以收集丢到背景，補資料時再回 UI 執行緒
        // （Slots／PossibleSlots／DriveNotes 是 ObservableCollection，只能在 UI 執行緒增刪）。
        _ = Task.Run(() =>
        {
            var snap = Collect();
            Shell.BeginOnUi(() => Apply(snap));
        });
    }

    /// <summary>一次偵測的結果；收集與套用分開，收集才能在背景跑、套用留在 UI 執行緒。</summary>
    internal sealed record Snapshot(
        List<SmbiosSlotRow> Typed, List<SmbiosSlotRow> Inferred,
        List<string> Drives, int Nvme, string Summary);

    /// <summary>
    /// 收集插槽與磁碟外型。<paramref name="readDisks"/>／<paramref name="timeoutMs"/> 是測試注入點。
    /// </summary>
    internal Snapshot Collect(Func<List<PhysicalDiskInfo>>? readDisks = null, int? timeoutMs = null)
    {
        var typed = new List<SmbiosSlotRow>();
        var inferred = new List<SmbiosSlotRow>();
        var drives = new List<string>();

        try
        {
            foreach (var slot in new SmbiosService().Slots)
            {
                bool isTyped = M2Detection.IsTypedM2OrU2(slot.Type);
                if (isTyped) { typed.Add(slot); continue; }
                if (M2Detection.IsInferredM2(slot, isTyped)) inferred.Add(slot);
            }
        }
        catch { /* 讀不到 SMBIOS 不影響下面的磁碟清單 */ }

        int nvme = 0;
        // DiskInfoService.Query() 會連兩個 WMI 命名空間、逐類別列舉；列舉沒有可用的逾時，
        // 整機 WMI 卡住時它就永遠不回來（見 WmiGuard）。
        var (disks, timedOut) = WmiGuard.Probe(readDisks ?? DiskInfoService.Query,
            timeoutMs ?? WmiGuard.DefaultTimeoutMs);

        if (disks is not null)
        {
            foreach (var disk in disks)
            {
                if (disk.Kind == DiskKind.NvmeSsd) nvme++;
                drives.Add($"{disk.CountText} ・ {disk.Model} ・ {M2Detection.DescribeDriveForm(disk.Kind, disk.Model)}");
            }
        }
        else if (timedOut)
        {
            // 讀不到就說讀不到：留白會被看成「這台機器沒有磁碟」。
            drives.Add("—（WMI 無回應，略過磁碟外型清單）");
        }

        return new Snapshot(typed, inferred, drives, nvme, M2Detection.Summarize(typed, inferred, nvme));
    }

    void Apply(Snapshot snap)
    {
        foreach (var s in snap.Typed) Slots.Add(s);
        foreach (var s in snap.Inferred) PossibleSlots.Add(s);
        foreach (string d in snap.Drives) DriveNotes.Add(d);
        DetectedSlots = snap.Summary;
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
        new("Key B", "缺口在 pin 12–19 處（靠第 1 腳端）",
            "PCIe x2 + SATA + USB 3.0",
            "SATA SSD、WWAN 模組",
            [
                "最多 PCIe x2（Gen 3 約 2 GB/s）",
                "同時支援 SATA 與 PCIe 協定",
                "常見於早期 M.2 SATA SSD",
            ]),
        new("Key M", "缺口在 pin 59–66 處（靠末腳端）",
            "PCIe x4 + SATA",
            "NVMe SSD（主流高速）",
            [
                "PCIe x4（Gen 3 約 4 GB/s、Gen 4 約 8 GB/s、Gen 5 約 16 GB/s）",
                "NVMe SSD 幾乎都走這個 Key",
                "部分 Key M 插槽也接受 Key B+M 的 SATA SSD",
            ]),
        new("Key B+M", "pin 12–19 與 59–66 兩處皆有缺口",
            "PCIe x2 + SATA",
            "相容性最廣的 SATA / PCIe x2 SSD",
            [
                "可以插入 Key B 或 Key M 的插槽",
                "但只用得到 PCIe x2（即使插在 Key M x4 插槽）",
                "多數 M.2 SATA SSD 採此設計",
            ]),
        new("Key A", "缺口在 pin 8–15 處（靠第 1 腳端）",
            "PCIe x2 + USB 2.0 + I2C / DP",
            "Wi-Fi / 藍牙模組",
            [
                "Intel AX200/AX210 等無線網卡使用",
                "不用於儲存裝置",
            ]),
        new("Key E", "缺口在 pin 24–31 處（近中央）",
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
