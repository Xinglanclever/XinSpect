using System.Collections.Generic;

namespace XinSpect.Tests;

/// <summary>
/// 假的 SMBus I/O 埠：照 Intel PCH 的 HST_STS／HST_CNT 語意行為，包含 INUSE 的「讀取即占用」。
/// </summary>
/// <remarks>
/// 讓 SMBus 與 SPD 的所有測試都跑在這上面，是因為真實的 SMBus 是共享匯流排——
/// BIOS／SMM、CPU-Z、AIDA64、燈光軟體都可能同時在上面，搶匯流排最壞會讓機器停頓甚至
/// 需要重開機。<b>驗證這些程式碼的過程本身絕不可以碰它。</b>
/// <para>
/// 掛 <see cref="Modules"/> 就會照 DDR4 的方式服務 SPD 讀取（含 SPA0／SPA1 切頁）；
/// 需要更低階的行為時改用 <see cref="Respond"/>。
/// </para>
/// </remarks>
internal sealed class FakeSmbusIo : ISmbusIo
{
    public const uint Base = 0xF040;
    private const uint Sts = Base + 0, Cnt = Base + 2, Cmd = Base + 3, Slva = Base + 4, D0 = Base + 5;

    public bool BusyForever;
    public bool InUseHeldByOther;
    public bool NeverCompletes;
    public byte ErrorBits;
    public bool BridgeGone;

    /// <summary>切頁裝置（SPA0／SPA1）不回應——真實情形是這條匯流排上根本沒有 DDR4 SPD。</summary>
    public bool NoPageSelectDevice;

    /// <summary>低階回應（slave7、命令位元組）→ 資料；回 null 代表該位址上沒有裝置。</summary>
    public Func<byte, byte, byte?>? Respond;

    /// <summary>掛在匯流排上的 SPD：鍵是 slave7（0x50–0x57），值是 512 位元組映像。</summary>
    public readonly Dictionary<byte, byte[]> Modules = [];

    public readonly List<(uint Port, byte Value)> Writes = [];

    /// <summary>目前選到的 SPD 頁（DDR4 的上半／下半），供測試斷言收尾有沒有復位。</summary>
    public byte Page { get; private set; }

    private byte _sts;
    private bool _inUse;
    private byte _slva, _cmd, _d0;

    public byte? In(uint port)
    {
        if (BridgeGone) return null;
        if (port == Sts)
        {
            byte v = _sts;
            if (BusyForever) v |= 0x01;
            if (_inUse || InUseHeldByOther) v |= 0x40;
            _inUse = true;                          // read-to-acquire：讀完就占住
            return v;
        }
        if (port == D0) return _d0;
        if (port == Slva) return _slva;
        if (port == Cmd) return _cmd;
        return 0;
    }

    public bool Out(uint port, byte value)
    {
        if (BridgeGone) return false;
        Writes.Add((port, value));
        if (port == Sts)
        {
            _sts &= (byte)~(value & 0xBE);          // 狀態位寫 1 清除；HOST_BUSY 唯讀
            if ((value & 0x40) != 0) { _inUse = false; InUseHeldByOther = false; }
            return true;
        }
        if (port == Slva) { _slva = value; return true; }
        if (port == Cmd) { _cmd = value; return true; }
        if (port != Cnt) return true;

        if ((value & 0x02) != 0) { _sts |= 0x10; return true; }     // KILL → FAILED
        if ((value & 0x40) == 0) return true;                        // 沒按 START
        if (NeverCompletes) return true;
        if (ErrorBits != 0) { _sts |= ErrorBits; return true; }

        byte slave7 = (byte)(_slva >> 1);
        switch ((value >> 2) & 0x07)
        {
            case 0x01:                                               // Send Byte：切頁
                if (NoPageSelectDevice) { _sts |= 0x04; return true; }
                if (slave7 == 0x36) Page = 0;
                else if (slave7 == 0x37) Page = 1;
                else { _sts |= 0x04; return true; }
                break;

            case 0x02:                                               // Byte Data 讀取
                byte? got = Respond is not null ? Respond(slave7, _cmd)
                          : Modules.TryGetValue(slave7, out var image) ? image[Page * 256 + _cmd]
                          : null;
                if (got is null) { _sts |= 0x04; return true; }       // 無裝置 → DEV_ERR
                _d0 = got.Value;
                break;
        }
        _sts |= 0x02;                                                // INTR＝完成
        return true;
    }
}
