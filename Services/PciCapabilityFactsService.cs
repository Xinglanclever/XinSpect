namespace XinSpect;

/// <summary>
/// PCIe 能力深化的<b>事實</b>產出（Vol2 SB 域）：能力鏈盤點（SB-007）、SR-IOV（SB-008）、ACS（SB-009）、
/// L1 PM Substates（SB-010）、DPC（SB-011）、Virtual Channel（SB-012）、AER 完整欄位（SB-013）、
/// 裝置 revision 知識框架（SB-014）。
/// </summary>
/// <remarks>
/// <para><b>走 ECAM</b>：能力鏈<br/>① legacy（0x34 起）與 ② 擴充（0x100 起）都在 4KB 組態空間裡，
/// 擴充能力只有 ECAM/MMIO 讀得到——因此本服務與 <see cref="EcamAerService"/> 同一條路，
/// 讀不到就整段三態標示，不假裝掃過。</para>
/// <para><b>逐項明細收斂回彙總值</b>（本專案的家族上限政策）：每個能力出「有哪些裝置、值是什麼」的摘要，
/// 不為每顆裝置各開一把鍵。</para>
/// <para><b>誠實界線</b>：能力存在≠已啟用（Control 暫存器的啟用位另行陳述）；revision 知識表本版<b>無資料</b>，
/// 只列出實際讀到的 revision 並明說表為空（比照 CVE 離線表的做法）。</para>
/// </remarks>
public static class PciCapabilityFactsService
{
    private const string Category = "PCI 裝置";
    private const byte MaxFunctions = 64;

    /// <summary>讀一顆功能的 4KB 組態空間頁；讀不到回 null（缺席與讀不到由呼叫端分辨）。</summary>
    public delegate byte[]? PageReader(byte bus, byte device, byte function);

    public static IReadOnlyList<HardwareFact> Collect(IMmioReader mmio, IAcpiTableSource acpi, DateTimeOffset at,
        PageReader? pageReader = null)
    {
        var ecam = EcamBase(acpi, out string? ecamReason, out FactAvailability ecamAvailability);
        if (ecam is null)
            return Na(at, ecamReason ?? "無 ECAM 可用", ecamAvailability);

        if (pageReader is null && !mmio.Available)
            return Na(at, $"{mmio.UnavailableReason ?? "缺 MMIO 讀取"}；擴充能力只在 ECAM（>0xFF）讀得到", FactAvailability.InsufficientPrivilege);

        ulong baseAddr = ecam.Base;
        PageReader read = pageReader ?? ((bus, dev, fn) =>
        {
            ulong addr = baseAddr + ((ulong)bus << 20 | (ulong)dev << 15 | (ulong)fn << 12);
            return mmio.ReadBlock(addr, 4096);
        });

        var scanned = new List<(byte Dev, byte Fn, byte[] Page)>();
        int readErrors = 0;
        for (byte dev = 0; dev < 32 && scanned.Count < MaxFunctions; dev++)
        {
            var head = read(0, dev, 0);
            if (head is null) { readErrors++; continue; }
            if (BitConverter.ToUInt32(head, 0) == 0xFFFFFFFF) continue; // 空槽：ECAM 慣例全 F
            scanned.Add((dev, 0, head));

            bool multi = (BitConverter.ToUInt32(head, 0x0C) & (1u << 23)) != 0; // header type bit7
            if (!multi) continue;
            for (byte fn = 1; fn < 8 && scanned.Count < MaxFunctions; fn++)
            {
                var page = read(0, dev, fn);
                if (page is null) { readErrors++; continue; }
                if (BitConverter.ToUInt32(page, 0) == 0xFFFFFFFF) continue;
                scanned.Add((dev, fn, page));
            }
        }

        if (scanned.Count == 0)
            return Na(at, readErrors > 0
                ? $"bus 0 組態空間讀取失敗 {readErrors} 次——能力鏈不可達"
                : "bus 0 未發現任何裝置（ECAM 讀得到但全為空槽？）",
                readErrors > 0 ? FactAvailability.ReadError : FactAvailability.Unknown);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var inventories = new List<string>();
        var vfParts = new List<string>();
        var acsParts = new List<string>();
        var l1Parts = new List<string>();
        var dpcParts = new List<string>();
        var vcParts = new List<string>();
        var aerParts = new List<string>();
        var revisions = new List<string>();

        foreach (var (dev, fn, page) in scanned)
        {
            string where = $"0:{dev:X2}.{fn}";
            var caps = PciCapabilities.Enumerate(page);
            foreach (var c in caps) counts[PciCapabilities.Name(c.Id, c.Extended)] = counts.GetValueOrDefault(PciCapabilities.Name(c.Id, c.Extended)) + 1;
            inventories.Add($"{where}[{string.Join(",", caps.Select(c => PciCapabilities.Name(c.Id, c.Extended)))}]");

            revisions.Add($"{where}=rev0x{page[0x08]:X2}");

            if (caps.FirstOrDefault(c => c.Extended && c.Id == 0x10) is { Offset: > 0 } sriov && PciCapabilities.Sriov(page, sriov.Offset) is { } vf)
                vfParts.Add($"{where}：InitialVFs {vf.InitialVfs}／TotalVFs {vf.TotalVfs}／NumVFs {vf.NumVfs}／VF offset {vf.VfOffset}、stride {vf.VfStride}");
            if (caps.FirstOrDefault(c => c.Extended && c.Id == 0x0D) is { Offset: > 0 } acs)
                acsParts.Add($"{where}：{PciCapabilities.AcsFlags(page, acs.Offset)}");
            if (caps.FirstOrDefault(c => c.Extended && c.Id == 0x1E) is { Offset: > 0 } l1 && PciCapabilities.L1Pm(page, l1.Offset) is { } l1ss)
                l1Parts.Add($"{where}：cap 0x{l1ss.Capabilities:X8}（PCI-PM L1.2 {(l1ss.Capabilities & 1) != 0}、ASPM L1.1 {(l1ss.Capabilities & 8) != 0}）");
            if (caps.FirstOrDefault(c => c.Extended && c.Id == 0x1D) is { Offset: > 0 } dpc && PciCapabilities.Dpc(page, dpc.Offset) is { } dp)
                dpcParts.Add($"{where}：能力暫存器 0x{dp.Capabilities:X4}（位元語意未收錄，只報原值）");
            if (caps.FirstOrDefault(c => c.Extended && c.Id == 0x02) is { Offset: > 0 } vc && PciCapabilities.VirtualChannel(page, vc.Offset) is { } vci)
                vcParts.Add($"{where}：Port VC Cap1 0x{vci.PortVcCapability1:X8}、Cap2 0x{vci.PortVcCapability2:X8}");
            if (caps.FirstOrDefault(c => c.Extended && c.Id == 0x01) is { Offset: > 0 } aer && PciCapabilities.AerFullDecode(page, aer.Offset) is { } af)
                aerParts.Add($"{where}：不可更正狀態 0x{af.UncorrectableStatus:X8}／遮罩 0x{af.UncorrectableMask:X8}／嚴重度 0x{af.UncorrectableSeverity:X8}；可更正狀態 0x{af.CorrectableStatus:X8}／遮罩 0x{af.CorrectableMask:X8}");

            // MSI-X 表大小（legacy 能力 0x11）：附在能力摘要後面，讓「有幾條中斷向量」也看得到
            if (caps.FirstOrDefault(c => !c.Extended && c.Id == 0x11) is { Offset: > 0 } msix && PciCapabilities.MsixTableSize(page, msix.Offset) is { } msixSize)
                inventories[^1] += $"（MSI-X 表 {msixSize} 條）";
        }

        var topCaps = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(12).Select(kv => $"{kv.Key}×{kv.Value}");

        return
        [
            new HardwareFact("sb.pci.cap.count", Category, "能力鏈盤點的功能數",
                scanned.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "個功能",
                "ECAM 4KB 組態空間（legacy 0x34 鏈＋擴充 0x100 鏈）", FactTrustLevel.Measured, false, at, scanned.Count),
            new HardwareFact("sb.pci.cap.summary", Category, "能力鏈盤點",
                $"bus 0 掃 {scanned.Count} 個功能：{string.Join("、", topCaps)}"
                + (readErrors > 0 ? $"；{readErrors} 次讀取失敗（如實缺席）" : "")
                + "。" + string.Join("；", inventories.Take(10)),
                "", "ECAM；能力名稱核對 Linux pci_regs.h", FactTrustLevel.Measured, false, at),
            Summarize("sb.pci.vf.summary", "SR-IOV 虛擬功能", vfParts, "本機 bus 0 無帶 SR-IOV 能力的裝置（虛擬化功能未提供）", at),
            Summarize("sb.pci.acs.summary", "ACS 存取控制", acsParts, "本機 bus 0 無帶 ACS 能力的裝置（多為根埠與交換器才有）", at),
            Summarize("sb.pci.l1sub.summary", "L1 PM Substates", l1Parts, "本機 bus 0 無帶 L1 PM Substates 的裝置", at),
            Summarize("sb.pci.dpc.summary", "DPC 下游埠遏制", dpcParts, "本機 bus 0 無帶 DPC 能力的裝置（多為下游埠才有）", at),
            Summarize("sb.pci.vc.summary", "Virtual Channel", vcParts, "本機 bus 0 無帶 Virtual Channel 能力的裝置", at),
            Summarize("sb.pcieaer.full.summary", "AER 完整欄位", aerParts, "本機 bus 0 無帶 AER 能力的裝置", at),
            new HardwareFact("sb.pci.rev.known", Category, "裝置 revision 與已知修正",
                $"本版僅列出實讀 revision，未附已知修正對照表（知識表快照日期：未附——表為空，不是沒有問題）："
                + string.Join("、", revisions.Take(20)),
                "", "PCI 組態空間 0x08；知識表框架比照知識包（缺快照日期即拒收）", FactTrustLevel.Measured, false, at),
        ];
    }

    /// <summary>有值就報值、沒值就明說這台沒有——兩種都是事實，不混用同一句話。</summary>
    private static HardwareFact Summarize(string key, string name, IReadOnlyList<string> parts, string absentReason, DateTimeOffset at)
        => parts.Count > 0
            ? new HardwareFact(key, Category, name, $"{parts.Count} 個裝置：{string.Join("；", parts)}", "",
                "ECAM 擴充能力；位移核對 Linux pci_regs.h", FactTrustLevel.Measured, false, at, parts.Count)
            : new HardwareFact(key, Category, name, "", "", "ECAM 擴充能力", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable, absentReason);

    private static McfgEntry? EcamBase(IAcpiTableSource acpi, out string? reason, out FactAvailability availability)
    {
        reason = null;
        availability = FactAvailability.InsufficientPrivilege;
        if (!acpi.Available)
        {
            reason = acpi.UnavailableReason ?? "無法列舉 ACPI 表";
            return null;
        }
        foreach (var t in acpi.ReadAll())
            foreach (var e in AcpiTable.McfgEntries(t))
                if (e.SegmentGroup == 0 && e.Base != 0) return e;
        reason = "平台未提供 MCFG（segment 0）——無 ECAM 可循";
        availability = FactAvailability.NotApplicable;
        return null;
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason, FactAvailability availability)
    {
        string[] keys =
        [
            "sb.pci.cap.count", "sb.pci.cap.summary", "sb.pci.vf.summary", "sb.pci.acs.summary",
            "sb.pci.l1sub.summary", "sb.pci.dpc.summary", "sb.pci.vc.summary",
            "sb.pcieaer.full.summary", "sb.pci.rev.known",
        ];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", "ECAM", FactTrustLevel.Unknown, false, at,
            null, availability, reason)).ToList();
    }
}
