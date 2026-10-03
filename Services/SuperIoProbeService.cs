namespace XinSpect;

/// <summary>
/// Super I/O 的純解碼器（V7 WP31／A28 的第一層）：晶片 ID 暫存器（0x20/0x21）的原始值處理。
/// 晶片名稱對照表刻意未納入——各廠商 ID 佈局文獻零散，錯誤對照比沒有對照更糟（誠實原則）；
/// 本層只負責「讀到什麼」，名稱對照留給有出處的知識庫（V7 D2）。
/// </summary>
public static class SuperIo
{
    /// <summary>組回 16 位元晶片 ID；兩位元組皆全 F（無裝置）或全 0（未解碼狀態）回 null。</summary>
    [SpecRef("coreboot util/superiotool：LDN0 暫存器 0x20/0x21 為晶片 ID（Winbond/ITE/Nuvoton/Fintek/SMSC 等多數廠商共通佈局）")]
    public static ushort? DecodeChipId(byte hi, byte lo)
        => (hi, lo) is (0xFF, 0xFF) or (0x00, 0x00) ? null : (ushort)((hi << 8) | lo);

    /// <summary>進入設定模式的魔術序列。Winbond／ITE／Nuvoton／Fintek 用 0x87,0x87；SMSC 用 0x55。</summary>
    public static readonly byte[][] EntrySequences = [[0x87, 0x87], [0x55]];

    /// <summary>退出設定模式的命令（寫到索引埠）。</summary>
    public const byte ExitCommand = 0xAA;

    /// <summary>晶片 ID 與廠商 ID 的 LDN0 暫存器位移（多數廠商共通）。</summary>
    public const byte RegChipIdHigh = 0x20, RegChipIdLow = 0x21, RegVendorIdHigh = 0x22, RegVendorIdLow = 0x23;
}

/// <summary>
/// Super I/O 探測（V7 WP31／A28）：對 LPC 的兩組標準設定埠（0x2E/0x2F、0x4E/0x4F）逐一嘗試
/// 進入設定模式、唯讀晶片 ID／廠商 ID、<b>必以 finally 退出設定模式</b>——把晶片留在設定模式是
/// 系統風險，本服務的所有路徑（含例外）都不允許。名稱對照表未納入，只報原始 ID（見 <see cref="SuperIo"/>）。
/// </summary>
public static class SuperIoProbeService
{
    private const string Category = "主機板";

    public static IReadOnlyList<HardwareFact> Collect(IIoPortAccess io, DateTimeOffset at) =>
    [
        Probe(io, 0x2E, at),
        Probe(io, 0x4E, at),
    ];

    private static HardwareFact Probe(IIoPortAccess io, uint indexPort, DateTimeOffset at)
    {
        string key = $"sio.0x{indexPort:x2}";
        string name = $"Super I/O（I/O 0x{indexPort:X2}）";
        string source = $"I/O 0x{indexPort:X2}/0x{indexPort + 1:X2} 設定模式唯讀";
        if (!io.Available)
            return Unavailable(key, name, source, at, FactAvailability.InsufficientPrivilege,
                io.UnavailableReason ?? "缺 I/O 埠存取");

        bool inConfigMode = false;
        try
        {
            foreach (var sequence in SuperIo.EntrySequences)
            {
                foreach (var b in sequence)
                {
                    if (!io.OutByte(indexPort, b)) return IoFail(key, name, source, at);
                    inConfigMode = true;
                }

                byte? hi = ReadReg(io, indexPort, SuperIo.RegChipIdHigh);
                byte? lo = ReadReg(io, indexPort, SuperIo.RegChipIdLow);
                if (hi is null || lo is null)
                    return IoFail(key, name, source, at); // I/O 存取失敗是讀取錯誤，不得與「無裝置」混為一談
                var id = SuperIo.DecodeChipId(hi.Value, lo.Value);
                if (id is null)
                {
                    Exit(io, indexPort, ref inConfigMode);
                    continue; // 換下一個進入序列
                }

                byte? vhi = ReadReg(io, indexPort, SuperIo.RegVendorIdHigh);
                byte? vlo = ReadReg(io, indexPort, SuperIo.RegVendorIdLow);
                string vendorText = vhi is not null && vlo is not null
                    ? $"、廠商 ID 0x{(vhi.Value << 8) | vlo.Value:X4}（原始值）"
                    : "、廠商 ID 暫存器讀取失敗";
                string nameText = SuperIoKnowledge.ChipName(id.Value) is { } chipName ? $"（{chipName}）" : "（名稱對照未收錄，出處化知識庫待擴充）";
                Exit(io, indexPort, ref inConfigMode);
                return new HardwareFact(key, Category, name,
                    $"晶片 ID 0x{id.Value:X4}{nameText}{vendorText}", "", source, FactTrustLevel.Measured, false, at, id.Value);
            }
            return Unavailable(key, name, source, at, FactAvailability.NotSupported,
                "無裝置回應（兩種進入序列的 ID 暫存器都是全 F／全 0）——該埠可能沒有 Super I/O 或被其他裝置占用");
        }
        finally
        {
            if (inConfigMode) io.OutByte(indexPort, SuperIo.ExitCommand); // 保證退出——留在設定模式是系統風險
        }
    }

    private static byte? ReadReg(IIoPortAccess io, uint indexPort, byte register)
    {
        if (!io.OutByte(indexPort, register)) return null;
        return io.InByte(indexPort + 1);
    }

    private static void Exit(IIoPortAccess io, uint indexPort, ref bool inConfigMode)
    {
        io.OutByte(indexPort, SuperIo.ExitCommand);
        inConfigMode = false;
    }

    private static HardwareFact IoFail(string key, string name, string source, DateTimeOffset at) =>
        Unavailable(key, name, source, at, FactAvailability.ReadError, "I/O 埠存取在探測途中失敗");

    private static HardwareFact Unavailable(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
