namespace XinSpect;

/// <summary>
/// 實體位址 MMIO 唯讀區塊的可注入接縫——自家核心驅動（深層暫存器計畫 Phase 3）的契約面。
/// 只讀不寫；讀不到回 null 由上層標三態，絕不回全 0／全 0xFF 假裝成功。
/// 之後自家驅動就緒時只換這一層實作，呼叫端不知底下是誰。
/// </summary>
public interface IMmioReader
{
    bool Available { get; }
    string? UnavailableReason { get; }

    /// <summary>服務本機的後端名（如「WinRing0 實體記憶體」）；未標示者回 null（事實層會誠實標「後端名未標示」）。</summary>
    string? BackendName => null;

    /// <summary>最近一次 ReadBlock 失敗的細節（null＝尚無失敗或無細節）；讀不到時上層併入原因，讓「為什麼讀不到」說得清楚。</summary>
    string? LastFailReason => null;

    /// <summary>讀實體位址起 length 位元組；讀不到回 null（上層標 ReadError）。</summary>
    byte[]? ReadBlock(ulong physicalAddress, int length);
}

/// <summary>自家核心驅動未載入時的固定「讀不到」實作：一律 Available=false，原因明確標示缺驅動，不在生產機自動觸發核心碼。</summary>
public sealed class NotLoadedMmioReader : IMmioReader
{
    public const string Reason = "缺自家核心驅動（未載入）：MMIO 讀取未執行";

    public bool Available => false;
    public string? UnavailableReason => Reason;
    public byte[]? ReadBlock(ulong physicalAddress, int length) => null;
}

/// <summary>帶自訂原因的固定「讀不到」實作：後端裁決全數落空時，把每個後端為什麼不行串成一句誠實的原因。</summary>
public sealed class UnavailableMmioReader : IMmioReader
{
    public UnavailableMmioReader(string reason) => Reason = reason;

    public string Reason { get; }

    public bool Available => false;
    public string? UnavailableReason => Reason;
    public byte[]? ReadBlock(ulong physicalAddress, int length) => null;
}
