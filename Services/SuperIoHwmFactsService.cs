namespace XinSpect;

/// <summary>
/// WP17 主機板解碼深化：SuperIO HWM 感測器（ITE／Fintek／Nuvoton NCT67xx，usermode 經 I/O 埠＋既有驅動白名單）。
/// 流程與 <see cref="SuperIoProbeService"/> 同一套風險紀律——對 0x2E／0x4E 兩組標準設定埠逐埠
/// 進設定模式→唯讀晶片 ID 定家族→選家族對應的 HWM LDN→取基址→<b>必以 finally 退出</b>→
/// 再以基址＋5（index）／＋6（data）讀感測器暫存器（三家族共用此通路；NCT 另需先寫 bank 暫存器 0x4E）。
/// 讀不到（無裝置／未收錄家族）如實三態；電壓標「未經主機板校準」。
/// </summary>
public static class SuperIoHwmFactsService
{
    private const string Category = "主機板";
    private const byte RegLdnSelect = 0x07;
    private const byte RegBaseAddressHigh = 0x60, RegBaseAddressLow = 0x61;
    private static readonly uint[] IndexPorts = [0x2E, 0x4E];

    public static IReadOnlyList<HardwareFact> Collect(IIoPortAccess io, DateTimeOffset at)
    {
        if (!io.Available)
        {
            return IndexPorts.SelectMany(port =>
                Unavailable(port, at, FactAvailability.InsufficientPrivilege,
                    io.UnavailableReason ?? "缺 I/O 埠存取")).ToList();
        }

        var facts = new List<HardwareFact>();
        foreach (uint indexPort in IndexPorts)
            facts.AddRange(CollectPort(io, indexPort, at));
        return facts;
    }

    private static IReadOnlyList<HardwareFact> CollectPort(IIoPortAccess io, uint indexPort, DateTimeOffset at)
    {
        bool inConfigMode = false;
        try
        {
            foreach (var sequence in SuperIo.EntrySequences)
            {
                foreach (var b in sequence)
                {
                    if (!io.OutByte(indexPort, b))
                        return Unavailable(indexPort, at, FactAvailability.ReadError, "I/O 埠寫入在進入設定模式時失敗");
                    inConfigMode = true;
                }

                var chipId = ReadChipId(io, indexPort);
                if (chipId is null)
                {
                    Exit(io, indexPort, ref inConfigMode);
                    continue; // 換下一個進入序列
                }
                SuperIoFamily family = SuperIoKnowledge.Family(chipId.Value);

                // 家族對應的 HWM LDN：ITE／Fintek＝0x04；NCT67xx＝0x0B（superiotool probe_idregs_nuvoton）
                byte hwmLdn = family switch
                {
                    SuperIoFamily.ITE or SuperIoFamily.Fintek => 0x04,
                    SuperIoFamily.NuvotonNct => 0x0B,
                    _ => 0x04,
                };

                if (!WriteConfig(io, indexPort, RegLdnSelect, hwmLdn) ||
                    !WriteConfig(io, indexPort, 0x30, 0x01))
                    return Unavailable(indexPort, at, FactAvailability.ReadError, "I/O 埠寫入在選 LDN 時失敗");

                byte? hi = ReadConfig(io, indexPort, RegBaseAddressHigh);
                byte? lo = ReadConfig(io, indexPort, RegBaseAddressLow);
                if (hi is null || lo is null || hi.Value == 0xFF || lo.Value == 0xFF)
                {
                    Exit(io, indexPort, ref inConfigMode);
                    return Unavailable(indexPort, at, FactAvailability.NotSupported,
                        "環境控制器基址全 F：此 SuperIO 沒有（或未啟用）HWM——如實標，不猜");
                }
                uint hwmBase = (uint)((hi.Value << 8) | lo.Value) & 0xFFF8; // 低 3 位為型別/狀態位元

                Exit(io, indexPort, ref inConfigMode);
                return family switch
                {
                    SuperIoFamily.ITE => ReadIteSensors(io, hwmBase, chipId.Value, indexPort, at),
                    SuperIoFamily.Fintek => ReadFintekSensors(io, hwmBase, chipId.Value, indexPort, at),
                    SuperIoFamily.NuvotonNct => ReadNctSensors(io, hwmBase, chipId.Value, indexPort, at),
                    _ => Unavailable(indexPort, at, FactAvailability.NotSupported,
                        $"晶片 ID 0x{chipId.Value:X4}（{SuperIoKnowledge.ChipName(chipId.Value) ?? "未知"}）名稱有收錄，但此家族的 HWM 佈局無出處化資料——不解讀，不猜"),
                };
            }
            return Unavailable(indexPort, at, FactAvailability.NotSupported,
                "無裝置回應（兩種進入序列都失敗）");
        }
        finally
        {
            if (inConfigMode) io.OutByte(indexPort, SuperIo.ExitCommand);
        }
    }

    private static ushort? ReadChipId(IIoPortAccess io, uint indexPort)
    {
        byte? hi = ReadConfig(io, indexPort, SuperIo.RegChipIdHigh);
        byte? lo = ReadConfig(io, indexPort, SuperIo.RegChipIdLow);
        return hi is null || lo is null ? null : SuperIo.DecodeChipId(hi.Value, lo.Value);
    }

    // ── ITE：TMPIN1 0x29；FAN1 0x0D/0x0E（LSB first）；VIN0 0x20（16 mV/LSB）──
    private static IReadOnlyList<HardwareFact> ReadIteSensors(IIoPortAccess io, uint hwmBase, ushort chipId, uint indexPort, DateTimeOffset at)
    {
        string keyPrefix = $"sio.hwm.0x{indexPort:x2}";
        string source = $"SuperIO HWM（I/O 0x{hwmBase:X}＋5/6，ITE 佈局）";
        string chip = SuperIoKnowledge.ChipName(chipId) ?? $"0x{chipId:X4}";
        var facts = new List<HardwareFact>
        {
            BaseFact(keyPrefix, hwmBase, chip, indexPort, at),
        };

        var tempRaw = ReadHwm(io, hwmBase, 0x29);
        facts.Add(tempRaw is { } t
            ? Fact($"{keyPrefix}.temp0", $"溫度 TMPIN1（{chip}）", $"{SuperIoHwmDecoder.DecodeTemperature(t)} °C", "°C",
                source, SuperIoHwmDecoder.DecodeTemperature(t), at)
            : UnavailableOne($"{keyPrefix}.temp0", "溫度 TMPIN1", source, at, FactAvailability.ReadError, "暫存器讀取失敗"));

        var countLo = ReadHwm(io, hwmBase, 0x0D);
        var countHi = ReadHwm(io, hwmBase, 0x0E);
        if (countLo is { } lo2 && countHi is { } hi2)
        {
            uint? rpm = SuperIoHwmDecoder.DecodeFanRpm((ushort)(lo2 | (hi2 << 8)));
            facts.Add(Fact($"{keyPrefix}.fan0", $"風扇 FAN1（{chip}）",
                rpm is not null ? $"{rpm} RPM" : "無效計數（停轉或未接）", "RPM", source, rpm, at));
        }
        else
        {
            facts.Add(UnavailableOne($"{keyPrefix}.fan0", "風扇 FAN1", source, at, FactAvailability.ReadError, "暫存器讀取失敗"));
        }

        var vinRaw = ReadHwm(io, hwmBase, 0x20);
        facts.Add(vinRaw is { } v
            ? Fact($"{keyPrefix}.vin0", $"電壓 VIN0（{chip}）",
                $"{SuperIoHwmDecoder.DecodeVoltageMv(v)} mV（未經主機板校準）", "mV",
                source, SuperIoHwmDecoder.DecodeVoltageMv(v), at)
            : UnavailableOne($"{keyPrefix}.vin0", "電壓 VIN0", source, at, FactAvailability.ReadError, "暫存器讀取失敗"));
        return facts;
    }

    // ── Fintek：TEMP 0x70（8-bit 二補數）；FAN 0xA0（hi）/0xA1（lo）MSB first，1,500,000/count；IN 0x20（8 mV/LSB）──
    private static IReadOnlyList<HardwareFact> ReadFintekSensors(IIoPortAccess io, uint hwmBase, ushort chipId, uint indexPort, DateTimeOffset at)
    {
        string keyPrefix = $"sio.hwm.0x{indexPort:x2}";
        string source = $"SuperIO HWM（I/O 0x{hwmBase:X}＋5/6，Fintek 佈局）";
        string chip = SuperIoKnowledge.ChipName(chipId) ?? $"0x{chipId:X4}";
        var facts = new List<HardwareFact>
        {
            BaseFact(keyPrefix, hwmBase, chip, indexPort, at),
        };

        var tempRaw = ReadHwm(io, hwmBase, 0x70);
        facts.Add(tempRaw is { } t
            ? Fact($"{keyPrefix}.temp0", $"溫度 TEMP1（{chip}）", $"{SuperIoHwmDecoder.DecodeFintekTemperature(t)} °C", "°C",
                source, SuperIoHwmDecoder.DecodeFintekTemperature(t), at)
            : UnavailableOne($"{keyPrefix}.temp0", "溫度 TEMP1", source, at, FactAvailability.ReadError, "暫存器讀取失敗"));

        var countHi = ReadHwm(io, hwmBase, 0xA0);
        var countLo = ReadHwm(io, hwmBase, 0xA1);
        if (countHi is { } hi2 && countLo is { } lo2)
        {
            uint? rpm = SuperIoHwmDecoder.DecodeFintekFanRpm((ushort)((hi2 << 8) | lo2));
            facts.Add(Fact($"{keyPrefix}.fan0", $"風扇 FAN1（{chip}）",
                rpm is not null ? $"{rpm} RPM" : "無效計數（停轉或未接）", "RPM", source, rpm, at));
        }
        else
        {
            facts.Add(UnavailableOne($"{keyPrefix}.fan0", "風扇 FAN1", source, at, FactAvailability.ReadError, "暫存器讀取失敗"));
        }

        var vinRaw = ReadHwm(io, hwmBase, 0x20);
        facts.Add(vinRaw is { } v
            ? Fact($"{keyPrefix}.vin0", $"電壓 VIN0（{chip}）",
                $"{SuperIoHwmDecoder.DecodeFintekVoltageMv(v)} mV（未經主機板校準）", "mV",
                source, SuperIoHwmDecoder.DecodeFintekVoltageMv(v), at)
            : UnavailableOne($"{keyPrefix}.vin0", "電壓 VIN0", source, at, FactAvailability.ReadError, "暫存器讀取失敗"));
        return facts;
    }

    // ── NCT67xx：banked 暫存器，先寫 bank 暫存器 0x4E（NCT6775_REG_BANK）──
    // TEMP1＝bank 0 暫存器 0x27；FAN1＝bank 6 暫存器 0x30/0x31（LSB first）；IN0＝bank 0 暫存器 0x20（scale 800 → 8 mV/LSB）
    private static IReadOnlyList<HardwareFact> ReadNctSensors(IIoPortAccess io, uint hwmBase, ushort chipId, uint indexPort, DateTimeOffset at)
    {
        string keyPrefix = $"sio.hwm.0x{indexPort:x2}";
        string source = $"SuperIO HWM（I/O 0x{hwmBase:X}＋5/6，NCT banked 佈局）";
        string chip = SuperIoKnowledge.ChipName(chipId) ?? $"0x{chipId:X4}";
        var facts = new List<HardwareFact>
        {
            BaseFact(keyPrefix, hwmBase, chip, indexPort, at),
        };

        SelectBank(io, hwmBase, 0);
        var tempRaw = ReadHwm(io, hwmBase, 0x27);
        facts.Add(tempRaw is { } t
            ? Fact($"{keyPrefix}.temp0", $"溫度 TEMP1（{chip}）", $"{SuperIoHwmDecoder.DecodeNctTemperature(t)} °C", "°C",
                source, SuperIoHwmDecoder.DecodeNctTemperature(t), at)
            : UnavailableOne($"{keyPrefix}.temp0", "溫度 TEMP1", source, at, FactAvailability.ReadError, "暫存器讀取失敗"));

        SelectBank(io, hwmBase, 6);
        var countLo = ReadHwm(io, hwmBase, 0x30);
        var countHi = ReadHwm(io, hwmBase, 0x31);
        if (countLo is { } lo2 && countHi is { } hi2)
        {
            uint? rpm = SuperIoHwmDecoder.DecodeNctFanRpm((ushort)(lo2 | (hi2 << 8)));
            facts.Add(Fact($"{keyPrefix}.fan0", $"風扇 FAN1（{chip}）",
                rpm is not null ? $"{rpm} RPM" : "無效計數（停轉或未接）", "RPM", source, rpm, at));
        }
        else
        {
            facts.Add(UnavailableOne($"{keyPrefix}.fan0", "風扇 FAN1", source, at, FactAvailability.ReadError, "暫存器讀取失敗"));
        }

        SelectBank(io, hwmBase, 0);
        var vinRaw = ReadHwm(io, hwmBase, 0x20);
        facts.Add(vinRaw is { } v
            ? Fact($"{keyPrefix}.vin0", $"電壓 VIN0（{chip}）",
                $"{SuperIoHwmDecoder.DecodeNctVoltageMv(v, 800)} mV（未經主機板校準）", "mV",
                source, SuperIoHwmDecoder.DecodeNctVoltageMv(v, 800), at)
            : UnavailableOne($"{keyPrefix}.vin0", "電壓 VIN0", source, at, FactAvailability.ReadError, "暫存器讀取失敗"));
        return facts;
    }

    private static void SelectBank(IIoPortAccess io, uint hwmBase, byte bank) =>
        WriteHwm(io, hwmBase, 0x4E, bank);

    private static HardwareFact BaseFact(string keyPrefix, uint hwmBase, string chip, uint indexPort, DateTimeOffset at) =>
        new($"{keyPrefix}.base", Category, $"HWM 基址（{chip} @ 0x{indexPort:X2}）", $"0x{hwmBase:X}", "",
            $"SuperIO 設定模式 HWM LDN 基址暫存器（{keyPrefix}）", FactTrustLevel.Measured, false, at, hwmBase);

    private static HardwareFact Fact(string key, string name, string value, string unit, string source,
        double? numeric, DateTimeOffset at) =>
        new(key, Category, name, value, unit, source, FactTrustLevel.Measured, false, at, numeric);

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

    private static bool WriteHwm(IIoPortAccess io, uint hwmBase, byte index, byte value)
        => io.OutByte(hwmBase + 5, index) && io.OutByte(hwmBase + 6, value);

    private static void Exit(IIoPortAccess io, uint indexPort, ref bool inConfigMode)
    {
        io.OutByte(indexPort, SuperIo.ExitCommand);
        inConfigMode = false;
    }

    private static HardwareFact UnavailableOne(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);

    private static IReadOnlyList<HardwareFact> Unavailable(uint indexPort, DateTimeOffset at,
        FactAvailability availability, string reason)
    {
        string source = $"SuperIO 設定模式（I/O 0x{indexPort:X2}）";
        return new[]
        {
            ("base", $"HWM 基址（0x{indexPort:X2}）"),
            ("temp0", $"溫度 TMPIN1（0x{indexPort:X2}）"),
            ("fan0", $"風扇 FAN1（0x{indexPort:X2}）"),
            ("vin0", $"電壓 VIN0（0x{indexPort:X2}）"),
        }.Select(e => UnavailableOne($"sio.hwm.0x{indexPort:x2}.{e.Item1}", e.Item2, source, at, availability, reason)).ToList();
    }
}
