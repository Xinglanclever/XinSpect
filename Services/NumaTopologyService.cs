using System.Runtime.InteropServices;
using System.Numerics;

namespace XinSpect;

public sealed record NumaProcessorGroup(ushort Group, ulong Mask);
public sealed record NumaTopologyNode(uint NodeId, IReadOnlyList<NumaProcessorGroup> Groups);
public sealed record NumaTopologySnapshot(FactAvailability Availability,
    IReadOnlyList<NumaTopologyNode> Nodes, string? Reason = null);

public sealed record NumaAvailableMemorySample(uint NodeId, DateTimeOffset MeasuredAtUtc,
    ulong? AvailableBytes, FactAvailability Availability, string? Reason = null);

/// <summary>OS NUMA node enumeration, retaining all reported groups and nodes without active processor masks.</summary>
public static class NumaTopologyService
{
    internal const int MaxEnumeratedNodes = 1024;
    internal const int MaxBufferBytes = 16 * 1024 * 1024;
    private const int RelationNumaNodeEx = 6;
    private const string Source = "GetLogicalProcessorInformationEx(RelationNumaNodeEx); Windows SDK 10.0.28000.0 NUMA_NODE_RELATIONSHIP";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<NumaTopologySnapshot>? snapshotProbe = null,
        Func<uint, NumaAvailableMemorySample>? availableMemoryProbe = null)
        => CollectCore(at, snapshotProbe, availableMemoryProbe, System.Diagnostics.Stopwatch.GetTimestamp,
            System.Diagnostics.Stopwatch.Frequency);

    internal static IReadOnlyList<HardwareFact> CollectCore(DateTimeOffset at,
        Func<NumaTopologySnapshot>? snapshotProbe, Func<uint, NumaAvailableMemorySample>? availableMemoryProbe,
        Func<long> ticks, long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        // Injected topology alone never opts tests/callers into native memory queries.
        Func<uint, NumaAvailableMemorySample>? memoryProbe = availableMemoryProbe;
        if (memoryProbe is null && snapshotProbe is null) memoryProbe = NativeAvailableMemory;
        var snapshot = (snapshotProbe ?? NativeSnapshot)();
        if (snapshot.Availability != FactAvailability.Present)
            return [Unavailable("numa.topology", "NUMA 拓撲", at, snapshot.Availability, snapshot.Reason ?? "NUMA enumeration unavailable"),
                Unavailable("numa.topology.xnode", "跨節點延遲／頻寬", at, snapshot.Availability, snapshot.Reason ?? "NUMA enumeration unavailable")];
        if (!Validate(snapshot.Nodes))
            return [Unavailable("numa.topology", "NUMA 拓撲", at, FactAvailability.ReadError, "Malformed or over-budget NUMA snapshot"),
                Unavailable("numa.topology.xnode", "跨節點延遲／頻寬", at, FactAvailability.Unknown, "NUMA node count unverified")];
        int count = snapshot.Nodes.Count;
        int processorNodes = snapshot.Nodes.Count(n => n.Groups.Any(g => g.Mask != 0));
        var facts = new List<HardwareFact>
        {
            new("numa.topology", "處理器", "NUMA 拓撲", count == 1
                ? "單 NUMA 節點——跨節點延遲與頻寬在本機無從驗證（未驗證不代表可用）"
                : $"{count} 個 NUMA 節點（{processorNodes} 個含邏輯處理器）；節點 ID 依作業系統列舉",
                "", Source, FactTrustLevel.Reported, false, at, count),
            new("numa.topology.xnode", "處理器", "跨節點延遲／頻寬",
                count == 1 ? "無從量測：本機只有一個節點，沒有第二個節點可比較"
                    : "多節點拓撲已列舉；跨節點記憶體可用性、延遲與頻寬尚未驗證",
                "", Source, FactTrustLevel.Unknown, false, at, null,
                count == 1 ? FactAvailability.NotApplicable : FactAvailability.Unknown,
                count == 1 ? "本機為單節點：未驗證不代表可用；沒量到不等於沒有問題。" : "節點列舉不構成跨節點量測能力證據")
        };
        long memoryStart = ticks();
        int memoryQueries = 0;
        foreach (var node in snapshot.Nodes.OrderBy(n => n.NodeId))
        {
            int cpus = node.Groups.Sum(g => BitOperations.PopCount(g.Mask));
            string masks = string.Join("；", node.Groups.Select(g => $"群組 {g.Group} 遮罩 0x{g.Mask:X}"));
            facts.Add(new($"numa.node.{node.NodeId}", "處理器", $"NUMA 節點 {node.NodeId}",
                cpus == 0 ? "作業系統未列舉作用中邏輯 CPU；是否為純記憶體節點未確認，記憶體容量未查詢" : $"{masks}（{cpus} 個邏輯 CPU）",
                "", Source, FactTrustLevel.Reported, false, at, cpus));
            NumaAvailableMemorySample sample;
            if (node.NodeId > ushort.MaxValue)
                sample = new(node.NodeId, at, null, FactAvailability.Unknown, "Node ID exceeds USHORT API identity; not truncated");
            else if (memoryProbe is null)
                sample = new(node.NodeId, at, null, FactAvailability.Unknown, "Available-memory source not supplied; no native query attempted");
            else if (memoryQueries >= MaxMemoryQueries || (ticks() - memoryStart) / (double)frequency >= MemoryDispatchBudget.TotalSeconds)
                sample = new(node.NodeId, at, null, FactAvailability.Unknown, "Available-memory query dispatch budget exhausted; no query attempted");
            else
            {
                memoryQueries++;
                try { sample = memoryProbe(node.NodeId); }
                catch (Exception error)
                { sample = new(node.NodeId, at, null, FactAvailability.ReadError, "Available-memory probe failed (" + error.GetType().Name + ")"); }
            }
            facts.Add(AvailableMemoryFact(node.NodeId, sample));
        }
        return facts;
    }

    internal const int MaxMemoryQueries = 64;
    internal static readonly TimeSpan MemoryDispatchBudget = TimeSpan.FromMilliseconds(250);
    private const string MemorySource = "GetNumaAvailableMemoryNodeEx (Windows); available bytes, not installed/total capacity";

    private static HardwareFact AvailableMemoryFact(uint expectedNode, NumaAvailableMemorySample sample)
    {
        bool identityMatches = sample.NodeId == expectedNode;
        var availability = identityMatches ? sample.Availability : FactAvailability.Unknown;
        string? reason = identityMatches ? sample.Reason : "Returned node identity differs from enumerated node; value rejected";
        ulong? bytes = identityMatches && availability == FactAvailability.Present ? sample.AvailableBytes : null;
        if (availability == FactAvailability.Present && bytes is null)
        { availability = FactAvailability.ReadError; reason = "Successful source omitted available bytes"; }
        return new($"numa.node.{expectedNode}.available_bytes", "處理器", $"NUMA 節點 {expectedNode} 可用記憶體",
            bytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", "bytes", MemorySource,
            bytes is null ? FactTrustLevel.Unknown : FactTrustLevel.Reported, false, sample.MeasuredAtUtc,
            bytes is { } value && value <= 9_007_199_254_740_992UL ? (double)value : null, availability,
            bytes is null ? reason ?? "Available-memory evidence unavailable" : null);
    }

    // Official Learn: available physical bytes, free+zeroed pages; standby treatment differs
    // between single-node and multi-node systems. This is neither total capacity nor a latency test.
    // SDK28000 um/WinBase.h GetNumaAvailableMemoryNodeEx: USHORT Node, PULONGLONG AvailableBytes.
    private static NumaAvailableMemorySample NativeAvailableMemory(uint node)
    {
        var sampledAt = DateTimeOffset.UtcNow;
        if (node > ushort.MaxValue) return new(node, sampledAt, null, FactAvailability.Unknown, "Node identity exceeds USHORT");
        try
        {
            bool success = GetNumaAvailableMemoryNodeEx((ushort)node, out ulong bytes);
            int error = success ? 0 : Marshal.GetLastWin32Error();
            sampledAt = DateTimeOffset.UtcNow;
            return success ? new(node, sampledAt, bytes, FactAvailability.Present)
                : new(node, sampledAt, null, error == 5 ? FactAvailability.InsufficientPrivilege
                    : error is 50 or 120 ? FactAvailability.NotSupported : FactAvailability.ReadError,
                    $"GetNumaAvailableMemoryNodeEx failed (Win32 {error})");
        }
        catch (EntryPointNotFoundException) { return new(node, sampledAt, null, FactAvailability.NotSupported, "NUMA available-memory API unavailable"); }
        catch (DllNotFoundException) { return new(node, sampledAt, null, FactAvailability.NotSupported, "Windows NUMA API unavailable"); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumaAvailableMemoryNodeEx(ushort node, out ulong availableBytes);

    private static bool Validate(IReadOnlyList<NumaTopologyNode> nodes)
    {
        if (nodes.Count is 0 or > MaxEnumeratedNodes || nodes.Select(n => n.NodeId).Distinct().Count() != nodes.Count) return false;
        var assigned = new Dictionary<ushort, ulong>();
        foreach (var node in nodes)
        {
            if (node.Groups.Count > 64 || node.Groups.Select(g => g.Group).Distinct().Count() != node.Groups.Count) return false;
            foreach (var group in node.Groups)
            {
                if ((assigned.GetValueOrDefault(group.Group) & group.Mask) != 0) return false;
                assigned[group.Group] = assigned.GetValueOrDefault(group.Group) | group.Mask;
            }
        }
        return true;
    }

    // SDK 10.0.28000.0 um/winnt.h: NUMA payload DWORD node + Reserved[18] + WORD count;
    // x64 GROUP_AFFINITY is 16 bytes. RelationNumaNodeEx requires the actual GroupCount.
    internal static NumaTopologySnapshot Parse(byte[] bytes)
    {
        var nodes = new List<NumaTopologyNode>();
        if (bytes.Length > MaxBufferBytes) return Failure("NUMA buffer budget exceeded", FactAvailability.Unknown);
        int offset = 0;
        while (offset < bytes.Length)
        {
            int remaining = bytes.Length - offset;
            if (remaining < 32) return Failure("Truncated NUMA record");
            int relationship = BitConverter.ToInt32(bytes, offset);
            int size = BitConverter.ToInt32(bytes, offset + 4);
            // RelationNumaNodeEx requests full group information; the response may
            // retain RelationNumaNode. Both enum values use NUMA_NODE_RELATIONSHIP.
            if (relationship is not (1 or RelationNumaNodeEx) || size < 32 || size > remaining) return Failure("Invalid NUMA record header");
            int count = BitConverter.ToUInt16(bytes, offset + 30);
            // Accept a zero-count empty trailing slot as a bounded shape, not proof
            // of memory capacity or of a verified native memory-only encoding.
            bool emptySlot = count == 0 && size == 48 && bytes.AsSpan(offset + 32, 16).IndexOfAnyExcept((byte)0) < 0;
            if (count > 64 || (size != 32 + count * 16 && !emptySlot)) return Failure("Invalid NUMA affinity count or record size");
            var groups = new List<NumaProcessorGroup>();
            for (int i = 0; i < count; i++)
            {
                int start = offset + 32 + i * 16;
                groups.Add(new(BitConverter.ToUInt16(bytes, start + 8), BitConverter.ToUInt64(bytes, start)));
            }
            nodes.Add(new(BitConverter.ToUInt32(bytes, offset + 8), groups));
            if (nodes.Count > MaxEnumeratedNodes) return Failure("NUMA node budget exceeded", FactAvailability.Unknown);
            offset += size;
        }
        return Validate(nodes) ? new(FactAvailability.Present, nodes) : Failure("Invalid NUMA identities or overlapping masks");
    }

    private static NumaTopologySnapshot Failure(string reason, FactAvailability availability = FactAvailability.ReadError) => new(availability, [], reason);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint returnedLength);

    private static NumaTopologySnapshot NativeSnapshot()
    {
        if (IntPtr.Size != 8) return Failure("NUMA parser requires x64", FactAvailability.NotSupported);
        try
        {
            uint length = 0;
            bool first = GetLogicalProcessorInformationEx(RelationNumaNodeEx, 0, ref length);
            int error = Marshal.GetLastWin32Error();
            if (first || error != 122) return NativeFailure(error);
            if (length < 32 || length > MaxBufferBytes) return Failure("NUMA native buffer budget exceeded", FactAvailability.Unknown);
            uint capacity = length;
            nint buffer = Marshal.AllocHGlobal((int)capacity);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationNumaNodeEx, buffer, ref length)) return NativeFailure(Marshal.GetLastWin32Error());
                if (length > capacity) return Failure("NUMA returned length exceeds allocation");
                var bytes = new byte[(int)length];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                return Parse(bytes);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (EntryPointNotFoundException) { return Failure("NUMA enumeration API unavailable", FactAvailability.NotSupported); }
        catch (DllNotFoundException) { return Failure("Windows NUMA API unavailable", FactAvailability.NotSupported); }
    }

    internal static NumaTopologySnapshot NativeFailure(int error) => Failure($"RelationNumaNodeEx failed (Win32 {error}); no legacy single-group fallback",
        error is 87 or 50 or 120 ? FactAvailability.NotSupported : FactAvailability.ReadError);

    private static HardwareFact Unavailable(string key, string name, DateTimeOffset at, FactAvailability availability, string reason) =>
        new(key, "處理器", name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
