namespace XinSpect;

/// <summary>一個 WMI 類別的欄位盤點：本專案實際查詢的欄位、以及已知存在但全庫未用的欄位。</summary>
/// <param name="FactSuffix">家族鍵尾段（<c>os.wmi.fields.{FactSuffix}</c>）。</param>
/// <param name="SelectedFields">本專案全部 SELECT 字面出現過的欄位（由守門測試對照原始碼釘住）。</param>
/// <param name="KnownUnusedFields">已知存在且全庫從未被 SELECT 或讀取的重點欄位（curated 重點清單，非完整 schema）。</param>
public sealed record WmiClassFieldInventory(
    string WmiClassName,
    string FactSuffix,
    IReadOnlyList<string> SelectedFields,
    IReadOnlyList<string> KnownUnusedFields,
    string FieldSourceNote);

/// <summary>
/// U10-OS-0025／0033／0038 三把合併（候選鍵 <c>wmi.cpu.gap</c>／<c>wmi.disk.gap</c>／<c>wmi.gpu.gap</c>）：
/// 判定文檔指出這三把性質是「盤點／成文工具，建議合併成一個項」，故合成
/// <b>「WMI 未用欄位盤點」</b>——盤點本專案<b>已查詢</b>的 WMI 類別裡、
/// 已知存在但從未被查詢或讀取的欄位。
///
/// <para>
/// <b>形狀：服務＋資料表＋守門測試</b>。資料表在本檔（成文）；守門在
/// <c>Tests/WmiFieldInventoryTests.cs</c>——它掃描 <c>Services/</c> 的 WMI 查詢字面，
/// 把 SELECT 過的欄位與 <see cref="Inventory"/> 釘住（查詢面改了而表沒改就紅燈），
/// 並驗證「未用」欄位確實全庫零查詢、零讀取。事實輸出：彙總一把
/// <see cref="OsWmiFieldsKey"/>，逐類一筆走 <see cref="OsWmiFieldsPrefix"/> 家族
/// （成員隨盤點表增減，故用前綴常數而非固定子鍵）。
/// </para>
///
/// <para>
/// 出處：各欄位名出自微軟 Win32 類別文件（learn.microsoft.com/Windows/win32/wmisdk，
/// 2026-10-25 對照）；「未用」以全庫字面檢索為證（守門測試常設化）。
/// </para>
/// </summary>
public static class WmiFieldInventoryService
{
    /// <summary>彙總鍵。</summary>
    public const string OsWmiFieldsKey = "os.wmi.fields";

    /// <summary>逐類家族前綴（刻意不含 Key——這是家族，成員隨盤點表增減）。</summary>
    public const string OsWmiFieldsPrefix = "os.wmi.fields.";

    private const string Category = "系統與軟體";

    public static readonly IReadOnlyList<WmiClassFieldInventory> Inventory =
    [
        new("Win32_Processor", "processor",
        [
            "Name", "Manufacturer", "NumberOfCores", "NumberOfLogicalProcessors", "NumberOfEnabledCore",
            "MaxClockSpeed", "ExtClock", "CurrentVoltage", "AddressWidth", "DataWidth",
            "SocketDesignation", "L2CacheSize", "L3CacheSize", "ProcessorId", "Description",
            "Version", "Revision", "VirtualizationFirmwareEnabled", "SecondLevelAddressTranslationExtensions",
        ],
        [
            "LoadPercentage", "CpuStatus", "ProcessorType", "VoltageCaps", "SystemName",
            "Role", "Architecture", "Stepping", "Availability",
        ],
        "微軟 Win32_Processor 文件；U10-OS-0025——已查詢面很廣，但負載／狀態碼／電壓上限等欄位從未取用"),

        new("Win32_DiskDrive", "diskdrive",
        [
            "Index", "Model", "Size", "InterfaceType", "PNPDeviceID", "SerialNumber",
            "FirmwareRevision", "Partitions", "BytesPerSector", "MediaType",
        ],
        [
            "SectorsPerTrack", "TotalCylinders", "TotalHeads", "TotalSectors", "TotalTracks",
            "TracksPerCylinder", "CapabilityDescriptions", "MaxBlockSize", "MinBlockSize",
            "ErrorCleared", "ErrorDescription", "LastErrorCode",
        ],
        "微軟 Win32_DiskDrive 文件；U10-OS-0033——分區／幾何（磁柱、磁頭、磁區）與錯誤狀態碼欄未取用（PartitionStyle 僅作 WHERE 過濾，未取值）"),

        new("Win32_VideoController", "videocontroller",
        [
            "Name", "PNPDeviceID", "AdapterCompatibility", "VideoProcessor", "VideoBIOSVersion",
            "DriverVersion", "CurrentBitsPerPixel", "AdapterRAM",
        ],
        [
            "VideoMemoryType", "CurrentScanMode", "VideoArchitecture", "VideoModeDescription",
            "MinRefreshRate", "MaxRefreshRate", "CurrentHorizontalResolution", "CurrentVerticalResolution",
            "NumberOfVideoPages", "AdapterDACType",
        ],
        "微軟 Win32_VideoController 文件；U10-OS-0038——VRAM 類型（VideoMemoryType）與掃描模式（CurrentScanMode）欄未取用"),
    ];

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at)
    {
        var facts = new List<HardwareFact> { Summary(at) };
        foreach (var inv in Inventory)
            facts.Add(ClassFact(at, inv));
        return facts;
    }

    private static HardwareFact Summary(DateTimeOffset at)
    {
        int classes = Inventory.Count;
        int unused = Inventory.Sum(x => x.KnownUnusedFields.Count);
        return new HardwareFact(OsWmiFieldsKey, Category, "WMI 未用欄位盤點",
            $"已查詢的 WMI 類別 {classes} 個，已知存在但全庫未用的欄位共 {unused} 個（逐類見 os.wmi.fields.* 家族）。" +
            "這是盤點／成文工具：未用不代表該用，只讓「沒拿什麼」有處可查。", "個",
            "本專案 WMI 查詢字面盤點（守門測試常設對照原始碼）", FactTrustLevel.Derived, false, at, unused);
    }

    private static HardwareFact ClassFact(DateTimeOffset at, WmiClassFieldInventory inv)
    {
        string unusedList = string.Join("、", inv.KnownUnusedFields);
        return new HardwareFact(OsWmiFieldsPrefix + inv.FactSuffix, Category,
            $"{inv.WmiClassName} 欄位盤點",
            $"本專案查詢欄位 {inv.SelectedFields.Count} 個；已知存在但全庫未用的欄位 {inv.KnownUnusedFields.Count} 個：{unusedList}。" +
            inv.FieldSourceNote, "個",
            "本專案 WMI 查詢字面盤點（守門測試常設對照原始碼）", FactTrustLevel.Derived, false, at,
            inv.KnownUnusedFields.Count);
    }
}
