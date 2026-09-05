using System.Collections.Generic;

namespace XinSpect.Tests;

/// <summary>
/// 假的 PCI 設定空間，模擬 Skylake-X uncore 上 8086:2085（PCU function 5）裡的 iMC SMBus 暫存器。
/// </summary>
/// <remarks>
/// 暫存器語意照實機讀到的與 Linux／RAMSPDToolkit 記載的行為建模：兩組控制器
/// （CMD 0x9C／0xA0、STS 0xA8／0xAC、DAT 0xB4／0xB8），命令暫存器寫入即觸發交易。
/// <para>
/// 這組測試<b>絕對不能碰真的處理器記憶體控制器</b>——PCI 設定空間裡一寫就讓機器當場停住的
/// 東西太多了，驗證狀態機的過程本身必須完全在假的空間裡進行。
/// </para>
/// </remarks>
internal sealed class FakeImcPci : IPciConfig
{
    public const byte Bus = 0x16, Dev = 0x1E, Fn = 5;

    private const uint CmdBase = 0x9C, StsBase = 0xA8, DatBase = 0xB4, Step = 4;
    private const uint Go = 0x00080000, WriteOp = 0x00008000, SelPtr = 0x00040000;
    private const uint TsodActive = 0x00100000, Toggle = 0x20000000;
    private const uint StsBusy = 0x1, StsError = 0x2, StsReadDone = 0x4, StsWriteDone = 0x8;

    public bool DeviceMissing;
    public bool AccessGone;
    public bool WritesRejected;
    public bool NeverCompletes;
    public bool BusyForever;
    public bool ErrorOnTransfer;

    /// <summary>初始狀態下韌體正在輪詢 DIMM 溫度感測器。</summary>
    public bool TsodPollingActive;

    /// <summary>只有一組控制器有效（另一組回 0xFFFFFFFF）。</summary>
    public bool OnlyOneSegment;

    /// <summary>(segment, slave7) → 512 位元組 SPD 映像。</summary>
    public readonly Dictionary<(int Seg, byte Slave), byte[]> Modules = [];

    public readonly List<(uint Reg, uint Value)> Writes = [];

    /// <summary>目前選到的 SPD 頁，供測試斷言收尾有沒有復位。</summary>
    public byte Page { get; private set; }

    private readonly uint[] _cmd = new uint[2];
    private readonly uint[] _sts = new uint[2];
    private readonly uint[] _dat = new uint[2];
    private bool _initialised;

    private void EnsureInit()
    {
        if (_initialised) return;
        _initialised = true;
        // 實機讀到的樣子：Toggle 位留著、位址欄是韌體上次用的、狀態欄留著上次的完成旗標
        for (int i = 0; i < 2; i++)
        {
            _cmd[i] = Toggle | 0x5500u | (TsodPollingActive ? TsodActive | Go : 0);
            _sts[i] = 0x1800 | StsReadDone | StsWriteDone;
            _dat[i] = 0xFF;
        }
    }

    public uint? Read(byte bus, byte device, byte function, uint register)
    {
        if (AccessGone) return null;
        EnsureInit();
        if (DeviceMissing || bus != Bus || device != Dev || function != Fn) return 0xFFFFFFFF;
        if (register == 0x00) return 0x2085_8086;

        int seg = Segment(register);
        if (seg < 0) return 0xFFFFFFFF;
        if (OnlyOneSegment && seg == 1) return 0xFFFFFFFF;

        if (register == CmdBase + seg * Step) return _cmd[seg];
        if (register == StsBase + seg * Step) return _sts[seg];
        if (register == DatBase + seg * Step) return _dat[seg];
        return 0xFFFFFFFF;
    }

    public bool Write(byte bus, byte device, byte function, uint register, uint value)
    {
        if (AccessGone || WritesRejected) return false;
        EnsureInit();
        if (bus != Bus || device != Dev || function != Fn) return false;
        Writes.Add((register, value));

        int seg = Segment(register);
        if (seg < 0 || register != CmdBase + seg * Step) return true;

        _cmd[seg] = value;
        if ((value & Go) == 0) return true;                       // 只是還原／清 TSOD，不是觸發
        if (NeverCompletes) { _sts[seg] = 0; return true; }
        if (BusyForever) { _sts[seg] = StsBusy; return true; }
        if (ErrorOnTransfer) { _sts[seg] = StsError; return true; }

        byte slave7 = (byte)((value >> 8) & 0x7F);
        byte cmdByte = (byte)(value & 0xFF);

        if ((value & WriteOp) != 0)
        {
            if ((value & SelPtr) == 0) { _sts[seg] = StsError; return true; }
            if (slave7 == 0x36) Page = 0;
            else if (slave7 == 0x37) Page = 1;
            else { _sts[seg] = StsError; return true; }
            _sts[seg] = StsWriteDone;
            return true;
        }

        if (!Modules.TryGetValue((seg, slave7), out var image)) { _sts[seg] = StsError; return true; }
        _dat[seg] = image[Page * 256 + cmdByte];
        _sts[seg] = StsReadDone;
        return true;
    }

    private static int Segment(uint register) => register switch
    {
        CmdBase or StsBase or DatBase => 0,
        CmdBase + Step or StsBase + Step or DatBase + Step => 1,
        _ => -1,
    };
}
