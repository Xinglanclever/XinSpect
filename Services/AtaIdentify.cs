namespace XinSpect;

/// <summary>ATA IDENTIFY DEVICE 解出來的識別資料。</summary>
/// <param name="RotationRate">
/// 標稱轉速（word 217）：<c>1</c>＝非旋轉裝置、<c>0x0401–0xFFFE</c>＝實際轉速 rpm、
/// <c>0</c> 與 <c>0xFFFF</c>＝未回報。**未回報就是未回報**，不要推論成任何一種。
/// </param>
public sealed record AtaIdentifyInfo(
    string Model,
    string Firmware,
    string Serial,
    ulong TotalLba,
    ushort RotationRate,
    int AcsMajorVersion)
{
    /// <summary>規格明訂 <c>0x0001</c> 代表非旋轉裝置（固態）。</summary>
    public bool IsSolidState => RotationRate == 1;

    /// <summary>有標稱轉速才算機械碟；未回報與保留值都不算。</summary>
    public bool IsMechanical => RotationRate is >= 0x0401 and <= 0xFFFE;

    /// <summary>廠商標示的容量（十進位 GB，每磁區 512 位元組）。</summary>
    public double CapacityGB => TotalLba * 512.0 / 1_000_000_000;
}

/// <summary>
/// IDENTIFY DEVICE（256 words＝512 位元組）的解碼器。純函式，零硬體相依。
/// </summary>
/// <remarks>
/// 讀取端是 <c>StorageSmartService.TryReadAtaIdentify</c>（走 SMART_RCV_DRIVE_DATA ＋ 0xEC 簽章，
/// 並經 <see cref="DiskIo"/> 的看門狗）。這裡只負責把位元組變成值。
/// <para>
/// <b>最重要的一條規則</b>：整片 0 必須判成「讀不到」。部分 USB 外接盒與 RAID 控制器不轉送
/// ATA 指令，回的就是一片 0；若把它解成「標稱轉速 0」，驗機規則就會據此推出「這是固態碟」
/// 這種憑空捏造的結論——那正是這類工具最容易騙人的地方。判斷依據取型號字串：
/// 任何真實裝置都會回報型號。
/// </para>
/// </remarks>
public static class AtaIdentify
{
    public const int DataSize = 512;

    private const int WordSerial = 10;      // 10 words＝20 字元
    private const int WordFirmware = 23;    // 4 words＝8 字元
    private const int WordModel = 27;       // 20 words＝40 字元
    private const int WordLba28 = 60;       // dword：28 位元可定址磁區數（舊上限）
    private const int WordVersion = 80;     // 主要版本位元圖
    private const int WordLba48 = 100;      // 4 words：48 位元可定址磁區數
    private const int WordRotation = 217;   // 標稱轉速

    public static AtaIdentifyInfo? Decode(byte[] d)
    {
        if (d is null || d.Length < DataSize) return null;

        string model = AtaString(d, WordModel, 20);
        if (model.Length == 0) return null;   // 一片 0／未轉送指令 → 讀不到，不是「轉速 0」

        // words 100–103 是 64 位元欄位，但規格只定義低 48 位元，高位必須遮掉
        ulong lba48 = BitConverter.ToUInt64(d, WordLba48 * 2) & 0x0000_FFFF_FFFF_FFFFul;
        uint lba28 = BitConverter.ToUInt32(d, WordLba28 * 2);

        return new AtaIdentifyInfo(
            model,
            AtaString(d, WordFirmware, 4),
            AtaString(d, WordSerial, 10),
            lba48 > 0 ? lba48 : lba28,
            BitConverter.ToUInt16(d, WordRotation * 2),
            HighestVersionBit(BitConverter.ToUInt16(d, WordVersion * 2)));
    }

    /// <summary>ATA 的字串欄位以 word 為單位交換位元組（規格如此，不是誰寫錯了）。</summary>
    private static string AtaString(byte[] d, int firstWord, int words)
    {
        var chars = new char[words * 2];
        for (int i = 0; i < words; i++)
        {
            chars[i * 2] = (char)d[(firstWord + i) * 2 + 1];
            chars[i * 2 + 1] = (char)d[(firstWord + i) * 2];
        }
        return new string(chars).Trim('\0', ' ');
    }

    /// <summary>word 80 是版本位元圖；取最高設定位元。0 與 0xFFFF 都是未回報。</summary>
    private static int HighestVersionBit(ushort word80)
    {
        if (word80 is 0 or 0xFFFF) return 0;
        int highest = 0;
        for (int bit = 1; bit <= 15; bit++)
            if ((word80 & (1 << bit)) != 0) highest = bit;
        return highest;
    }
}
