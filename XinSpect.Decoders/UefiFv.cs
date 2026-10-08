using System;
using System.Collections.Generic;
using System.Text;

namespace XinSpect;

/// <summary>FFS 檔案裡一個區段（section）的解碼結果。<see cref="Extra"/> 是補充說明（例如 GUID_DEFINED 的引擎名與資料位移）。</summary>
public sealed record UefiFvSection(string TypeName, ulong Size, string? Extra);

/// <summary>FV 頂層的一個 FFS 檔案。<see cref="UiName"/> 來自 USER_INTERFACE 區段（UTF-16LE），沒有就為 null。</summary>
public sealed record UefiFvFile(
    string Guid, string TypeName, ulong Size, bool HeaderChecksumOk, bool Deleted,
    string? UiName, IReadOnlyList<UefiFvSection> Sections);

/// <summary>一個韌體磁碟區（FV）的解碼結果。<see cref="SkippedCount"/> 是未過資料有效位元而略過的檔案數；
/// <see cref="ChecksumMismatchCount"/> 是表頭校驗和不符合規格的檔案數——如實呈現，不拒收也不掩蓋。</summary>
public sealed record UefiFvInfo(
    int Index, ulong Offset, string FileSystemGuid, ulong Length, int Revision,
    int FileCount, int SkippedCount, int ChecksumMismatchCount, bool HeaderChecksumOk, bool ExtHeader,
    IReadOnlyList<UefiFvFile> Files);

/// <summary>
/// UEFI 韌體磁碟區（FV）與 FFS 檔案／區段的純解碼器：輸入一段唯讀讀回的內容（BIOS 區或使用者提供的映像），
/// 輸出可導覽的 FV → FFS 檔案 → 區段樹（GUID、型別、位移、大小）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這個解碼器做什麼、不做什麼：</b>它只回答「這段內容依 UEFI PI 規格長成什麼結構」。
/// GUID_DEFINED／COMPRESSION 區段<b>只列出、不解壓</b>——解壓是另一件事，需要引入解壓引擎；
/// 解不開的位元組如實標示（略過數、校驗和不符數），不猜、不用 0 補齊。
/// 結構存在與否不構成對韌體真偽的判決。
/// </para>
/// <para>
/// <b>區段／檔案型別的未收錄原則：</b>型別對照表只收錄有把握的子集，未收錄的代碼一律顯示原始
/// 16 進位（「未收錄 (0xXX)」），不猜名字——與 PciKnowledge 的處理同一條規矩。
/// </para>
/// </remarks>
public static class UefiFv
{
    /// <summary>'_FVH' 簽章（SIGNATURE_32('_','F','V','H')＝0x4856465F；記憶體位元組序＝5F 46 56 48），在 FV 標頭位移 0x28。</summary>
    public const uint FvhSignature = 0x4856465F;

    /// <summary>FFS 狀態位元（erase polarity 處理<b>之後</b>的意義）。</summary>
    public const byte StateHeaderConstruction = 0x01;
    public const byte StateHeaderValid = 0x02;
    public const byte StateDataValid = 0x04;
    public const byte StateDeleted = 0x10;
    public const byte StateHeaderInvalid = 0x20;

    /// <summary>FFS 屬性：FFS3 大檔（Size 欄為 0xFFFFFF 時改讀 ExtendedSize u64）。</summary>
    public const byte FileAttribLargeFile = 0x01;

    /// <summary>FFS 檔案型別：FFS_PAD（對齊填充，不列為檔案）。</summary>
    public const byte FileTypePad = 0xF0;

    /// <summary>區段型別：使用者介面名（UTF-16LE）、GUID_DEFINED、COMPRESSION、FIRMWARE_VOLUME_IMAGE。</summary>
    public const byte SectTypeCompression = 0x01;
    public const byte SectTypeGuidDefined = 0x02;
    public const byte SectTypeUserInterface = 0x15;
    public const byte SectTypeFvImage = 0x17;

    /// <summary>LZMA 自訂解壓 GUID（EDK II 慣例）：GUID_DEFINED 區段引用它時只標名，不展開資料。</summary>
    public static readonly Guid GuidLzmaCustomDecompress = new("EE4E5898-3914-4259-9D6E-DC7BD79403CF");

    /// <summary>CRC32 引導抽取 GUID（PI Spec 定義的標準引導引擎之一）。</summary>
    public static readonly Guid GuidCrc32GuidedSection = new("FC1BCDB0-7D31-49AA-936A-A4600D9DD083");

    /// <summary>GUID 的 16 位元組二進位轉標準文字形（前 3 欄位小端序由 Guid 建構子處理；統一大寫，與專案其他 GUID 輸出一致）。</summary>
    [SpecRef("UEFI Specification 2.10 Appendix A（GUID 的混合端序文字形）；.NET Guid(byte[]) 依同樣的端序規則解讀前 3 欄位")]
    public static string FormatGuid(byte[] raw16) => new Guid(raw16).ToString().ToUpperInvariant();

    [SpecRef("UEFI PI Specification, Vol. 3, §2.1.4 EFI_FFS_FILE_HEADER.Type 的檔案型別表；未收錄代碼如實顯示原始值，不猜名稱")]
    public static string FileTypeName(byte type) => type switch
    {
        0x01 => "RAW",
        0x02 => "FREEFORM",
        0x03 => "SECURITY_CORE",
        0x04 => "PEI_CORE",
        0x05 => "DXE_CORE",
        0x06 => "PEIM",
        0x07 => "DRIVER",
        0x08 => "COMBINED_PEIM_DRIVER",
        0x09 => "APPLICATION",
        0x0A => "SMM",
        0x0B => "FIRMWARE_VOLUME_IMAGE",
        0x0C => "COMBINED_SMM_DXE",
        0x0D => "SMM_CORE",
        0xF0 => "FFS_PAD",
        _ => $"未收錄 (0x{type:X2})",
    };

    [SpecRef("UEFI PI Specification, Vol. 3, §2.1.5 EFI_COMMON_SECTION_HEADER.Type 的區段型別表；未收錄代碼如實顯示原始值")]
    public static string SectionTypeName(byte type) => type switch
    {
        0x01 => "COMPRESSION",
        0x02 => "GUID_DEFINED",
        0x03 => "DISPOSABLE",
        0x10 => "PE32",
        0x11 => "PIC",
        0x12 => "TE",
        0x13 => "DXE_DEPEX",
        0x14 => "VERSION",
        0x15 => "USER_INTERFACE",
        0x16 => "COMPATIBILITY16",
        0x17 => "FIRMWARE_VOLUME_IMAGE",
        0x18 => "FREEFORM_SUBTYPE_GUID",
        0x19 => "RAW",
        0x1B => "PEI_DEPEX",
        0x1C => "SMM_DEPEX",
        _ => $"未收錄 (0x{type:X2})",
    };

    /// <summary>
    /// 在一段內容裡依序找出 FV 並解碼頂層結構。找不到任何 '_FVH' 標記時回空清單——
    /// 「沒有找到」是如實的答案，不是錯誤。
    /// </summary>
    [SpecRef("UEFI PI Specification, Vol. 3, §2.1.1 EFI_FIRMWARE_VOLUME_HEADER（ZeroVector/FileSystemGuid/FvLength/Signature/Attributes/HeaderLength/Checksum/ExtHeaderOffset/BlockMap；"
           + "Checksum 欄使標頭所有 u16 總和為 0）；§2.1.2 EFI_FIRMWARE_VOLUME_EXT_HEADER（FvName＋ExtHeaderSize，檔案從延伸標頭之後開始）")]
    public static IReadOnlyList<UefiFvInfo> DecodeFvs(byte[] data)
    {
        var result = new List<UefiFvInfo>();
        ulong pos = 0;
        int index = 0;
        while (pos + 0x40 <= (ulong)data.Length)
        {
            if (BitConverter.ToUInt32(data, (int)pos + 0x28) != FvhSignature)
            {
                pos += 8; // FV 以 8 位元組對齊；掃描也以 8 為步長
                continue;
            }

            ulong fvLen = BitConverter.ToUInt64(data, (int)pos + 0x20);
            int headerLength = BitConverter.ToUInt16(data, (int)pos + 0x30);
            // 標頭長度至少要容納一個 block map 終止項（0x38 + 8）；FV 長度至少涵蓋標頭且不越出輸入——
            // 不滿足就是標記巧合或結構損壞，停在這裡如實收場，不繼續漫遊。
            if (headerLength < 0x40 || pos + (ulong)headerLength > (ulong)data.Length ||
                fvLen < (ulong)headerLength || pos + fvLen > (ulong)data.Length)
                break;

            var fvs = ParseFv(data, pos, index);
            if (fvs is null) break;
            result.Add(fvs);
            index++;
            pos = Align8(pos + fvLen);
        }
        return result;
    }

    private static UefiFvInfo ParseFv(byte[] data, ulong pos, int index)
    {
        ulong fvLen = BitConverter.ToUInt64(data, (int)pos + 0x20);
        uint attributes = BitConverter.ToUInt32(data, (int)pos + 0x2C);
        int headerLength = BitConverter.ToUInt16(data, (int)pos + 0x30);
        ushort extHeaderOffset = BitConverter.ToUInt16(data, (int)pos + 0x34);
        int revision = data[(int)pos + 0x37];
        bool erasePolarity = (attributes & 0x0000_0800) != 0; // EFI_FVB2_ERASE_POLARITY

        // 標頭校驗：標頭內所有 u16 總和必須為 0
        bool headerChecksumOk;
        uint sum = 0;
        for (int i = 0; i + 2 <= headerLength; i += 2) sum += BitConverter.ToUInt16(data, (int)pos + i);
        headerChecksumOk = (sum & 0xFFFF) == 0;

        bool extHeader = extHeaderOffset != 0 && pos + extHeaderOffset + 20 <= pos + fvLen;
        ulong filesStart = Align8(pos + (uint)headerLength);
        if (extHeader)
        {
            int extSize = BitConverter.ToInt32(data, (int)pos + extHeaderOffset + 16);
            if (extSize > 0 && pos + extHeaderOffset + (ulong)extSize <= pos + fvLen)
                filesStart = Align8(pos + (ulong)extHeaderOffset + (ulong)extSize);
        }

        var files = new List<UefiFvFile>();
        int skipped = 0, checksumMismatch = 0;
        ulong p = filesStart;
        ulong fvEnd = pos + fvLen;
        while (p + 0x18 <= fvEnd)
        {
            int state = data[(int)p + 0x17];
            if (erasePolarity) state = ~state & 0xFF; // 抹除極性 1：位元反相後才是邏輯值
            if ((state & StateHeaderInvalid) != 0) break; // 之後是自由空間

            byte type = data[(int)p + 0x12];
            byte attribs = data[(int)p + 0x13];
            ulong size = (ulong)data[(int)p + 0x14] | ((ulong)data[(int)p + 0x15] << 8) | ((ulong)data[(int)p + 0x16] << 16);
            ulong headerSize = 0x18;
            bool large = size == 0xFF_FF_FF && (attribs & FileAttribLargeFile) != 0;
            if (large)
            {
                if (p + 0x20 > fvEnd) { skipped++; break; }
                size = BitConverter.ToUInt64(data, (int)p + 0x18);
                headerSize = 0x20;
            }
            if (size < headerSize || p + size > fvEnd) { skipped++; break; }

            bool deleted = (state & StateDeleted) != 0;
            bool dataValid = (state & StateDataValid) != 0;
            if (dataValid && type != FileTypePad)
            {
                bool ok = VerifyHeaderChecksum(data, (int)p, (int)headerSize);
                if (!ok) checksumMismatch++;
                var sections = DecodeSections(data, (int)(p + headerSize), (int)(p + size) - (int)(p + headerSize));
                string? uiName = null;
                foreach (var s in sections)
                    if (s.TypeName == "USER_INTERFACE") { uiName = s.Extra; break; }
                var guidBytes = new byte[16];
                Array.Copy(data, (int)p, guidBytes, 0, 16);
                files.Add(new UefiFvFile(FormatGuid(guidBytes), FileTypeName(type), size, ok, deleted,
                    uiName, sections));
            }

            p = Align8(p + size);
        }

        var guidFs = new byte[16];
        Array.Copy(data, (int)pos + 0x10, guidFs, 0, 16);
        return new UefiFvInfo(index, pos, FormatGuid(guidFs), fvLen, revision,
            files.Count, skipped, checksumMismatch, headerChecksumOk, extHeader, files);
    }

    /// <summary>
    /// FFS 表頭校驗和：表頭全部位元組（<b>File 欄與 State 欄視為 0</b>）總和必須為 0。
    /// 實測過的韌體有不符的個案——回傳 false 如實呈現，不拒收整個檔案。
    /// </summary>
    [SpecRef("UEFI PI Specification, Vol. 3, §2.1.4 EFI_FFS_FILE_HEADER.IntegrityCheck：Header 校驗把 IntegrityCheck.Checksum.File 與 State 兩欄視為 0 後使表頭總和為 0；"
           + "File 校驗在 FFS_ATTRIB_CHECKSUM 未設時為固定值 0xAA（FFS_FIXED_CHECKSUM）")]
    public static bool VerifyHeaderChecksum(byte[] data, int offset, int headerLength)
    {
        int sum = 0;
        for (int i = 0; i < headerLength; i++)
        {
            byte b = data[offset + i];
            if (i == 0x11 || i == 0x17) b = 0; // IntegrityCheck.Checksum.File、State
            sum += b;
        }
        return (sum & 0xFF) == 0;
    }

    private static UefiFvSection[] DecodeSections(byte[] data, int start, int length)
    {
        var sections = new List<UefiFvSection>();
        int p = start;
        int end = start + length;
        while (p + 4 <= end)
        {
            ulong size = (ulong)data[p] | ((ulong)data[p + 1] << 8) | ((ulong)data[p + 2] << 16);
            int headerSize = 4;
            if (size == 0xFF_FF_FF)
            {
                if (p + 8 > end) break; // FFS3 區段需要 ExtendedSize
                size = BitConverter.ToUInt64(data, p + 4);
                headerSize = 8;
            }
            if (size < (ulong)headerSize || (ulong)p + size > (ulong)end) break; // 損壞：如實停在這裡
            byte type = data[p + 3];

            string? extra = null;
            if (type == SectTypeUserInterface)
                extra = DecodeUiName(data, p + headerSize, (int)((ulong)p + size) - (p + headerSize));
            else if (type == SectTypeGuidDefined && p + 0x18 <= end)
            {
                var guid = new Guid(data[(p + 4)..(p + 0x14)]);
                ushort dataOffset = BitConverter.ToUInt16(data, p + 0x14);
                string name = guid == GuidLzmaCustomDecompress ? "LZMA 壓縮"
                    : guid == GuidCrc32GuidedSection ? "CRC32 引導"
                    : $"未收錄引擎 {guid.ToString().ToUpperInvariant()}";
                extra = $"{name}；資料自區段起 0x{dataOffset:X}（只列出，不解壓）";
            }
            else if (type == SectTypeCompression && p + 8 <= end)
            {
                uint uncomp = BitConverter.ToUInt32(data, p + 4);
                extra = $"聲明解壓後 {uncomp} bytes（只列出，不解壓）";
            }
            else if (type == SectTypeFvImage)
            {
                extra = "巢狀 FV（只列出，不遞迴）";
            }

            sections.Add(new UefiFvSection(SectionTypeName(type), size, extra));
            p = (p + (int)size + 3) & ~3; // 區段以 4 位元組對齊
        }
        return sections.ToArray();
    }

    private static string DecodeUiName(byte[] data, int start, int length)
    {
        // UTF-16LE，遇到補零處停止——尾段對齊造成的補零不是名字的一部分
        var sb = new StringBuilder();
        for (int i = 0; i + 1 < length; i += 2)
        {
            char c = (char)(data[start + i] | (data[start + i + 1] << 8));
            if (c == '\0') break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static ulong Align8(ulong v) => (v + 7) & ~7UL;
}
