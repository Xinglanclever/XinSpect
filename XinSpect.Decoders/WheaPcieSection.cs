using System.Buffers.Binary;
namespace XinSpect;

public sealed record WheaPcieDeviceId(ushort VendorId, ushort DeviceId, uint ClassCode,
    byte Function, byte Device, ushort Segment, byte PrimaryBus, byte SecondaryBus, ushort Slot);

/// <summary>Only fields whose SDK validity bits are set are projected. No PCI configuration read occurs.</summary>
public sealed record WheaPcieSection(ulong ValidBits, uint? PortType, byte? MajorVersion, byte? MinorVersion,
    ushort? Command, ushort? Status, WheaPcieDeviceId? DeviceId,
    [property: System.Text.Json.Serialization.JsonIgnore] ulong? DeviceSerialNumber,
    ushort? BridgeSecondaryStatus, ushort? BridgeControl, bool HasExpressCapability, bool HasAerInfo)
{
    public string? DeviceIdRejection { get; init; }
    public override string ToString() => $"WheaPcieSection ValidBits=0x{ValidBits:X16}; serial omitted";
}

public static class WheaPcieSectionDecoder
{
    public const int SectionBytes = 208;
    [SpecRef("Windows SDK10.0.28000.0 um/cper.h WHEA_PCIEXPRESS_ERROR_SECTION / VALIDBITS / DEVICE_ID and CPER_FIELD_CHECK: 208B; offsets0,8,12,16,24,40,48,52,112; Microsoft Learn WHEA_PCIEXPRESS_ERROR_SECTION")]
    public static WheaPcieSection? Parse(byte[] bytes) => Parse(bytes, out _);

    [SpecRef("Windows SDK10.0.28000.0 um/cper.h WHEA_PCIEXPRESS_ERROR_SECTION_VALIDBITS bits0..7; DEVICE_ID bitfield order; CPER_FIELD_CHECK 208B")]
    public static WheaPcieSection? Parse(byte[] bytes, out string? reason)
    {
        reason = null;
        if (bytes.Length < SectionBytes) { reason = "PCIe section shorter than SDK 208-byte layout."; return null; }
        ulong U64(int p) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(p, 8));
        uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
        ushort U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p, 2));
        ulong valid = U64(0);
        if ((valid & ~0xffUL) != 0) { reason = "PCIe validity bits outside SDK bits0..7 are unsupported."; return null; }
        bool Has(int bit) => (valid & (1UL << bit)) != 0;
        WheaPcieDeviceId? id = null;
        string? identityRejection = null;
        if (Has(3))
        {
            byte function = bytes[31], device = bytes[32];
            if (function > 7 || device > 31) identityRejection = "DeviceId validity bit is set but device/function exceeds PCI BDF range; identity not projected.";
            else id = new(U16(24), U16(26), U32(28) & 0xffffff, function, device,
                U16(33), bytes[35], bytes[36], (ushort)((U16(37) >> 3) & 0x1fff));
        }
        return new(valid, Has(0) ? U32(8) : null, Has(1) ? bytes[13] : null, Has(1) ? bytes[12] : null,
            Has(2) ? U16(16) : null, Has(2) ? U16(18) : null, id, Has(4) ? U64(40) : null,
            Has(5) ? U16(48) : null, Has(5) ? U16(50) : null, Has(6), Has(7)) { DeviceIdRejection = identityRejection };
        // Reserved bytes and trailing extension bytes have no projected semantics.
    }
}
