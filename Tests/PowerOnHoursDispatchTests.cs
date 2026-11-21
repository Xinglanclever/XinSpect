using Xunit;

namespace XinSpect.Tests;

/// <summary>Production routing and decoder with synthetic byte arrays; no native storage access.</summary>
public sealed class PowerOnHoursDispatchTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(7u)]
    [InlineData(8u)]
    [InlineData(10u)]
    [InlineData(14u)]
    [InlineData(15u)]
    [InlineData(16u)]
    [InlineData(uint.MaxValue)]
    public void UnverifiedChannelMustNotDispatchProtocolRead(uint bus)
    {
        int nvmeReads = 0, ataReads = 0;
        long hours = StorageSmartService.ReadPowerOnHours(bus,
            () => { nvmeReads++; return NvmeHours(37); },
            () => { ataReads++; return AtaHours(41); });

        Assert.Equal(0, hours);
        Assert.Equal(0, nvmeReads);
        Assert.Equal(0, ataReads);
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(11u)]
    public void ExplicitAtaChannelDispatchesOnlyAtaAndPreservesDecodedHours(uint bus)
    {
        int nvmeReads = 0, ataReads = 0;
        long hours = StorageSmartService.ReadPowerOnHours(bus,
            () => { nvmeReads++; return NvmeHours(37); },
            () => { ataReads++; return AtaHours(41); });

        Assert.Equal(41, hours);
        Assert.Equal(0, nvmeReads);
        Assert.Equal(1, ataReads);
    }

    [Fact]
    public void ExplicitNvmeChannelDispatchesOnlyNvmeAndPreservesDecodedHours()
    {
        int nvmeReads = 0, ataReads = 0;
        long hours = StorageSmartService.ReadPowerOnHours(17,
            () => { nvmeReads++; return NvmeHours(37); },
            () => { ataReads++; return AtaHours(41); });

        Assert.Equal(37, hours);
        Assert.Equal(1, nvmeReads);
        Assert.Equal(0, ataReads);
    }

    [Fact]
    public void UnavailableNvmeResultMustNotFallBackToAta()
    {
        int ataReads = 0;
        Assert.Equal(0, StorageSmartService.ReadPowerOnHours(17,
            () => null, () => { ataReads++; return AtaHours(41); }));
        Assert.Equal(0, ataReads);
    }

    [Fact]
    public void UnavailableAtaResultPreservesUnavailableSentinel()
    {
        int nvmeReads = 0;
        Assert.Equal(0, StorageSmartService.ReadPowerOnHours(11,
            () => { nvmeReads++; return NvmeHours(37); }, () => null));
        Assert.Equal(0, nvmeReads);
    }

    private static byte[] NvmeHours(byte hours)
    {
        var bytes = new byte[512];
        bytes[0x80] = hours;
        return bytes;
    }

    private static byte[] AtaHours(byte hours)
    {
        var bytes = new byte[512];
        bytes[2] = 9;
        bytes[7] = hours;
        return bytes;
    }
}