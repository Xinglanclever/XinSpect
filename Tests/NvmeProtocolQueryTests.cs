using System.Buffers.Binary;
using Xunit;

namespace XinSpect.Tests;

/// <summary>SDK 封包契約與截斷／錯位回覆；不開磁碟、不發 IOCTL。</summary>
public class NvmeProtocolQueryTests
{
    [Theory]
    [InlineData(1u, 1u, 4096, NvmeProtocolScope.Adapter, 49u)]
    [InlineData(2u, 2u, 512, NvmeProtocolScope.Device, 50u)]
    [InlineData(2u, 1u, 512, NvmeProtocolScope.Adapter, 49u)]
    [InlineData(2u, 3u, 512, NvmeProtocolScope.Adapter, 49u)]
    [InlineData(3u, 12u, 256, NvmeProtocolScope.Adapter, 49u)]
    public void 請求使用SDK的裝置屬性與協定相對偏移(uint type, uint value, int length,
        NvmeProtocolScope scope, uint property)
    {
        var request = NvmeProtocolQuery.CreateRequest(type, value, length, scope);
        Assert.Equal(48 + length, request.Length);
        Assert.Equal(property, Read(request, 0)); // 51 是 AdapterTemperatureProperty。
        Assert.Equal(0u, Read(request, 4));
        Assert.Equal(3u, Read(request, 8));
        Assert.Equal(type, Read(request, 12)); // Identify=1，0 是 Unknown。
        Assert.Equal(value, Read(request, 16));
        Assert.Equal(0u, Read(request, 20));
        Assert.Equal(40u, Read(request, 24)); // 相對於位址 8，故 payload 在 48。
        Assert.Equal((uint)length, Read(request, 28));
        Assert.All(request[32..], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(1u, 4096)]
    [InlineData(2u, 512)]
    [InlineData(3u, 256)]
    public void 三種資料區均原樣交給既有解碼器(uint type, int length)
    {
        var response = Response(type, length);
        var payload = NvmeProtocolQuery.ReadPayload(response, (uint)response.Length, type, length);
        Assert.Equal(response[48..], payload);
        response[48] ^= 0xff;
        Assert.NotEqual(response[48], payload![0]);
    }

    [Fact]
    public void 回覆有填充時依返回偏移定位_不寫死48()
    {
        var response = Response(2, 512, 48);
        Assert.Equal(response[56..], NvmeProtocolQuery.ReadPayload(response, (uint)response.Length, 2, 512));
    }

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(0, 47u)]
    [InlineData(4, 0u)]
    [InlineData(4, 49u)]
    [InlineData(8, 2u)]
    [InlineData(12, 1u)]
    [InlineData(24, 0u)]
    [InlineData(24, 39u)]
    [InlineData(24, 41u)]
    [InlineData(24, uint.MaxValue)]
    [InlineData(28, 0u)]
    [InlineData(28, 511u)]
    [InlineData(28, 513u)]
    [InlineData(28, uint.MaxValue)]
    public void 異常描述子不得拿配置中的零或錯位資料冒充成功(int field, uint value)
    {
        var response = Response(2, 512);
        Write(response, field, value);
        Assert.Null(NvmeProtocolQuery.ReadPayload(response, (uint)response.Length, 2, 512));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(47u)]
    [InlineData(48u)]
    [InlineData(559u)]
    [InlineData(561u)]
    [InlineData(uint.MaxValue)]
    public void 實際返回長度必須容納整個宣告且不超配置(uint returned)
    {
        Assert.Null(NvmeProtocolQuery.ReadPayload(Response(2, 512), returned, 2, 512));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4097)]
    [InlineData(int.MaxValue)]
    public void 非法資料長度先拒絕_不開裝置(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NvmeProtocolQuery.CreateRequest(3, 12, length));
        Assert.Throws<ArgumentOutOfRangeException>(() => StorageSmartService.TryReadNvmeFeature(-4242, 12, length));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    [InlineData(uint.MaxValue)]
    public void 不支援的資料類型不送入協定查詢(uint type)
        => Assert.Throws<ArgumentOutOfRangeException>(() => NvmeProtocolQuery.CreateRequest(type, 0, 512));

    [Theory]
    [InlineData(48u)]
    [InlineData(51u)]
    [InlineData(uint.MaxValue)]
    public void 其他儲存屬性不允許混入協定請求(uint property)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            NvmeProtocolQuery.CreateRequest(2, 2, 512, (NvmeProtocolScope)property));

    private static byte[] Response(uint type, int length, int protocolOffset = 40)
    {
        // STORAGE_PROTOCOL_DATA_DESCRIPTOR: DWORD Version、Size，接 40-byte ProtocolSpecificData。
        var response = new byte[8 + protocolOffset + length];
        Write(response, 0, 48); Write(response, 4, 48);
        Write(response, 8, 3); Write(response, 12, type);
        Write(response, 24, (uint)protocolOffset); Write(response, 28, (uint)length);
        for (int i = 0; i < length; i++) response[8 + protocolOffset + i] = (byte)(i * 37 + 19);
        return response;
    }

    private static uint Read(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset));
    private static void Write(byte[] b, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), value);
}
