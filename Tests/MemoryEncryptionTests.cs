using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 處理器深化的契約：記憶體加密狀態（TME＝CPUID 7 ECX bit25＋MSR 0x982；SGX＝CPUID 7 ECX bit30
/// ＋leaf 0x12 EPC 區域）與 package C-state 駐留（0x60D/0x3FC/0x3F9/0x3FA，µs）。
/// 平台不支援＝NotSupported、MSR 讀不到＝三態——與既有 CPU 事實同一套哲學。
/// </summary>
public class MemoryEncryptionTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TME啟用暫存器_解碼釘值()
    {
        // bits[3:0]＝啟用、bits[7:4]＝演算法（0 AES-XTS-128、1 AES-XTS-256）
        var on128 = XinSpect.MemoryEncryptionDecoder.DecodeTmeActivate(0x0000_0000_0000_0001);
        Assert.True(on128.Enabled);
        Assert.Contains("AES-XTS-128", on128.Algorithm);

        var off = XinSpect.MemoryEncryptionDecoder.DecodeTmeActivate(0);
        Assert.False(off.Enabled);

        var aes256 = XinSpect.MemoryEncryptionDecoder.DecodeTmeActivate(0x11);
        Assert.True(aes256.Enabled);
        Assert.Contains("AES-XTS-256", aes256.Algorithm);

        var unknown = XinSpect.MemoryEncryptionDecoder.DecodeTmeActivate(0x21);
        Assert.Contains("未收錄", unknown.Algorithm);
    }

    [Fact]
    public void 記憶體加密_TME支援但未啟用與MSR讀不到分清()
    {
        var facts = XinSpect.MemoryEncryptionFactsService.Collect(At,
            cpuidProbe: () => (0u, 0u, 1u << 25, 0u),   // ECX bit25＝TME 支援
            msr: new FakeEncMsr { Values = { [0x982] = 0x10 } });
        var tme = Assert.Single(facts, f => f.Key == "mem.encryption.tme");
        Assert.Equal(FactAvailability.Present, tme.Availability);
        Assert.Contains("支援", tme.Value);
        Assert.Contains("未啟用", tme.Value);

        var noMsr = XinSpect.MemoryEncryptionFactsService.Collect(At,
            cpuidProbe: () => (0u, 0u, 1u << 25, 0u), msr: new FakeEncMsr { Available = false });
        Assert.Equal(FactAvailability.InsufficientPrivilege,
            Assert.Single(noMsr, f => f.Key == "mem.encryption.tme").Availability);
    }

    [Fact]
    public void SGX_EPC區域解碼與無SGX標記()
    {
        // leaf 0x12 sub1：EAX bit0＝有效、base＝EBX:EAX、size＝EDX:ECX
        var epc = XinSpect.MemoryEncryptionDecoder.DecodeEpcRegion(
            eax: 0x1 | (0x7000_0000u << 0), ebx: 0, ecx: 0x0000_4000, edx: 0x0000_0001);
        Assert.True(epc.Valid);
        Assert.Equal(0x7000_0000ul, epc.Base);
        Assert.Equal(0x0000_0001_0000_4000ul, epc.Size);

        var facts = XinSpect.MemoryEncryptionFactsService.Collect(At,
            cpuidProbe: () => (0u, 0u, 1u << 30, 0u),   // ECX bit30＝SGX 支援
            msr: new FakeEncMsr());
        var sgx = Assert.Single(facts, f => f.Key == "mem.encryption.sgx");
        Assert.Contains("支援", sgx.Value);

        var none = XinSpect.MemoryEncryptionFactsService.Collect(At,
            cpuidProbe: () => (0u, 0u, 0u, 0u), msr: new FakeEncMsr());
        var noSgx = Assert.Single(none, f => f.Key == "mem.encryption.sgx");
        Assert.Equal(FactAvailability.NotSupported, noSgx.Availability);
    }

    [Fact]
    public void CState駐留_可讀帶微秒_不支援三態()
    {
        var facts = XinSpect.CStateResidencyFactsService.Collect(At,
            msr: new FakeEncMsr { Values = { [0x60D] = 123_456, [0x3F9] = 987_654 } });
        var c2 = Assert.Single(facts, f => f.Key == "cpu.pkg_c2_us");
        Assert.Equal(123456.0, c2.NumericValue);
        var c6 = Assert.Single(facts, f => f.Key == "cpu.pkg_c6_us");
        Assert.Equal(987654.0, c6.NumericValue);
        var c3 = Assert.Single(facts, f => f.Key == "cpu.pkg_c3_us");
        Assert.Equal(FactAvailability.NotSupported, c3.Availability);   // 假件沒給＝平台未實作

        var noRing0 = XinSpect.CStateResidencyFactsService.Collect(At, msr: new FakeEncMsr { Available = false });
        Assert.All(noRing0, f => Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability));
    }

    private sealed class FakeEncMsr : IKernelMsrReader
    {
        public bool Available { get; init; } = true;
        public string? UnavailableReason => Available ? null : "缺 ring0（假件）";
        public Dictionary<uint, ulong> Values { get; init; } = [];
        public ulong? ReadMsr(uint index) => Values.TryGetValue(index, out ulong v) ? v : null;
    }
}
