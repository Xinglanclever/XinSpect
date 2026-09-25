namespace XinSpect;

/// <summary>
/// 把已經讀到的硬體資料攤成 <see cref="VerifyFact"/>。
/// </summary>
/// <remarks>
/// 刻意做成一組純函式:輸入是「已經讀回來的值」(NVMe 快照、ATA 識別資料、SMART 屬性、電池容量),
/// 輸出是帶血統的事實。真正去發 IOCTL、碰硬體的那一層由呼叫端負責——那一層卡得死、測不動,
/// 不該跟「把值變成事實」的邏輯綁在一起。這樣映射本身可以完全用合成資料測。
/// <para>
/// 記憶體事實由 <see cref="SmbiosFacts.From"/> 產出,不在這裡重做。這裡補的是儲存與電池。
/// </para>
/// </remarks>
public static class VerifyFactsCollector
{
    /// <summary>電池:設計容量與滿充容量(mWh)。讀不到請傳 0——規則會據此判「讀不到」。</summary>
    public static IEnumerable<VerifyFact> Battery(double designMWh, double fullMWh, DateTime now)
    {
        yield return Wmi(FactId.BatteryDesignCapacityMWh, designMWh, "mWh",
            "Win32_Battery.DesignedCapacity", now);
        yield return Wmi(FactId.BatteryFullCapacityMWh, fullMWh, "mWh",
            "Win32_Battery.FullChargedCapacity", now);
    }

    /// <summary>NVMe 健康紀錄 → 事實。快照是 <c>null</c>(讀不到)時不產出任何事實,規則自然判「讀不到」。</summary>
    public static IEnumerable<VerifyFact> Nvme(NvmeHealthSnapshot? snap, DateTime now)
    {
        if (snap is not { } h) yield break;
        yield return NvmeFact(FactId.NvmePowerOnHours, h.PowerOnHours, "小時", "+0x80", now);
        yield return NvmeFact(FactId.NvmeDataUnitsWritten, h.DataWrittenGiB, "GiB", "+0x30(換算)", now);
        yield return NvmeFact(FactId.NvmePercentageUsed, h.PercentageUsed, "%", "+0x05", now);
        yield return NvmeFact(FactId.NvmePowerCycles, h.PowerCycles, "次", "+0x70", now);
        yield return NvmeFact(FactId.NvmeUnsafeShutdowns, h.UnsafeShutdowns, "次", "+0x90", now);
        yield return NvmeFact(FactId.NvmeCriticalWarning, h.CriticalWarning, "", "+0x00", now);
    }

    /// <summary>
    /// ATA 識別資料 ＋ SMART 屬性 → 事實。<paramref name="claimedCapacityGB"/> 是 OS/型號宣稱的容量
    /// (由 Win32_DiskDrive 那邊來,非 IDENTIFY),用來和可定址 LBA 對帳。
    /// </summary>
    public static IEnumerable<VerifyFact> Ata(
        AtaIdentifyInfo? info, double? claimedCapacityGB, IReadOnlyList<SmartRow>? smartAttrs, DateTime now)
    {
        if (info is not null)
        {
            yield return Ata(FactId.AtaRotationRate, info.RotationRate, "", "IDENTIFY word 217", now);
            yield return Ata(FactId.AtaTotalLba, info.TotalLba, "", "IDENTIFY word 100–103", now);
            yield return Ata(FactId.AtaAcsVersion, info.AcsMajorVersion, "", "IDENTIFY word 80", now);
        }
        if (claimedCapacityGB is { } gb)
            yield return new VerifyFact(FactId.DiskClaimedCapacityGB, FactCatalog.Name(FactId.DiskClaimedCapacityGB),
                $"{gb:N0} GB", gb, "GB", FactSource.Wmi, "Win32_DiskDrive.Size", false, FactTrust.FirmwareReported, now);

        // R-SSD-06 要的是「有沒有機械專屬屬性」。SMART 屬性 0x03(起轉時間)只有會轉的碟才有。
        // 讀得到屬性表才能回答這個是非題;讀不到就不產出,讓規則判「讀不到」。
        if (smartAttrs is not null)
        {
            bool spinUp = smartAttrs.Any(r => r.Id == 0x03);
            yield return new VerifyFact(FactId.SmartSpinUpPresent, FactCatalog.Name(FactId.SmartSpinUpPresent),
                spinUp ? "有(SMART 0x03 起轉時間)" : "無", spinUp ? 1 : 0, "",
                FactSource.SmartAttr, "SMART 屬性 0x03 是否存在", true, FactTrust.FirmwareReported, now);
        }
    }

    private static VerifyFact NvmeFact(FactId id, double n, string unit, string off, DateTime now)
        => new(id, FactCatalog.Name(id), Fmt(n, unit), n, unit, FactSource.NvmeLog,
            $"NVMe Health Log {off}", true, FactTrust.FirmwareReported, now);

    private static VerifyFact Ata(FactId id, double n, string unit, string method, DateTime now)
        => new(id, FactCatalog.Name(id), Fmt(n, unit), n, unit, FactSource.AtaIdentify,
            method, true, FactTrust.FirmwareReported, now);

    private static VerifyFact Wmi(FactId id, double n, string unit, string method, DateTime now)
        => new(id, FactCatalog.Name(id), Fmt(n, unit), n, unit, FactSource.Wmi,
            method, false, FactTrust.FirmwareReported, now);

    private static string Fmt(double n, string unit) => n.ToString("0.##") + (unit.Length > 0 ? " " + unit : "");
}
