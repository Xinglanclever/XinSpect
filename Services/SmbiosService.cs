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
    public SmbiosDimmRow(string locator, string bank, string size, string type, string speed, string configured, string manufacturer, string serial, string part, string rank)
    { Locator = locator; Bank = bank; Size = size; Type = type; Speed = speed; Configured = configured; Manufacturer = manufacturer; Serial = serial; Part = part; Rank = rank; }
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

    public ObservableCollection<SmbiosRow> Bios { get; } = [];
    public ObservableCollection<SmbiosRow> System { get; } = [];
    public ObservableCollection<SmbiosRow> Board { get; } = [];
    public ObservableCollection<SmbiosRow> Processor { get; } = [];
    public ObservableCollection<SmbiosRow> MemoryArray { get; } = [];
    public ObservableCollection<SmbiosSlotRow> Slots { get; } = [];
    public ObservableCollection<SmbiosDimmRow> MemoryDevices { get; } = [];

    public SmbiosService() => Available = Load();

    private bool Load()
    {
        try
        {
            uint size = GetSystemFirmwareTable(0x52534D42 /* 'RSMB' */, 0, null, 0);
            if (size == 0) return false;
            var buf = new byte[size];
            if (GetSystemFirmwareTable(0x52534D42, 0, buf, size) != size) return false;

            // RawSMBIOSData：使用方式／主版本／次版本／修訂（各 1B）＋表長（4B LE）＋表本體
            VersionText = $"SMBIOS {buf[1]}.{buf[2]}";
            int tableLen = BitConverter.ToInt32(buf, 4);
            if (tableLen <= 0 || 8 + tableLen > buf.Length) return false;
            var table = buf[8..(8 + tableLen)];
            var structs = SmbiosParser.Parse(table);

            foreach (var s in structs)
            {
                switch (s.Type)
                {
                    case 0: DecodeBios(s); break;
                    case 1: DecodeSystem(s); break;
                    case 2: DecodeBoard(s); break;
                    case 3: DecodeChassis(s); break;
                    case 4: DecodeProcessor(s); break;
                    case 9: DecodeSlot(s); break;
                    case 16: DecodeMemoryArray(s); break;
                    case 17: DecodeMemoryDevice(s); break;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DecodeBios(SmbiosStruct s)
    {
        if (s.Length < 0x09) return;
        Bios.Add(new SmbiosRow("平台韌體供應商", OrDash(s.GetString(s.ByteAt(0x04)))));
        Bios.Add(new SmbiosRow("平台韌體版本", OrDash(s.GetString(s.ByteAt(0x05)))));
        Bios.Add(new SmbiosRow("發行日期", OrDash(s.GetString(s.ByteAt(0x08)))));
    }

    private void DecodeSystem(SmbiosStruct s)
    {
        if (s.Length < 0x08) return;
        System.Add(new SmbiosRow("系統製造商", OrDash(s.GetString(s.ByteAt(0x04)))));
        System.Add(new SmbiosRow("系統型號", OrDash(s.GetString(s.ByteAt(0x05)))));
        System.Add(new SmbiosRow("系統版本", OrDash(s.GetString(s.ByteAt(0x06)))));
        System.Add(new SmbiosRow("系統序號", OrDash(s.GetString(s.ByteAt(0x07)))));
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
        if (s.Length < 0x07) return;
        Board.Add(new SmbiosRow("機箱製造商", OrDash(s.GetString(s.ByteAt(0x04))))); 
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
        byte core = s.ByteAt(0x23), enabled = s.ByteAt(0x24), threads = s.ByteAt(0x25);
        if (core != 0)
            Processor.Add(new SmbiosRow("核心數", s.Length >= 0x2C && core == 0xFF ? s.WordAt(0x2A).ToString() : core.ToString()));
        if (enabled != 0)
            Processor.Add(new SmbiosRow("啟用核心數", s.Length >= 0x2E && enabled == 0xFF ? s.WordAt(0x2C).ToString() : enabled.ToString()));
        if (threads != 0)
            Processor.Add(new SmbiosRow("執行緒數", s.Length >= 0x30 && threads == 0xFF ? s.WordAt(0x2E).ToString() : threads.ToString()));
        if (s.Length >= 0x23)
        {
            var serial = OrDash(s.GetString(s.ByteAt(0x20)));
            var part = OrDash(s.GetString(s.ByteAt(0x22)));
            if (serial != "—") Processor.Add(new SmbiosRow("序號", serial));
            if (part != "—") Processor.Add(new SmbiosRow("型號", part));
        }
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
            MemoryArray.Add(new SmbiosRow("最大容量", $"{cap / 1024.0:0.#} GB"));
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
        else if (size == 0x7FFF && s.Length >= 0x20)
        {
            uint ext = s.DwordAt(0x1C) & 0x7FFFFFFF;
            sizeText = (ext & 0x3FF) != 0 ? $"{ext} MiB"
                     : (ext & 0xFFC00) != 0 ? $"{ext >> 10} GiB"
                     : $"{ext >> 20} TiB";
        }
        else
            sizeText = (size & 0x8000) != 0 ? $"{size & 0x7FFF} MB" : $"{size / 1024.0:0.#} GB";

        ushort speed = s.WordAt(0x15);
        ushort configured = s.Length >= 0x22 ? s.WordAt(0x20) : (ushort)0;

        var rankByte = s.Length >= 0x1C ? s.ByteAt(0x1B) : (byte)0;
        string rank = (rankByte & 0x0F) == 0 ? "—" : $"{rankByte & 0x0F}";

        return new SmbiosDimmRow(
            OrDash(s.GetString(s.ByteAt(0x10))),
            OrDash(s.GetString(s.ByteAt(0x11))),
            sizeText,
            MemoryTypeName(s.ByteAt(0x12)),
            speed > 0 && speed != 0xFFFF ? $"{speed} MT/s" : "—",
            configured > 0 && configured != 0xFFFF ? $"{configured} MT/s" : "—",
            OrDash(s.GetString(s.ByteAt(0x17))),
            OrDash(s.GetString(s.ByteAt(0x18))),
            OrDash(s.GetString(s.ByteAt(0x1A))),
            rank);
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
