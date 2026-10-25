using Xunit;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect.Tests;

/// <summary>
/// SGX EPC 逐區列舉（U10-CP-0148）的守門：注入 cpuidProbe（leaf 7）與 cpuid12Probe（leaf 0x12 子葉），
/// 驗證列舉、終止、零區、不支援四種行為。解碼器本身的契約已在 <c>Tests/MemoryEncryptionTests.cs</c>
/// 那邊釘住，這一檔只測<b>列舉層</b>——兩份不重複。
/// </summary>
public class MemoryEncryptionEpcTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 11, 0, 0, TimeSpan.Zero);

    private static (uint, uint, uint, uint) Leaf7SgxOn => (0, 0, 1u << 30, 0);
    private static (uint, uint, uint, uint) Leaf7SgxOff => (0, 0, 0, 0);

    /// <summary>造一個有效 EPC 區的暫存器組（base＝ebx:eax、size＝edx:ecx）。</summary>
    private static (uint, uint, uint, uint) Epc(ulong start, ulong size)
        => ((uint)start | 1u, (uint)(start >> 32), (uint)size, (uint)(size >> 32));

    private static Func<uint, (uint, uint, uint, uint)> Probe(
        params (uint Sub, uint Eax, uint Ebx, uint Ecx, uint Edx)[] entries)
        => sub =>
        {
            var hit = entries.FirstOrDefault(e => e.Sub == sub);
            return hit.Sub == sub ? (hit.Eax, hit.Ebx, hit.Ecx, hit.Edx) : (0, 0, 0, 0);
        };

    [Fact]
    public void 兩個EPC區要逐區成鍵且彙總寫計數()
    {
        var facts = MemoryEncryptionFactsService.Collect(At,
            cpuidProbe: () => Leaf7SgxOn,
            cpuid12Probe: Probe(
                (2, 1, 0, 128 << 20, 0),          // 128 MiB
                (3, 1, 0, 64 << 20, 0),           // 64 MiB
                (4, 0, 0, 0, 0)));                 // 終止

        var sgx = facts.Single(f => f.Key == "mem.encryption.sgx");
        Assert.Equal(FactAvailability.Present, sgx.Availability);
        Assert.Equal(2, sgx.NumericValue);
        Assert.Contains("2 區", sgx.Value);

        var epc0 = facts.Single(f => f.Key == "mem.encryption.sgx.epc.0");
        Assert.Equal(FactAvailability.Present, epc0.Availability);
        Assert.Contains("Base 0x0・Size 128.0 MiB", epc0.Value);
        Assert.Contains("MiB", epc0.Value);

        var epc1 = facts.Single(f => f.Key == "mem.encryption.sgx.epc.1");
        Assert.Equal(64 << 20, epc1.NumericValue);
    }

    [Fact]
    public void 第一個子葉就無效要零區並說明兩因()
    {
        var facts = MemoryEncryptionFactsService.Collect(At,
            cpuidProbe: () => Leaf7SgxOn,
            cpuid12Probe: Probe((2, 0, 0, 0, 0)));

        var sgx = facts.Single(f => f.Key == "mem.encryption.sgx");
        Assert.Equal(0, sgx.NumericValue);
        Assert.Contains("0 區", sgx.Value);
        Assert.Contains("兩種可能", sgx.Value);
        Assert.DoesNotContain(facts, f => f.Key.StartsWith("mem.encryption.sgx.epc."));
    }

    [Fact]
    public void SGX不支援時不產EPC鍵()
    {
        var facts = MemoryEncryptionFactsService.Collect(At,
            cpuidProbe: () => Leaf7SgxOff,
            cpuid12Probe: Probe((2, 1, 0, 128 << 20, 0)));  // 探針有值也不該走

        var sgx = facts.Single(f => f.Key == "mem.encryption.sgx");
        Assert.Equal(FactAvailability.NotSupported, sgx.Availability);
        Assert.DoesNotContain(facts, f => f.Key.StartsWith("mem.encryption.sgx.epc."));
    }

    [Fact]
    public void 列舉上限32區_超出不無限迴圈()
    {
        var entries = new List<(uint, uint, uint, uint, uint)>();
        for (uint i = 2; i < 2 + 40; i++)
            entries.Add((i, 1, 0, 4096, 0));
        var facts = MemoryEncryptionFactsService.Collect(At,
            cpuidProbe: () => Leaf7SgxOn,
            cpuid12Probe: Probe(entries.ToArray()));

        var epcFacts = facts.Where(f => f.Key.StartsWith("mem.encryption.sgx.epc.")).ToList();
        Assert.Equal(32, epcFacts.Count);
        Assert.Equal(32, facts.Single(f => f.Key == "mem.encryption.sgx").NumericValue);
    }
}
