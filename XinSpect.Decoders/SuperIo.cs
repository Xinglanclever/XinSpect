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
