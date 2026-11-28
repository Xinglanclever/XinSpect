using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>OS topology/affinity and CPUID only; MSRs remain behind the already-open read lease.</summary>
internal sealed class CpuPackagePowerDiagnosticRuntime : ICpuPackagePowerDiagnosticRuntime
{
    public long Timestamp => Stopwatch.GetTimestamp();
    public long Frequency => Stopwatch.Frequency;
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint returnedLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorNumber { public ushort Group; public byte Number; public byte Reserved; }

    [DllImport("kernel32.dll")]
    private static extern void GetCurrentProcessorNumberEx(out ProcessorNumber number);

    [SpecRef("Microsoft Win32 GetLogicalProcessorInformationEx(RelationProcessorPackage=3), SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX, PROCESSOR_RELATIONSHIP.GroupMask.")]
    public IReadOnlyList<IReadOnlyList<ProcessorRef>> ReadPackages()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return [];
        uint length = 0;
        GetLogicalProcessorInformationEx(3, 0, ref length);
        if (!WindowsTopologyBuffer.IsWithinBudget(length) || Marshal.GetLastWin32Error() != 122) return [];
        uint capacity = length;
        nint buffer = Marshal.AllocHGlobal((int)capacity);
        try
        {
            if (!GetLogicalProcessorInformationEx(3, buffer, ref length) || length > capacity) return [];
            var bytes = new byte[(int)length];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return ParsePackages(bytes);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [SpecRef("Microsoft PROCESSOR_RELATIONSHIP (RelationProcessorPackage), GROUP_AFFINITY; package records may span groups; groups are not package identities.")]
    internal static IReadOnlyList<IReadOnlyList<ProcessorRef>> ParsePackages(byte[] bytes)
    {
        var records = WindowsTopologyBuffer.Parse(bytes);
        if (records is null || records.Count == 0 || records.Any(r => r.Relationship != 3)) return [];
        var seen = new HashSet<ProcessorRef>();
        var packages = new List<IReadOnlyList<ProcessorRef>>();
        foreach (var record in records)
        {
            var members = new List<ProcessorRef>();
            foreach (var (mask, group) in record.Affinities)
                foreach (int index in CpuAffinity.IndicesFromMask(mask))
                {
                    var processor = new ProcessorRef(group, index);
                    if (!seen.Add(processor)) return []; // Ambiguous overlapping package membership.
                    members.Add(processor);
                }
            if (members.Count == 0) return [];
            packages.Add(members.OrderBy(p => p.Group).ThenBy(p => p.Index).ToArray());
        }
        return packages;
    }

    public ICpuPackagePowerDiagnosticPin Pin(ProcessorRef processor) => new PinLease(CpuAffinity.Pinned(processor));
    private sealed class PinLease(CpuAffinity.Pin pin) : ICpuPackagePowerDiagnosticPin
    {
        public bool Ok => pin.Ok;
        public void Dispose() => pin.Dispose();
    }

    [SpecRef("Intel SDM Vol.2 CPUID leaves0,1,0BH: vendor, family/model/stepping, topology EAX4:0 shift and EDX x2APIC ID. Microsoft TLFS Feature Discovery: CPUID.1.ECX31 hypervisor present.")]
    public CpuPackagePowerDiagnosticPlatform IdentifyPinned(ProcessorRef processor, int packageCount)
    {
        const string topology = "GetLogicalProcessorInformationEx(RelationProcessorPackage), pinned CPUID.0BH";
        if (!X86Base.IsSupported || !OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return new("unsupported", 0, 0, 0, false, null, packageCount, processor, topology);
        var (maximum, vendorB, vendorC, vendorD) = X86Base.CpuId(0, 0);
        string vendor = string.Concat(Word(vendorB), Word(vendorD), Word(vendorC));
        if ((uint)maximum < 1) return new(vendor, 0, 0, 0, false, null, packageCount, processor, topology);
        var (signature, _, ecx1, _) = X86Base.CpuId(1, 0);
        int baseFamily = (signature >> 8) & 0xF;
        int family = baseFamily == 0xF ? baseFamily + ((signature >> 20) & 0xFF) : baseFamily;
        int model = (signature >> 4) & 0xF;
        if (baseFamily is 6 or 0xF) model |= ((signature >> 16) & 0xF) << 4;
        uint? package = null;
        if ((uint)maximum >= 0xB)
        {
            int highestShift = -1;
            uint? x2Apic = null;
            bool terminated = false;
            bool coreLevel = false;
            for (int level = 0; level < 32; level++)
            {
                var (a, b, c, d) = X86Base.CpuId(0xB, level);
                int type = (c >> 8) & 0xFF;
                if ((b & 0xFFFF) == 0 || type == 0) { terminated = true; break; }
                int shift = a & 0x1F;
                if (shift < highestShift || (x2Apic is uint observedId && observedId != (uint)d)) break;
                highestShift = shift; x2Apic = (uint)d; coreLevel |= type == 2;
            }
            if (terminated && coreLevel && highestShift >= 0 && x2Apic is uint packageApicId)
                package = packageApicId >> highestShift;
        }
        return new(vendor, family, model, signature & 0xF, (ecx1 & unchecked((int)0x80000000)) != 0,
            package, packageCount, processor, topology);
    }

    private static string Word(int word) => new string(new[] { (char)(word & 0xFF), (char)((word >> 8) & 0xFF),
        (char)((word >> 16) & 0xFF), (char)((word >> 24) & 0xFF) });

    public bool IsOnProcessor(ProcessorRef processor)
    {
        GetCurrentProcessorNumberEx(out var current);
        return current.Group == processor.Group && current.Number == processor.Index;
    }

    public void Wait(int milliseconds, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(milliseconds)) cancellationToken.ThrowIfCancellationRequested();
    }
}
