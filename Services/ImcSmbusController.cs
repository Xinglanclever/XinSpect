using System.Diagnostics;
using System.Threading;

namespace XinSpect;

/// <summary>Skylake-X 0x9C proxy transaction state machine. Production access remains unsupported until this backend has a verified firmware/host handoff contract.</summary>
public sealed class ImcSmbusController : ISpdBus
{
    private readonly IPciConfig pci;
    private readonly ImcSmbusLocation location;
    private readonly int transactionTimeoutMs;
    private readonly bool _injectedProtocol;
    private readonly object _sync = new();
    private bool _faulted;
    private uint _lastCommand;

    public ImcSmbusController(IPciConfig pci, ImcSmbusLocation location,
        int transactionTimeoutMs = DefaultTransactionTimeoutMs)
        : this(pci, location, transactionTimeoutMs, injectedProtocol: false) { }

    // Test-only seam, never an assertion of firmware or cross-process ownership.
    internal ImcSmbusController(IPciConfig pci, ImcSmbusLocation location,
        int transactionTimeoutMs, bool injectedProtocol)
    {
        this.pci = pci ?? throw new ArgumentNullException(nameof(pci));
        this.location = location ?? throw new ArgumentNullException(nameof(location));
        if (location.Segment is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(location));
        if (transactionTimeoutMs is <= 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(transactionTimeoutMs));
        this.transactionTimeoutMs = transactionTimeoutMs;
        _injectedProtocol = injectedProtocol;
    }

    // Source boundary: Intel E5-v3 Volume 2, 330784-003 (June 2015),
    // §§2.1.13–15, printed pp.34–38 document direct SMB_STAT/CMD/CNTL at
    // device 19/22 function 0, offsets 0x180/0x184/0x188. They do not validate
    // this 8086:2085 function-5 proxy or establish arbitration with SMM/BMC.
    // No new register write encoding is inferred from that different interface.
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

    private bool Fail(SmbusStatus status, string message)
    {
        _faulted = true;
        LastStatus = status;
        LastError = message;
        return false;
    }

    public bool TryAcquireBus(out string reason)
    {
        lock (_sync)
        {
            if (_faulted) { reason = LastError; return false; }
            if (!_injectedProtocol)
            {
                Fail(SmbusStatus.NotSupported, "Skylake-X iMC 0x9C proxy：尚未核定此通路的 firmware／其他程序 ownership 交接；Intel E5-v3 direct SMBus 規格不是本 proxy 的仲裁契約。未讀寫 PCI；PCH、Windows SMBIOS/WMI 及已授權 CPU-Z 報告來源仍可用。");
                reason = LastError;
                return false;
            }
            if (_acquired) { reason = "This controller is already acquired."; return false; }
            uint? old = ReadCmd();
            uint? status = ReadStatus();
            if (old is null || status is null || old == uint.MaxValue || status == uint.MaxValue)
                Fail(SmbusStatus.IoUnavailable, "Cannot verify command/status before acquisition.");
            else if ((old.Value & (CmdGo | CmdTsodActive)) != 0 || (status.Value & StsBusy) != 0)
                Fail(SmbusStatus.BusError, "Controller is active or firmware-owned; no ownership change attempted.");
            else
            {
                // No firmware polling change and no probe write. A fake idle image is sufficient only for injected tests.
                _restoreCmd = _lastCommand = old.Value;
                _acquired = true;
                LastStatus = SmbusStatus.Ok;
                LastError = reason = "";
                return true;
            }
            reason = LastError;
            return false;
        }
    }

    public void ReleaseBus()
    {
        lock (_sync)
        {
            if (!_acquired) return;
            _acquired = false;
            if (_faulted) return; // Timeout is not completion. Never replay a command to clean up.
            uint? current = ReadCmd();
            uint? status = ReadStatus();
            if (current is null || status is null || status == uint.MaxValue)
            { Fail(SmbusStatus.IoUnavailable, "Release skipped: idle state cannot be verified."); return; }
            if ((status.Value & StsBusy) != 0 || (current.Value & (CmdGo | CmdTsodActive)) != 0
                || current != _lastCommand)
            { Fail(SmbusStatus.BusError, "Release skipped: busy controller or ownership changed."); return; }
            if (current == _restoreCmd) return;
            // Only an originally idle, Go-clear image can ever be restored.
            if (!WriteCmd(_restoreCmd) || ReadCmd() != _restoreCmd)
                Fail(SmbusStatus.IoUnavailable, "Release restoration failed verification; controller fault latched.");
        }
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

    /// <summary>
    /// <b>誠實的不支援</b>：iMC SMBus 的 Byte-Data 寫入編碼（指標＋資料兩段式）沒有公開文件可核，
    /// 而猜錯編碼的後果是把位元組寫進不該寫的地方。DDR5 切頁因此不在 iMC 路徑上提供——
    /// 寧可如實報 NotSupported，不拿猜測去碰記憶體模組。
    /// </summary>
    public bool WriteByteData(byte slave7, byte command, byte value)
    {
        lock (_sync)
        {
            if (_faulted) return false;
            LastError = "iMC Byte-Data write encoding is not verified; operation unsupported.";
            LastStatus = SmbusStatus.NotSupported;
            return false;
        }
    }

    private byte? Run(uint command, uint expectedDone, bool readsData)
    {
        lock (_sync) return RunLocked(command, expectedDone, readsData);
    }

    private byte? RunLocked(uint command, uint expectedDone, bool readsData)
    {
        if (_faulted) return null;
        if (!_acquired)
            throw new InvalidOperationException("必須先 TryAcquireBus；沒有經驗證的取得狀態不能發起交易。");

        uint? before = ReadCmd();
        uint? beforeStatus = ReadStatus();
        if (before is null || beforeStatus is null || beforeStatus == uint.MaxValue)
        { Fail(SmbusStatus.IoUnavailable, "Transaction skipped: command/status unavailable."); return null; }
        if (before != _lastCommand || (before.Value & (CmdGo | CmdTsodActive)) != 0 || (beforeStatus.Value & StsBusy) != 0)
        { Fail(SmbusStatus.BusError, "Transaction skipped: busy controller or ownership changed."); return null; }
        LastError = "";
        LastStatus = SmbusStatus.Ok;
        var sw = Stopwatch.StartNew();

        if (!WriteCmd(command))
        {
            LastError = "寫不進 iMC SMBus 的命令暫存器（PCI 設定空間存取不可用）。";
            LastStatus = SmbusStatus.IoUnavailable;
            _faulted = true;
            return null;
        }

        uint doneMask = StsError | expectedDone;
        uint last = 0;
        while (true)
        {
            uint? sts = ReadStatus();
            if (sts is null || sts == uint.MaxValue)
            {
                LastError = "輪詢期間讀不到 iMC SMBus 的狀態暫存器（PCI 設定空間存取不可用）。";
                LastStatus = SmbusStatus.IoUnavailable;
                _faulted = true;
                return null;
            }
            last = sts.Value;
            if ((last & StsBusy) == 0 && (last & doneMask) != 0) break;

            if (sw.ElapsedMilliseconds > transactionTimeoutMs)
            {
                LastError = $"iMC SMBus 交易逾時（{transactionTimeoutMs} ms 內沒有完成，"
                          + $"狀態 0x{last:X8}）；不重試。";
                LastStatus = SmbusStatus.Timeout;
                _faulted = true;
                return null;
            }
            Thread.SpinWait(64);
        }

        if ((last & doneMask) != expectedDone)
        {
            // The ambiguous error bit cannot establish an empty slot.
            LastError = $"iMC SMBus 交易失敗（狀態 0x{last:X8}）——"
                      + "位址上沒有裝置，或匯流排出錯；這個控制器的錯誤位分不出是哪一種。";
            LastStatus = SmbusStatus.TransactionFailed;
            _faulted = true;
            return null;
        }

        uint? completed = ReadCmd();
        if (completed != (command & ~CmdGo))
        { Fail(SmbusStatus.BusError, "Completion command differs from the submitted command; ownership not verified."); return null; }
        _lastCommand = completed.Value;
        if (!readsData) return 0;

        uint? dat = ReadRegister(DatRegister(location.Segment));
        if (dat is null)
        {
            LastError = "交易完成但讀不到 iMC SMBus 的資料暫存器（PCI 設定空間存取不可用）。";
            LastStatus = SmbusStatus.IoUnavailable;
            _faulted = true;
            return null;
        }
        return (byte)(dat.Value & 0xFF);
    }

    private uint? ReadStatus()
        => ReadRegister(StsRegister(location.Segment));

    private uint? ReadCmd()
        => ReadRegister(CmdRegister(location.Segment));

    private uint? ReadRegister(uint register)
    {
        try { return pci.Read(location.Bus, location.Device, location.Function, register); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private bool WriteCmd(uint value)
    {
        try { return pci.Write(location.Bus, location.Device, location.Function, CmdRegister(location.Segment), value); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }
}
