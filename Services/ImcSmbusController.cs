using System.Diagnostics;
using System.Threading;

namespace XinSpect;

/// <summary>
/// 處理器記憶體控制器（iMC）自己那條 SMBus 的交易狀態機。HEDT／伺服器平台的 DIMM SPD 掛在這裡。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼需要它</b>：本機（i9-7980XE／X299）實測過——PCH 的 SMBus 上八個 SPD 位址全部 NAK，
/// 連 DDR4 的切頁裝置都不回應，而整台機器只有一個 PCI 類別碼 0x0C05 的裝置。SPD 在處理器
/// uncore 的 SMBus 區段上，共兩組（所以 CPU-Z 會把兩條不同的模組都標成「SMBus address 0x50」）。
/// </para>
/// <para>
/// <b>這條路徑比 PCH 那條危險一級。</b>控制器的命令暫存器在 PCI 設定空間裡，而設定空間裡有
/// 一大堆一寫就讓機器當場停住的東西。因此：只寫探測到的那三個位移、裝置位址仍受
/// <see cref="SpdBusAddresses"/> 白名單約束、狀態機完整跑在假的設定空間上測過。
/// </para>
/// <para>
/// <b>要讓韌體讓路。</b>韌體會週期性輪詢 DIMM 上的溫度感測器（TSOD），用的就是這條匯流排。
/// <see cref="TryAcquireBus"/> 會把那個輪詢停下來，<see cref="ReleaseBus"/> 把命令暫存器
/// <i>原樣</i>寫回去。沒還原的話主機板從此讀不到記憶體溫度，風扇曲線會跟著失準——那是使用者
/// 完全看不出原因、卻真的會壞事的後果。
/// </para>
/// <para>
/// <b>一個誠實的限制</b>：這個控制器只有一個錯誤位，分不出「位址上沒有裝置」與「匯流排錯誤」。
/// 掃 SPD 的情境下把它歸成 <see cref="SmbusStatus.NoDevice"/>（空插槽）是較合理的一邊，
/// 但那是推斷而不是硬體告訴我們的。
/// </para>
/// </remarks>
public sealed class ImcSmbusController(IPciConfig pci, ImcSmbusLocation location,
                                      int transactionTimeoutMs = ImcSmbusController.DefaultTransactionTimeoutMs)
    : ISpdBus
{
    public const int DefaultTransactionTimeoutMs = 10;

    // 暫存器在 PCU function 5 的設定空間裡，兩組控制器相差 4 位元組
    private const uint CmdBase = 0x9C, StsBase = 0xA8, DatBase = 0xB4, RegStep = 4;

    public static uint CmdRegister(int segment) => CmdBase + (uint)segment * RegStep;
    public static uint StsRegister(int segment) => StsBase + (uint)segment * RegStep;
    public static uint DatRegister(int segment) => DatBase + (uint)segment * RegStep;

    // 命令暫存器
    private const uint CmdGo = 0x00080000;           // 寫下去就開始跑
    private const uint CmdWrite = 0x00008000;        // 這是寫入交易
    private const uint CmdSelPtr = 0x00040000;       // 「寫指標」——DDR4 切頁走這個
    private const uint CmdTsodActive = 0x00100000;   // 韌體的溫度輪詢正在進行
    private const uint CmdToggle = 0x20000000;
    private const uint CmdKeepMask = 0xFFEFFFFF;     // 清掉 TSOD 那一位、其餘保留
    private const uint CmdPrefix = CmdToggle | CmdGo;
    private const int CmdAddrShift = 8;

    // 狀態暫存器
    private const uint StsBusy = 0x1, StsError = 0x2, StsReadDone = 0x4, StsWriteDone = 0x8;

    private bool _acquired;
    private uint _restoreCmd;

    public string LastError { get; private set; } = "";
    public SmbusStatus LastStatus { get; private set; } = SmbusStatus.Ok;

    public string Description
        => $"處理器 iMC SMBus（{location.Bus:X2}:{location.Device:X2}.{location.Function} 第 {location.Segment} 組）";

    /// <summary>
    /// 讓韌體停止輪詢 DIMM 溫度並取得這條匯流排。<b>命令暫存器的原值會記下來，
    /// 由 <see cref="ReleaseBus"/> 原樣寫回。</b>
    /// </summary>
    public bool TryAcquireBus(out string reason)
    {
        uint? old = ReadCmd();
        if (old is null)
        {
            reason = "讀不到 iMC SMBus 的命令暫存器（PCI 設定空間存取不可用）。";
            LastError = reason;
            LastStatus = SmbusStatus.IoUnavailable;
            return false;
        }

        var sw = Stopwatch.StartNew();
        if ((old.Value & CmdTsodActive) != 0 && !WaitNotBusy(sw, out bool ioFailed))
        {
            reason = ioFailed
                ? "讀不到 iMC SMBus 的狀態暫存器（PCI 設定空間存取不可用）。"
                : $"韌體的 DIMM 溫度輪詢在 {transactionTimeoutMs} ms 內沒有結束，放棄——不搶。";
            LastError = reason;
            LastStatus = ioFailed ? SmbusStatus.IoUnavailable : SmbusStatus.Timeout;
            return false;
        }

        // 清掉 TSOD 與 Go：不觸發任何交易，只是把控制器交到我們手上。
        // 這一步同時也是寫入權限的探針——寫不進去就不該再往下走。
        if (!WriteCmd((old.Value & CmdKeepMask) & ~CmdGo))
        {
            reason = "寫不進 iMC SMBus 的命令暫存器（PCI 設定空間不允許寫入）。";
            LastError = reason;
            LastStatus = SmbusStatus.IoUnavailable;
            return false;
        }

        if (!WaitGoClear(sw))
        {
            reason = $"iMC SMBus 的 Go 位在 {transactionTimeoutMs} ms 內沒有放掉，放棄。";
            LastError = reason;
            LastStatus = SmbusStatus.Timeout;
            // 已經動過命令暫存器，所以就算取不到也要把原值還回去
            WriteCmd(old.Value);
            return false;
        }

        _restoreCmd = old.Value;
        _acquired = true;
        reason = "";
        LastStatus = SmbusStatus.Ok;
        return true;
    }

    /// <summary>把命令暫存器原樣寫回去，讓韌體接手（含它的溫度輪詢）。</summary>
    public void ReleaseBus()
    {
        if (!_acquired) return;
        _acquired = false;
        WriteCmd(_restoreCmd);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">位址不在 SPD EEPROM 白名單內。</exception>
    public byte? ReadByteData(byte slave7, byte command)
    {
        SpdBusAddresses.EnsureSpdRead(slave7);
        return Run(CmdPrefix | ((uint)slave7 << CmdAddrShift) | command, StsReadDone, readsData: true);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">位址不是 0x36／0x37。</exception>
    public bool SendByte(byte slave7, byte data)
    {
        SpdBusAddresses.EnsurePageSelect(slave7);
        uint cmd = CmdPrefix | CmdSelPtr | CmdWrite | ((uint)slave7 << CmdAddrShift) | data;
        return Run(cmd, StsWriteDone, readsData: false) is not null;
    }

    private byte? Run(uint command, uint expectedDone, bool readsData)
    {
        if (!_acquired)
            throw new InvalidOperationException("必須先 TryAcquireBus 讓韌體讓路，才能發起交易。");

        LastError = "";
        LastStatus = SmbusStatus.Ok;
        var sw = Stopwatch.StartNew();

        if (!WriteCmd(command))
        {
            LastError = "寫不進 iMC SMBus 的命令暫存器（PCI 設定空間存取不可用）。";
            LastStatus = SmbusStatus.IoUnavailable;
            return null;
        }

        uint doneMask = StsError | expectedDone;
        uint last = 0;
        while (true)
        {
            uint? sts = pci.Read(location.Bus, location.Device, location.Function, StsRegister(location.Segment));
            if (sts is null)
            {
                LastError = "輪詢期間讀不到 iMC SMBus 的狀態暫存器（PCI 設定空間存取不可用）。";
                LastStatus = SmbusStatus.IoUnavailable;
                return null;
            }
            last = sts.Value;
            if ((last & StsBusy) == 0 && (last & doneMask) != 0) break;

            if (sw.ElapsedMilliseconds > transactionTimeoutMs)
            {
                LastError = $"iMC SMBus 交易逾時（{transactionTimeoutMs} ms 內沒有完成，"
                          + $"狀態 0x{last:X8}）；不重試。";
                LastStatus = SmbusStatus.Timeout;
                return null;
            }
            Thread.SpinWait(64);
        }

        if ((last & doneMask) != expectedDone)
        {
            // 這個控制器只有一個錯誤位，分不出「沒有裝置」與「匯流排錯誤」。
            // 掃 SPD 時歸成「沒有裝置」是較合理的一邊，但那是推斷，不是硬體說的。
            LastError = $"iMC SMBus 交易失敗（狀態 0x{last:X8}）——"
                      + "位址上沒有裝置，或匯流排出錯；這個控制器的錯誤位分不出是哪一種。";
            LastStatus = SmbusStatus.NoDevice;
            return null;
        }

        if (!readsData) return 0;

        uint? dat = pci.Read(location.Bus, location.Device, location.Function, DatRegister(location.Segment));
        if (dat is null)
        {
            LastError = "交易完成但讀不到 iMC SMBus 的資料暫存器（PCI 設定空間存取不可用）。";
            LastStatus = SmbusStatus.IoUnavailable;
            return null;
        }
        return (byte)(dat.Value & 0xFF);
    }

    private bool WaitNotBusy(Stopwatch sw, out bool ioFailed)
    {
        ioFailed = false;
        while (true)
        {
            uint? sts = pci.Read(location.Bus, location.Device, location.Function, StsRegister(location.Segment));
            if (sts is null) { ioFailed = true; return false; }
            if ((sts.Value & StsBusy) == 0) return true;
            if (sw.ElapsedMilliseconds > transactionTimeoutMs) return false;
            Thread.SpinWait(64);
        }
    }

    private bool WaitGoClear(Stopwatch sw)
    {
        while (true)
        {
            uint? cmd = ReadCmd();
            if (cmd is null) return false;
            if ((cmd.Value & CmdGo) == 0) return true;
            if (sw.ElapsedMilliseconds > transactionTimeoutMs) return false;
            Thread.SpinWait(64);
        }
    }

    private uint? ReadCmd()
        => pci.Read(location.Bus, location.Device, location.Function, CmdRegister(location.Segment));

    private bool WriteCmd(uint value)
        => pci.Write(location.Bus, location.Device, location.Function, CmdRegister(location.Segment), value);
}
