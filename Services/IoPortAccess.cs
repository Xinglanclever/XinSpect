namespace XinSpect;

/// <summary>
/// I/O 埠存取的可注入接縫（V7 WP1）。byte 寬度——WinRing0 橋接只提供 byte in/out，
/// word/dword 須等自寫驅動擴充，這是如實的能力界線，不假裝有。
/// </summary>
/// <remarks>
/// ⚠ <see cref="OutByte"/> 是會改變機器狀態的能力面。防線不在這裡，在服務層：
/// 每個消費者只准寫自己的選址埠（如 CMOS 讀取寫 0x70 選 index、SMBus 寫控制器命令暫存器），
/// 資料埠（如 0x71）在唯讀服務裡連程式碼路徑都不存在。
/// </remarks>
public interface IIoPortAccess
{
    bool Available { get; }
    string? UnavailableReason { get; }

    /// <summary>讀一個埠位元組（in）；不可用或失敗回 null（上層標三態）。</summary>
    byte? InByte(uint port);

    /// <summary>寫一個埠位元組（out）；不可用或失敗回 false。僅供服務層白名單內的選址用。</summary>
    bool OutByte(uint port, byte value);
}

/// <summary>以 WinRing0 橋接實作 I/O 埠存取；載不進或橋接缺 I/O 能力時誠實降級。</summary>
public sealed class WinRing0IoPortAccess : IIoPortAccess, IDisposable
{
    private readonly WinRing0Bridge _bridge;

    public WinRing0IoPortAccess() : this(WinRing0Bridge.Create()) { }

    /// <summary>注入既有橋接（測試／共用會話用）；取得所有權，Dispose 時交還。</summary>
    public WinRing0IoPortAccess(WinRing0Bridge bridge) => _bridge = bridge;

    public bool Available => _bridge.IoPortAvailable;

    public string? UnavailableReason => !_bridge.Available ? _bridge.Error
        : !_bridge.IoPortAvailable ? "WinRing0 驅動未提供 I/O 埠存取（Ring0.ReadIoPort/WriteIoPort 缺少）"
        : null;

    public byte? InByte(uint port) => _bridge.ReadIoPortByte(port);

    public bool OutByte(uint port, byte value) => _bridge.WriteIoPortByte(port, value);

    public void Dispose() => _bridge.Dispose();
}

/// <summary>固定不可用的 I/O 埠後端（測試／降級用）：一律 Available=false 帶原因，存取全部短路。</summary>
public sealed class UnavailableIoPortAccess : IIoPortAccess
{
    public UnavailableIoPortAccess(string reason) => Reason = reason;

    public string Reason { get; }

    public bool Available => false;
    public string? UnavailableReason => Reason;
    public byte? InByte(uint port) => null;
    public bool OutByte(uint port, byte value) => false;
}
