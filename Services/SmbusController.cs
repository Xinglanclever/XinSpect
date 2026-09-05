using System.Diagnostics;
using System.Threading;

namespace XinSpect;

/// <summary>
/// I/O 埠讀寫的最小抽象。存在的唯一理由是讓 <see cref="SmbusController"/> 的狀態機
/// 能在測試裡被完整驅動——SMBus 是共享匯流排，驗證狀態機的過程本身不可以碰它。
/// </summary>
/// <remarks><c>In</c> 回 <c>null</c> 代表讀取本身失敗（橋接不在了），<b>不是</b>讀到 0。</remarks>
public interface ISmbusIo
{
    byte? In(uint port);
    bool Out(uint port, byte value);
}

/// <summary>把 <see cref="ISmbusIo"/> 接到真實的 WinRing0 橋接上。</summary>
public sealed class WinRing0SmbusIo(WinRing0Bridge bridge) : ISmbusIo
{
    public byte? In(uint port) => bridge.ReadIoPortByte(port);
    public bool Out(uint port, byte value) => bridge.WriteIoPortByte(port, value);
}

/// <summary>讀 PCI 設定空間一個 DWORD；讀不到回 <c>null</c>。</summary>
public delegate uint? PciDwordReader(byte bus, byte device, byte function, uint register);

/// <summary>SMBus 主機控制器的位置與組態。</summary>
/// <param name="IoBase">SMB_BASE：控制器暫存器在 I/O 空間的基底位址。</param>
/// <param name="SpdWriteDisabled">HOSTC 的 SPD_WD：控制器層面已停用對 SPD 位址的寫入。純為診斷用——本專案本來就不寫。</param>
public sealed record SmbusLocation(byte Bus, byte Device, byte Function,
                                   uint IoBase, ushort VendorId, ushort DeviceId,
                                   bool SpdWriteDisabled);

/// <summary>
/// 在 PCI 設定空間裡找出 SMBus 主機控制器。
/// </summary>
/// <remarks>
/// <para>
/// 用類別碼找（基類 0x0C 序列匯流排／子類 0x05 SMBus）而<b>不寫死 0:31.4</b>：
/// Intel 在 100 系列 PCH 把 SMBus 從 function 3 搬到 function 4，寫死等於挑一代賭。
/// 掃法沿用 <see cref="PcieLinkService"/> 既有的組態空間走訪方式。
/// </para>
/// <para>
/// 只掃 bus 0。SMBus 主機控制器是 PCH／FCH 的一部分，永遠掛在根匯流排上；
/// 掃滿 256 條匯流排只是多花 65536 次讀取去找同一個東西。
/// </para>
/// </remarks>
public static class SmbusDiscovery
{
    private const uint RegVendorDevice = 0x00, RegClassCode = 0x08, RegSmbBase = 0x20, RegHostC = 0x40;

    // HOSTC（Intel 100／200／300 系列 PCH 的定義）
    private const uint HostCI2cEn = 1 << 2;
    private const uint HostCSpdWd = 1 << 4;

    private const ushort VendorIntel = 0x8086;

    /// <summary>
    /// 找到就回位置，否則回 <c>null</c>；<paramref name="diagnostic"/> 一律填上人看得懂的原因，
    /// 因為「讀不到」在這個專案裡必須說得出缺的是哪一邊。
    /// </summary>
    public static SmbusLocation? Find(PciDwordReader read, out string diagnostic)
    {
        for (byte dev = 0; dev < 32; dev++)
        {
            for (byte fn = 0; fn < 8; fn++)
            {
                uint id = read(0, dev, fn, RegVendorDevice) ?? 0xFFFFFFFF;
                if (id is 0xFFFFFFFF or 0) continue;

                uint cls = read(0, dev, fn, RegClassCode) ?? 0;
                if ((cls >> 24) != 0x0C || ((cls >> 16) & 0xFF) != 0x05) continue;

                ushort vendor = (ushort)(id & 0xFFFF), device = (ushort)(id >> 16);
                string at = $"0.{dev}.{fn}";

                if (vendor != VendorIntel)
                {
                    // AMD FCH 的 SMBus 基底不在 PCI BAR 裡，是躲在 0xCD6／0xCD7 索引埠後面的
                    // PMIO 暫存器。照 BAR 硬算會得到一個看似合理卻完全錯誤的位址，然後往
                    // 不相干的 I/O 埠寫東西——那是這類程式最不該犯的錯。
                    diagnostic = vendor is 0x1022 or 0x1002
                        ? $"在 {at} 找到 AMD／ATI 的 SMBus 控制器（{vendor:X4}:{device:X4}）。"
                          + "AMD FCH 的基底位址走 0xCD6／0xCD7 索引埠而非 PCI BAR，且本機無 AMD 硬體可驗證，故不實作。"
                        : $"在 {at} 找到廠商 {vendor:X4} 的 SMBus 控制器，本讀取器只驗證過 Intel PCH。";
                    return null;
                }

                uint hostc = read(0, dev, fn, RegHostC) ?? 0;
                if ((hostc & HostCI2cEn) != 0)
                {
                    diagnostic = $"{at} 的 SMBus 控制器處於 I2C 模式（HOSTC.I2C_EN＝1），"
                               + "該模式下的協定語意與 SMBus 不同，本讀取器拒絕使用。";
                    return null;
                }

                uint bar = read(0, dev, fn, RegSmbBase) ?? 0;
                if (bar is 0xFFFFFFFF or 0 || (bar & 1) == 0)
                {
                    diagnostic = $"{at} 的 SMB_BASE 不是有效的 I/O 位址（讀到 0x{bar:X8}）——"
                               + "BAR 未配置或不在 I/O 空間，無法存取控制器暫存器。";
                    return null;
                }

                uint ioBase = bar & 0xFFE0;
                bool spdWd = (hostc & HostCSpdWd) != 0;
                diagnostic = $"SMBus 控制器 {at}（{vendor:X4}:{device:X4}），SMB_BASE＝0x{ioBase:X4}"
                           + (spdWd ? "，HOSTC.SPD_WD 已設（控制器層面停用 SPD 寫入）" : "");
                return new SmbusLocation(0, dev, fn, ioBase, vendor, device, spdWd);
            }
        }

        diagnostic = "在 bus 0 上找不到 SMBus 主機控制器（PCI 類別碼 0x0C05）。";
        return null;
    }
}

/// <summary>
/// 上一次 SMBus 交易的結果分類。
/// </summary>
/// <remarks>
/// 之所以要分類而不是只留一句錯誤字串：呼叫端必須能分辨「這個位址上沒有裝置」（空插槽，正常）
/// 與「有裝置但讀不到」（那是驗機時的一筆發現）。用字串比對去分辨這兩件事，
/// 是那種改一次措辭就悄悄壞掉的程式碼。
/// </remarks>
public enum SmbusStatus
{
    Ok,
    /// <summary>DEV_ERR：位址上沒有裝置，或裝置不接受這個命令。</summary>
    NoDevice,
    /// <summary>BUS_ERR：匯流排衝突或仲裁失敗——通常代表有別人也在用。</summary>
    BusError,
    /// <summary>FAILED：交易被中止。</summary>
    TransactionFailed,
    /// <summary>逾時。已送出 KILL 收尾，不重試。</summary>
    Timeout,
    /// <summary>I/O 埠存取本身不可用（橋接沒了或沒有權限）。</summary>
    IoUnavailable,
}

/// <summary>
/// Intel PCH SMBus 主機控制器的交易狀態機（只讀 SPD，別的都不做）。
/// </summary>
/// <remarks>
/// <para>
/// <b>裝置位址白名單是這個類別存在的主要理由。</b>只有 0x50–0x57（SPD EEPROM，讀）與
/// 0x36／0x37（DDR4 切頁，寫）進得來，其餘一律拋例外。SPD 的寫入保護指令
/// （SWP0–2、CWP，位址 0x31／0x33／0x34／0x35）與 EEPROM 資料區寫入<b>連程式碼路徑都不存在</b>。
/// 寫壞一條模組的 SPD，主機板會認不出它、開機直接不過，而且沒有軟體層的復原方式。
/// </para>
/// <para>
/// <b>切頁本身是一次匯流排寫入</b>——寫到 SPA 裝置位址，不是寫到 EEPROM 資料區。
/// 不把這件事寫清楚，後人讀到「絕不寫入」會以為被違反了。同理，發起一次 SMBus「讀取」
/// 在協定上必須先寫控制器的位址／命令／控制暫存器；那些是控制器暫存器，不是記憶體模組。
/// </para>
/// <para>
/// <b>逾時用碼錶而不是執行緒看門狗</b>（與 <see cref="DiskIo"/> 不同）：驅動的 in／out
/// 是立即返回的，這裡真正會卡住的是「等 HOST_BUSY 放掉」與「等完成訊號」這兩個由我們自己
/// 控制的輪詢圈，所以期限判斷放在圈內就夠，不需要為它另外洩一條執行緒。
/// </para>
/// <para>
/// <b>逾時後送 KILL 收尾，但不重試。</b>KILL 不是重試——它是把卡住的交易收掉，免得
/// HOST_BUSY 一直亮著把下一個用匯流排的人（CPU-Z 也算）全部擋住。收完就整條放棄。
/// </para>
/// </remarks>
public sealed class SmbusController(ISmbusIo io, uint ioBase,
                                   int transactionTimeoutMs = SmbusController.DefaultTransactionTimeoutMs)
    : ISpdBus
{
    /// <summary>單筆交易的上限。100 kHz 下一個位元組交易約 0.2 ms；到了 10 ms 就已經不對了。</summary>
    public const int DefaultTransactionTimeoutMs = 10;

    // 暫存器位移（相對 SMB_BASE）
    private const uint HstSts = 0, HstCnt = 2, HstCmd = 3, XmitSlva = 4, HstD0 = 5;

    // HST_STS：狀態位寫 1 清除，HOST_BUSY 唯讀，INUSE_STS 是讀取即占用的旗號
    private const byte StsHostBusy = 0x01, StsIntr = 0x02, StsDevErr = 0x04,
                       StsBusErr = 0x08, StsFailed = 0x10, StsInUse = 0x40;
    private const byte StsErrorMask = StsDevErr | StsBusErr | StsFailed;
    private const byte StsClearMask = 0xBE;          // 刻意不含 INUSE(0x40)：清狀態不等於放掉旗號

    // HST_CNT
    private const byte CntKill = 0x02, CntStart = 0x40;
    private const byte ProtoByte = 0x01 << 2, ProtoByteData = 0x02 << 2;

    private bool _acquired;

    /// <summary>最後一次失敗的原因（人看得懂的中文）。成功時為空字串。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>最後一次交易的結果分類。呼叫端用它分辨「空插槽」與「有裝置但讀不到」。</summary>
    public SmbusStatus LastStatus { get; private set; } = SmbusStatus.Ok;

    /// <inheritdoc/>
    public string Description => $"PCH SMBus（i801，SMB_BASE＝0x{ioBase:X4}）";

    /// <summary>SPD EEPROM 的八個裝置位址——本讀取器唯一允許讀取的範圍。</summary>
    public static bool IsSpdReadAddress(byte slave7) => SpdBusAddresses.IsSpdRead(slave7);

    /// <summary>DDR4 的兩個切頁位址（SPA0／SPA1）——本讀取器唯一允許寫入的範圍。</summary>
    public static bool IsPageSelectAddress(byte slave7) => SpdBusAddresses.IsPageSelect(slave7);

    /// <summary>
    /// 取得匯流排的硬體旗號（INUSE_STS）。取不到就放棄——<b>不搶、不等</b>。
    /// </summary>
    /// <remarks>
    /// INUSE_STS 是「讀取即占用」：讀回來的值反映的是我們讀之前的狀態，而這一讀同時把它設起來。
    /// 所以讀到 0 表示現在是我們的，讀到 1 表示別人（可能是 BIOS／SMM，那不理 Windows 的具名互斥鎖）
    /// 正在用。後者不可以寫 1 去清掉，那等於把別人進行中的交易掀掉。
    /// </remarks>
    public bool TryAcquireBus(out string reason)
    {
        byte? sts = io.In(ioBase + HstSts);
        if (sts is null)
        {
            reason = "讀不到 SMBus 控制器狀態暫存器（I/O 埠存取不可用）。";
            LastError = reason;
            LastStatus = SmbusStatus.IoUnavailable;
            return false;
        }
        if ((sts.Value & StsInUse) != 0)
        {
            reason = "SMBus 正被其他程式或韌體使用（INUSE_STS 已被持有）。"
                   + "請關閉 CPU-Z／AIDA64／HWiNFO 與主機板燈光軟體後重試——本工具不搶匯流排。";
            LastError = reason;
            LastStatus = SmbusStatus.BusError;
            return false;
        }
        _acquired = true;
        reason = "";
        LastStatus = SmbusStatus.Ok;
        return true;
    }

    /// <summary>歸還硬體旗號（寫 1 清除）。沒取得過就什麼都不做。</summary>
    public void ReleaseBus()
    {
        if (!_acquired) return;
        _acquired = false;
        io.Out(ioBase + HstSts, StsInUse);
    }

    /// <summary>讀 SPD EEPROM 的一個位元組（SMBus Byte Data 讀取協定）。讀不到回 <c>null</c>。</summary>
    /// <exception cref="ArgumentOutOfRangeException">位址不在 SPD EEPROM 白名單內。</exception>
    public byte? ReadByteData(byte slave7, byte command)
    {
        SpdBusAddresses.EnsureSpdRead(slave7);
        return RunTransaction((byte)((slave7 << 1) | 1), command, ProtoByteData, readsData: true);
    }

    /// <summary>
    /// 對 DDR4 的切頁位址發一個位元組（SMBus Send Byte 協定），用來選 SPD 的上半頁或下半頁。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">位址不是 0x36／0x37。</exception>
    public bool SendByte(byte slave7, byte data)
    {
        SpdBusAddresses.EnsurePageSelect(slave7);
        return RunTransaction((byte)(slave7 << 1), data, ProtoByte, readsData: false) is not null;
    }

    private byte? RunTransaction(byte slva, byte cmdByte, byte protocol, bool readsData)
    {
        if (!_acquired)
            throw new InvalidOperationException("必須先 TryAcquireBus 取得匯流排旗號才能發起交易。");

        LastError = "";
        LastStatus = SmbusStatus.Ok;
        var sw = Stopwatch.StartNew();

        if (!WaitNotBusy(sw, out bool ioFailed))
        {
            if (ioFailed)
            {
                // 送 KILL 也要靠同一條 I/O 路徑，路徑壞了就別再打了；而且原因必須說對——
                // 說成「逾時」會把人引去查匯流排壅塞，真正的原因是驅動不在了。
                LastError = "讀不到 SMBus 控制器狀態暫存器（I/O 埠存取不可用）。";
                LastStatus = SmbusStatus.IoUnavailable;
                return null;
            }
            Kill();
            LastError = $"SMBus 控制器的 HOST_BUSY 在 {transactionTimeoutMs} ms 內沒有放掉（交易逾時），"
                      + "已送出 KILL 收尾；不重試。";
            LastStatus = SmbusStatus.Timeout;
            return null;
        }

        io.Out(ioBase + HstSts, StsClearMask);
        if (!io.Out(ioBase + XmitSlva, slva)
            || !io.Out(ioBase + HstCmd, cmdByte)
            || !io.Out(ioBase + HstCnt, (byte)(protocol | CntStart)))
        {
            LastError = "寫入 SMBus 控制器暫存器失敗（I/O 埠存取不可用）。";
            LastStatus = SmbusStatus.IoUnavailable;
            return null;
        }

        while (true)
        {
            byte? sts = io.In(ioBase + HstSts);
            if (sts is null)
            {
                LastError = "輪詢期間讀不到 HST_STS（I/O 埠存取不可用）。";
                LastStatus = SmbusStatus.IoUnavailable;
                return null;
            }

            byte v = sts.Value;
            if ((v & StsErrorMask) != 0)
            {
                LastError = Describe(v);
                LastStatus = (v & StsBusErr) != 0 ? SmbusStatus.BusError
                           : (v & StsFailed) != 0 ? SmbusStatus.TransactionFailed
                           : SmbusStatus.NoDevice;
                io.Out(ioBase + HstSts, StsClearMask);
                return null;
            }
            if ((v & StsIntr) != 0) break;

            if (sw.ElapsedMilliseconds > transactionTimeoutMs)
            {
                Kill();
                LastError = $"交易逾時（{transactionTimeoutMs} ms 內沒有收到完成訊號），"
                          + "已送出 KILL 收尾；不重試、不換路徑。";
                LastStatus = SmbusStatus.Timeout;
                return null;
            }
            Thread.SpinWait(64);
        }

        byte? data = readsData ? io.In(ioBase + HstD0) : (byte)0;
        io.Out(ioBase + HstSts, StsClearMask);
        if (data is null)
        {
            LastError = "交易完成但讀不到 HST_D0（I/O 埠存取不可用）。";
            LastStatus = SmbusStatus.IoUnavailable;
        }
        return data;
    }

    /// <summary>等 HOST_BUSY 放掉。<paramref name="ioFailed"/> 區分「I/O 讀不到」與「真的等太久」。</summary>
    private bool WaitNotBusy(Stopwatch sw, out bool ioFailed)
    {
        ioFailed = false;
        while (true)
        {
            byte? sts = io.In(ioBase + HstSts);
            if (sts is null) { ioFailed = true; return false; }
            if ((sts.Value & StsHostBusy) == 0) return true;
            if (sw.ElapsedMilliseconds > transactionTimeoutMs) return false;
            Thread.SpinWait(64);
        }
    }

    /// <summary>把卡住的交易收掉並清狀態。這不是重試——是不要讓下一個用匯流排的人也被卡住。</summary>
    private void Kill()
    {
        io.Out(ioBase + HstCnt, CntKill);
        Thread.SpinWait(256);
        io.Out(ioBase + HstSts, StsClearMask);
    }

    private static string Describe(byte sts)
    {
        var parts = new List<string>();
        if ((sts & StsDevErr) != 0) parts.Add("裝置沒有回應或不接受這個命令（DEV_ERR）");
        if ((sts & StsBusErr) != 0) parts.Add("匯流排發生衝突或仲裁失敗（BUS_ERR）");
        if ((sts & StsFailed) != 0) parts.Add("交易失敗，先前的交易被中止（FAILED）");
        return string.Join("；", parts) + "。";
    }
}

