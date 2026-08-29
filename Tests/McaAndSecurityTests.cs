using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>MCA 與安全位元的解碼純函式（合成值，不讀真實 MSR）。</summary>
public class McaAndSecurityTests
{
    [Fact]
    public void MCi_STATUS解碼_valid與已修正計數()
    {
        // valid(bit63) + 修正計數 5（位 38–52）
        ulong status = (1UL << 63) | (5UL << 38);
        var (valid, uc, addrValid, miscValid, corrected) = McaService.DecodeStatus(status);
        Assert.True(valid);
        Assert.False(uc);
        Assert.False(addrValid);
        Assert.Equal(5, corrected);
    }

    [Fact]
    public void MCi_STATUS解碼_不可修正與位址有效()
    {
        ulong status = (1UL << 63) | (1UL << 61) | (1UL << 58);
        var (valid, uc, addrValid, _, _) = McaService.DecodeStatus(status);
        Assert.True(valid);
        Assert.True(uc);
        Assert.True(addrValid);
    }

    [Fact]
    public void MCi_STATUS_全零為無效()
    {
        var (valid, _, _, _, corrected) = McaService.DecodeStatus(0);
        Assert.False(valid);
        Assert.Equal(0, corrected);
    }

    [Fact]
    public void ARCH_CAPABILITIES位元解碼()
    {
        // RDCL_NO + MDS_NO
        ulong v = 1 | (1UL << 5);
        Assert.Equal("是", CpuSecurityDecoder.OnOff(v, 0));
        Assert.Equal("是", CpuSecurityDecoder.OnOff(v, 5));
        Assert.Equal("否", CpuSecurityDecoder.OnOff(v, 1));
    }

    [Fact]
    public void SPEC_CTRL解碼_IBRS與SSBD啟用()
    {
        var (ibrs, stibp, ssbd) = CpuSecurityDecoder.DecodeSpecCtrl(1 | 8);
        Assert.True(ibrs);
        Assert.False(stibp);
        Assert.True(ssbd);
    }

    [Fact]
    public void 免疫位元表_位元不重複()
    {
        var bits = CpuSecurityDecoder.ArchCapsBits.Select(b => b.Bit).ToHashSet();
        Assert.Equal(CpuSecurityDecoder.ArchCapsBits.Length, bits.Count);
    }
}
