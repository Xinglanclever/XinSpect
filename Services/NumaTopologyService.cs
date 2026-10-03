using System.Runtime.InteropServices;
using System.Numerics;

namespace XinSpect;

/// <summary>
/// NUMA 拓撲事實（V7 WP21／A5 的 kernel-truth 層）：以 GetNumaHighestNodeNumber＋
/// GetNumaNodeProcessorMaskEx（kernel32，usermode 唯讀）列出節點數與每節點邏輯處理器遮罩。
/// 刻意不走 GetLogicalProcessorInformationEx 的變長結構解析（欄位偏移隨 Windows 版本演進，
/// 解析錯位的代價是把遮罩當事實）——本層 API 每節點一問，答案直接可稽核。
/// </summary>
public static class NumaTopologyService
{
    private const string Category = "處理器";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<uint?>? highestNodeProbe = null,
        Func<ushort, (ulong Mask, ushort Group)?>? nodeMaskProbe = null)
    {
        const string key = "numa.topology", name = "NUMA 拓撲", source = "GetNumaHighestNodeNumber／GetNumaNodeProcessorMaskEx";
        var highest = (highestNodeProbe ?? NativeHighestNode)();
        if (highest is null)
            return [Unavailable(key, name, source, at, FactAvailability.NotSupported, "NUMA API 不可用（非 Windows 或呼叫失敗）")];

        uint nodes = highest.Value + 1;
        var facts = new List<HardwareFact>
        {
            new(key, Category, name,
                nodes == 1
                    ? "單 NUMA 節點（桌面平台常態）；節點遮罩見 numa.node.0"
                    : $"{nodes} 個 NUMA 節點——多節點平台的記憶體親和性會影響延遲類量測的解讀",
                "", source, FactTrustLevel.Measured, false, at, nodes),
        };

        var maskProbe = nodeMaskProbe ?? NativeNodeMask;
        for (ushort node = 0; node < nodes; node++)
        {
            var mask = maskProbe(node);
            if (mask is null)
            {
                facts.Add(new HardwareFact($"numa.node.{node}", Category, $"NUMA 節點 {node}", "", "",
                    "GetNumaNodeProcessorMaskEx", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.ReadError, "節點遮罩查詢失敗"));
                continue;
            }
            var (m, group) = mask.Value;
            int cpus = BitOperations.PopCount(m);
            facts.Add(new HardwareFact($"numa.node.{node}", Category, $"NUMA 節點 {node}",
                $"邏輯處理器遮罩 0x{m:X}（{cpus} 個邏輯 CPU、群組 {group}）",
                "", "GetNumaNodeProcessorMaskEx", FactTrustLevel.Measured, false, at, cpus));
        }
        return facts;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumaHighestNodeNumber(out uint highestNodeNumber);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumaNodeProcessorMaskEx(ushort node, out NativeGroupAffinity processorMask);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGroupAffinity
    {
        public nuint Mask;      // KAFFINITY（x64 8 bytes）
        public ushort Group;
        public ushort Flags;
        public uint R1, R2, R3; // Reserved[3]——必須占滿 24 bytes 的原生大小
    }

    private static uint? NativeHighestNode()
    {
        try { return GetNumaHighestNodeNumber(out uint v) ? v : null; }
        catch { return null; }
    }

    private static (ulong, ushort)? NativeNodeMask(ushort node)
    {
        try
        {
            if (!GetNumaNodeProcessorMaskEx(node, out var affinity)) return null;
            return (affinity.Mask, affinity.Group);
        }
        catch { return null; }
    }

    private static HardwareFact Unavailable(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
