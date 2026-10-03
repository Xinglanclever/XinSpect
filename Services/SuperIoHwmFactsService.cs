namespace XinSpect;

/// <summary>
/// WP17 主機板解碼深化：SuperIO HWM 感測器（ITE 家族，usermode 經 I/O 埠＋既有驅動白名單）。
/// 流程與 <see cref="SuperIoProbeService"/> 同一套風險紀律——進設定模式→選 LDN 4（環境控制器）→
/// 取 HWM 基址→<b>必以 finally 退出</b>→再以基址＋5（index）／＋6（data）讀感測器暫存器。
/// 讀不到（無裝置／非 ITE 佈局）如實三態；電壓標「未經主機板校準」。
/// </summary>
public static class SuperIoHwmFactsService
{
    private const string Category = "主機板";
    private const byte LdnEnvironmentController = 0x04;
    private const byte RegLdnSelect = 0x07;
    private const byte RegBaseAddressHigh = 0x60, RegBaseAddressLow = 0x61;
    private const uint IndexPort = 0x2E, DataPort = 0x2F;

    public static IReadOnlyList<HardwareFact> Collect(IIoPortAccess io, DateTimeOffset at)
    {
        if (!io.Available)
            return Unavailable(at, FactAvailability.InsufficientPrivilege, io.UnavailableReason ?? "缺 I/O 埠存取");

        uint indexPort = IndexPort; // ITE 家族用 0x2E/0x2F；0x4E/0x4F 平台本探測略（Probe 服務已涵蓋 ID）
        bool inConfigMode = false;
        try
        {
            foreach (var sequence in SuperIo.EntrySequences)
            {
                foreach (var b in sequence)
                {
                    if (!io.OutByte(indexPort, b)) return Unavailable(at, FactAvailability.ReadError, "I/O 埠寫入在進入設定模式時失敗");
                    inConfigMode = true;
                }

                // LDN 4（環境控制器）＋啟用位（0x30 bit0）
                if (!WriteConfig(io, indexPort, RegLdnSelect, LdnEnvironmentController) ||
                    !WriteConfig(io, indexPort, 0x30, 0x01))
                    return Unavailable(at, FactAvailability.ReadError, "I/O 埠寫入在選 LDN 時失敗");

                byte? hi = ReadConfig(io, indexPort, RegBaseAddressHigh);
                byte? lo = ReadConfig(io, indexPort, RegBaseAddressLow);
                if (hi is null || lo is null || hi.Value == 0xFF || lo.Value == 0xFF)
                {
                    Exit(io, indexPort, ref inConfigMode);
                    return Unavailable(at, FactAvailability.NotSupported,
                        "環境控制器基址全 F：此 SuperIO 沒有（或未啟用）HWM——如實標，不猜");
                }
                uint hwmBase = (uint)((hi.Value << 8) | lo.Value) & 0xFFF8; // 低 3 位為型別/狀態位元

                // 退出設定模式後，HWM 以基址＋5（index）／＋6（data）存取
                Exit(io, indexPort, ref inConfigMode);
                return ReadSensors(io, hwmBase, at);
            }
            return Unavailable(at, FactAvailability.NotSupported, "無裝置回應（兩種進入序列都失敗）");
        }
        finally
        {
            if (inConfigMode) io.OutByte(indexPort, SuperIo.ExitCommand);
        }
    }

    private static IReadOnlyList<HardwareFact> ReadSensors(IIoPortAccess io, uint hwmBase, DateTimeOffset at)
    {
        string source = $"SuperIO HWM（I/O 0x{hwmBase:X}＋5/6，ITE 佈局）";
        var facts = new List<HardwareFact>
        {
            new("sio.hwm.base", Category, "HWM 基址", $"0x{hwmBase:X}", "",
                $"SuperIO 設定模式 LDN 4 基址暫存器（I/O 0x{IndexPort:X2}）", FactTrustLevel.Measured, false, at, hwmBase),
        };

        // TMPIN1（0x29）
        var tempRaw = ReadHwm(io, hwmBase, 0x29);
        facts.Add(tempRaw is { } t
            ? new HardwareFact("sio.hwm.temp0", Category, "溫度 TMPIN1",
                $"{SuperIoHwmDecoder.DecodeTemperature(t)} °C", "°C", source, FactTrustLevel.Measured, false, at,
                SuperIoHwmDecoder.DecodeTemperature(t))
            : UnavailableOne("sio.hwm.temp0", "溫度 TMPIN1", source, at, FactAvailability.ReadError, "暫存器讀取失敗"));

        // FAN1：0x0D/0x0E（16-bit count，LSB first）
        var countLo = ReadHwm(io, hwmBase, 0x0D);
        var countHi = ReadHwm(io, hwmBase, 0x0E);
        if (countLo is { } lo2 && countHi is { } hi2)
        {
            uint? rpm = SuperIoHwmDecoder.DecodeFanRpm((ushort)(lo2 | (hi2 << 8)));
            facts.Add(new HardwareFact("sio.hwm.fan0", Category, "風扇 FAN1",
                rpm is not null ? $"{rpm} RPM" : "無效計數（停轉或未接）",
                "RPM", source, FactTrustLevel.Measured, false, at, rpm));
        }
        else
        {
            facts.Add(UnavailableOne("sio.hwm.fan0", "風扇 FAN1", source, at, FactAvailability.ReadError, "暫存器讀取失敗"));
        }

        // VIN0（0x20）
        var vinRaw = ReadHwm(io, hwmBase, 0x20);
        facts.Add(vinRaw is { } v
            ? new HardwareFact("sio.hwm.vin0", Category, "電壓 VIN0",
                $"{SuperIoHwmDecoder.DecodeVoltageMv(v)} mV（未經主機板校準）", "mV", source, FactTrustLevel.Measured, false, at,
                SuperIoHwmDecoder.DecodeVoltageMv(v))
            : UnavailableOne("sio.hwm.vin0", "電壓 VIN0", source, at, FactAvailability.ReadError, "暫存器讀取失敗"));
        return facts;
    }

    private static byte? ReadConfig(IIoPortAccess io, uint indexPort, byte register)
    {
        if (!io.OutByte(indexPort, register)) return null;
        return io.InByte(indexPort + 1);
    }

    private static bool WriteConfig(IIoPortAccess io, uint indexPort, byte register, byte value)
        => io.OutByte(indexPort, register) && io.OutByte(indexPort + 1, value);

    private static byte? ReadHwm(IIoPortAccess io, uint hwmBase, byte index)
    {
        if (!io.OutByte(hwmBase + 5, index)) return null;
        return io.InByte(hwmBase + 6);
    }

    private static void Exit(IIoPortAccess io, uint indexPort, ref bool inConfigMode)
    {
        io.OutByte(indexPort, SuperIo.ExitCommand);
        inConfigMode = false;
    }

    private static HardwareFact UnavailableOne(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);

    private static IReadOnlyList<HardwareFact> Unavailable(DateTimeOffset at, FactAvailability availability, string reason) =>
        new[] { "sio.hwm.base", "sio.hwm.temp0", "sio.hwm.fan0", "sio.hwm.vin0" }.Select(key =>
            UnavailableOne(key, key switch
            {
                "sio.hwm.base" => "HWM 基址",
                "sio.hwm.temp0" => "溫度 TMPIN1",
                "sio.hwm.fan0" => "風扇 FAN1",
                _ => "電壓 VIN0",
            }, $"SuperIO 設定模式（I/O 0x{IndexPort:X2}）", at, availability, reason)).ToList();
}
