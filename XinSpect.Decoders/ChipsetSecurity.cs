namespace XinSpect;


public enum ChipsetSecurityVerdict
{
    Unknown,
    Unprotected,
    Protected,
    SmmProtected,
}

/// <summary>BIOS_CNTL 解碼結果（Intel PCH D31:F0 +0xDC）。</summary>
public readonly record struct BiosCntlDecode(bool BiosWe, bool Ble, bool SmmBwp, ChipsetSecurityVerdict Verdict);

/// <summary>SMRAMC 解碼結果（Intel 主機橋 D0:F0 +0x88）。D_LCK 鎖住即保護；D_OPEN 代表 SMRAM 對非 SMM 開放。</summary>
public readonly record struct SmramcDecode(bool DLck, bool DOpen, bool DCls, ChipsetSecurityVerdict Verdict);

/// <summary>ME 運作模式（依 coreboot intelmetool me_hfs：operation_mode 在 bits[19:16]）。未知值標 Other 並保留原始 nibble，不臆測。</summary>
public enum MeOperationMode { Normal, Debug, SoftDisable, OverrideJumper, OverrideMei, Other }

/// <summary>HFSTS1 解碼（HECI1 D22:F0 +0x40）。working_state[3:0]、fw_init_complete bit9、operation_mode[19:16]。</summary>
public readonly record struct HfsDecode(byte WorkingState, bool FwInitComplete, MeOperationMode OperationMode, byte OperationModeRaw);

/// <summary>
/// 晶片組安全暫存器的純解碼器。不碰硬體、不讀 PCI——所有實際讀取由服務層經 WinRing0/自家驅動取得後餵進來，
/// 讀不到就在服務層標三態，不在這裡假裝有值。
/// </summary>
public static class ChipsetSecurity
{
    /// <summary>解碼 BIOS_CNTL：BLE=0 代表無鎖、任何 ring0 都能開啟 BIOS 寫入；SMM_BWP 為最強。</summary>
    [SpecRef("Intel PCH EDS, BIOS_CNTL（PCI 0:1F.0 +0xDC）：BIOSWE bit0、BLE bit1、SMM_BWP bit5；CHIPSEC bmp Controls 交叉核對")]
    public static BiosCntlDecode DecodeBiosCntl(uint raw)
    {
        bool biosWe = (raw & 0x01) != 0;
        bool ble = (raw & 0x02) != 0;
        bool smmBwp = (raw & 0x20) != 0;
        var verdict = smmBwp ? ChipsetSecurityVerdict.SmmProtected
            : ble ? ChipsetSecurityVerdict.Protected
            : ChipsetSecurityVerdict.Unprotected;
        return new(biosWe, ble, smmBwp, verdict);
    }

    /// <summary>解碼 SMRAMC：D_LCK 鎖住 SMRAM 設定即保護；未鎖（含 D_OPEN 對外開放）皆視為無保護。</summary>
    [SpecRef("Intel EDS, SMRAMC（PCI 0:0.0 +0x88）：D_LCK bit4、D_CLS bit5、D_OPEN bit6；CHIPSEC smram 交叉核對")]
    public static SmramcDecode DecodeSmramc(uint raw)
    {
        bool dLck = (raw & 0x10) != 0;
        bool dCls = (raw & 0x20) != 0;
        bool dOpen = (raw & 0x40) != 0;
        var verdict = dLck ? ChipsetSecurityVerdict.Protected : ChipsetSecurityVerdict.Unprotected;
        return new(dLck, dOpen, dCls, verdict);
    }

    /// <summary>解碼 HFSTS1（coreboot me_hfs 佈局）。未定義的 operation_mode 標 Other 並保留原始 nibble，不臆測。</summary>
    [SpecRef("coreboot me_hfs 佈局（HFSTS1，HECI1 0:16.0 +0x40）：working_state bits[3:0]、fw_init_complete bit9、operation_mode bits[19:16]；coreboot util/intelmetool 交叉核對")]
    public static HfsDecode DecodeHfs(uint raw)
    {
        byte workingState = (byte)(raw & 0xF);
        bool fwInit = (raw & (1u << 9)) != 0;
        byte modeRaw = (byte)((raw >> 16) & 0xF);
        var mode = modeRaw switch
        {
            0 => MeOperationMode.Normal,
            2 => MeOperationMode.Debug,
            3 => MeOperationMode.SoftDisable,
            4 => MeOperationMode.OverrideJumper,
            5 => MeOperationMode.OverrideMei,
            _ => MeOperationMode.Other,
        };
        return new(workingState, fwInit, mode, modeRaw);
    }
}
