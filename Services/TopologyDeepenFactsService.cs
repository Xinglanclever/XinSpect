namespace XinSpect;

/// <summary>WP20＋WP21 深化事實：die 拓撲（CPUID 0x1F）與 NUMA 節點距離（SLIT）。</summary>
public static class TopologyDeepenFactsService
{
    private const string Category = "處理器";
    private const string CategoryNuma = "系統與軟體";
    private const byte LevelTypeDie = 5, LevelTypeSmt = 1;

    /// <summary>WP20：die 拓撲。probe 回空清單＝平台未提供 0x1F（NotSupported，不退回 0xB 猜）。</summary>
    public static IReadOnlyList<HardwareFact> CollectCpuTopology(DateTimeOffset at,
        Func<IReadOnlyList<TopologyLevel>>? probe = null, Func<uint>? packageLogicalCpus = null)
    {
        const string source = "CPUID leaf 0x1F（Extended Topology Enumeration V2）";
        var levels = (probe ?? (() => CpuTopologyDecoder.EnumerateLevels(CpuTopologyDecoder.ReadCpId)))();
        if (levels.Count == 0)
            return [new HardwareFact("cpu.die.count", Category, "CPU die 數", "", "", source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "平台未提供 CPUID leaf 0x1F（V2 拓撲）——不退回 leaf 0xB 猜 die，讀不到就是不猜")];

        uint package = packageLogicalCpus?.Invoke() ?? levels[^1].LogicalProcessors;
        var facts = new List<HardwareFact>();
        var dieLevel = levels.FirstOrDefault(l => l.LevelType == LevelTypeDie);
        if (dieLevel is { } d && d.LogicalProcessors > 0 && package % d.LogicalProcessors == 0)
        {
            facts.Add(new HardwareFact("cpu.die.count", Category, "CPU die 數",
                $"{package / d.LogicalProcessors}（die 層級 {d.LogicalProcessors} 個邏輯處理器/package {package}）", "",
                source, FactTrustLevel.Measured, false, at, package / d.LogicalProcessors));
        }
        else
        {
            facts.Add(new HardwareFact("cpu.die.count", Category, "CPU die 數", "", "", source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "0x1F 沒有 die 層級（單 die 或未劃分）——如實標，不算「1 個 die」冒充"));
        }
        var smt = levels.FirstOrDefault(l => l.LevelType == LevelTypeSmt);
        if (smt is { } s && s.LogicalProcessors > 0)
            facts.Add(new HardwareFact("cpu.topology.smt", Category, "每核心執行緒數（SMT）",
                s.LogicalProcessors.ToString(), "個", source, FactTrustLevel.Measured, false, at, s.LogicalProcessors));
        return facts;
    }

    /// <summary>WP21：NUMA 節點距離（SLIT）。無表＝NotApplicable。</summary>
    public static IReadOnlyList<HardwareFact> CollectNumaDistance(DateTimeOffset at, IAcpiTableSource acpi)
    {
        byte[]? slit = null;
        if (acpi.Available)
        {
            foreach (var table in acpi.ReadAll())
            {
                if (AcpiTable.TryParseHeader(table, out var header) && header.Signature == "SLIT")
                {
                    slit = table;
                    break;
                }
            }
        }
        if (slit is null)
        {
            return [new HardwareFact("numa.slit.nodes", CategoryNuma, "NUMA 節點距離（SLIT）", "", "",
                "ACPI 表列（SLIT）", FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "表列裡沒有 SLIT：此平台沒有（或韌體未提供）節點距離資訊——單節點平台常見，不是錯誤")];
        }

        var matrix = SlitDecoder.DecodeMatrix(slit);
        if (matrix.Count == 0)
        {
            return [new HardwareFact("numa.slit.nodes", CategoryNuma, "NUMA 節點距離（SLIT）", "", "",
                "ACPI 表列（SLIT）", FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "SLIT 矩陣宣告與資料不符（截斷）——如實拒解")];
        }

        var facts = new List<HardwareFact>
        {
            new("numa.slit.nodes", CategoryNuma, "NUMA 節點距離（SLIT）", matrix.Count.ToString(), "個節點",
                "ACPI 表列（SLIT）", FactTrustLevel.Measured, false, at, matrix.Count),
        };
        for (uint a = 0; a < matrix.Count; a++)
        {
            for (uint b = a + 1; b < matrix.Count; b++) // 對角線 10 不列，只列上三角
                facts.Add(new HardwareFact($"numa.distance.{a}.{b}", CategoryNuma, $"節點 {a} ↔ {b} 距離",
                    matrix[(int)a][(int)b].ToString(), "", "ACPI 表列（SLIT）",
                    FactTrustLevel.Reported, false, at, matrix[(int)a][(int)b]));
        }
        return facts;
    }
}
