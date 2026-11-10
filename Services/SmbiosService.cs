using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

/// <summary>單一 SMBIOS 結構：類型、控制代碼、格式區原始位元組與字串區。</summary>
public sealed class SmbiosStruct
{
    public SmbiosStruct(byte type, ushort handle, byte[] data, string[] strings)
    {
        Type = type; Handle = handle; Data = data; Strings = strings;
    }
    public byte Type { get; }
    public ushort Handle { get; }
    public byte[] Data { get; }
    public string[] Strings { get; }

    /// <summary>依 1 起始索引取字串；0 或越界＝無字串（回 null）。此為 SMBIOS 的字串慣例。</summary>
    public string? GetString(int index)
        => index > 0 && index <= Strings.Length ? Strings[index - 1] : null;

    public byte ByteAt(int offset) => Data[offset];
    public ushort WordAt(int offset) => (ushort)(Data[offset] | (Data[offset + 1] << 8));
    public uint DwordAt(int offset) => (uint)(Data[offset] | (Data[offset + 1] << 8) | (Data[offset + 2] << 16) | (Data[offset + 3] << 24));
    public int Length => Data.Length;
}

/// <summary>插槽一列（Type 9）。</summary>
public sealed class SmbiosSlotRow
{
    public SmbiosSlotRow(string designation, string type, string width, string usage)
    { Designation = designation; Type = type; Width = width; Usage = usage; }
    public string Designation { get; }
    public string Type { get; }
    public string Width { get; }
    public string Usage { get; }
}

/// <summary>一條記憶體裝置（Type 17）的解讀列。</summary>
public sealed class SmbiosDimmRow
{
    public SmbiosDimmRow(string locator, string bank, string size, string type, string speed, string configured, string manufacturer, string serial, string part, string rank,
        int dataWidth = 0, int totalWidth = 0, ushort registered = 0)
    { Locator = locator; Bank = bank; Size = size; Type = type; Speed = speed; Configured = configured; Manufacturer = manufacturer; Serial = serial; Part = part; Rank = rank;
      DataWidth = dataWidth; TotalWidth = totalWidth; Registered = registered; }
    public string Locator { get; }
    public string Bank { get; }
    public string Size { get; }
    public string Type { get; }
    public string Speed { get; }
    public string Configured { get; }
    public string Manufacturer { get; }
    public string Serial { get; }
    public string Part { get; }
    public string Rank { get; }
    /// <summary>資料寬度（Type 17 位移 0x0A，位元）；0＝讀不到。</summary>
    public int DataWidth { get; }
    /// <summary>總寬度（位移 0x08，位元）；0＝讀不到。72＝含 8 位元 ECC。</summary>
    public int TotalWidth { get; }
    /// <summary>Type Detail（位移 0x13 WORD）；bits 13／14／15 為 Registered／Unbuffered／LRDIMM。</summary>
    public ushort Registered { get; }
}

/// <summary>鍵值資訊列。</summary>
public sealed class SmbiosRow
{
    public SmbiosRow(string key, string value) { Key = key; Value = value; }
    public string Key { get; }
    public string Value { get; }
}

/// <summary>
/// SMBIOS 原始表全解：以 <c>GetSystemFirmwareTable('RSMB')</c> 取回整份表自行解析，
/// 拿到 WMI 沒轉譯的欄位——記憶體條的插槽位置／序號／型號／設定速度 vs 標稱速度／Rank、
/// 每個系統插槽（Type 9）的使用狀態、BIOS 版本與日期等。
/// </summary>
/// <remarks>
/// 誠實界線：欄位位移逐欄核對過 dmidecode 3.x 原始碼；沒填的欄位顯示「—」，
/// 不認得的列舉值顯示原始位元組（如「0x21」）而不硬掰；結構長度不足的欄位直接略過。
/// </remarks>
public sealed class SmbiosService
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint bufferSize);

    public bool Available { get; }
    public string VersionText { get; private set; } = "—";
    internal bool UuidLittleEndian { get; private set; } = true;

    public ObservableCollection<SmbiosRow> Bios { get; } = [];
    public ObservableCollection<SmbiosRow> System { get; } = [];
    public ObservableCollection<SmbiosRow> Board { get; } = [];
    public ObservableCollection<SmbiosRow> Processor { get; } = [];
    public ObservableCollection<SmbiosRow> MemoryArray { get; } = [];
    /// <summary>
    /// 平台層錯誤更正類型（Type 16 位移 0x06）；0＝未讀到。
    /// 與單支模組的 ECC 位元是兩件事，判讀時兩者分開陳述（見 <see cref="DimmEccJudge"/>）。
    /// </summary>
    public byte EcType { get; private set; }

    /// <summary>機箱（Type 3）——類型代碼直接回答「這是不是伺服器／機架式」。</summary>
    public ObservableCollection<SmbiosRow> Chassis { get; } = [];
    /// <summary>溫度探針（Type 28）——韌體自己回報的溫度感測器，與 OS 感測器是兩套口徑。</summary>
    public ObservableCollection<SmbiosRow> TemperatureProbes { get; } = [];
    /// <summary>冷卻裝置（Type 27）——韌體回報的型態、狀態與標稱轉速。</summary>
    public ObservableCollection<SmbiosRow> CoolingDevices { get; } = [];
    public ObservableCollection<SmbiosSlotRow> Slots { get; } = [];
    public ObservableCollection<SmbiosDimmRow> MemoryDevices { get; } = [];

    /// <summary>解析後保留的原始結構，供驗機事實收集(<see cref="SmbiosFacts.From"/>)重用,不必再讀一次表。</summary>
    public IReadOnlyList<SmbiosStruct> Structs { get; private set; } = [];

    public SmbiosService() => Available = Load();

    internal SmbiosService(IReadOnlyList<SmbiosStruct> structs, bool uuidLittleEndian = true)
    {
        Structs = structs;
        UuidLittleEndian = uuidLittleEndian;
        DecodeStructures(structs, uuidLittleEndian);
        Available = true;
    }

    private bool Load()
    {
        try
        {
            uint size = GetSystemFirmwareTable(0x52534D42 /* 'RSMB' */, 0, null, 0);
            if (size < 8) return false;
            var buf = new byte[size];
            if (GetSystemFirmwareTable(0x52534D42, 0, buf, size) != size) return false;

            // RawSMBIOSData：使用方式／主版本／次版本／修訂（各 1B）＋表長（4B LE）＋表本體
            VersionText = $"SMBIOS {buf[1]}.{buf[2]}";
            int tableLen = BitConverter.ToInt32(buf, 4);
            if (tableLen <= 0 || tableLen > buf.Length - 8) return false;
            var table = buf[8..(8 + tableLen)];
            var structs = SmbiosParser.Parse(table);
            Structs = structs;

            UuidLittleEndian = buf[1] > 2 || buf[1] == 2 && buf[2] >= 6;
            DecodeStructures(structs, UuidLittleEndian);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DecodeStructures(IReadOnlyList<SmbiosStruct> structs, bool uuidLittleEndian)
    {
        foreach (var s in structs)
        {
            switch (s.Type)
            {
                case 0: DecodeBios(s); break;
                case 1: DecodeSystem(s, uuidLittleEndian); break;
                case 2: DecodeBoard(s); break;
                case 3: DecodeChassis(s); break;
                case 4: DecodeProcessor(s); break;
                case 9: DecodeSlot(s); break;
                case 16: DecodeMemoryArray(s); break;
                case 17: DecodeMemoryDevice(s); break;
                case 27: DecodeCoolingDevice(s); break;
                case 28: DecodeTemperatureProbe(s); break;
            }
        }
    }

    private void DecodeBios(SmbiosStruct s)
    {
        if (s.Length < 0x09) return;
        Bios.Add(new SmbiosRow("平台韌體供應商", OrDash(s.GetString(s.ByteAt(0x04)))));
        Bios.Add(new SmbiosRow("平台韌體版本", OrDash(s.GetString(s.ByteAt(0x05)))));
        Bios.Add(new SmbiosRow("發行日期", OrDash(s.GetString(s.ByteAt(0x08)))));
    }

    private void DecodeSystem(SmbiosStruct s, bool uuidLittleEndian)
    {
        if (s.Length < 0x08) return;
        System.Add(new SmbiosRow("系統製造商", OrDash(s.GetString(s.ByteAt(0x04)))));
        System.Add(new SmbiosRow("系統型號", OrDash(s.GetString(s.ByteAt(0x05)))));
        System.Add(new SmbiosRow("系統版本", OrDash(s.GetString(s.ByteAt(0x06)))));
        System.Add(new SmbiosRow("系統序號", OrDash(s.GetString(s.ByteAt(0x07)))));
        // Type 1 的 SKU 與家族在格式區後段：0x19＝SKU（SMBIOS 2.4+）、0x1A＝Family（2.4+）。
        // 組裝機常填佔位字串（"System Serial Number"／"Default string"），如實顯示並標注。
        if (s.Length > 0x19)
        {
            string sku = OrDash(s.GetString(s.ByteAt(0x19)));
            System.Add(new SmbiosRow("SKU", sku + PlaceholderNote(sku)));
        }
        if (s.Length > 0x1A)
        {
            string family = OrDash(s.GetString(s.ByteAt(0x1A)));
            System.Add(new SmbiosRow("產品家族", family + PlaceholderNote(family)));
        }
        if (s.Length >= 0x18)
            System.Add(new SmbiosRow("UUID", BufferToUuid(s.Data.AsSpan(0x08, 16), uuidLittleEndian)));
    }

    /// <summary>
    /// 韌體填的佔位字串辨識：「Default string」「System Serial Number」「To be filled by O.E.M.」這類
    /// 不是真的值，只是欄位沒填時的預設文字。<b>如實顯示原字串但加上標注</b>——
    /// 直接照登會讓使用者以為那串字是序號，靜默改掉又會隱藏「韌體沒填」這個事實。
    /// </summary>
    internal static string PlaceholderNote(string value) =>
        IsPlaceholder(value) ? "（韌體未填，這是預設字串不是實際值）" : "";

    /// <summary>是否是韌體未填時的預設字串。</summary>
    internal static bool IsPlaceholder(string value)
    {
        string v = value.Trim();
        return v.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            || v.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase)
            || v.Equals("System Serial Number", StringComparison.OrdinalIgnoreCase)
            || v.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)
            || v.Equals("System Version", StringComparison.OrdinalIgnoreCase)
            || v.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase)
            || v.Equals("Not Specified", StringComparison.OrdinalIgnoreCase)
            || v.Equals("None", StringComparison.OrdinalIgnoreCase)
            || v.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || v.Length == 0;
    }

    /// <summary>
    /// Type 1 的完整 UUID；SMBIOS 2.6 起前三欄為小端序，較舊版本保留網路序。
    /// 全 0 表示不存在且不可設定，全 F 表示目前不存在。
    /// </summary>
    [SpecRef("SMBIOS DSP0134 7.2.1, Type 1 offset 0x08–0x17：完整 128 位元 UUID；2.6 起前三欄 little-endian。全 00h＝不存在且不可設定，全 FFh＝目前不存在。")]
    internal static string BufferToUuid(ReadOnlySpan<byte> raw, bool littleEndian = true)
    {
        if (raw.Length != 16) return "—（UUID 長度不足）";
        if (raw.IndexOfAnyExcept((byte)0) < 0) return "—（全 0：UUID 不存在且不可設定）";
        if (raw.IndexOfAnyExcept((byte)0xFF) < 0) return "—（全 F：UUID 目前不存在）";
        return new Guid(raw, bigEndian: !littleEndian).ToString("D").ToUpperInvariant();
    }

    private void DecodeBoard(SmbiosStruct s)
    {
        if (s.Length < 0x08) return;
        Board.Add(new SmbiosRow("主機板製造商", OrDash(s.GetString(s.ByteAt(0x04)))));
        Board.Add(new SmbiosRow("主機板型號", OrDash(s.GetString(s.ByteAt(0x05)))));
        Board.Add(new SmbiosRow("主機板版本", OrDash(s.GetString(s.ByteAt(0x06))))); 
    }

    private void DecodeChassis(SmbiosStruct s)
    {
        if (s.Length < 0x08) return;
        string maker = OrDash(s.GetString(s.ByteAt(0x04)));
        Board.Add(new SmbiosRow("機箱製造商", maker));
        Chassis.Add(new SmbiosRow("機箱製造商", maker + PlaceholderNote(maker)));

        // 低 7 位是單一類型代碼，bit 7 另表示機箱鎖存在。
        byte types = s.ByteAt(0x05);
        bool lockPresent = (types & 0x80) != 0;
        byte first = (byte)(types & 0x7F);
        Chassis.Add(new SmbiosRow("機箱類型", ChassisTypeName(first)));

        string serial = OrDash(s.GetString(s.ByteAt(0x07)));
        Chassis.Add(new SmbiosRow("機箱序號", serial + PlaceholderNote(serial)));
        if (s.Length > 0x08)
        {
            string asset = OrDash(s.GetString(s.ByteAt(0x08)));
            Chassis.Add(new SmbiosRow("資產標籤", asset + PlaceholderNote(asset)));
        }
        Chassis.Add(new SmbiosRow("機箱鎖", lockPresent ? "存在" : "不存在或未回報"));
        if (s.Length > 0x09)
            Chassis.Add(new SmbiosRow("開機狀態", ChassisStateName(s.ByteAt(0x09))));
    }

    /// <summary>
    /// 機箱類型（SMBIOS Type 3 位移 0x05 的低 7 位）。
    /// <b>這一欄是判斷「伺服器 vs 工作站」最直接的證據</b>：機架式／刀鋒／塔式是韌體自己宣告的，
    /// 不需要從機殼外觀或型號去猜。未收錄的代碼如實帶出。
    /// </summary>
    [SpecRef("SMBIOS DSP0134 7.4.1, Type 3 offset 0x05：bit 7 為 Chassis Lock Present，低 7 位為 Chassis Type 列舉代碼（01h–24h），不是位元遮罩。")]
    public static string ChassisTypeName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "桌上型（Desktop）", 0x04 => "低腳位桌上型",
        0x05 => "披薩盒", 0x06 => "迷你塔式", 0x07 => "塔式", 0x08 => "可攜式",
        0x09 => "筆記型", 0x0A => "筆記型", 0x0B => "手持式", 0x0C => "連線站",
        0x0D => "一體式（All-in-One）", 0x0E => "子筆記型", 0x0F => "節省空間型",
        0x10 => "午餐盒式", 0x11 => "主伺服器機箱", 0x12 => "擴充機箱", 0x13 => "子機箱",
        0x14 => "匯流排擴充機箱", 0x15 => "周邊裝置機箱", 0x16 => "RAID 機箱",
        0x17 => "機架式機箱（Rack Mount）", 0x18 => "密封式 PC", 0x19 => "多系統機箱",
        0x1A => "CompactPCI", 0x1B => "AdvancedTCA", 0x1C => "刀鋒機箱", 0x1D => "刀鋒容納機箱",
        0x1E => "平板", 0x1F => "可轉換式", 0x20 => "可拆卸式", 0x21 => "物聯網閘道",
        0x22 => "嵌入式 PC", 0x23 => "迷你 PC", 0x24 => "棒狀 PC",
        _ => $"0x{code:X2}（規格未收錄）",
    };

    /// <summary>機箱開機狀態（Type 3 位移 0x09）。</summary>
    [SpecRef("SMBIOS Specification, System Enclosure or Chassis (Type 3), offset 0x09：Boot-up State，代碼表見規格 7.4.2。未收錄如實帶原代碼。")]
    public static string ChassisStateName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "安全", 0x04 => "警告", 0x05 => "重大",
        0x06 => "不可回復", _ => $"0x{code:X2}",
    };

    /// <summary>
    /// 溫度探針（Type 28）。這一組是<b>韌體自己回報</b>的感測器，與 OS／驅動的感測器是兩套口徑——
    /// 同一台機器上兩邊常常對不起來，如實並列對照，不互相取代。
    /// </summary>
    private void DecodeTemperatureProbe(SmbiosStruct s)
    {
        if (s.Length < 0x14) return;
        string desc = OrDash(s.GetString(s.ByteAt(0x04)));
        string nominal = s.Length >= 0x16 ? TemperatureText(s.WordAt(0x14)) : "—（未回報）";
        string status = TemperatureStatusName((byte)(s.ByteAt(0x05) >> 5));
        TemperatureProbes.Add(new SmbiosRow(desc,
            $"標稱 {nominal}，最小 {TemperatureText(s.WordAt(0x08))}，最大 {TemperatureText(s.WordAt(0x06))}（{status}；韌體規格，非即時溫度）"));
    }

    /// <summary>
    /// Type 28 溫度為有號 16 位元、0.1 °C；只有 0x8000 是未知值。
    /// </summary>
    [SpecRef("SMBIOS DSP0134 7.29, Type 28：Maximum@0x06、Minimum@0x08、Nominal@0x14，signed WORD，單位 0.1 °C；8000h 表示未知，其餘負值有效。")]
    internal static string TemperatureText(ushort raw)
    {
        if (raw == 0x8000) return "—（值未知，感測器未回報）";
        return $"{unchecked((short)raw) / 10.0:0.0} °C";
    }

    /// <summary>Type 28 位移 0x05 bits 7:5 的狀態代碼。</summary>
    [SpecRef("SMBIOS DSP0134 7.27.1 / 7.29, Type 28 offset 0x05 bits 7:5：01h Other、02h Unknown、03h OK、04h Non-critical、05h Critical、06h Non-recoverable。未宣告過高或過低方向。")]
    public static string TemperatureStatusName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "正常", 0x04 => "非重大",
        0x05 => "重大", 0x06 => "不可回復",
        _ => $"狀態 0x{code:X2}",
    };

    /// <summary>冷卻裝置（Type 27）；標稱轉速不代表目前轉速。</summary>
    private void DecodeCoolingDevice(SmbiosStruct s)
    {
        if (s.Length < 0x0C) return;
        string desc = s.Length >= 0x0F ? OrDash(s.GetString(s.ByteAt(0x0E))) : "冷卻裝置";
        byte typeAndStatus = s.ByteAt(0x06);
        string kind = CoolingTypeName((byte)(typeAndStatus & 0x1F));
        string status = TemperatureStatusName((byte)(typeAndStatus >> 5));
        string speed = s.Length >= 0x0E ? CoolingValueText(s.WordAt(0x0C)) : "—（未回報）";
        CoolingDevices.Add(new SmbiosRow(desc,
            $"{kind}，{status}，標稱轉速 {speed}（非即時轉速）"));
    }

    /// <summary>Type 27 位移 0x06 bits 4:0 的裝置類型。</summary>
    [SpecRef("SMBIOS DSP0134 7.28.1, Type 27 offset 0x06：bits 4:0 為類型，08h Heat Pipe、09h Integrated Refrigeration、10h Active Cooling、11h Passive Cooling；bits 7:5 為狀態。")]
    public static string CoolingTypeName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "風扇", 0x04 => "離心鼓風機",
        0x05 => "晶片風扇", 0x06 => "機箱風扇", 0x07 => "電源供應器風扇",
        0x08 => "熱導管", 0x09 => "整合式製冷", 0x10 => "主動散熱", 0x11 => "被動散熱",
        _ => $"0x{code:X2}",
    };

    /// <summary>
    /// Type 27 的標稱轉速（RPM）；0x8000 表示未知或非旋轉裝置。
    /// </summary>
    [SpecRef("SMBIOS DSP0134 7.28, Type 27 offset 0x0C：Nominal Speed，unsigned WORD，單位 RPM。只有 8000h 表示未知或非旋轉裝置。")]
    internal static string CoolingValueText(ushort raw)
    {
        if (raw == 0x8000) return "—（值未知或非旋轉裝置）";
        return $"{raw} RPM";
    }

    private void DecodeProcessor(SmbiosStruct s)
    {
        if (s.Length < 0x1A) return;
        Processor.Add(new SmbiosRow("插槽", OrDash(s.GetString(s.ByteAt(0x04)))));
        Processor.Add(new SmbiosRow("製造商", OrDash(s.GetString(s.ByteAt(0x07)))));
        Processor.Add(new SmbiosRow("版本", OrDash(s.GetString(s.ByteAt(0x10)))));
        uint extClk = s.WordAt(0x12), max = s.WordAt(0x14), cur = s.WordAt(0x16);
        if (max > 0) Processor.Add(new SmbiosRow("標稱最大時脈", $"{max} MHz"));
        if (cur > 0) Processor.Add(new SmbiosRow("目前時脈", $"{cur} MHz"));
        else if (extClk > 0) Processor.Add(new SmbiosRow("外部時脈", $"{extClk} MHz"));
        if (s.Length < 0x28) return;
        if (ProcessorCount(s, 0x23, 0x2A) is { } core)
            Processor.Add(new SmbiosRow("核心數", core.ToString()));
        if (ProcessorCount(s, 0x24, 0x2C) is { } enabled)
            Processor.Add(new SmbiosRow("啟用核心數", enabled.ToString()));
        if (ProcessorCount(s, 0x25, 0x2E) is { } threads)
            Processor.Add(new SmbiosRow("執行緒數", threads.ToString()));
        if (s.Length >= 0x23)
        {
            var serial = OrDash(s.GetString(s.ByteAt(0x20)));
            var part = OrDash(s.GetString(s.ByteAt(0x22)));
            if (serial != "—") Processor.Add(new SmbiosRow("序號", serial));
            if (part != "—") Processor.Add(new SmbiosRow("型號", part));
        }
    }

    // Type 4 counts are bytes; FFh selects a separate extended WORD, never the adjacent byte.
    internal static int? ProcessorCount(SmbiosStruct s, int offset, int extendedOffset)
    {
        if (s.Length <= offset) return null;
        int count = s.ByteAt(offset);
        if (count == 0xFF)
        {
            if (s.Length < extendedOffset + 2) return null;
            count = s.WordAt(extendedOffset);
        }
        return count > 0 ? count : null;
    }

    private void DecodeSlot(SmbiosStruct s)
    {
        var row = DecodeSlotStruct(s);
        if (row is not null) Slots.Add(row);
    }

    private void DecodeMemoryArray(SmbiosStruct s)
    {
        if (s.Length < 0x0F) return;
        MemoryArray.Add(new SmbiosRow("位置", ArrayLocationName(s.ByteAt(0x04))));
        MemoryArray.Add(new SmbiosRow("用途", ArrayUseName(s.ByteAt(0x05))));
        // 平台層的錯誤更正能力。與「模組有沒有 ECC 位元」是<b>兩個獨立的欄位</b>——
        // 這一欄講的是整個陣列，模組那一欄在 Type 17 的總寬度（見 DimmEccJudge）。
        EcType = s.ByteAt(0x06);
        MemoryArray.Add(new SmbiosRow("錯誤修正", ArrayEcName(s.ByteAt(0x06))));
        uint cap = s.DwordAt(0x07);
        if (cap == 0x80000000 && s.Length >= 0x17)
        {
            // 3.1+：擴充容量為 64 位元位元組數
            long bytes = (long)s.DwordAt(0x0F) << 0;   // 低 32 位
            long bytesHi = s.DwordAt(0x13);
            long total = (bytesHi << 32) | (bytes & 0xFFFFFFFFL);
            MemoryArray.Add(new SmbiosRow("最大容量", $"{total / (1024.0 * 1024 * 1024):0.#} GB"));
        }
        else if (cap != 0)
        {
            MemoryArray.Add(new SmbiosRow("最大容量", $"{cap / (1024.0 * 1024):0.#} GB"));
        }
    }

    private void DecodeMemoryDevice(SmbiosStruct s)
    {
        var row = DecodeMemoryDeviceStruct(s);
        if (row is not null) MemoryDevices.Add(row);
    }

    /// <summary>Type 17 → 記憶體裝置列（純函式；結構長度不足 SMBIOS 2.7 格式時回 null）。位移核對 dmidecode 3.x。</summary>
    public static SmbiosDimmRow? DecodeMemoryDeviceStruct(SmbiosStruct s)
    {
        if (s.Length < 0x1C) return null;
        ushort size = s.WordAt(0x0C);
        string sizeText;
        if (size == 0) sizeText = "未安裝";
        else if (size == 0xFFFF) sizeText = "—";
        else if (size == 0x7FFF)
        {
            uint ext = s.Length >= 0x20 ? s.DwordAt(0x1C) & 0x7FFFFFFF : 0;
            sizeText = ext == 0 ? "—" : (ext & 0x3FF) != 0 ? $"{ext} MiB"
                     : (ext & 0xFFC00) != 0 ? $"{ext >> 10} GiB"
                     : $"{ext >> 20} TiB";
        }
        else if ((size & 0x8000) != 0)
        {
            // 規格（Type 17 偏移 0x0C）：位 15 為 1 時單位是 <b>kB</b>，不是 MB——
            // 誤當成 MB 會把 16 MB 的小模組寫成 16 GB。dmidecode 也是這樣解。
            int kb = size & 0x7FFF;
            sizeText = kb >= 1024 ? $"{kb / 1024.0:0.#} MB" : $"{kb} kB";
        }
        else
            sizeText = $"{size / 1024.0:0.#} GB";

        uint speed = EffectiveSpeed(s, 0x15, 0x54);
        uint configured = EffectiveSpeed(s, 0x20, 0x58);

        var rankByte = s.Length >= 0x1C ? s.ByteAt(0x1B) : (byte)0;
        string rank = (rankByte & 0x0F) == 0 ? "—" : $"{rankByte & 0x0F}";

        int totalWidth = s.WordAt(0x08);
        int dataWidth = s.WordAt(0x0A);
        ushort registered = s.WordAt(0x13);

        return new SmbiosDimmRow(
            OrDash(s.GetString(s.ByteAt(0x10))),
            OrDash(s.GetString(s.ByteAt(0x11))),
            sizeText,
            MemoryTypeName(s.ByteAt(0x12)),
            speed > 0 ? $"{speed} MT/s" : "—",
            configured > 0 ? $"{configured} MT/s" : "—",
            OrDash(s.GetString(s.ByteAt(0x17))),
            OrDash(s.GetString(s.ByteAt(0x18))),
            OrDash(s.GetString(s.ByteAt(0x1A))),
            rank, dataWidth, totalWidth, registered);
    }

    internal static uint EffectiveSpeed(SmbiosStruct s, int offset, int extendedOffset)
    {
        if (s.Length < offset + 2) return 0;
        ushort legacy = s.WordAt(offset);
        return legacy == 0xFFFF
            ? s.Length >= extendedOffset + 4 ? s.DwordAt(extendedOffset) : 0
            : legacy;
    }

    /// <summary>Type 9 → 插槽列（純函式；結構長度不足時回 null）。</summary>
    public static SmbiosSlotRow? DecodeSlotStruct(SmbiosStruct s)
    {
        if (s.Length < 0x0C) return null;
        return new SmbiosSlotRow(
            OrDash(s.GetString(s.ByteAt(0x04))),
            SlotTypeName(s.ByteAt(0x05)),
            SlotWidthName(s.ByteAt(0x06)),
            SlotUsageName(s.ByteAt(0x07)));
    }

    private static string OrDash(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();

    // ── 列舉解碼（核對 dmidecode 3.x；不認得的值顯示原始位元組）──────────────

    public static string SlotTypeName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "ISA", 0x04 => "MCA", 0x05 => "EISA",
        0x06 => "PCI", 0x07 => "PC Card (PCMCIA)", 0x08 => "VLB", 0x09 => "專屬",
        0x0A => "處理器卡", 0x0B => "專屬記憶體卡", 0x0C => "I/O Riser 卡", 0x0D => "NuBus",
        0x0E => "PCI-66", 0x0F => "AGP", 0x10 => "AGP 2x", 0x11 => "AGP 4x", 0x12 => "PCI-X",
        0x13 => "AGP 8x", 0x14 => "M.2 Socket 1-DP", 0x15 => "M.2 Socket 1-SD", 0x16 => "M.2 Socket 2",
        0x17 => "M.2 Socket 3", 0x18 => "MXM Type I", 0x19 => "MXM Type II", 0x1A => "MXM Type III",
        0x1B => "MXM Type III-HE", 0x1C => "MXM Type IV", 0x1D => "MXM 3.0 Type A", 0x1E => "MXM 3.0 Type B",
        0x1F => "PCIe 2 SFF-8639 (U.2)", 0x20 => "PCIe 3 SFF-8639 (U.2)",
        0x21 => "PCIe Mini 52-pin（含底部避讓）", 0x22 => "PCIe Mini 52-pin（無底部避讓）",
        0x23 => "PCIe Mini 76-pin", 0x24 => "PCIe 4 SFF-8639 (U.2)", 0x25 => "PCIe 5 SFF-8639 (U.2)",
        0x26 => "OCP NIC 3.0 SFF", 0x27 => "OCP NIC 3.0 LFF", 0x28 => "OCP NIC（3.0 前）",
        0x30 => "CXL Flexbus 1.0",
        0xA5 => "PCI Express", 0xA6 => "PCI Express x1", 0xA7 => "PCI Express x2",
        0xA8 => "PCI Express x4", 0xA9 => "PCI Express x8", 0xAA => "PCI Express x16",
        0xAB => "PCI Express 2", 0xAC => "PCI Express 2 x1", 0xAD => "PCI Express 2 x2",
        0xAE => "PCI Express 2 x4", 0xAF => "PCI Express 2 x8", 0xB0 => "PCI Express 2 x16",
        0xB1 => "PCI Express 3", 0xB2 => "PCI Express 3 x1", 0xB3 => "PCI Express 3 x2",
        0xB4 => "PCI Express 3 x4", 0xB5 => "PCI Express 3 x8", 0xB6 => "PCI Express 3 x16",
        0xB8 => "PCI Express 4", 0xB9 => "PCI Express 4 x1", 0xBA => "PCI Express 4 x2",
        0xBB => "PCI Express 4 x4", 0xBC => "PCI Express 4 x8", 0xBD => "PCI Express 4 x16",
        0xBE => "PCI Express 5", 0xBF => "PCI Express 5 x1", 0xC0 => "PCI Express 5 x2",
        0xC1 => "PCI Express 5 x4", 0xC2 => "PCI Express 5 x8", 0xC3 => "PCI Express 5 x16",
        0xC4 => "PCI Express 6+", 0xC5 => "EDSFF E1", 0xC6 => "EDSFF E3",
        _ => $"0x{code:X2}",
    };

    public static string SlotWidthName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "8 bit", 0x04 => "16 bit", 0x05 => "32 bit",
        0x06 => "64 bit", 0x07 => "128 bit", 0x08 => "x1", 0x09 => "x2", 0x0A => "x4",
        0x0B => "x8", 0x0C => "x12", 0x0D => "x16", 0x0E => "x32",
        _ => $"0x{code:X2}",
    };

    public static string SlotUsageName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "可用", 0x04 => "使用中", 0x05 => "不可用",
        _ => $"0x{code:X2}",
    };

    public static string MemoryTypeName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "DRAM", 0x04 => "EDRAM", 0x05 => "VRAM",
        0x06 => "SRAM", 0x07 => "RAM", 0x08 => "ROM", 0x09 => "Flash", 0x0A => "EEPROM",
        0x0B => "FEPROM", 0x0C => "EPROM", 0x0D => "CDRAM", 0x0E => "3DRAM", 0x0F => "SDRAM",
        0x10 => "SGRAM", 0x11 => "RDRAM", 0x12 => "DDR", 0x13 => "DDR2", 0x14 => "DDR2 FB-DIMM",
        0x18 => "DDR3", 0x19 => "FB-DIMM 2", 0x1A => "DDR4", 0x1B => "LPDDR", 0x1C => "LPDDR2",
        0x1D => "LPDDR3", 0x1E => "LPDDR4", 0x1F => "非揮發性裝置", 0x20 => "HBM", 0x21 => "HBM2",
        0x22 => "DDR5", 0x23 => "LPDDR5",
        _ => $"0x{code:X2}",
    };

    public static string FormFactorName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x08 => "DIMM", 0x0D => "SODIMM", 0x0F => "FB-DIMM",
        0x10 => "Die", 0x11 => "CAMM", 0x12 => "CUDIMM", 0x13 => "CSODIMM",
        _ => $"0x{code:X2}",
    };

    public static string ArrayLocationName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "主機板", 0x04 => "ISA 介面卡", 0x05 => "EISA 介面卡",
        0x06 => "PCI 介面卡", 0x07 => "MCA 介面卡", 0x08 => "PCMCIA", 0x09 => "專屬", 0x0A => "NuBus",
        _ => $"0x{code:X2}",
    };

    public static string ArrayUseName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "系統記憶體", 0x04 => "顯示記憶體", 0x05 => "快閃記憶體",
        0x06 => "非揮發性 RAM", 0x07 => "可快取記憶體",
        _ => $"0x{code:X2}",
    };

    public static string ArrayEcName(byte code) => code switch
    {
        0x01 => "其他", 0x02 => "未知", 0x03 => "無", 0x04 => "同位", 0x05 => "單位元 ECC",
        0x06 => "多位元 ECC", 0x07 => "CRC",
        _ => $"0x{code:X2}",
    };
}

/// <summary>SMBIOS 表解析器（純函式，可用合成位元組測試）。</summary>
public static class SmbiosParser
{
    /// <summary>解析 SMBIOS 表本體：連續的「4 位元組標頭＋格式區＋以雙 NULL 結尾的字串區」。</summary>
    public static List<SmbiosStruct> Parse(byte[] table)
    {
        var list = new List<SmbiosStruct>();
        int off = 0;
        while (off + 4 <= table.Length)
        {
            byte type = table[off];
            byte len = table[off + 1];
            if (type == 127) break;                 // End-of-Table
            if (len < 4 || off + len > table.Length) break;

            ushort handle = (ushort)(table[off + 2] | (table[off + 3] << 8));
            var data = new byte[len];
            Array.Copy(table, off, data, 0, len);

            int p = off + len;
            var strings = new List<string>();
            while (p < table.Length)
            {
                int start = p;
                while (p < table.Length && table[p] != 0) p++;
                int strLen = p - start;
                p++;                                             // 吃掉字串結尾的 0
                bool end = p >= table.Length || table[p] == 0;   // 下一個 0＝字串區結束
                if (end) p++;
                if (strLen > 0) strings.Add(Encoding.Latin1.GetString(table, start, strLen));
                if (end) break;                                  // 終止的空字串不列入
            }
            off = p;
            list.Add(new SmbiosStruct(type, handle, data, strings.ToArray()));
        }
        return list;
    }
}
