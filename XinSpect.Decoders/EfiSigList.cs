namespace XinSpect;

/// <summary>UEFI EFI_SIGNATURE_LIST 解碼結果：一條簽章清單的摘要。</summary>
public sealed record EfiSignatureListInfo(
    string TypeGuid,
    int SignatureCount,
    int TotalBytes);

/// <summary>
/// UEFI EFI_SIGNATURE_LIST 的純解碼器（UEFI Spec §32.4.1）：
/// SignatureType GUID (16) + SignatureListSize (4) + SignatureHeaderSize (4) + SignatureSize (4)
/// + SignatureHeader + 簽章條目（每條 Owner GUID 16 + 資料）。
/// </summary>
public static class EfiSigListDecoder
{
    /// <summary>解析 EFI_SIGNATURE_LIST 位元組流，回傳每條清單的摘要。格式不符回空（不猜）。</summary>
    public static IReadOnlyList<EfiSignatureListInfo> Decode(byte[] data)
    {
        var results = new List<EfiSignatureListInfo>();
        if (data is null || data.Length < 28) return results;
        int off = 0;
        while (off + 28 <= data.Length)
        {
            var typeGuid = FormatGuid(data, off);
            uint listSize = BitConverter.ToUInt32(data, off + 16);
            uint headerSize = BitConverter.ToUInt32(data, off + 20);
            uint sigSize = BitConverter.ToUInt32(data, off + 24);
            if (listSize == 0 || off + (int)listSize > data.Length) break;
            if (sigSize <= 16) { off += (int)listSize; continue; } // 簽章條目至少要有 Owner GUID
            int sigDataStart = off + 28 + (int)headerSize;
            int sigDataEnd = off + (int)listSize;
            int count = sigDataEnd > sigDataStart ? (sigDataEnd - sigDataStart) / (int)sigSize : 0;
            results.Add(new EfiSignatureListInfo(typeGuid, count, (int)listSize));
            off += (int)listSize;
        }
        return results;
    }

    /// <summary>16-byte GUID → 標準字串格式（8-4-4-4-12，小端混合）。</summary>
    private static string FormatGuid(byte[] data, int offset)
    {
        int a = BitConverter.ToInt32(data, offset);
        short b = BitConverter.ToInt16(data, offset + 4);
        short c = BitConverter.ToInt16(data, offset + 6);
        return string.Format("{0:X8}-{1:X4}-{2:X4}-{3:X2}{4:X2}-{5:X2}{6:X2}{7:X2}{8:X2}{9:X2}{10:X2}",
            a, b, c,
            data[offset + 8], data[offset + 9],
            data[offset + 10], data[offset + 11],
            data[offset + 12], data[offset + 13],
            data[offset + 14], data[offset + 15]);
    }
}
