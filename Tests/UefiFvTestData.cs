using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XinSpect.Tests;

/// <summary>
/// UEFI FV／FFS 的測試資料建構器（<b>單一來源</b>）。
///
/// <para>
/// <b>為什麼要抽出來：</b>這些建構器原本是 <c>UefiFvTests</c> 的 private static。
/// 而 <c>UefiParseGapsTests</c> 也需要造合法的 FV——
/// <b>我第一版在那边自己猜了一個，結果漏了 FileSystemGuid、Attributes、
/// block map 與表頭校驗和四個欄位，被自己的守門擋下。</b>
/// </para>
///
/// <para>
/// <b>兩份 FV 合成器就是兩處漂移的來源</b>——這是
/// <c>BootStatusName</c>（解碼重複）、GUID 表（清單重複）那兩個教訓的第三次應用。
/// 抽成共用之後，規格變了只需要改一個地方。
/// </para>
///
/// <para>
/// 逐欄編碼依 UEFI PI Specification Vol. 3：
/// FV 表頭（ZeroVector 16 ＋ FileSystemGuid 16 ＋ FvLength 4 ＋ Signature 4
/// ＋ Attributes 4 ＋ HeaderLength 2 ＋ Checksum 2 ＋ ExtHeaderOffset 2
/// ＋ Reserved 1 ＋ Revision 1），FFS2 檔頭 0x18，區段 4 位元組對齊。
/// </para>
/// </summary>
internal static class UefiFvTestData
{
    public const string Fs2Guid = "8C8CE578-8A3D-4F1C-9935-896185C32DD3"; // EFI_FIRMWARE_FILE_SYSTEM2_GUID

    /// <summary>造一個 USER_INTERFACE 區段（4 位元組頭 ＋ UTF-16LE 名，含補零終止）。</summary>
    public static byte[] UiSection(string name)
    {
        var payload = Encoding.Unicode.GetBytes(name + "\0");
        return Section(0x15, payload);
    }

    public static byte[] Section(byte type, byte[] payload)
    {
        int size = 4 + payload.Length;
        var s = new byte[size];
        s[0] = (byte)size; s[1] = (byte)(size >> 8); s[2] = (byte)(size >> 16);
        s[3] = type;
        Array.Copy(payload, 0, s, 4, payload.Length);
        return s;
    }

    /// <summary>造一個 FFS 檔（FFS2，0x18 表頭 ＋ 區段列，區段間按規格 4 位元組對齊）。state 預設 0x07（三個有效位元）。</summary>
    public static byte[] FfsFile(string guid, byte type, byte[][] sections,
        byte? state = null, byte? attributes = null)
    {
        int payloadLen = sections.Sum(s => (s.Length + 3) & ~3);
        int size = 0x18 + payloadLen;
        var f = new byte[size];
        new Guid(guid).ToByteArray().CopyTo(f, 0);
        f[0x12] = type;
        f[0x13] = attributes ?? 0;
        f[0x14] = (byte)size; f[0x15] = (byte)(size >> 8); f[0x16] = (byte)(size >> 16);
        f[0x17] = state ?? 0x07;
        f[0x10] = ComputeFileHeaderChecksum(f);
        f[0x11] = 0xAA; // FFS_FIXED_CHECKSUM（未設 FFS_ATTRIB_CHECKSUM 時）
        int p = 0x18;
        foreach (var s in sections)
        {
            Array.Copy(s, 0, f, p, s.Length);
            p = (p + s.Length + 3) & ~3;
        }
        return f;
    }

    public static byte ComputeFileHeaderChecksum(byte[] header)
    {
        int sum = 0;
        for (int i = 0; i < header.Length; i++)
        {
            if (i == 0x10 || i == 0x11 || i == 0x17) continue;
            sum += header[i];
        }
        return (byte)(-(byte)sum & 0xFF);
    }

    /// <summary>把若干 FFS 檔包進一個 FV（FFS2 檔案系統 GUID、修訂 2、無抹除極性、含正確的 FV 表頭校驗和）。</summary>
    public static byte[] BuildFv(IReadOnlyList<byte[]> files, int revision = 2,
        uint extraAttributes = 0, bool padFile = false)
    {
        var body = new List<byte[]>();
        if (padFile)
        {
            var pad = new byte[0x18];
            new Guid("00000000-0000-0000-0000-000000000000").ToByteArray().CopyTo(pad, 0);
            pad[0x12] = 0xF0;
            pad[0x14] = 0x18;
            pad[0x17] = 0x07;
            body.Add(pad);
        }
        foreach (var f in files) body.Add(f);
        int bodyLen = body.Sum(b => (b.Length + 7) & ~7);
        ulong fvLength = 0x48 + (ulong)bodyLen;

        var fv = new byte[fvLength];
        new Guid(Fs2Guid).ToByteArray().CopyTo(fv, 0x10);
        BitConverter.GetBytes(fvLength).CopyTo(fv, 0x20);
        BitConverter.GetBytes(UefiFv.FvhSignature).CopyTo(fv, 0x28);
        BitConverter.GetBytes(0x0004_0400u | extraAttributes).CopyTo(fv, 0x2C);
        BitConverter.GetBytes((ushort)0x48).CopyTo(fv, 0x30);
        BitConverter.GetBytes((ushort)0x100).CopyTo(fv, 0x38);
        BitConverter.GetBytes(0x1000u).CopyTo(fv, 0x3C);
        fv[0x36] = 0;
        fv[0x37] = (byte)revision;

        ushort sum = 0;
        for (int i = 0; i < 0x48; i += 2) sum += BitConverter.ToUInt16(fv, i);
        BitConverter.GetBytes((ushort)(0 - sum)).CopyTo(fv, 0x32);

        int p = 0x48;
        foreach (var b in body)
        {
            Array.Copy(b, 0, fv, p, b.Length);
            p = (p + b.Length + 7) & ~7;
        }
        return fv;
    }

    /// <summary>一個空的合法 FV（沒有檔案）——用來驗「FV 存在但沒有內容」。</summary>
    public static byte[] EmptyFv() => BuildFv([]);

    /// <summary>一個含單一 USER_INTERFACE 檔的合法 FV——用來驗「有走訪」的路徑。</summary>
    public static byte[] FvWithOneFile(string uiName = "TestDriver") =>
        BuildFv([FfsFile("9B680FCE-AD6B-4F3A-B60B-F59899003443", 0x07, [UiSection(uiName)])]);
}
