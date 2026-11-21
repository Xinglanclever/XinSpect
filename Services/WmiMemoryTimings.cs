namespace XinSpect;

/// <summary>Pure Windows configured-rate projection shared by desktop startup and CLI evidence.</summary>
internal static class WmiMemoryTimings
{
    internal static MemoryTimings? Build(IReadOnlyList<MemoryModuleInfo> mods)
    {
        if (mods is { Count: > 0 })
        {
            var rates = mods.Select(m => m.ConfiguredSpeedMHz).Distinct().ToArray();
            int configured = rates.Length == 1 && rates[0] > 0 ? rates[0] : 0;
            double totalGb = mods.Sum(m => m.CapacityGB);
            string type = mods[0].MemoryType;
            bool knownType = !string.IsNullOrWhiteSpace(type) && type is not ("—" or "-");
            string prefix = knownType ? type : "記憶體";
            return new MemoryTimings
            {
                Loaded = false,   // 時序欄位沒有來源，不假裝有
                SourceText = "Windows 設定速率（WMI）",
                RateIsConfigured = true,
                RateFactTrust = FactTrustLevel.Reported,
                RateFactAvailability = FactAvailability.Present,
                Status = "已由 Windows（WMI）取得目前設定的資料速率；時序（CL／tRCD 等）WMI 不提供，"
                       + "需 SPD 直讀（以系統管理員執行）或 CPU-Z 報告才讀得到。",
                MemoryTypeText = type,
                DataRateText = configured > 0 ? $"{prefix}-{configured}（WMI 設定值）" : "—",
                DramFrequencyMHz = !knownType ? 0 : type.Contains("DDR", StringComparison.OrdinalIgnoreCase)
                    ? configured / 2.0 : configured,
                MemorySizeText = totalGb > 0 ? $"{totalGb:0.#} GB（{mods.Count} 條）" : "—",
            };
        }

        return null;
    }
}
