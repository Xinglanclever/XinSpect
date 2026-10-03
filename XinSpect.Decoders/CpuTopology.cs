using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>CPUID 0x1F 單次呼叫的結果（EAX/EBX/ECX/EDX）。</summary>
public readonly record struct CpuIdResult(uint Eax, uint Ebx, uint Ecx, uint Edx);

/// <summary>一個拓撲層級：層級類型、APIC 位移、該層級的邏輯處理器數。</summary>
public sealed record TopologyLevel(byte LevelType, byte ApicShift, uint LogicalProcessors);

/// <summary>
/// WP20 die 拓撲的純解碼器：CPUID leaf 0x1F（Extended Topology Enumeration V2）逐子葉列舉。
/// 層級類型（EDX bits[15:8]）：1＝SMT、2＝Core、3＝Module、4＝Tile、5＝Die、6＝Package；
/// 類型 0＝列舉結束。leaf 0x1F 未支援（subleaf 0 類型即 0）就回空清單——
/// <b>不退回 leaf 0xB 猜 die</b>：0xB 沒有 die 層級，猜出來的「單 die」是編的。
/// </summary>
public static class CpuTopologyDecoder
{
    [SpecRef("Intel SDM Vol.3A, Table 3-94（CPUID leaf 0x1F, Extended Topology Enumeration）：EAX bits[4:0]＝x2APIC shift、EBX bits[15:0]＝該層級邏輯處理器數、EDX bits[15:8]＝Level Type（1 SMT/2 Core/3 Module/4 Tile/5 Die/6 Package、0 結束）")]
    public static IReadOnlyList<TopologyLevel> EnumerateLevels(Func<uint, CpuIdResult?> cpuId)
    {
        var levels = new List<TopologyLevel>();
        for (uint subleaf = 0; subleaf < 64; subleaf++)
        {
            if (cpuId(subleaf) is not { } r) break;
            byte levelType = (byte)((r.Edx >> 8) & 0xFF);
            if (levelType == 0) break;
            levels.Add(new TopologyLevel(levelType, (byte)(r.Eax & 0x1F), r.Ebx & 0xFFFF));
        }
        return levels;
    }

    /// <summary>生產讀取：CPUID leaf 0x1F。非 x86 回 null（如實三態）。</summary>
    [SpecRef("Intel SDM Vol.2, CPUID leaf 0x1F：subleaf ECX 遞增直至 Level Type＝0；讀取本身零特權、零副作用")]
    public static CpuIdResult? ReadCpId(uint subleaf)
        => X86Base.IsSupported ? ToResult(X86Base.CpuId(unchecked((int)0x1F), unchecked((int)subleaf))) : null;

    private static CpuIdResult? ToResult((int Eax, int Ebx, int Ecx, int Edx) r) =>
        new(unchecked((uint)r.Eax), unchecked((uint)r.Ebx), unchecked((uint)r.Ecx), unchecked((uint)r.Edx));
}
