using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// AMD 安全事實（R5）的契約：非 AMD 平台整組 NotApplicable；AMD 平台的 CPUID 0x8000001F
/// 支援位與 MSR 0xC0010131 啟用位（Linux 核心位元定義交叉核對）；PSP 以 PCI 類別碼 0x10800 偵測。
/// 本機是 Intel——AMD 路徑標「未在本機驗證」，以注入的探針測行為形狀。
/// </summary>
public class AmdSecurityFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    private static (uint Eax, uint Ebx, uint Ecx, uint Edx) IntelVendor(uint leaf) => leaf switch
    {
        0 => (0x1C, 0x756E6547, 0x6C65746E, 0x49656E69), // "GenuineIntel"
        _ => (0, 0, 0, 0),
    };

    private static (uint Eax, uint Ebx, uint Ecx, uint Edx) AmdVendor(uint leaf) => leaf switch
    {
        0 => (0x1C, 0x68747541, 0x444D4163, 0x69746E65), // "AuthenticAMD"（EBX→EDX→ECX）
        0x8000001F => (0x13, 0, 0, 0),                   // SME+SEV 支援（bit0、bit1）
        _ => (0, 0, 0, 0),
    };

    private sealed class FakeMsr(ulong? sevStatus, bool available = true) : IKernelMsrReader
    {
        public bool Available { get; } = available;
        public string? UnavailableReason => Available ? null : "假件：MSR 不可用";
        public ulong? ReadMsr(uint index) => index == 0xC0010131 ? sevStatus : null;
    }

    private sealed class FakePci : IPciConfigReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public uint? ReadDword(byte bus, byte device, byte function, uint register)
        {
            // 0:14.0 放一顆 AMD PSP（vendor 0x1022、device 0x1486、class 0x10800）
            if (bus == 0 && device == 0x14 && function == 0)
                return register switch
                {
                    0x00 => 0x14861022u,
                    0x08 => 0x01080006u,   // class code 0x010800 在 bits[31:8] → >>8 後＝0x010800
                    _ => 0,
                };
            return 0xFFFFFFFF;
        }
    }

    [Fact]
    public void 非AMD平台_整組NotApplicable()
    {
        var facts = AmdSecurityFactsService.Collect(At, IntelVendor);
        Assert.Equal(8, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.Contains(facts, f => f.Key == "amd.sme.supported");
        Assert.Contains(facts, f => f.Key == "amd.psp.present");
    }

    [Fact]
    public void AMD平台_支援位與MSR啟用位()
    {
        var msr = new FakeMsr(0b101); // bit0 記憶體加密啟用、bit2 SNP 啟用
        var facts = AmdSecurityFactsService.Collect(At, AmdVendor, msr, new FakePci());

        var sme = Assert.Single(facts, f => f.Key == "amd.sme.supported");
        Assert.Equal(FactAvailability.Present, sme.Availability);
        Assert.Single(facts, f => f.Key == "amd.sev.supported" && f.Availability == FactAvailability.Present);

        var sevEs = Assert.Single(facts, f => f.Key == "amd.sev_es.supported");
        Assert.Equal(FactAvailability.NotSupported, sevEs.Availability); // 0x13 只有 bit0/1

        var enc = Assert.Single(facts, f => f.Key == "amd.mem_enc.enabled");
        Assert.Equal("已啟用", enc.Value);
        Assert.Single(facts, f => f.Key == "amd.sev_es.enabled" && f.Value == "未啟用");
        Assert.Single(facts, f => f.Key == "amd.sev_snp.enabled" && f.Value == "已啟用");

        Assert.All(facts, f => Assert.True(
            f.Source.Contains("未在本機驗證") || f.Value.Contains("未在本機驗證")));
    }

    [Fact]
    public void AMD平台_缺MSR時三態()
    {
        var facts = AmdSecurityFactsService.Collect(At, AmdVendor, new FakeMsr(0, available: false));
        Assert.All(facts.Where(f => f.Key.EndsWith("enabled")), f =>
            Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability));
        // CPUID 支援位不需要驅動：支援的 Present、不支援的 NotSupported——都不是三態
        Assert.All(facts.Where(f => f.Key.EndsWith("supported")), f =>
            Assert.Contains(f.Availability, new[] { FactAvailability.Present, FactAvailability.NotSupported }));
    }

    [Fact]
    public void PSP以PCI類別碼偵測_掃到時報裝置ID()
    {
        var facts = AmdSecurityFactsService.Collect(At, AmdVendor, new FakeMsr(0), new FakePci());
        var psp = Assert.Single(facts, f => f.Key == "amd.psp.present");
        Assert.Equal(FactAvailability.Present, psp.Availability);
        Assert.Contains("14:00", psp.Value);
        Assert.Contains("1486", psp.Value);
    }
}
