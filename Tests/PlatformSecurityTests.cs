using Xunit;

namespace XinSpect.Tests;

public sealed class PlatformSecurityTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    // IA32_FEATURE_CONTROL (0x3A)：bit0 Lock、bit1 VMX-in-SMX、bit2 VMX-outside-SMX（Intel SDM Vol.4）。
    [Fact]
    public void FEATURE_CONTROL_鎖定加VMX開_判為已鎖保護()
    {
        var d = PlatformSecurity.DecodeFeatureControl(0x5); // Lock + VMX outside SMX
        Assert.True(d.Lock);
        Assert.False(d.VmxInSmx);
        Assert.True(d.VmxOutsideSmx);
        Assert.Equal(ChipsetSecurityVerdict.Protected, d.Verdict);
    }

    [Fact]
    public void FEATURE_CONTROL_未鎖_判為未保護且VMX位元如實()
    {
        var d = PlatformSecurity.DecodeFeatureControl(0x4); // VMX outside 開但未鎖
        Assert.False(d.Lock);
        Assert.True(d.VmxOutsideSmx);
        Assert.Equal(ChipsetSecurityVerdict.Unprotected, d.Verdict);
    }

    // IA32_DEBUG_INTERFACE (0xC80)：bit0 ENABLE、bit30 LOCK、bit31 DEBUG_OCCURRED。
    [Fact]
    public void DEBUG_INTERFACE_啟用未鎖_判為對外開放()
    {
        var d = PlatformSecurity.DecodeDebugInterface(0x1);
        Assert.True(d.Enable);
        Assert.False(d.Lock);
        Assert.Equal(ChipsetSecurityVerdict.Unprotected, d.Verdict);
    }

    [Fact]
    public void DEBUG_INTERFACE_鎖定_判為保護()
    {
        var d = PlatformSecurity.DecodeDebugInterface(1UL << 30);
        Assert.True(d.Lock);
        Assert.False(d.Enable);
        Assert.Equal(ChipsetSecurityVerdict.Protected, d.Verdict);
    }

    [Fact]
    public void DEBUG_INTERFACE_DEBUG_OCCURRED_如實保留鑑識線索()
    {
        var d = PlatformSecurity.DecodeDebugInterface(1UL << 31);
        Assert.True(d.DebugOccurred);
        Assert.False(d.Enable);
        Assert.Equal(ChipsetSecurityVerdict.Protected, d.Verdict); // 未啟用不算開放
    }

    // ===== 服務層三態 =====

    [Fact]
    public void 服務_讀取器不可用_兩筆事實標權限不足()
    {
        var facts = PlatformSecurityMsrService.Collect(new FakeMsr(available: false, reason: "缺 ring0", value: null), At);
        Assert.Equal(2, facts.Count);
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability);
            Assert.Contains("缺 ring0", f.UnavailableReason);
        });
    }

    [Fact]
    public void 服務_MSR讀取失敗_標讀取失敗不補0()
    {
        var facts = PlatformSecurityMsrService.Collect(new FakeMsr(true, null, null), At);
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.Contains("未實作", f.UnavailableReason);
            Assert.Null(f.NumericValue);
        });
    }

    [Fact]
    public void 服務_讀到鎖定與除錯事件_文字如實呈現()
    {
        var reader = FakeMsr.From((0x3A, 0x5), (0xC80, 0x0));
        var facts = PlatformSecurityMsrService.Collect(reader, At);
        var fc = facts.Single(x => x.Key == "platform.feature_control");
        Assert.Equal(FactAvailability.Present, fc.Availability);
        Assert.Contains("已鎖定", fc.Value);
        Assert.Contains("VMX outside SMX=1", fc.Value);

        var di = facts.Single(x => x.Key == "platform.debug_interface");
        Assert.Contains("除錯埠未啟用", di.Value);
    }

    [Fact]
    public void 服務_全零FEATURE_CONTROL_明說未實作與未啟用無法區分()
    {
        var facts = PlatformSecurityMsrService.Collect(new FakeMsr(true, null, 0), At);
        Assert.Contains("無法區分", facts.Single(x => x.Key == "platform.feature_control").Value);
    }

    private sealed class FakeMsr(bool available, string? reason, ulong? value = null,
        IReadOnlyDictionary<uint, ulong?>? perMsr = null) : IKernelMsrReader
    {
        public bool Available => available;
        public string? UnavailableReason => reason;
        public ulong? ReadMsr(uint index) => perMsr is { Count: > 0 } ? perMsr[index] : value;

        public static FakeMsr From(params (uint Msr, ulong Value)[] values) =>
            new(true, null, perMsr: values.ToDictionary(v => v.Msr, v => (ulong?)v.Value));
    }
}
