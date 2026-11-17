using System.Collections.ObjectModel;
using System.Diagnostics;

namespace XinSpect;

/// <summary>
/// RTC 交易的所有權接縫；只有已協調 OS／韌體、0x70/0x71 及 NMI 狀態的後端才可實作。
/// Available 只表示埠能力，不能代替所有權。一般 WinRing0IoPortAccess 不提供此承諾。
/// </summary>
public interface ICmosReadOwnership : IIoPortAccess
{
    /// <summary>
    /// 非阻塞嘗試取得整趟 RTC 讀取的 lease；null 代表不能建立安全所有權。
    /// lease 有效期間須協調所有競爭者、保證 bit7=0 選址不改變 NMI 狀態，並在 Dispose 還原／交還所有權。
    /// 後端須已核相容 RTC 地址圖及 0x10–0x2F 無讀取副作用的 RAM 語意，不可只按埠號推定相容。
    /// 實作仍須保留 OutByte 的既有兩級風險 gate；此 lease 不授權繞過 gate。
    /// 一個程式內 lock 不能滿足 OS／韌體所有權契約。
    /// </summary>
    IDisposable? TryAcquireCmosReadLease();
    string? CmosOwnershipUnavailableReason { get; }
}

/// <summary>
/// 稀疏的已讀暫存器；未讀地址不存在，尤其 0x0C 不以 0／FF 冒充已讀。
/// Availability 為整趟交易狀態；RTC 無法取得一致樣本時另以 TimeUnavailableReason 表示。
/// </summary>
public sealed record CmosReadSnapshot(
    FactAvailability Availability,
    string? UnavailableReason,
    IReadOnlyDictionary<byte, byte> Registers,
    string? TimeUnavailableReason = null);

/// <summary>
/// CMOS/RTC 有界讀取：只讀 VRT、時間格式及 PC-AT 校驗和所需地址；永不選取 read-to-clear 0x0C。
/// 不寫資料埠 0x71、不凍結 RTC。先取得 OS／韌體協調所有權，再經原 OutByte gate 選址。
/// 本版一般 WinRing0 後端缺少 RTC／NMI 所有權，回 NotSupported；不等於機器沒有 RTC。
/// 廠商 0x10–0x2D 只供 PC-AT 加總，不推定設定區語意或全平台支援。
/// </summary>
public static class CmosService
{
    private const string Category = "CMOS";
    private const uint IndexPort = 0x70, DataPort = 0x71;
    private const int MaxTimeAttempts = 3, MaxRegisterReads = 81;
    private static readonly TimeSpan ReadBudget = TimeSpan.FromMilliseconds(100);
    private const string OwnershipMissing = "本版未建立 RTC 0x70/0x71 與 NMI 的 OS／韌體協調所有權，禁止原始 CMOS 存取；不代表沒有 RTC 硬體";

    public static IReadOnlyList<HardwareFact> Collect(IIoPortAccess io, DateTimeOffset at,
        Func<TimeSpan>? elapsed = null)
    {
        var snapshot = Snapshot(io, elapsed);
        if (snapshot.Availability != FactAvailability.Present)
            return CmosFactKeys().Zip(CmosFactNames()).Select(p => Unavailable(p.First, p.Second,
                "CMOS 0x70/0x71 受限讀取", at, snapshot.Availability, snapshot.UnavailableReason!)).ToList();
        return [VrtFact(snapshot.Registers, at), TimeFact(snapshot, at), ChecksumFact(snapshot.Registers, at)];
    }

    /// <summary>
    /// 最多 81 次 register 讀取、三組雙樣本嘗試、100ms 單調鐘預算；每次 I/O 前後檢查。
    /// 同步底層 I/O 若卡住，預算不能強制取消；本服務不重送失敗 I/O、不提前釋放 lease。
    /// elapsed 只供注入單調時鐘；生產預設 Stopwatch，沒有 244µs 排程保證。
    /// </summary>
    public static CmosReadSnapshot Snapshot(IIoPortAccess io, Func<TimeSpan>? elapsed = null)
    {
        ArgumentNullException.ThrowIfNull(io);
        if (!io.Available)
            return Failure(FactAvailability.InsufficientPrivilege, io.UnavailableReason ?? "缺 I/O 埠存取");
        if (io is not ICmosReadOwnership owner)
            return Failure(FactAvailability.NotSupported, OwnershipMissing);
        using var lease = owner.TryAcquireCmosReadLease();
        if (lease is null)
            return Failure(FactAvailability.NotSupported, owner.CmosOwnershipUnavailableReason ?? OwnershipMissing);

        var stopwatch = Stopwatch.StartNew();
        var getElapsed = elapsed ?? (() => stopwatch.Elapsed);
        var regs = new Dictionary<byte, byte>();
        string? ioError = null;
        int reads = 0;
        bool InBudget()
        {
            var duration = getElapsed();
            return duration >= TimeSpan.Zero && duration < ReadBudget;
        }
        byte? Read(byte register)
        {
            // Fail closed before selecting anything outside the consumed, non-clearing allowlist.
            if (!Allowed(register)) { ioError = "CMOS 地址未列入無清旗標副作用白名單"; return null; }
            if (reads >= MaxRegisterReads || !InBudget())
            { ioError = "CMOS 讀取超過有界交易／100ms 時間預算"; return null; }
            reads++;
            // No alternate protocol or bridge-only bypass: the existing write gate remains in force.
            if (!io.OutByte(IndexPort, register))
            { ioError = "CMOS 選址被拒絕或失敗；未改走其他通路"; return null; }
            if (!InBudget()) { ioError = "CMOS 選址後超過100ms時間預算"; return null; }
            byte? value = io.InByte(DataPort);
            if (value is null) { ioError = "CMOS 資料埠讀取失敗"; return null; }
            if (!InBudget()) { ioError = "CMOS 讀取完成後超過100ms時間預算"; return null; }
            return value;
        }

        if (Read(0x0D) is not { } vrt) return Failure(FactAvailability.ReadError, ioError!);
        regs[0x0D] = vrt;
        for (byte register = 0x10; register <= 0x2F; register++)
        {
            if (Read(register) is not { } value) return Failure(FactAvailability.ReadError, ioError!);
            regs[register] = value;
        }

        string timeError = "RTC 在三組有界雙樣本內未取得一致時間；不發布混合時間";
        RtcSample? Sample()
        {
            if (Read(0x0A) is not { } a0) return null;
            if ((a0 & 0x80) != 0)
            { timeError = "曾觀察到更新進行中（UIP=1）；三組有界重讀內仍未建立一致時間"; return null; }
            if (Read(0x0B) is not { } b0 || Read(0x00) is not { } second0 ||
                Read(0x02) is not { } minute || Read(0x04) is not { } hour ||
                Read(0x00) is not { } second1 || Read(0x0B) is not { } b1 ||
                Read(0x0A) is not { } a1) return null;
            // Read-back of seconds catches a rollover even when UIP is clear again at the end.
            // B includes SET and format bits: never alter SET merely to obtain a sample.
            if ((a1 & 0x80) != 0 || a0 != a1 || b0 != b1 || (b1 & 0x80) != 0 || second0 != second1)
                return null;
            return new(second1, minute, hour, a1, b1);
        }

        for (int attempt = 0; attempt < MaxTimeAttempts; attempt++)
        {
            var first = Sample();
            if (ioError is not null) return Failure(FactAvailability.ReadError, ioError);
            if (first is null) continue;
            var second = Sample();
            if (ioError is not null) return Failure(FactAvailability.ReadError, ioError);
            if (second is null || first != second) continue;
            regs[0x00] = second.Second;
            regs[0x02] = second.Minute;
            regs[0x04] = second.Hour;
            regs[0x0A] = second.StatusA;
            regs[0x0B] = second.StatusB;
            return new(FactAvailability.Present, null, new ReadOnlyDictionary<byte, byte>(regs));
        }
        // VRT and checksum remain real readings; time fields are absent rather than zero-filled.
        return new(FactAvailability.Present, null, new ReadOnlyDictionary<byte, byte>(regs), timeError);
    }

    private sealed record RtcSample(byte Second, byte Minute, byte Hour, byte StatusA, byte StatusB);
    private static bool Allowed(byte register) => register is 0x00 or 0x02 or 0x04 or 0x0A or 0x0B or 0x0D
        || register is >= 0x10 and <= 0x2F;
    private static CmosReadSnapshot Failure(FactAvailability state, string reason) =>
        new(state, reason, new ReadOnlyDictionary<byte, byte>(new Dictionary<byte, byte>()));

    private static HardwareFact VrtFact(IReadOnlyDictionary<byte, byte> regs, DateTimeOffset at)
    {
        const string key = "cmos.rtc_valid", name = "RTC 電池與時間有效位", source = "CMOS 0x0D bit7（VRT）";
        byte vrt = regs[0x0D];
        return vrt == 0xFF
            ? Unavailable(key, name, source, at, FactAvailability.NotSupported, "暫存器回全 F——此平台未實作 VRT 位，無法判斷")
            : new HardwareFact(key, Category, name,
                Cmos.VrtValid(vrt) ? "有效（VRT=1）：電池供電正常" : "RTC 掉電（VRT=0）：電池失效或曾斷電，時間與設定可能已重置",
                "", source, FactTrustLevel.Measured, false, at, Cmos.VrtValid(vrt) ? 1 : 0);
    }

    private static HardwareFact TimeFact(CmosReadSnapshot snapshot, DateTimeOffset at)
    {
        const string key = "cmos.rtc_time", name = "RTC 時鐘", source = "CMOS 0x00/02/04、0x0A/0x0B；前後UIP／格式檢查與一致雙樣本";
        if (snapshot.TimeUnavailableReason is { } reason)
            return Unavailable(key, name, source, at, FactAvailability.ReadError, reason);
        var regs = snapshot.Registers;
        var (_, binary, hour24) = Cmos.DecodeStatus(regs[0x0A], regs[0x0B]);
        var t = Cmos.DecodeTime(regs[0x00], regs[0x02], regs[0x04], binary, hour24);
        return t is null
            ? Unavailable(key, name, source, at, FactAvailability.ReadError,
                $"格式異常（raw {regs[0x04]:X2}:{regs[0x02]:X2}:{regs[0x00]:X2}，{(binary ? "二進位" : "BCD")}/{(hour24 ? "24h" : "12h")}）——不解碼垃圾")
            : new HardwareFact(key, Category, name,
                $"{t.Value.Hour:00}:{t.Value.Minute:00}:{t.Value.Second:00}（RTC 本地時間，{(binary ? "二進位" : "BCD")}/{(hour24 ? "24h" : "12h")}格式）",
                "", source, FactTrustLevel.Measured, false, at);
    }

    private static HardwareFact ChecksumFact(IReadOnlyDictionary<byte, byte> regs, DateTimeOffset at)
    {
        const string key = "cmos.checksum", name = "PC-AT 設定區校驗和", source = "CMOS 0x2E/0x2F 覆蓋 0x10–0x2D";
        // Dense buffer is only an adapter for the pure checksum decoder, which consumes 0x10..0x2F.
        // It is never exposed as a raw CMOS snapshot of the unobserved addresses.
        var checksumInput = new byte[0x30];
        for (byte address = 0x10; address <= 0x2F; address++) checksumInput[address] = regs[address];
        bool matches = Cmos.ChecksumMatches(checksumInput);
        return new HardwareFact(key, Category, name,
            matches
                ? "校驗和相符——設定區自上次儲存後未被更動（或 BIOS 恰好維護一致）"
                : "校驗和不符——設定區曾被更動、未初始化，或此 BIOS 不維護 PC-AT 校驗和（依機種而異，非必然異常）",
            "", source, FactTrustLevel.Measured, false, at, matches ? 1 : 0);
    }

    private static string[] CmosFactKeys() => ["cmos.rtc_valid", "cmos.rtc_time", "cmos.checksum"];
    private static string[] CmosFactNames() => ["RTC 電池與時間有效位", "RTC 時鐘", "PC-AT 設定區校驗和"];
    private static HardwareFact Unavailable(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
