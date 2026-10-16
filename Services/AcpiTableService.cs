using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>ACPI 表來源的可注入接縫：真實以 EnumSystemFirmwareTables + GetSystemFirmwareTable 取；測試注入合成表，不碰韌體。</summary>
public interface IAcpiTableSource
{
    bool Available { get; }
    string? UnavailableReason { get; }
    IReadOnlyList<byte[]> ReadAll();
}

/// <summary>把 ACPI 表列舉成三態事實：列不到就標不可用，列得到則逐表給簽章/版本/OEM/校驗和，另附一條清單摘要。</summary>
public static class AcpiService
{
    public static IReadOnlyList<HardwareFact> Collect(IAcpiTableSource source, DateTimeOffset at)
    {
        var facts = new List<HardwareFact>();
        if (!source.Available)
        {
            facts.Add(new HardwareFact("acpi.tables", "ACPI", "ACPI 表清單", "", "", "EnumSystemFirmwareTables",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.InsufficientPrivilege,
                source.UnavailableReason ?? "無法列舉 ACPI 表"));
            return facts;
        }

        var parsed = new List<(byte[] Bytes, AcpiTableHeader Header)>();
        foreach (var bytes in source.ReadAll())
            if (AcpiTable.TryParseHeader(bytes, out var h)) parsed.Add((bytes, h));

        var sigCount = parsed.GroupBy(p => Slug(p.Header.Signature)).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var occ = new Dictionary<string, int>(StringComparer.Ordinal);
        var checksumBad = new List<string>();
        // 逐簽章的表事實（P2-018 其餘表逐簽章解碼並標未支援）：鍵名依簽章小寫、
        // 同簽章多張時加序號。這是動態家族（acpi.table. 前綴已登記），不是固定全鍵。
        var decodeGaps = new List<string>();
        foreach (var (bytes, h) in parsed)
        {
            string slug = Slug(h.Signature);
            int n = occ.TryGetValue(slug, out int c) ? c + 1 : 1;
            occ[slug] = n;
            string key = sigCount[slug] > 1 ? $"acpi.table.{slug}.{n}" : $"acpi.table.{slug}";
            facts.Add(new HardwareFact(key, "ACPI", h.Signature,
                $"rev {h.Revision}，OEM {h.OemId}，校驗和 {(h.ChecksumValid ? "正確" : "錯誤")}", "",
                "GetSystemFirmwareTable", FactTrustLevel.Measured, false, at));
            if (!h.ChecksumValid) checksumBad.Add(sigCount[slug] > 1 ? $"{h.Signature}#{n}" : h.Signature);

            // 逐表記錄「本解碼器有沒有對準這張表的欄位」——有專用解碼器的走專用路，其餘如實標未支援。
            // 未支援不等於失敗，也不等於表有問題：它是「本版規格沒有這張表的欄位出處」。
            if (!AcpiDeep.HasDedicatedDecoder(h.Signature)) decodeGaps.Add(h.Signature);
        }

        // ── P2-001 逐表表頭校驗和（0x00 總和）：具名校單，不只給張數 ──────────
        facts.Add(new HardwareFact("ac.checksum.bad", "ACPI", "表頭校驗和不符的表",
            checksumBad.Count > 0
                ? $"{checksumBad.Count} 張：{string.Join("、", checksumBad)}"
                : "0 張（本機列舉到的表頭 0x00 總和全為 0）",
            "張", "GetSystemFirmwareTable＋表頭 0x00 總和（ACPI Spec 表頭 Checksum@9）",
            FactTrustLevel.Measured, false, at, checksumBad.Count));

        // ── P2-018 逐簽章解碼覆蓋申報：有解碼器／未收錄，不以 0 補 ────────────
        // ── 簽章清單：**從上面那一次列舉推得，不另外再讀一次** ──────────────
        //
        // 這一列存在的理由是「讓消費者不必再列舉一次」。既有的 acpi.table.*
        // 是動態家族（成員編譯期數不出來），所以需要「有沒有某張表」的消費者
        // （例如 EC 存在性看 ECDT、P2-093）只能自己想辦法拿清單——
        // 而在這把鍵之前，唯一的辦法是再呼叫一次 Collect，
        // 那會是**同一次掃描裡的第二次列舉**，兩次可能拿到不同的結果。
        var signaturesPresent = parsed.Select(p => p.Header.Signature)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        facts.Add(new HardwareFact("ac.tables.signatures", "ACPI", "ACPI 表簽章清單",
            signaturesPresent.Count == 0 ? "（沒有表）" : string.Join("、", signaturesPresent),
            "種", "ACPI 表列舉（與 acpi.table.* 同一次）", FactTrustLevel.Measured, false, at,
            signaturesPresent.Count,
            // **Present 的事實不得帶 unavailableReason**（HardwareSnapshotService 的不變式）。
            // 我第一版在這裡寫了理由，而**單元測試抓不到——是整合測試抓到的**：
            // 「可用的事實不得帶 unavailableReason」。
            //
            // 那個理由的內容沒有消失，它移到**這把鍵存在的理由**該在的地方：
            // 呼叫端（EmbeddedControllerFactsService）的註解裡，
            // 以及下面 ac.decode.unrecorded 那一列的鄰近說明。
            FactAvailability.Present, null));

        var unrecorded = decodeGaps.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();        facts.Add(new HardwareFact("ac.decode.unrecorded", "ACPI", "本版未逐欄解碼的表簽章",
            unrecorded.Count > 0
                ? $"{unrecorded.Count} 種簽章未收錄逐欄解碼（只出表頭事實）：{string.Join("、", unrecorded)}"
                : "（本機列舉到的表簽章皆有專用解碼器）",
            "種", "ACPI 表列×解碼器覆蓋申報", FactTrustLevel.Derived, false, at, unrecorded.Count));

        // ── P2-005 位元碼評估界線聲明：明示不評估 AML 位元碼 ──────────────────
        int dsdtSsdt = parsed.Count(p => p.Header.Signature is "DSDT" or "SSDT");
        facts.Add(new HardwareFact("ac.aml.boundary", "ACPI", "AML 位元碼評估界線",
            $"本專案不評估 AML 位元碼：_PSS／_CPC／_DSM 等 AML 物件不在任何輸出中。"
            + $"本機列舉到 {dsdtSsdt} 張 DSDT/SSDT，只做詞法字串掃描（ac.aml.str.*）與表頭 OEM 對照（ac.ssdt.third.*）；"
            + "反編譯為 ASL 亦不在範圍（那是原始碼不是事實，見階段二明文不做）",
            "", "專案界線宣告（無外部來源）", FactTrustLevel.Measured, false, at, dsdtSsdt));

        foreach (var (bytes, h) in parsed)
        {
            string slug = Slug(h.Signature);
            // 單一實例表才解的安全加值（只解已明確定義的單一欄位，不碰可變長度 body）。
            // n 由簽章出現次數判定：同簽章多張時語意未定（哪一張是「那張 HEST」？），故只解單一實例。
            bool single = occ.TryGetValue(slug, out int seen) && seen == 1;
            if (slug == "hest" && single && AcpiTable.HestErrorSourceCount(bytes) is { } sources)
                facts.Add(new HardwareFact("acpi.hest.sources", "ACPI", "硬體錯誤來源數",
                    sources.ToString(System.Globalization.CultureInfo.InvariantCulture), "", "ACPI HEST",
                    FactTrustLevel.Measured, false, at, sources));
            if (slug == "bert" && single && AcpiTable.BertBootErrorRegionLength(bytes) is { } regionLen)
            {
                facts.Add(new HardwareFact("acpi.bert.region_length", "ACPI", "開機錯誤區長度",
                    regionLen.ToString(System.Globalization.CultureInfo.InvariantCulture), "bytes", "ACPI BERT",
                    FactTrustLevel.Measured, false, at, regionLen));
                // 實際錯誤記錄在實體位址，usermode 讀不到——誠實標三態，不假裝沒有錯誤。
                facts.Add(new HardwareFact("acpi.bert.record", "ACPI", "上次開機錯誤記錄", "", "", "ACPI BERT + 實體記憶體",
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.InsufficientPrivilege,
                    "錯誤記錄在實體位址，需 ring0 讀取（Phase 3 MMIO）"));
            }
        }

        facts.Add(new HardwareFact("acpi.tables", "ACPI", "ACPI 表清單",
            string.Join(" ", parsed.Select(p => p.Header.Signature).OrderBy(x => x, StringComparer.Ordinal)), "",
            "EnumSystemFirmwareTables", FactTrustLevel.Measured, false, at));

        // ── Vol2 AC 域：ACPI 表層深化（10 項：AC-006/008/009/010/011/012/013/014/015/018）──
        // 全部只讀靜態欄位；需 AML 解譯器者（_PSS/_CPC/_DSM）刻意不在這一組，怕的是「解不出來卻有值」。
        byte[]? Find(string sig) => parsed.FirstOrDefault(p => p.Header.Signature == sig).Bytes;

        // AC-018 ACPI 表匯總：張數／簽章種類／校驗和錯誤數，逐表 sig@rev。
        // 校驗和錯誤數在 P2-001 的 ac.checksum.bad 已具名列出，這裡只給計數，兩者不衝突也不重複定義。
        facts.Add(new HardwareFact("ac.tables.summary", "ACPI", "ACPI 表匯總",
            $"{parsed.Count} 張表、{parsed.Select(p => p.Header.Signature).Distinct(StringComparer.Ordinal).Count()} 種簽章、校驗和錯誤 {checksumBad.Count} 張："
            + string.Join(" ", parsed.Select(p => $"{p.Header.Signature}@rev{p.Header.Revision}").OrderBy(x => x, StringComparer.Ordinal)),
            "", "EnumSystemFirmwareTables", FactTrustLevel.Measured, false, at, parsed.Count));

        // AC-006 DSDT/SSDT 可列印字串掃描（詞法掃描，非反編譯）。
        var amlStrings = new List<string>();
        foreach (var (bytes, h) in parsed)
            if (h.Signature is "DSDT" or "SSDT") amlStrings.AddRange(AcpiDeep.AmlStrings(bytes));
        var amlDistinct = amlStrings.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (amlDistinct.Count > 0)
        {
            facts.Add(new HardwareFact("ac.aml.str.count", "ACPI", "AML 可列印字串數",
                amlDistinct.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "條", "ACPI DSDT/SSDT 詞法掃描",
                FactTrustLevel.Measured, false, at, amlDistinct.Count));
            facts.Add(new HardwareFact("ac.aml.str.sample", "ACPI", "AML 字串樣本",
                string.Join(" | ", amlDistinct.Take(8)), "", "ACPI DSDT/SSDT 詞法掃描",
                FactTrustLevel.Measured, false, at));
        }
        else
        {
            facts.Add(Na("ac.aml.str.count", "AML 可列印字串數", "本機未列舉到 DSDT/SSDT 或其中無可列印字串", at));
            facts.Add(Na("ac.aml.str.sample", "AML 字串樣本", "本機未列舉到 DSDT/SSDT 或其中無可列印字串", at));
        }

        // AC-008 第三方 SSDT 來源對照（OEM ID 與主 DSDT 不同者；差異≠惡意）。
        var ssdtReport = AcpiDeep.SsdtSources(parsed.Select(p => p.Header).ToList());
        facts.Add(new HardwareFact("ac.ssdt.third.count", "ACPI", "非主廠商簽署的 SSDT 數",
            ssdtReport.ThirdPartyOemIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "張",
            "ACPI DSDT/SSDT 表頭 OEMID 對照", FactTrustLevel.Measured, false, at, ssdtReport.ThirdPartyOemIds.Count));
        facts.Add(new HardwareFact("ac.ssdt.third.list", "ACPI", "非主廠商 SSDT 清單",
            ssdtReport.ThirdPartyOemIds.Count > 0 ? string.Join(" | ", ssdtReport.ThirdPartyOemIds) : "（無）", "",
            "ACPI DSDT/SSDT 表頭 OEMID 對照（主 DSDT OEM ID：" + (ssdtReport.PlatformOemId.Length > 0 ? ssdtReport.PlatformOemId : "未提供") + "）",
            FactTrustLevel.Measured, false, at));

        // AC-009 NFIT（NVDIMM）：無此表＝N/A，如實說沒這張表，不是讀不到。
        if (Find("NFIT") is { } nfitBytes)
        {
            var structs = AcpiDeep.NfitStructures(nfitBytes);
            var spas = AcpiDeep.NfitSpaRanges(nfitBytes);
            var devs = AcpiDeep.NfitMemoryDevices(nfitBytes);
            facts.Add(new HardwareFact("ac.nfit.summary", "ACPI", "NFIT 摘要",
                $"{structs.Count} 個子結構（{string.Join("、", structs.GroupBy(s => s.Type).OrderBy(g => g.Key).Select(g => $"型別{g.Key}×{g.Count()}"))}），SPA 範圍 {spas.Count}、記憶體裝置 {devs.Count}",
                "", "ACPI NFIT", FactTrustLevel.Measured, false, at, structs.Count));
            facts.Add(new HardwareFact("ac.nfit.spa.count", "ACPI", "NFIT SPA 範圍數",
                spas.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "個", "ACPI NFIT",
                FactTrustLevel.Measured, false, at, spas.Count));
            facts.Add(new HardwareFact("ac.nfit.devices", "ACPI", "NFIT 記憶體裝置",
                devs.Count > 0
                    ? string.Join(" | ", devs.Select(d => $"handle {d.DeviceHandle} region {d.RegionId} 交錯×{d.InterleaveWays} {d.RegionSize} bytes"))
                    : "（無）",
                "", "ACPI NFIT", FactTrustLevel.Measured, false, at, devs.Count));
        }
        else
        {
            facts.Add(Na("ac.nfit.summary", "NFIT 摘要", "本機無 NFIT 表（非 NVDIMM 平台）", at));
            facts.Add(Na("ac.nfit.spa.count", "NFIT SPA 範圍數", "本機無 NFIT 表", at));
            facts.Add(Na("ac.nfit.devices", "NFIT 記憶體裝置", "本機無 NFIT 表", at));
        }

        // AC-010/011/012/013/014/015：單表存在性與靜態欄位。
        if (Find("MPAM") is { } mpam)
            facts.Add(new HardwareFact("ac.mpam", "ACPI", "MPAM 表", $"存在（OEM {SigText(mpam)}）", "", "ACPI MPAM", FactTrustLevel.Measured, false, at));
        else facts.Add(Na("ac.mpam", "MPAM 表", "本機未列舉到 MPAM 表", at));

        if (Find("BGRT") is { } bgrt && AcpiDeep.Bgrt(bgrt) is { } bgrtInfo)
            facts.Add(new HardwareFact("ac.bgrt", "ACPI", "BGRT 開機圖形表",
                $"版本 {bgrtInfo.Version}，開機 logo {(bgrtInfo.Displayed ? "已顯示" : "未顯示")}，影像型別 {bgrtInfo.ImageType}，位移 ({bgrtInfo.OffsetX},{bgrtInfo.OffsetY})",
                "", "ACPI BGRT", FactTrustLevel.Measured, false, at, bgrtInfo.Version));
        else facts.Add(Na("ac.bgrt", "BGRT 開機圖形表", "本機未列舉到 BGRT 表（或表短於欄位需求）", at));

        if (Find("SPCR") is { } spcr && AcpiDeep.Spcr(spcr) is { } spcrInfo)
            facts.Add(new HardwareFact("ac.spcr", "ACPI", "SPCR 序列主控台表",
                $"介面型別 {spcrInfo.InterfaceType}（0=完整 16550），位址空間 0x{spcrInfo.AddressSpaceId:x2}，基底 0x{spcrInfo.BaseAddress:x}",
                "", "ACPI SPCR", FactTrustLevel.Measured, false, at, spcrInfo.InterfaceType));
        else facts.Add(Na("ac.spcr", "SPCR 序列主控台表", "本機未列舉到 SPCR 表（或表短於欄位需求）", at));

        if (Find("DBG2") is { } dbg2 && AcpiDeep.Dbg2(dbg2) is { } dbg2Info)
        {
            facts.Add(new HardwareFact("ac.dbg2", "ACPI", "DBG2 除錯埠表", $"存在（子結構位移 0x{dbg2Info.InfoOffset:x}）", "", "ACPI DBG2", FactTrustLevel.Measured, false, at));
            facts.Add(new HardwareFact("ac.dbg2.devices", "ACPI", "DBG2 除錯裝置數",
                dbg2Info.InfoCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "個", "ACPI DBG2",
                FactTrustLevel.Measured, false, at, dbg2Info.InfoCount));
        }
        else
        {
            facts.Add(Na("ac.dbg2", "DBG2 除錯埠表", "本機未列舉到 DBG2 表", at));
            facts.Add(Na("ac.dbg2.devices", "DBG2 除錯裝置數", "本機未列舉到 DBG2 表", at));
        }

        if (Find("HPET") is { } hpet && AcpiDeep.Hpet(hpet) is { } hpetInfo)
        {
            facts.Add(new HardwareFact("ac.hpet.full.id", "ACPI", "HPET 硬體 ID", $"0x{hpetInfo.HardwareId:x8}", "", "ACPI HPET", FactTrustLevel.Measured, false, at, hpetInfo.HardwareId));
            facts.Add(new HardwareFact("ac.hpet.full.base", "ACPI", "HPET 基底位址", $"0x{hpetInfo.BaseAddress:x}（位址空間 0x{hpetInfo.AddressSpaceId:x2}）", "", "ACPI HPET", FactTrustLevel.Measured, false, at));
            facts.Add(new HardwareFact("ac.hpet.full.min_tick", "ACPI", "HPET 最小刻度", hpetInfo.MinimumTick.ToString(System.Globalization.CultureInfo.InvariantCulture), "ticks", "ACPI HPET", FactTrustLevel.Measured, false, at, hpetInfo.MinimumTick));
            facts.Add(new HardwareFact("ac.hpet.full.sequence", "ACPI", "HPET 序列號", hpetInfo.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), "", "ACPI HPET", FactTrustLevel.Measured, false, at, hpetInfo.Sequence));
        }
        else
        {
            facts.Add(Na("ac.hpet.full.id", "HPET 硬體 ID", "本機未列舉到 HPET 表（或表短於欄位需求）", at));
            facts.Add(Na("ac.hpet.full.base", "HPET 基底位址", "本機未列舉到 HPET 表", at));
            facts.Add(Na("ac.hpet.full.min_tick", "HPET 最小刻度", "本機未列舉到 HPET 表", at));
            facts.Add(Na("ac.hpet.full.sequence", "HPET 序列號", "本機未列舉到 HPET 表", at));
        }

        if (Find("MCFG") is { } mcfg)
        {
            var entries = AcpiTable.McfgEntries(mcfg);
            facts.Add(new HardwareFact("ac.mcfg.full.count", "ACPI", "MCFG 條目數",
                entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "條", "ACPI MCFG",
                FactTrustLevel.Measured, false, at, entries.Count));
            facts.Add(new HardwareFact("ac.mcfg.full.list", "ACPI", "MCFG 條目明細",
                entries.Count > 0
                    ? string.Join(" | ", entries.Select(e => $"seg {e.SegmentGroup} bus {e.StartBus}-{e.EndBus} @0x{e.Base:x}"))
                    : "（無條目）",
                "", "ACPI MCFG", FactTrustLevel.Measured, false, at));
        }
        else
        {
            facts.Add(Na("ac.mcfg.full.count", "MCFG 條目數", "本機未列舉到 MCFG 表", at));
            facts.Add(Na("ac.mcfg.full.list", "MCFG 條目明細", "本機未列舉到 MCFG 表", at));
        }

        return facts;
    }

    /// <summary>讀不到／不存在的事實一律三態，不假裝有值——值欄留空但原因必填。</summary>
    private static HardwareFact Na(string key, string name, string reason, DateTimeOffset at) =>
        new(key, "ACPI", name, "", "", "EnumSystemFirmwareTables", FactTrustLevel.Unknown, false, at, null,
            FactAvailability.NotApplicable, reason);

    private static string SigText(byte[] table) =>
        AcpiTable.TryParseHeader(table, out var h) ? (h.OemId.Length > 0 ? h.OemId : "未提供") : "未提供";

    private static string Slug(string signature)
    {
        string s = new(signature.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return s.Length == 0 ? "unknown" : s;
    }
}

/// <summary>以 Win32 firmware-table API 列舉並讀取所有 ACPI 表（usermode，不需驅動）。失敗即 Available=false 帶原因。</summary>
public sealed class Win32AcpiTableSource : IAcpiTableSource
{
    private const uint AcpiProvider = 0x41435049; // 'ACPI'（與 SecurityPostureService 一致）

    public bool Available { get; private set; } = true;
    public string? UnavailableReason { get; private set; }

    public IReadOnlyList<byte[]> ReadAll()
    {
        try
        {
            uint size = EnumSystemFirmwareTables(AcpiProvider, null, 0);
            if (size == 0)
            {
                Available = false;
                UnavailableReason = "EnumSystemFirmwareTables 回報 0 張 ACPI 表";
                return [];
            }
            var ids = new byte[size];
            EnumSystemFirmwareTables(AcpiProvider, ids, size);

            var tables = new List<byte[]>();
            for (int i = 0; i + 4 <= ids.Length; i += 4)
            {
                uint id = BitConverter.ToUInt32(ids, i);
                uint tsize = GetSystemFirmwareTable(AcpiProvider, id, null, 0);
                if (tsize == 0) continue;
                var buf = new byte[tsize];
                if (GetSystemFirmwareTable(AcpiProvider, id, buf, tsize) == tsize) tables.Add(buf);
            }
            return tables;
        }
        catch (Exception ex)
        {
            Available = false;
            UnavailableReason = ex.Message;
            return [];
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint EnumSystemFirmwareTables(uint provider, byte[]? buffer, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);
}
