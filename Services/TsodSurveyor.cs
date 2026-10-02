namespace XinSpect;

/// <summary>
/// TSOD（TSE2004 記憶體溫度感測器）掃描（V7 WP2／A12）：在 PCH SMBus 上探 0x18–0x1F，
/// 逐顆給溫度事實。空位址不列（不是插槽）；掃描範圍如實標示——處理器 iMC 匯流排上的
/// TSOD（HEDT／伺服器平台）本批未納入。整段唯讀；對 TSOD 位址的寫入程式碼路徑不存在。
/// </summary>
public static class TsodSurveyor
{
    private const string Category = "SMBus";
    private const byte FirstAddress = 0x18, LastAddress = 0x1F;
    private const byte RegTemperature = 0x05, RegManufacturer = 0x06;

    /// <summary>生產組合：先取軟體層匯流排鎖再掃描。鎖被別的本程式功能持有時如實三態，不等不搶。</summary>
    public static IReadOnlyList<HardwareFact> CollectWithLock(ISmbusIo? io, PciDwordReader read, DateTimeOffset at)
    {
        using var busLock = SmbusBusLock.TryAcquire(500, out string lockNote);
        if (busLock is null)
            return [Unavailable("smbus.tsod", "TSOD 溫度感測器掃描", at,
                FactAvailability.ReadError, $"SMBus 軟體層互斥鎖被持有，不搶匯流排：{lockNote}")];
        return Collect(io, read, at);
    }

    public static IReadOnlyList<HardwareFact> Collect(ISmbusIo? io, PciDwordReader read, DateTimeOffset at)
    {
        if (io is null)
            return [Unavailable("smbus.tsod", "TSOD 溫度感測器掃描", at,
                FactAvailability.InsufficientPrivilege, "缺 I/O 埠存取（SMBus 後端未注入）")];

        var location = SmbusDiscovery.Find(read, out string diagnostic);
        if (location is null)
            return [Unavailable("smbus.tsod", "TSOD 溫度感測器掃描", at,
                FactAvailability.NotApplicable, diagnostic)];

        var controller = new SmbusController(io, location.IoBase);
        if (!controller.TryAcquireBus(out string acquireReason))
            return [Unavailable("smbus.tsod", "TSOD 溫度感測器掃描", at,
                FactAvailability.ReadError, acquireReason)];

        try
        {
            var facts = new List<HardwareFact>();
            int found = 0;
            for (byte addr = FirstAddress; addr <= LastAddress; addr++)
            {
                var temp = controller.ReadTsodWord(addr, RegTemperature);
                if (temp is null)
                {
                    // 空位址（DEV_ERR）不是發現，不列；其餘失敗是發現，如實成列。
                    if (controller.LastStatus == SmbusStatus.NoDevice) continue;
                    facts.Add(Unavailable($"smbus.tsod.0x{addr:x2}", $"TSOD 溫度感測器（0x{addr:X2}）", at,
                        FactAvailability.ReadError, controller.LastError));
                    continue;
                }
                found++;
                double? c = Tsod.TemperatureC(temp.Value);
                var source = $"PCH SMBus 0x{addr:X2} 暫存器 0x{RegTemperature:X2}（word）";
                facts.Add(new HardwareFact($"smbus.tsod.0x{addr:x2}", Category, $"TSOD 溫度感測器（0x{addr:X2}）",
                    c is { } t ? $"{t:0.##}°C（原始 0x{temp.Value:X4}）" : $"原始 0x{temp.Value:X4}（溫度欄無定義值）",
                    "", source, FactTrustLevel.Measured, false, at, c));
            }
            if (found == 0 && facts.Count == 0)
                facts.Add(new HardwareFact("smbus.tsod", Category, "TSOD 溫度感測器掃描",
                    "0x18–0x1F 無裝置回應（模組未掛 TSOD，或掛在 iMC 匯流排——後者本批未掃）",
                    "", "PCH SMBus 掃描", FactTrustLevel.Measured, false, at));
            return facts;
        }
        finally
        {
            controller.ReleaseBus();
        }
    }

    private static HardwareFact Unavailable(string key, string name, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", "PCH SMBus 0x18–0x1F 唯讀", FactTrustLevel.Unknown, false, at,
            null, availability, reason);
}
