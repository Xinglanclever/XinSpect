using System.Globalization;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>以原生 SPD 為主證據，將 CPU-Z、SMBIOS 與全機目前時序做保守的一致性稽核。</summary>
public static class SpdConsistencyAuditService
{
    private static readonly HashSet<string> GenericValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "—", "-", "unknown", "not specified", "not available", "not applicable", "n/a", "na",
        "none", "null", "default string", "system manufacturer", "to be filled by o.e.m.",
        "to be filled by oem", "manufacturer", "part number", "serial number", "00000000",
        "0000000000000000", "ffffffff", "ffffffffffffffff",
    };

    public static SpdAuditSourceInput FromNative(SpdDirectRead read, string? locator = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        var s = read.Decoded;
        return new SpdAuditSourceInput(
            SpdAuditSourceKind.NativeSpd,
            $"原生 SPD ・ {read.Bus} ・ 0x{read.Address:X2}",
            locator,
            s.ModuleManufacturer.Name,
            s.PartNumber,
            s.SerialHex,
            Positive(s.Geometry.CapacityMib),
            Positive(s.Timings.MaxJedecDataRate),
            s.ManufactureYear,
            s.ManufactureWeek,
            s.BaseCrc.Valid,
            s.ModuleCrc.Valid,
            s.ModuleManufacturer.ParityOk,
            $"基本段：存 0x{s.BaseCrc.Stored:X4}／算 0x{s.BaseCrc.Computed:X4}；模組段：存 0x{s.ModuleCrc.Stored:X4}／算 0x{s.ModuleCrc.Computed:X4}");
    }

    public static SpdAuditSourceInput FromCpuZ(SpdModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        ParseManufactureDate(module.ManufacturingDate, out int? year, out int? week);
        return new SpdAuditSourceInput(
            SpdAuditSourceKind.CpuZ,
            NonGeneric(module.Source) ?? "CPU-Z 報告",
            module.Slot,
            module.Manufacturer,
            module.PartNumber,
            CapacityMiB: ParseCapacityMiB(module.Size),
            SpeedMTs: ParseSpeedMTs(module.MaxJedec) ?? ParseSpeedMTs(module.MaxBandwidth),
            ManufactureYear: year,
            ManufactureWeek: week);
    }

    public static SpdAuditSourceInput FromSmbios(SmbiosDimmRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new SpdAuditSourceInput(
            SpdAuditSourceKind.Smbios,
            "SMBIOS Type 17",
            JoinLocator(row.Locator, row.Bank),
            row.Manufacturer,
            row.Part,
            row.Serial,
            ParseCapacityMiB(row.Size),
            ParseSpeedMTs(row.Speed));
    }

    public static CurrentMemoryTimingEvidence? FromCurrentTimings(MemoryTimings? timings)
    {
        if (timings is null || !timings.Loaded) return null;
        int? rate = ParseSpeedMTs(timings.DataRateText);
        if (rate is null && timings.DramFrequencyMHz > 0)
            rate = (int)Math.Round(timings.DramFrequencyMHz * 2 / 10.0) * 10;
        return new CurrentMemoryTimingEvidence(
            "CPU-Z 目前時序（全機）",
            NonGeneric(timings.MemoryTypeText),
            rate,
            NonGeneric(timings.PrimaryTimingsText));
    }

    public static IReadOnlyList<SpdSlotAudit> Audit(SpdAuditInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var native = input.NativeSpd.Where(x => x.Kind == SpdAuditSourceKind.NativeSpd).ToList();
        var cpuz = input.CpuZ.Where(x => x.Kind == SpdAuditSourceKind.CpuZ).ToList();
        var smbios = input.Smbios.Where(x => x.Kind == SpdAuditSourceKind.Smbios).ToList();
        var cpuzMatches = Match(native, cpuz);
        var smbiosMatches = Match(native, smbios);
        var results = new List<SpdSlotAudit>(native.Count);

        for (int i = 0; i < native.Count; i++)
        {
            var primary = native[i];
            SpdAuditSourceInput? c = cpuzMatches[i];
            SpdAuditSourceInput? s = smbiosMatches[i];
            var sources = new List<SpdAuditSourceInput> { primary };
            if (c is not null) sources.Add(c);
            if (s is not null) sources.Add(s);
            if (input.CurrentTimings is { } t)
                sources.Add(CurrentSource(t));

            var findings = BuildFindings(primary, c, s, input.CurrentTimings);
            var verdict = Overall(findings);
            results.Add(new SpdSlotAudit(
                NonGeneric(primary.Locator) ?? $"DIMM #{i + 1}（原生 SPD）",
                verdict,
                Summary(verdict),
                sources,
                findings));
        }
        return results;
    }

    public static IReadOnlyList<SpdSlotAudit> Audit(
        IEnumerable<SpdDirectRead> native,
        IEnumerable<SpdModule>? cpuz = null,
        IEnumerable<SmbiosDimmRow>? smbios = null,
        MemoryTimings? currentTimings = null)
    {
        var n = native.Select(x => FromNative(x)).ToList();
        var c = cpuz?.Select(FromCpuZ).ToList() ?? [];
        var s = smbios?.Where(x => !IsUninstalled(x.Size)).Select(FromSmbios).ToList() ?? [];
        return Audit(new SpdAuditInput(n, c, s, FromCurrentTimings(currentTimings)));
    }

    private static List<SpdAuditSourceInput?> Match(
        IReadOnlyList<SpdAuditSourceInput> native,
        IReadOnlyList<SpdAuditSourceInput> candidates)
    {
        var result = Enumerable.Repeat<SpdAuditSourceInput?>(null, native.Count).ToList();
        var unused = new HashSet<int>(Enumerable.Range(0, candidates.Count));

        // 唯一的精確 locator 先配，避免多 DIMM 同型號時靠清單順序錯配。
        for (int i = 0; i < native.Count; i++)
        {
            string? locator = Normalize(native[i].Locator);
            if (locator is null) continue;
            var exact = unused.Where(j => Normalize(candidates[j].Locator) == locator).ToList();
            if (exact.Count == 1) { result[i] = candidates[exact[0]]; unused.Remove(exact[0]); }
        }

        // 再以識別欄位評分；若最高分並列就不猜。序號最強，料號次之，其餘只輔助。
        for (int i = 0; i < native.Count; i++)
        {
            if (result[i] is not null) continue;
            int bestScore = 0;
            var best = new List<int>();
            foreach (int j in unused)
            {
                int score = MatchScore(native[i], candidates[j]);
                if (score > bestScore) { bestScore = score; best.Clear(); best.Add(j); }
                else if (score == bestScore && score > 0) best.Add(j);
            }
            if (bestScore >= 4 && best.Count == 1)
            {
                result[i] = candidates[best[0]];
                unused.Remove(best[0]);
            }
        }

        // 只有雙方都沒有任何可用識別資訊時，單一候選才可按唯一性配對；已有明確但矛盾的資訊就不硬配。
        var openNative = Enumerable.Range(0, native.Count).Where(i => result[i] is null).ToList();
        if (openNative.Count == 1 && unused.Count == 1)
        {
            int i = openNative[0], j = unused.Single();
            bool nativeHasIdentity = HasIdentity(native[i]);
            bool candidateHasIdentity = HasIdentity(candidates[j]);
            if (!nativeHasIdentity && !candidateHasIdentity) result[i] = candidates[j];
        }
        return result;

        static bool HasIdentity(SpdAuditSourceInput x)
            => NonGeneric(x.SerialNumber) is not null || NonGeneric(x.PartNumber) is not null
            || NonGeneric(x.Vendor) is not null || x.CapacityMiB is > 0;
    }

    private static int MatchScore(SpdAuditSourceInput a, SpdAuditSourceInput b)
    {
        int score = 0;
        if (EqualValue(a.SerialNumber, b.SerialNumber)) score += 8;
        if (EqualValue(a.PartNumber, b.PartNumber)) score += 4;
        if (EqualValue(a.Vendor, b.Vendor)) score += 2;
        if (a.CapacityMiB is > 0 && a.CapacityMiB == b.CapacityMiB) score++;
        return score;
    }

    private static List<SpdAuditFinding> BuildFindings(
        SpdAuditSourceInput native,
        SpdAuditSourceInput? cpuz,
        SpdAuditSourceInput? smbios,
        CurrentMemoryTimingEvidence? current)
    {
        var list = new List<SpdAuditFinding>();
        AddChecksum(list, native);
        AddTextComparison(list, SpdAuditField.JedecVendor, "JEDEC 廠商", native, cpuz, smbios,
            x => x.Vendor, native.VendorParityValid);
        AddTextComparison(list, SpdAuditField.PartNumber, "料號", native, cpuz, smbios, x => x.PartNumber);
        AddTextComparison(list, SpdAuditField.SerialNumber, "序號", native, cpuz, smbios, x => x.SerialNumber);
        AddNumberComparison(list, SpdAuditField.Capacity, "容量", "MiB", native, cpuz, smbios, x => x.CapacityMiB);
        AddNumberComparison(list, SpdAuditField.Speed, "JEDEC 速度", "MT/s", native, cpuz, smbios, x => x.SpeedMTs);
        AddManufactureDate(list, native, cpuz, smbios);
        AddSourceAvailability(list, cpuz, smbios);
        AddCurrentTimings(list, native, current);
        AddSuspicionAssessment(list);
        return list;
    }

    private static void AddChecksum(List<SpdAuditFinding> list, SpdAuditSourceInput native)
    {
        var evidence = Evidence(native, native.ChecksumText
            ?? $"基本段 {BoolText(native.BaseCrcValid)}；模組段 {BoolText(native.ModuleCrcValid)}");
        bool known = native.BaseCrcValid.HasValue && native.ModuleCrcValid.HasValue;
        bool bad = native.BaseCrcValid == false || native.ModuleCrcValid == false;
        list.Add(new SpdAuditFinding(
            SpdAuditField.Checksum,
            !known ? SpdAuditVerdict.MissingData : bad ? SpdAuditVerdict.Conflict : SpdAuditVerdict.Consistent,
            !known ? "原生 SPD 沒有可核對的 CRC 狀態。"
                : bad ? "至少一段 SPD CRC 不符；這證明受校驗區內容與儲存 CRC 不一致，但單獨不足以判定原因。"
                : "原生 SPD 的基本段與模組段 CRC 均一致。",
            [evidence]));
    }

    private static void AddTextComparison(
        List<SpdAuditFinding> list,
        SpdAuditField field,
        string label,
        SpdAuditSourceInput native,
        SpdAuditSourceInput? cpuz,
        SpdAuditSourceInput? smbios,
        Func<SpdAuditSourceInput, string?> selector,
        bool? nativeValidity = null)
    {
        var values = PresentValues(native, cpuz, smbios, selector);
        var evidence = SourceEvidence(native, cpuz, smbios, x => Display(selector(x)));
        bool primaryPresent = NonGeneric(selector(native)) is not null;
        bool conflict = primaryPresent && values.Any(x => x.Source != native &&
            !EqualValue(selector(native), selector(x.Source)));
        bool parityBad = nativeValidity == false;
        var verdict = conflict ? SpdAuditVerdict.Conflict
            : parityBad ? SpdAuditVerdict.Suspicious
            : !primaryPresent || values.Count < 2 ? SpdAuditVerdict.MissingData
            : SpdAuditVerdict.Consistent;
        string summary = conflict ? $"{label}在來源間矛盾。"
            : parityBad ? $"原生 SPD 的{label}代碼可辨識，但 JEDEC 奇同位不正確；保守列為可疑資料，不推定重刷。"
            : !primaryPresent ? $"原生 SPD 沒有可用的{label}。"
            : values.Count < 2 ? $"只有原生 SPD 提供可用的{label}，沒有第二來源可交叉核對。"
            : $"各可用來源的{label}一致。";
        list.Add(new SpdAuditFinding(field, verdict, summary, evidence));
    }

    private static void AddNumberComparison(
        List<SpdAuditFinding> list,
        SpdAuditField field,
        string label,
        string unit,
        SpdAuditSourceInput native,
        SpdAuditSourceInput? cpuz,
        SpdAuditSourceInput? smbios,
        Func<SpdAuditSourceInput, int?> selector)
    {
        var sources = Existing(native, cpuz, smbios);
        int? primary = selector(native) is > 0 ? selector(native) : null;
        var usable = sources.Where(x => selector(x) is > 0).ToList();
        bool conflict = primary.HasValue && usable.Any(x => x != native && selector(x) != primary);
        var verdict = conflict ? SpdAuditVerdict.Conflict
            : !primary.HasValue || usable.Count < 2 ? SpdAuditVerdict.MissingData
            : SpdAuditVerdict.Consistent;
        string summary = conflict ? $"{label}在來源間矛盾。"
            : !primary.HasValue ? $"原生 SPD 無法提供可用的{label}。"
            : usable.Count < 2 ? $"只有原生 SPD 提供可用的{label}，沒有第二來源可交叉核對。"
            : $"各可用來源的{label}一致。";
        var evidence = sources.Select(x => Evidence(x,
            selector(x) is > 0 ? $"{selector(x)} {unit}" : "缺資料／通用值")).ToList();
        list.Add(new SpdAuditFinding(field, verdict, summary, evidence));
    }

    private static void AddManufactureDate(
        List<SpdAuditFinding> list,
        SpdAuditSourceInput native,
        SpdAuditSourceInput? cpuz,
        SpdAuditSourceInput? smbios)
    {
        var sources = Existing(native, cpuz, smbios)
            .Where(x => x.ManufactureYear is > 0 && x.ManufactureWeek is >= 1 and <= 53).ToList();
        if (sources.Count == 0) return; // 需求：只有資料存在才顯示。
        string Date(SpdAuditSourceInput x) => $"{x.ManufactureYear}-W{x.ManufactureWeek:00}";
        bool primary = native.ManufactureYear is > 0 && native.ManufactureWeek is >= 1 and <= 53;
        bool conflict = primary && sources.Any(x => x != native && Date(x) != Date(native));
        var verdict = conflict ? SpdAuditVerdict.Conflict
            : !primary || sources.Count < 2 ? SpdAuditVerdict.MissingData
            : SpdAuditVerdict.Consistent;
        list.Add(new SpdAuditFinding(
            SpdAuditField.ManufactureDate,
            verdict,
            conflict ? "製造週年在來源間矛盾。"
                : !primary ? "其他來源有製造週年，但原生 SPD 沒有有效日期。"
                : sources.Count < 2 ? "原生 SPD 有製造週年，但沒有第二來源可核對。"
                : "各可用來源的製造週年一致。",
            sources.Select(x => Evidence(x, Date(x))).ToList()));
    }

    private static void AddSourceAvailability(
        List<SpdAuditFinding> list,
        SpdAuditSourceInput? cpuz,
        SpdAuditSourceInput? smbios)
    {
        var missing = new List<string>();
        if (cpuz is null) missing.Add("CPU-Z");
        if (smbios is null) missing.Add("SMBIOS");
        if (missing.Count == 0) return;
        list.Add(new SpdAuditFinding(
            SpdAuditField.SourceAvailability,
            SpdAuditVerdict.MissingData,
            $"缺少可安全配對的{string.Join("、", missing)}插槽資料；未以清單順序強行配對。",
            missing.Select(x => new SpdAuditEvidence(
                x == "CPU-Z" ? SpdAuditSourceKind.CpuZ : SpdAuditSourceKind.Smbios,
                x,
                "缺資料／無法唯一配對")).ToList()));
    }

    private static void AddCurrentTimings(
        List<SpdAuditFinding> list,
        SpdAuditSourceInput native,
        CurrentMemoryTimingEvidence? current)
    {
        if (current is null) return;
        string value = $"{Display(current.MemoryType)}；{(current.DataRateMTs is > 0 ? current.DataRateMTs + " MT/s" : "速度缺資料")}；{Display(current.PrimaryTimings)}";
        var evidence = new List<SpdAuditEvidence>
        {
            new(SpdAuditSourceKind.NativeSpd, native.Source,
                native.SpeedMTs is > 0 ? $"最高 JEDEC {native.SpeedMTs} MT/s" : "最高 JEDEC 速度缺資料"),
            new(SpdAuditSourceKind.CurrentTimings, current.Source, value),
        };
        bool exceeds = native.SpeedMTs is > 0 && current.DataRateMTs is > 0 && current.DataRateMTs > native.SpeedMTs;
        list.Add(new SpdAuditFinding(
            SpdAuditField.CurrentTimings,
            exceeds ? SpdAuditVerdict.Suspicious : SpdAuditVerdict.Consistent,
            exceeds
                ? "目前全機資料速率高於此模組的最高 JEDEC 速率；可能來自 XMP／超頻，不能據此判定 SPD 異常。"
                : "已附上全機目前時序作背景證據；它不是單一插槽專屬讀值，不參與身份矛盾判定。",
            evidence));
    }

    private static void AddSuspicionAssessment(List<SpdAuditFinding> list)
    {
        int independentSignals = 0;
        if (list.Any(x => x.Field == SpdAuditField.Checksum && x.Verdict == SpdAuditVerdict.Conflict)) independentSignals++;
        if (list.Any(x => x.Field == SpdAuditField.JedecVendor && x.Verdict is SpdAuditVerdict.Conflict or SpdAuditVerdict.Suspicious)) independentSignals++;
        if (list.Any(x => x.Field is SpdAuditField.PartNumber or SpdAuditField.SerialNumber
                       or SpdAuditField.Capacity or SpdAuditField.Speed or SpdAuditField.ManufactureDate
                       && x.Verdict == SpdAuditVerdict.Conflict)) independentSignals++;
        if (independentSignals < 2) return;

        var evidence = list.Where(x => x.Verdict is SpdAuditVerdict.Conflict or SpdAuditVerdict.Suspicious)
            .SelectMany(x => x.Evidence).Distinct().ToList();
        list.Add(new SpdAuditFinding(
            SpdAuditField.SourceAvailability,
            SpdAuditVerdict.Suspicious,
            "有至少兩類獨立異常訊號，因此列為「可疑」並建議人工複核；這些證據仍不能單獨證明 SPD 曾被重刷。",
            evidence));
    }

    private static SpdAuditVerdict Overall(IReadOnlyList<SpdAuditFinding> findings)
    {
        bool multiSignalSuspicion = findings.Any(x => x.Verdict == SpdAuditVerdict.Suspicious
            && x.Summary.Contains("至少兩類", StringComparison.Ordinal));
        if (multiSignalSuspicion) return SpdAuditVerdict.Suspicious;
        if (findings.Any(x => x.Verdict == SpdAuditVerdict.Conflict)) return SpdAuditVerdict.Conflict;
        if (findings.Any(x => x.Verdict == SpdAuditVerdict.Suspicious)) return SpdAuditVerdict.Suspicious;
        if (findings.Any(x => x.Verdict == SpdAuditVerdict.MissingData)) return SpdAuditVerdict.MissingData;
        return SpdAuditVerdict.Consistent;
    }

    private static string Summary(SpdAuditVerdict verdict) => verdict switch
    {
        SpdAuditVerdict.Consistent => "可核對的證據一致。",
        SpdAuditVerdict.Conflict => "至少一項跨來源證據矛盾；這是差異，不等同於偽造。",
        SpdAuditVerdict.MissingData => "原生 SPD 已讀取，但外部來源或欄位不足以完整交叉核對。",
        _ => "有多項異常訊號，保守列為可疑並建議人工複核；不據此斷言曾重刷。",
    };

    private static SpdAuditSourceInput CurrentSource(CurrentMemoryTimingEvidence t)
        => new(SpdAuditSourceKind.CurrentTimings, t.Source,
            Vendor: t.MemoryType,
            PartNumber: t.PrimaryTimings,
            SpeedMTs: t.DataRateMTs);

    private static List<SpdAuditSourceInput> Existing(params SpdAuditSourceInput?[] sources)
        => sources.Where(x => x is not null).Cast<SpdAuditSourceInput>().ToList();

    private static List<(SpdAuditSourceInput Source, string Value)> PresentValues(
        SpdAuditSourceInput native,
        SpdAuditSourceInput? cpuz,
        SpdAuditSourceInput? smbios,
        Func<SpdAuditSourceInput, string?> selector)
        => Existing(native, cpuz, smbios)
            .Select(x => (Source: x, Value: NonGeneric(selector(x))))
            .Where(x => x.Value is not null)
            .Select(x => (x.Source, x.Value!)).ToList();

    private static List<SpdAuditEvidence> SourceEvidence(
        SpdAuditSourceInput native,
        SpdAuditSourceInput? cpuz,
        SpdAuditSourceInput? smbios,
        Func<SpdAuditSourceInput, string> value)
        => Existing(native, cpuz, smbios).Select(x => Evidence(x, value(x))).ToList();

    private static SpdAuditEvidence Evidence(SpdAuditSourceInput source, string value)
        => new(source.Kind, source.Source, value);

    private static bool EqualValue(string? left, string? right)
    {
        string? a = Normalize(left), b = Normalize(right);
        return a is not null && b is not null && a == b;
    }

    private static string? Normalize(string? value)
    {
        value = NonGeneric(value);
        return value is null ? null : Regex.Replace(value, "[^A-Z0-9]", "", RegexOptions.IgnoreCase)
            .ToUpperInvariant();
    }

    private static string? NonGeneric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        if (trimmed.StartsWith("未知（bank ", StringComparison.OrdinalIgnoreCase)) return null;
        return GenericValues.Contains(trimmed) ? null : trimmed;
    }

    private static string Display(string? value) => NonGeneric(value) ?? "缺資料／通用值";
    private static string BoolText(bool? value) => value switch { true => "OK", false => "不符", null => "缺資料" };
    private static int? Positive(int value) => value > 0 ? value : null;
    private static bool IsUninstalled(string? value) => string.Equals(value?.Trim(), "未安裝", StringComparison.Ordinal);

    private static string? JoinLocator(string? locator, string? bank)
    {
        locator = NonGeneric(locator); bank = NonGeneric(bank);
        if (locator is null) return bank;
        if (bank is null || Normalize(locator) == Normalize(bank)) return locator;
        return locator + " / " + bank;
    }

    private static int? ParseCapacityMiB(string? value)
    {
        value = NonGeneric(value);
        if (value is null) return null;
        var m = Regex.Match(value, @"(?<n>[0-9]+(?:[.,][0-9]+)?)\s*(?<u>TiB|GiB|GB|MiB|MB|MBytes|kB)", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups["n"].Value.Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return null;
        double mib = m.Groups["u"].Value.ToUpperInvariant() switch
        {
            "TIB" => number * 1024 * 1024,
            "GIB" or "GB" => number * 1024,
            "MIB" or "MB" or "MBYTES" => number,
            "KB" => number / 1024,
            _ => 0,
        };
        return mib > 0 ? (int)Math.Round(mib) : null;
    }

    private static int? ParseSpeedMTs(string? value)
    {
        value = NonGeneric(value);
        if (value is null) return null;
        var explicitRate = Regex.Match(value, @"(?:DDR\d?[- ]|\b)(?<n>\d{3,5})\s*(?:MT/s)?", RegexOptions.IgnoreCase);
        return explicitRate.Success && int.TryParse(explicitRate.Groups["n"].Value, out int rate) && rate > 0
            ? rate : null;
    }

    private static void ParseManufactureDate(string? value, out int? year, out int? week)
    {
        year = null; week = null;
        value = NonGeneric(value);
        if (value is null) return;
        var m = Regex.Match(value, @"Week\s*(?<w>\d{1,2})\s*/\s*Year\s*(?<y>\d{2,4})", RegexOptions.IgnoreCase);
        if (!m.Success || !int.TryParse(m.Groups["w"].Value, out int w)
            || !int.TryParse(m.Groups["y"].Value, out int y) || w is < 1 or > 53) return;
        week = w;
        year = y < 100 ? 2000 + y : y;
    }
}
