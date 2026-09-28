namespace XinSpect;

/// <summary>
/// 跑整機驗機並收攏成 <see cref="MachineVerdict"/>。
/// </summary>
/// <remarks>
/// <see cref="Build"/> 是純函式(引擎跑在已收集好的事實上),可用合成事實完整測試;
/// <see cref="FromLive"/> 是薄薄的活體轉接層——真正去碰 SMBIOS／電池／磁碟的地方,
/// 磁碟一律走 <see cref="StorageSmartService"/> 的看門狗,卡住的碟自動降成「讀不到」。
/// 兩者分開,是為了讓收攏邏輯測得動,而把碰硬體、會卡的部分隔在最外層。
/// </remarks>
public static class MachineVerdictService
{
    public const string MachineScope = "整機";

    /// <summary>純函式:整機事實跑一次 Machine 規則、每顆碟各跑一次 Disk 規則,收攏成報告。</summary>
    public static MachineVerdict Build(DateTime now, VerifyFacts machineFacts,
        IReadOnlyList<(string Scope, VerifyFacts Facts)> disks)
    {
        var lines = new List<VerdictLine>();
        foreach (var f in VerifyEngine.Run(machineFacts, VerifyScope.Machine))
            lines.Add(new VerdictLine(MachineScope, f));
        foreach (var (scope, facts) in disks)
            foreach (var f in VerifyEngine.Run(facts, VerifyScope.Disk))
                lines.Add(new VerdictLine(scope, f));
        return MachineVerdictBuilder.Build(now, lines);
    }

    /// <summary>活體:從執行中的檢視模型收集事實再建 verdict。碰硬體,不做單元測試。</summary>
    public static MachineVerdict FromLive(MainViewModel vm, DateTime now)
    {
        var machine = new List<VerifyFact>(SmbiosFacts.From(vm.Smbios.Structs, now));
        try
        {
            var b = new BatteryService().Read();
            if (b.Present) machine.AddRange(VerifyFactsCollector.Battery(b.DesignCapacity, b.FullCapacity, now));
        }
        catch (Exception ex) { Diag.Swallow("MachineVerdict.Battery", ex, "電池讀不到，略過電池規則"); }

        // ring0 逐核微碼(唯讀 MSR);橋接不可用就略過,規則自然判無法判定。
        try { machine.AddRange(CpuMsrFacts.Microcode(now)); }
        catch (Exception ex) { Diag.Swallow("MachineVerdict.Microcode", ex, "MSR 讀不到，略過微碼規則"); }

        // CPUID／拓撲對帳事實(品牌字串、虛擬層、快取、核心數、混合架構、矽晶推算基礎頻率)。
        try { machine.AddRange(CpuIdVerifyFacts.Collect(now)); }
        catch (Exception ex) { Diag.Swallow("MachineVerdict.CpuId", ex, "CPUID 事實讀不到，略過相關處理器規則"); }

        var disks = new List<(string, VerifyFacts)>();
        foreach (var d in vm.PhysicalDisks.OrderBy(x => x.Index))
        {
            var f = new List<VerifyFact>();
            if (d.Kind == DiskKind.NvmeSsd)
                f.AddRange(VerifyFactsCollector.Nvme(StorageSmartService.TryReadNvmeHealth(d.Index), now));
            else
            {
                var info = StorageSmartService.TryReadAtaIdentify(d.Index) is { } raw ? AtaIdentify.Decode(raw) : null;
                double? claimed = d.SizeBytes > 0 ? d.SizeBytes / 1_000_000_000.0 : null;
                f.AddRange(VerifyFactsCollector.Ata(info, claimed, StorageSmartService.TryReadAtaAttributes(d.Index), now));
            }
            if (f.Count > 0) disks.Add((DiskScope(d), new VerifyFacts(f)));
        }
        return Build(now, new VerifyFacts(machine), disks);
    }

    private static string DiskScope(PhysicalDiskInfo d)
        => d.Model is { Length: > 0 } m ? $"{PartName(d)} ・ {m}" : PartName(d);

    private static string PartName(PhysicalDiskInfo d) => d.Kind switch
    {
        DiskKind.NvmeSsd => "NVMe 固態硬碟",
        DiskKind.SataSsd => "SATA 固態硬碟",
        DiskKind.Hdd => "機械硬碟",
        _ => "儲存裝置",
    };
}
