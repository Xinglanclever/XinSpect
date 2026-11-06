namespace XinSpect;

/// <summary>
/// 核心存取後端的能力集。每一種能力對應 WinRing0Bridge 現有的一個選用方法組；
/// 後端只申報自己<b>真的</b>有的能力，選擇器按能力逐項找路，不假設「一個後端全包」。
/// </summary>
[Flags]
public enum KernelCapability
{
    None = 0,
    /// <summary>讀 MSR（RDMSR）。</summary>
    MsrRead = 1 << 0,
    /// <summary>寫 MSR（WRMSR）——高危；寫入永遠經同意閘門＋<see cref="WriteGate"/> 稽核。</summary>
    MsrWrite = 1 << 1,
    /// <summary>讀 PCI 設定空間。</summary>
    PciRead = 1 << 2,
    /// <summary>寫 PCI 設定空間。</summary>
    PciWrite = 1 << 3,
    /// <summary>讀 I/O 埠（SMBus／SPD 直讀的地基）。</summary>
    IoPortRead = 1 << 4,
    /// <summary>寫 I/O 埠。</summary>
    IoPortWrite = 1 << 5,
    /// <summary>讀實體記憶體（MMIO）。</summary>
    MmioRead = 1 << 6,
}

/// <summary>
/// 後端的信任層級——這是「來源品質標示」，不是保證（與四級可信度同一哲學）：
/// <see cref="Sandboxed"/>＝能力被白名單與沙箱收口（PawnIO）；<see cref="Universal"/>＝
/// 萬能鑰匙驅動，驅動載入期間同機其他進程同樣可用（WinRing0）；<see cref="Native"/>＝
/// 自研簽章驅動（BlueSquadron，未來）。
/// </summary>
public enum KernelTrustTier
{
    /// <summary>沙箱化：模組雜湊白名單，任意代碼進不了核心。</summary>
    Sandboxed = 0,
    /// <summary>萬能鑰匙：能力不被收口，暴露面延伸到同機所有進程。</summary>
    Universal = 1,
    /// <summary>自研原生驅動：信任等級取決於自身稽核與簽章，介面上單獨標示。</summary>
    Native = 2,
}

/// <summary>MSR 寫入的結果：成功與否都帶一句人話原因（進超頻頁與稽核帳本）。</summary>
public sealed record KernelWriteOutcome(bool Succeeded, string? Reason);

/// <summary>
/// 核心存取後端介面。設計約束：
/// <list type="number">
/// <item><b>三態</b>：讀不到一律回 null／false 帶原因，絕不以 0 頂替（六態哲學在後端層的落地）。</item>
/// <item><b>能力誠實</b>：<see cref="Capabilities"/> 只申報當前真的可用的能力；能力會隨環境變化
/// （HVCI 開關、驅動載入成敗），呼叫端不快取。</item>
/// <item><b>寫入收口</b>：所有寫入方法內部記 <see cref="WriteGate"/>，服務層不必各自接線。</item>
/// <item><b>會話引用計數</b>：實作自行保證「先做完的不把還在讀的關掉」（WinRing0Bridge 既有教訓）。</item>
/// </list>
/// </summary>
public interface IKernelBackend : IDisposable
{
    /// <summary>穩定識別碼（進事實鍵與設定檔）：如 "pawnio"、"winring0"、"bluesquadron"。</summary>
    string Id { get; }

    /// <summary>顯示名（進驅動就緒卡與事實值）。</summary>
    string DisplayName { get; }

    /// <summary>信任層級（介面單獨標示，不與能力混寫）。</summary>
    KernelTrustTier Tier { get; }

    /// <summary>目前可用的能力集（動態；不可用時回 <see cref="KernelCapability.None"/>）。</summary>
    KernelCapability Capabilities { get; }

    /// <summary>不可用或降級的原因（Capabilities 為 None 或少於申報值時必須非空）。</summary>
    string? UnavailableReason { get; }

    /// <summary>讀 MSR。讀不到回 null（上層標「無法判定」，不頂 0）。</summary>
    ulong? ReadMsr(uint index);

    /// <summary>寫 MSR。呼叫端必須已過同意閘門；本方法只負責執行＋記 <see cref="WriteGate"/>。</summary>
    KernelWriteOutcome WriteMsr(uint index, ulong value, string caller);

    /// <summary>讀 PCI 設定空間（bus/device/function/register）。</summary>
    uint? ReadPciConfig(byte bus, byte device, byte function, uint register);

    /// <summary>寫 PCI 設定空間。記 <see cref="WriteGate"/>。</summary>
    KernelWriteOutcome WritePciConfig(byte bus, byte device, byte function, uint register, uint value, string caller);

    /// <summary>讀 I/O 埠（位元組）。</summary>
    byte? ReadIoPortByte(uint port);

    /// <summary>寫 I/O 埠（位元組）。記 <see cref="WriteGate"/>。</summary>
    KernelWriteOutcome WriteIoPortByte(uint port, byte value, string caller);

    /// <summary>讀實體記憶體區塊（MMIO）。</summary>
    byte[]? ReadMemoryBlock(ulong address, int length);
}

/// <summary>後端的靜態描述（選擇器排序與介面渲染用），不攜帶執行期狀態。</summary>
public sealed record KernelBackendDescriptor(string Id, string DisplayName, KernelTrustTier Tier);
