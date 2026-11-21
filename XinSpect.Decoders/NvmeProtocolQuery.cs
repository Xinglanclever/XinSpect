using System.Buffers.Binary;

namespace XinSpect;

public enum NvmeProtocolScope : uint { Adapter = 49, Device = 50 }

/// <summary>
/// Windows STORAGE_PROPERTY_QUERY／STORAGE_PROTOCOL_DATA_DESCRIPTOR 的 NVMe 唯讀封裝。
/// 版面依 Windows SDK winioctl.h；ProtocolDataOffset 相對於 ProtocolSpecificData，非整包。
/// </summary>
public static class NvmeProtocolQuery
{
    public const uint Identify = 1;
    public const uint LogPage = 2;
    public const uint Feature = 3;
    public const int HeaderBytes = 48;
    public const int MaxPayloadBytes = 4096;
    private const uint ProtocolNvme = 3;
    private const int ProtocolStart = 8;
    private const int ProtocolBytes = 40;

    public static byte[] CreateRequest(uint dataType, uint requestValue, int length,
        NvmeProtocolScope scope = NvmeProtocolScope.Device)
    {
        Validate(dataType, length);
        if (scope is not (NvmeProtocolScope.Adapter or NvmeProtocolScope.Device))
            throw new ArgumentOutOfRangeException(nameof(scope));
        var buffer = new byte[HeaderBytes + length];
        Write(buffer, 0, (uint)scope);
        // QueryType = PropertyStandardQuery (0)；其餘 subvalue 保持零。
        Write(buffer, 8, ProtocolNvme);
        Write(buffer, 12, dataType);
        Write(buffer, 16, requestValue);
        Write(buffer, 24, ProtocolBytes);
        Write(buffer, 28, (uint)length);
        return buffer;
    }

    /// <summary>只接受已完成 IOCTL 的完整回覆；不以配置容量代替實際返回長度。</summary>
    public static byte[]? ReadPayload(ReadOnlySpan<byte> buffer, uint returnedBytes, uint dataType, int length)
    {
        Validate(dataType, length);
        if (returnedBytes < HeaderBytes || returnedBytes > (uint)buffer.Length) return null;
        buffer = buffer[..(int)returnedBytes];
        if (Read(buffer, 0) != HeaderBytes || Read(buffer, 4) != HeaderBytes ||
            Read(buffer, 8) != ProtocolNvme || Read(buffer, 12) != dataType) return null;

        uint offset = Read(buffer, 24), declaredLength = Read(buffer, 28);
        if (offset < ProtocolBytes || declaredLength < (uint)length) return null;
        // UInt32 的 offset／length 先擴寬再相加，避免包內宣告繞回小位址。
        ulong start = ProtocolStart + (ulong)offset;
        if (start + declaredLength > returnedBytes) return null;
        return buffer.Slice((int)start, length).ToArray();
    }

    private static void Validate(uint dataType, int length)
    {
        if (dataType is < Identify or > Feature) throw new ArgumentOutOfRangeException(nameof(dataType));
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaxPayloadBytes);
    }

    private static uint Read(ReadOnlySpan<byte> buffer, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
    private static void Write(Span<byte> buffer, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(buffer[offset..], value);
}
