using System;
using System.Collections.Generic;
using System.Text;

namespace XinSpect;

/// <summary>PE 區段：名稱、虛擬大小／位址、檔案內原始大小與位移（RVA→檔案位移對映的單一真相）。</summary>
public sealed record PeSectionInfo(
    string Name, uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawPointer)
{
    /// <summary>RVA 是否落在本區段的檔案映像內（VirtualSize 與 RawSize 取大者為界）。</summary>
    public bool ContainsRva(uint rva) =>
        rva >= VirtualAddress && rva < VirtualAddress + Math.Max(VirtualSize, RawSize);
}

/// <summary>一段符合 CTL_CODE 形狀的 4 位元組值。<b>這是候選不是確認</b>——靜態掃描無法證明它真的是 IOCTL 碼。</summary>
public sealed record PeCtlCandidate(uint Raw, ushort DeviceType, ushort Access, ushort Function, ushort Method)
{
    /// <summary>METHOD_*：0 BUFFERED、1 IN_DIRECT、2 OUT_DIRECT、3 NEITHER。</summary>
    public string MethodText => Method switch
    {
        0 => "METHOD_BUFFERED",
        1 => "METHOD_IN_DIRECT",
        2 => "METHOD_OUT_DIRECT",
        _ => "METHOD_NEITHER",
    };

    /// <summary>FILE_* 存取權：1 讀、2 寫、3 讀寫、0 無。</summary>
    public string AccessText => Access switch
    {
        1 => "FILE_READ_ACCESS",
        2 => "FILE_WRITE_ACCESS",
        3 => "FILE_READ_ACCESS|FILE_WRITE_ACCESS",
        _ => "FILE_ANY_ACCESS",
    };
}

/// <summary>一顆 PE 檔的靜態檢視結果。解析失敗時 <see cref="Parsed"/> 為 false 並帶原因，不猜其餘欄位。</summary>
public sealed record PeInspectResult(
    bool Parsed, string? ParseError,
    string MachineText, string SubsystemText, uint TimeDateStamp,
    IReadOnlyList<PeSectionInfo> Sections,
    IReadOnlyList<string> ImportedDlls,
    IReadOnlyList<string> DeviceStrings,
    IReadOnlyList<PeCtlCandidate> IoctlCandidates);

/// <summary>
/// 驅動檔（.sys）的純靜態檢視：不載入驅動、不呼叫 IOCTL，只解析 PE 結構與內嵌字串。
/// </summary>
/// <remarks>
/// <para>
/// <b>這個解碼器回答什麼、不回答什麼：</b>它報出檔案的機器／子系統／區段／匯入表、內嵌的
/// 裝置與符號連結字串（\Device\、\DosDevices\、\??\），以及 .text 裡符合 CTL_CODE 編碼形狀的
/// <b>候選</b>值——候選不保證真的是 IOCTL 分派碼，靜態掃描無法證明；要確認得反組譯分派表，
/// 那不是這裡的事。它也不判斷驅動好壞——那是 BYOVD 封鎖清單比對與簽章稽核的事（服務層交叉引用）。
/// </para>
/// <para>
/// <b>候選的篩選：</b>Function 欄落在 0x800–0xFFF（微軟保留 0–0x7FF 給通用碼，自訂 IOCTL
/// 慣例從 0x800 起）、DeviceType 在公開的 FILE_DEVICE_* 範圍內。即使如此仍會有誤報
/// （程式碼位元組本來就可能湊成同形值），所以欄位命名是「候選」並在服務層文字如實標注。
/// </para>
/// </remarks>
public static class PeInspect
{
    /// <summary>IMAGE_FILE_HEADER.Machine：x64。</summary>
    public const ushort MachineAmd64 = 0x8664;
    /// <summary>IMAGE_FILE_HEADER.Machine：ARM64。</summary>
    public const ushort MachineArm64 = 0xAA64;
    /// <summary>IMAGE_FILE_HEADER.Machine：x86。</summary>
    public const ushort MachineI386 = 0x014C;
    /// <summary>IMAGE_OPTIONAL_HEADER32 魔術值。</summary>
    public const ushort OptMagic32 = 0x10B;
    /// <summary>IMAGE_OPTIONAL_HEADER64 魔術值。</summary>
    public const ushort OptMagic64 = 0x20B;
    /// <summary>IMAGE_SUBSYSTEM.NATIVE——核心驅動的子系統。</summary>
    public const ushort SubsystemNative = 1;

    /// <summary>匯入表條目上限；超過如實截斷。</summary>
    public const int MaxImports = 64;
    /// <summary>裝置字串上限。</summary>
    public const int MaxDeviceStrings = 32;
    /// <summary>IOCTL 候選上限。</summary>
    public const int MaxCtlCandidates = 128;

    [SpecRef("Microsoft PE Format (PE/COFF) Specification §2.4.2 IMAGE_FILE_HEADER.Machine 代碼表（0x8664 AMD64、0xAA64 ARM64、0x014C I386）；未收錄代碼如實顯示原始值")]
    public static string MachineText(ushort machine) => machine switch
    {
        MachineAmd64 => "x64",
        MachineArm64 => "ARM64",
        MachineI386 => "x86",
        0x01C4 => "ARM",
        _ => $"未收錄 (0x{machine:X4})",
    };

    [SpecRef("Microsoft PE Format Specification §2.5.1 IMAGE_OPTIONAL_HEADER.Subsystem：1 NATIVE（核心驅動）、2 Windows GUI、3 Windows CUI")]
    public static string SubsystemText(ushort subsystem) => subsystem switch
    {
        1 => "NATIVE（核心驅動）",
        2 => "Windows GUI",
        3 => "Windows CUI",
        _ => $"未收錄 ({subsystem})",
    };

    [SpecRef("Wdm.h／Winioctl.h 的 CTL_CODE(DeviceType, Function, Access, Method) 巨集：(DeviceType << 16) | (Access << 14) | (Function << 2) | Method；"
           + "Function 0–0x7FF 為微軟保留、自訂碼自 0x800 起；FILE_DEVICE_* 公開範圍至 0x8FF")]
    public static PeCtlCandidate? DecodeCtlCode(uint raw)
    {
        ushort deviceType = (ushort)(raw >> 16);
        ushort access = (ushort)((raw >> 14) & 0x3);
        ushort function = (ushort)((raw >> 2) & 0xFFF);
        ushort method = (ushort)(raw & 0x3);
        // 候選條件：自訂函數範圍、公開裝置型別、存取欄合法——這是形狀篩選不是語意確認
        if (function < 0x800 || deviceType == 0 || deviceType > 0x8FF) return null;
        return new PeCtlCandidate(raw, deviceType, access, function, method);
    }

    /// <summary>
    /// 解析一段 PE 內容：DOS 頭 → PE 簽章 → 選擇性標頭 → 區段表 → 匯入表 → 內嵌字串與 IOCTL 候選。
    /// 解析失敗如實回原因（ParseError），其餘欄位為空。
    /// </summary>
    [SpecRef("Microsoft PE Format Specification：§2.2 DOS header（e_lfanew @0x3C 指向 'PE\\0\\0'）；§2.4.1 選擇性標頭魔術值與資料目錄（匯入表＝index 1）；"
           + "§2.4.3 區段表（VirtualAddress／PointerToRawData 組成 RVA→檔案位移對映）")]
    public static PeInspectResult Inspect(byte[] data)
    {
        if (data.Length < 0x40 || data[0] != (byte)'M' || data[1] != (byte)'Z')
            return Fail("不是 MZ 開頭——不是 PE 檔");
        int peOffset = BitConverter.ToInt32(data, 0x3C);
        if (peOffset <= 0 || peOffset + 24 > data.Length)
            return Fail("e_lfanew 越界——標頭損壞或不是 PE 檔");
        if (data[peOffset] != (byte)'P' || data[peOffset + 1] != (byte)'E' || data[peOffset + 2] != 0 || data[peOffset + 3] != 0)
            return Fail("PE 簽章不符");

        ushort machine = BitConverter.ToUInt16(data, peOffset + 4);
        int numSections = BitConverter.ToUInt16(data, peOffset + 6);
        uint timeStamp = BitConverter.ToUInt32(data, peOffset + 8);
        int optSize = BitConverter.ToUInt16(data, peOffset + 20);
        int optOffset = peOffset + 24;
        if (optSize < 0x70 || optOffset + optSize > data.Length)
            return Fail("選擇性標頭過短或越界");
        ushort magic = BitConverter.ToUInt16(data, optOffset);
        if (magic != OptMagic32 && magic != OptMagic64)
            return Fail($"選擇性標頭魔術值未收錄 (0x{magic:X4})");

        ushort subsystem = BitConverter.ToUInt16(data, optOffset + 68);
        int dataDirOffset = optOffset + (magic == OptMagic64 ? 112 : 96);
        if (dataDirOffset + 16 > data.Length)
            return Fail("資料目錄越界");
        uint importRva = BitConverter.ToUInt32(data, dataDirOffset + 8);  // 資料目錄 index 1＝匯入表
        uint importSize = BitConverter.ToUInt32(data, dataDirOffset + 12);

        int sectOffset = optOffset + optSize;
        if (numSections is < 0 or > 96 || sectOffset + 40 * numSections > data.Length)
            return Fail("區段表數量異常或越界");
        var sections = new List<PeSectionInfo>(numSections);
        for (int i = 0; i < numSections; i++)
        {
            int s = sectOffset + i * 40;
            sections.Add(new PeSectionInfo(
                ReadAsciiZ(data, s, 8),
                BitConverter.ToUInt32(data, s + 8),    // VirtualSize
                BitConverter.ToUInt32(data, s + 12),   // VirtualAddress
                BitConverter.ToUInt32(data, s + 16),   // SizeOfRawData
                BitConverter.ToUInt32(data, s + 20))); // PointerToRawData
        }

        var imports = new List<string>();
        if (importRva != 0 && importSize != 0)
        {
            // IMAGE_IMPORT_DESCRIPTOR 陣列，全零項終止；名稱是 RVA 指向的 ASCII-Z
            int descOff = RvaToFile(sections, importRva) ?? -1;
            for (int n = 0; descOff >= 0 && descOff + 20 <= data.Length && imports.Count < MaxImports; n++)
            {
                uint nameRva = BitConverter.ToUInt32(data, descOff + 12);
                uint firstThunk = BitConverter.ToUInt32(data, descOff + 16);
                if (nameRva == 0 && firstThunk == 0) break; // 全零終止項
                int nameOff = RvaToFile(sections, nameRva) ?? -1;
                if (nameOff >= 0) imports.Add(ReadAsciiZ(data, nameOff, 64));
                descOff = RvaToFile(sections, importRva + 20u * ((uint)n + 1)) ?? -1;
            }
        }

        var (deviceStrings, ioctlCandidates) = ScanSignals(data, sections);

        return new PeInspectResult(true, null, MachineText(machine), SubsystemText(subsystem), timeStamp,
            sections, imports, deviceStrings, ioctlCandidates);
    }

    private static PeInspectResult Fail(string reason) =>
        new(false, reason, "", "", 0, [], [], [], []);

    private static int? RvaToFile(IReadOnlyList<PeSectionInfo> sections, uint rva)
    {
        foreach (var s in sections)
        {
            if (!s.ContainsRva(rva) || s.RawPointer == 0) continue;
            int delta = (int)(rva - s.VirtualAddress);
            if (delta >= s.RawSize) return null; // 落在虛擬空間多出的部分（如 .bss）——檔案裡沒有
            return (int)s.RawPointer + delta;
        }
        return null;
    }

    private static (IReadOnlyList<string>, IReadOnlyList<PeCtlCandidate>) ScanSignals(byte[] data, IReadOnlyList<PeSectionInfo> sections)
    {
        var strings = new List<string>();
        var candidates = new List<PeCtlCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRaw = new HashSet<uint>();

        foreach (var s in sections)
        {
            if (s.RawPointer == 0 || s.RawSize == 0) continue;
            int fileStart = (int)s.RawPointer;
            int len = (int)Math.Min(s.RawSize, (uint)(data.Length - fileStart));
            if (len <= 4) continue;

            if (s.Name.Equals(".text", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i + 4 <= len && candidates.Count < MaxCtlCandidates; i += 4)
                {
                    uint raw = BitConverter.ToUInt32(data, fileStart + i);
                    if (seenRaw.Add(raw) && DecodeCtlCode(raw) is { } c) candidates.Add(c);
                }
            }
            else
            {
                ScanDeviceStrings(data, fileStart, len, wide: true, strings, seen);
                ScanDeviceStrings(data, fileStart, len, wide: false, strings, seen);
            }
        }
        return (strings, candidates);
    }

    private static void ScanDeviceStrings(byte[] data, int start, int length, bool wide,
        List<string> sink, HashSet<string> seen)
    {
        int step = wide ? 2 : 1;
        for (int i = 0; i + 2 <= length && sink.Count < MaxDeviceStrings; i += step)
        {
            if (data[start + i] != (byte)'\\') continue;
            var sb = new StringBuilder();
            for (int j = i; j + step <= length && (j - i) / step < 256; j += step)
            {
                char c = wide
                    ? (char)(data[start + j] | (data[start + j + 1] << 8))
                    : (char)data[start + j];
                if (c == '\0') break;
                if (c < 0x20 || c > 0x7E) { sb.Clear(); break; }
                sb.Append(c);
            }
            string s = sb.ToString();
            if (s.Length < 6) continue;
            bool hit = s.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("\\DosDevices\\", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("\\Applications\\", StringComparison.OrdinalIgnoreCase);
            if (hit && seen.Add(s)) sink.Add(s);
        }
    }

    private static string ReadAsciiZ(byte[] data, int offset, int max)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < max && offset + i < data.Length; i++)
        {
            byte b = data[offset + i];
            if (b == 0) break;
            sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '?');
        }
        return sb.ToString();
    }
}
