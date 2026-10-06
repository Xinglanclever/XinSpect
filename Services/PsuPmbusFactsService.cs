namespace XinSpect;

/// <summary>一次 PMBus 電源軌讀取的原始結果（解讀交給 <see cref="PmbusDecoder"/>）。</summary>
public sealed record PsuPmbusReading(
    byte Address, string? MfrId,
    ushort? VinRaw, ushort? IinRaw, ushort? VoutRaw, ushort? IoutRaw, ushort? PoutRaw, ushort? TempRaw,
    byte? VoutModeRaw);

/// <summary>
/// PMBus 電源軌（R7）：對 SMBus 0x40–0x4F 的 PMBus 標準 READ_* 命令做<b>唯讀</b>掃描。
/// 高階 PSU（CORSAIR AXi、Seasonic Prime TX 等）的 PMBus 介面與 VRM 控制器共用這個位址範圍；
/// 沒有裝置回應時整組 NotApplicable。<b>未在本機驗證</b>（本機沒有 PMBus PSU）。
/// 安全邊界：只有讀——不寫 PAGE、不寫 OPERATION，寫入路徑不存在。
/// </summary>
/// <remarks>出處：PMBus Power System Management Protocol Specification Rev 1.3 Part II（READ_VIN 0x88、
/// READ_IIN 0x89、READ_VOUT 0x8B、READ_IOUT 0x8C、READ_TEMPERATURE_1 0x8D、READ_POUT 0x96、MFR_ID 0x99、VOUT_MODE 0x20）。</remarks>
public static class PsuPmbusFactsService
{
    private const string Category = "電源";
    private const byte AddressMin = 0x40, AddressMax = 0x4F;

    public interface ITransport
    {
        byte? ReadByte(byte slave7, byte command);
        ushort? ReadWord(byte slave7, byte command);
    }

    /// <summary>真實機器入口：軟體互斥鎖＋SMBus 控制器定位都到齊才掃；取不到如實三態。</summary>
    public static IReadOnlyList<HardwareFact> CollectWithLock(ISmbusIo? io, PciDwordReader read, DateTimeOffset at)
    {
        using var busLock = SmbusBusLock.TryAcquire(500, out string lockNote);
        if (busLock is null)
            return NotApplicable($"SMBus 軟體層互斥鎖被持有，不搶匯流排：{lockNote}");
        if (io is null)
            return NotApplicable("缺 I/O 埠存取（SMBus 後端未注入）");
        var location = SmbusDiscovery.Find(read, out string diagnostic);
        if (location is null)
            return NotApplicable(diagnostic);
        return Collect(at, new PsuPmbusTransport(io, location.IoBase));
    }

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, ITransport? transport)
    {
        if (transport is null)
            return NotApplicable("SMBus 不可用（缺 ring0 或控制器未就緒）");

        for (byte address = AddressMin; address <= AddressMax; address++)
        {
            // 偵測：MFR_ID（0x99）應回可列印 ASCII；NAK 就換下一個位址
            var mfrId = transport.ReadByte(address, PmbusDecoder.CmdMfrId);
            if (mfrId is not { } id || id is < 0x20 or > 0x7E) continue;

            var reading = ReadRails(transport, address, id);
            return Facts(reading, at);
        }
        return NotApplicable($"SMBus 0x{AddressMin:X2}–0x{AddressMax:X2} 上沒有回應 MFR_ID 的 PMBus 裝置——沒有（或沒接）PMBus 電源，如實標");
    }

    private static PsuPmbusReading ReadRails(ITransport transport, byte address, byte mfrId) => new(
        address,
        MfrId: Convert.ToChar(mfrId).ToString(),
        VinRaw: transport.ReadWord(address, PmbusDecoder.CmdReadVin),
        IinRaw: transport.ReadWord(address, PmbusDecoder.CmdReadIin),
        VoutRaw: transport.ReadWord(address, PmbusDecoder.CmdReadVout),
        IoutRaw: transport.ReadWord(address, PmbusDecoder.CmdReadIout),
        PoutRaw: transport.ReadWord(address, PmbusDecoder.CmdReadPout),
        TempRaw: transport.ReadWord(address, PmbusDecoder.CmdReadTemperature1),
        VoutModeRaw: transport.ReadByte(address, PmbusDecoder.CmdVoutMode));

    private static IReadOnlyList<HardwareFact> Facts(PsuPmbusReading r, DateTimeOffset at)
    {
        string source = $"PMBus 唯讀（SMBus 0x{r.Address:X2}，MFR_ID＝{r.MfrId}）——可能為 PSU 或 VRM 控制器；未在本機驗證";
        string note = "；此位址範圍與 VRM 控制器重疊，無法單靠 MFR_ID 區分 PSU 與 VRM——呈現時如實註明";

        double? vin = r.VinRaw is { } v1 ? PmbusDecoder.DecodeLinear11(v1) : null;
        double? pout = r.PoutRaw is { } p1 ? PmbusDecoder.DecodeLinear11(p1) : null;
        double? temp = r.TempRaw is { } t1 ? PmbusDecoder.DecodeLinear11(t1) : null;
        double? vout = r.VoutRaw is { } v2
            ? (r.VoutModeRaw is { } mode && PmbusDecoder.DecodeVoutModeExponent(mode) is { } exp
                ? PmbusDecoder.DecodeLinear16(v2, exp)
                : PmbusDecoder.DecodeLinear11(v2))
            : null;

        return
        [
            Fact("psu.pmbus.vin", "輸入電壓 READ_VIN", vin, " V", r.VinRaw is { } ? $"0x{r.VinRaw:X4}" : null, source, note, at),
            Fact("psu.pmbus.vout", "輸出電壓 READ_VOUT", vout, " V",
                r.VoutRaw is { } raw2 ? $"0x{raw2:X4}（VOUT_MODE 0x{r.VoutModeRaw:X2}）" : null, source, note, at),
            Fact("psu.pmbus.pout", "輸出功率 READ_POUT", pout, " W", r.PoutRaw is { } ? $"0x{r.PoutRaw:X4}" : null, source, note, at),
            Fact("psu.pmbus.temp1", "溫度 READ_TEMPERATURE_1", temp, " °C", r.TempRaw is { } ? $"0x{r.TempRaw:X4}" : null, source, note, at),
        ];
    }

    private static HardwareFact Fact(string key, string name, double? value, string unit,
        string? raw, string source, string note, DateTimeOffset at) =>
        value is { } v
            ? new HardwareFact(key, Category, name, $"{v:0.###}{unit}" + note, unit.Trim(),
                source + (raw is { } ? $"；原始 {raw}" : ""), FactTrustLevel.Measured, false, at, v)
            : new HardwareFact(key, Category, name, "—（命令未回應）" + note, unit.Trim(),
                source, FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                raw is null ? "該命令 NAK" : "解碼失敗");

    private static IReadOnlyList<HardwareFact> NotApplicable(string reason) =>
    [
        new("psu.pmbus.vin", Category, "輸入電壓 READ_VIN", "", "",
            "PMBus 唯讀掃描（SMBus 0x40–0x4F）", FactTrustLevel.Unknown, false, DateTimeOffset.UtcNow,
            null, FactAvailability.NotApplicable, reason),
        new("psu.pmbus.vout", Category, "輸出電壓 READ_VOUT", "", "",
            "PMBus 唯讀掃描（SMBus 0x40–0x4F）", FactTrustLevel.Unknown, false, DateTimeOffset.UtcNow,
            null, FactAvailability.NotApplicable, reason),
        new("psu.pmbus.pout", Category, "輸出功率 READ_POUT", "", "",
            "PMBus 唯讀掃描（SMBus 0x40–0x4F）", FactTrustLevel.Unknown, false, DateTimeOffset.UtcNow,
            null, FactAvailability.NotApplicable, reason),
        new("psu.pmbus.temp1", Category, "溫度 READ_TEMPERATURE_1", "", "",
            "PMBus 唯讀掃描（SMBus 0x40–0x4F）", FactTrustLevel.Unknown, false, DateTimeOffset.UtcNow,
            null, FactAvailability.NotApplicable, reason),
    ];
}

/// <summary>
/// 走 i801 SMBus 控制器（ISmbusIo）的<b>唯讀</b> PMBus 傳輸層。與 <see cref="VrmControllerService"/>
/// 同一套暫存器語意，但只有 Word/Byte Data 讀取——沒有任何寫入路徑，因此也不需要讀回驗證。
/// </summary>
public sealed class PsuPmbusTransport(ISmbusIo io, uint ioBase, int timeoutMs = 100) : PsuPmbusFactsService.ITransport
{
    private const uint HstSts = 0, HstCnt = 2, HstCmd = 3, XmitSlva = 4, HstD0 = 5, HstD1 = 6;
    private const byte StsIntr = 0x02, StsErrorMask = 0x1C, StsHostBusy = 0x01;
    private const byte StsClearMask = 0xBE, CntStart = 0x40, CntKill = 0x02;
    private const byte ProtoByteData = 0x02 << 2, ProtoWordData = 0x03 << 2;

    public byte? ReadByte(byte slave7, byte command) => Run(slave7, command, ProtoByteData, word: false) is { } v ? (byte)v : null;
    public ushort? ReadWord(byte slave7, byte command) => Run(slave7, command, ProtoWordData, word: true);

    private ushort? Run(byte slave7, byte command, byte protocol, bool word)
    {
        // 忙碌就不搶——PSU 電壓不是值得冒匯流排風險的資料
        if ((io.In(ioBase + HstSts) & StsHostBusy) != 0)
            return null;
        if (!io.Out(ioBase + HstSts, StsClearMask)
            || !io.Out(ioBase + XmitSlva, (byte)((slave7 << 1) | 1))
            || !io.Out(ioBase + HstCmd, command)
            || !io.Out(ioBase + HstCnt, (byte)(protocol | CntStart)))
            return null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            byte? sts = io.In(ioBase + HstSts);
            if (sts is null) return null;
            if ((sts.Value & StsIntr) != 0)
            {
                if ((sts.Value & StsErrorMask) != 0) return null;
                byte d0 = io.In(ioBase + HstD0) ?? 0;
                if (!word) return d0;
                byte d1 = io.In(ioBase + HstD1) ?? 0;
                return (ushort)(d0 | (d1 << 8));
            }
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                io.Out(ioBase + HstCnt, CntKill);   // 卡住的交易收掉，不留給下一個使用者
                io.Out(ioBase + HstSts, StsClearMask);
                return null;
            }
            System.Threading.Thread.SpinWait(64);
        }
    }
}
