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
    private const uint Sts = Base + 0, Cnt = Base + 2, Cmd = Base + 3, Slva = Base + 4, D0 = Base + 5, D1 = Base + 6;

    public bool BusyForever;
    public bool InUseHeldByOther;
    public bool NeverCompletes;
    public byte ErrorBits;
    public bool BridgeGone;

    /// <summary>切頁裝置（SPA0／SPA1）不回應——真實情形是這條匯流排上根本沒有 DDR4 SPD。</summary>
    public bool NoPageSelectDevice;

    /// <summary>低階回應（slave7、命令位元組）→ 資料；回 null 代表該位址上沒有裝置。</summary>
    public Func<byte, byte, byte?>? Respond;

    /// <summary>word 讀取回應（slave7、命令位元組）→ 16 位元資料；null 代表無裝置。未設時 word 交易一律 DEV_ERR。</summary>
    public Func<byte, byte, ushort?>? WordRespond;

    /// <summary>掛在匯流排上的 SPD：鍵是 slave7（0x50–0x57），值是 512 位元組映像。</summary>
    public readonly Dictionary<byte, byte[]> Modules = new();

    /// <summary>掛在匯流排上的 DDR5 模組（SPD5118 hub）：值是 1024 位元組映像。</summary>
    public readonly Dictionary<byte, byte[]> Ddr5Modules = new();

    /// <summary>目前選到的 DDR5 頁（MR11 bits[2:0]），供測試斷言收尾有沒有復位。</summary>
    public byte Ddr5Page { get; private set; }

    /// <summary>讓某個 EEPROM 暫存器在「每隔一次讀取」時回翻轉值——模擬傳輸不穩（連讀兩次比對用）。</summary>
    public (byte Cmd, byte Value)? Ddr5Flip;
    private int _ddr5FlipReads;

    public readonly List<(uint Port, byte Value)> Writes = new();

    /// <summary>目前選到的 SPD 頁（DDR4 的上半／下半），供測試斷言收尾有沒有復位。</summary>
    public byte Page { get; private set; }

    private byte _sts;
    private bool _inUse;
    private byte _slva, _cmd, _d0, _d1;

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
        if (port == D1) return _d1;
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
        if (port == D0) { _d0 = value; return true; }   // 寫入交易的資料位元組（MR11 切頁值）
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

            case 0x02:                                               // Byte Data 讀取／寫入
                if ((_slva & 1) == 0)
                {
                    // 寫入：唯一允許的是 DDR5 hub 的 MR11（0x0B）切頁
                    if (Ddr5Modules.ContainsKey(slave7) && _cmd == 0x0B) { Ddr5Page = (byte)(_d0 & 0x07); break; }
                    _sts |= 0x04; return true;
                }
                byte? got = Respond is not null ? Respond(slave7, _cmd)
                          : Modules.TryGetValue(slave7, out var image) ? image[Page * 256 + _cmd]
                          : Ddr5Modules.TryGetValue(slave7, out var img5) ? ReadDdr5Register(img5, _cmd)
                          : null;
                if (got is null) { _sts |= 0x04; return true; }       // 無裝置 → DEV_ERR
                _d0 = got.Value;
                break;

            case 0x03:                                               // Word Data 讀取（TSOD）
                ushort? word = WordRespond?.Invoke(slave7, _cmd);
                if (word is null) { _sts |= 0x04; return true; }
                _d0 = (byte)word.Value;
                _d1 = (byte)(word.Value >> 8);
                break;
        }
        _sts |= 0x02;                                                // INTR＝完成
        return true;
    }

    /// <summary>SPD5118 hub 的暫存器空間：MR0/MR1＝0x51/0x18（識別）、MR11＝頁暫存器、0x80 起＝當頁 EEPROM。</summary>
    private byte ReadDdr5Register(byte[] image, byte cmd) => cmd switch
    {
        0x00 => 0x51,
        0x01 => 0x18,
        0x0B => (byte)(0x08 | Ddr5Page),  // bit3＝legacy mode（與頁位元一起存）
        _ when cmd >= 0x80 && cmd <= 0xFF => Ddr5Flip is { } f && f.Cmd == cmd && _ddr5FlipReads++ >= 8
                ? f.Value   // 該暫存器每遍被讀 8 次（8 頁）；第 9 次起＝第二遍，回翻轉值製造不一致
                : image[Ddr5Page * 128 + (cmd - 0x80)],
        _ => throw new System.Diagnostics.UnreachableException($"未模擬的 hub 暫存器 0x{cmd:X2}"),
    };
}
