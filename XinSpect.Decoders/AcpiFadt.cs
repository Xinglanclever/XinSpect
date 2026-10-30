namespace XinSpect;

/// <summary>
/// ACPI FADT（簽章 <c>FACP</c>）裡指向「根以下的兩張表」的欄位（U10 第三圈 AC-0001／AC-0002）。
/// </summary>
/// <param name="Signature">表簽章，應為 <c>FACP</c>。</param>
/// <param name="DeclaredLength">表頭宣告長度（決定延伸欄位在不在）。</param>
/// <param name="Revision">FADT 修訂號——韌體自述的 ACPI 世代。</param>
/// <param name="MinorVersion">ACPI 6.2 起多出的 minor version（舊表為 0）。</param>
/// <param name="FirmwareCtrl">FACS 實體位址（32 位欄，位移 36）。</param>
/// <param name="Dsdt">DSDT 實體位址（32 位欄，位移 40）。</param>
/// <param name="XFirmwareCtrl">FACS 的 64 位位址（位移 132）；表太短時為 <c>null</c>。</param>
/// <param name="XDsdt">DSDT 的 64 位位址（位移 140）；表太短時為 <c>null</c>。</param>
public sealed record AcpiFadtInfo(
    string Signature,
    uint DeclaredLength,
    byte Revision,
    byte MinorVersion,
    uint FirmwareCtrl,
    uint Dsdt,
    ulong? XFirmwareCtrl,
    ulong? XDsdt)
{
    /// <summary>FADT 的 32 位與 64 位 DSDT 欄位是否指到同一個位址（兩欄都在時才比）。</summary>
    public bool? DsdtAgrees => XDsdt is { } x ? x == Dsdt : null;

    /// <summary>FADT 是否帶 ACPI 2.0 以後的延伸欄位（長度足夠且 64 位欄非零）。</summary>
    public bool HasExtendedFields => XDsdt is { } x && x != 0;
}

/// <summary>
/// ACPI FADT 的指標欄位解碼器。純位元組解析，不碰韌體。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要這個解碼器：</b>RSDP 本身（以及它指到的 RSDT／XSDT）在 usermode 拿不到——
/// 見 <see cref="AcpiRootFactsService"/> 的界線宣告。FADT 是<b>唯一一張會告訴我們
/// 「根以下那兩張表在哪」的表</b>，而它自己是列舉得到的。
/// </para>
/// <para>
/// <b>這一格的重要性在「兩欄一致」：</b>DSDT 在 FADT 裡有兩個欄位——32 位（位移 40）
/// 與 64 位（位移 140）。兩者相同代表這張表落在 4 GiB 以下；**不相同**則代表它只在
/// 64 位那一欄指得到，也就是「只走 RSDT 的舊 OS 看不到這張表」那個情形。
/// </para>
/// </remarks>
public static class AcpiFadt
{
    /// <summary>表頭長度（ACPI 通用表頭）。</summary>
    public const int HeaderLength = 36;

    /// <summary><c>X_FIRMWARE_CTRL</c> 的位移。</summary>
    public const int XFirmwareCtrlOffset = 132;

    /// <summary><c>X_DSDT</c> 的位移。</summary>
    public const int XDsdtOffset = 140;

    /// <summary>延伸欄位要完整存在所需的最小長度（<c>X_DSDT</c> 結尾）。</summary>
    public const int ExtendedMinimumLength = 148;

    /// <summary>4 GiB 的界線：超過它就只有 64 位欄指得到。</summary>
    public const ulong FourGiB = 1UL << 32;

    [SpecRef("ACPI Spec 6.4 §5.2.9 FADT（Fixed ACPI Description Table）：表頭 36 位元組之後，"
        + "Firmware Control（FACS 位址）u32@36、DSDT u32@40；ACPI 2.0 起追加 "
        + "X_FIRMWARE_CTRL u64@132、X_DSDT u64@140（FADT minor version u8@131）。"
        + "舊版表長度不足 148 時這兩個 64 位欄不存在。小端序。"
        + "界線＝只解這四個位址欄與版本，其餘 FADT 欄位本版不解（不從 Flags 推測任何行為）")]
    public static AcpiFadtInfo? Parse(byte[] facp)
    {
        if (facp.Length < HeaderLength + 8) return null;
        string sig = System.Text.Encoding.ASCII.GetString(facp, 0, 4).TrimEnd('\0', ' ');
        if (sig != "FACP") return null;

        uint declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(facp.AsSpan(4, 4));
        byte revision = facp[8];
        byte minor = facp.Length > 131 ? facp[131] : (byte)0;
        uint firmwareCtrl = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(facp.AsSpan(36, 4));
        uint dsdt = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(facp.AsSpan(40, 4));

        ulong? xFirmwareCtrl = null;
        ulong? xDsdt = null;
        if (facp.Length >= ExtendedMinimumLength && declared >= ExtendedMinimumLength)
        {
            xFirmwareCtrl = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(facp.AsSpan(XFirmwareCtrlOffset, 8));
            xDsdt = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(facp.AsSpan(XDsdtOffset, 8));
        }

        return new AcpiFadtInfo(sig, declared, revision, minor, firmwareCtrl, dsdt, xFirmwareCtrl, xDsdt);
    }

    /// <summary>這個位址是否落在 4 GiB 以下（只有 32 位欄也指得到的範圍）。</summary>
    [SpecRef("ACPI：RSDT 的指標是 32 位、XSDT 的是 64 位；因此 4 GiB 以上的表只有 XSDT 指得到"
        + "（ACPI Spec 6.4 §5.2.7 XSDT）。這條界線決定「舊 OS 看不看得到某張表」")]
    public static bool IsBelow4GiB(ulong address) => address < FourGiB;
}
