using System;
using System.Text;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// NVMe Firmware Slot Information log（page 0x03）的純解碼契約。
/// 出處：Windows SDK nvme.h 的 NVME_FIRMWARE_SLOT_INFO_LOG：512 bytes、AFI bits 2:0／6:4、FRS[7] 各 8 bytes。
/// </summary>
public class NvmeFwSlotTests
{
    private static byte[] Log(string[] revisions, byte afi = 0)
    {
        var log = new byte[NvmeFwSlot.DataSize];
        log[0] = afi;
        for (int i = 0; i < Math.Min(revisions.Length, NvmeFwSlot.SlotCount); i++)
        {
            var bytes = Encoding.ASCII.GetBytes(revisions[i]);
            Array.Copy(bytes, 0, log, 8 + i * 8, Math.Min(bytes.Length, 8));
        }
        return log;
    }

    [Fact]
    public void 七個槽逐槽解出並保留空槽與有版本槽的差別()
    {
        var info = NvmeFwSlot.Decode(Log(["FW1", "", "FW3"]));
        Assert.NotNull(info);
        Assert.Equal(7, info!.SlotRevisions.Count);
        Assert.Equal("FW1", info.SlotRevisions[0]);
        Assert.Equal("", info.SlotRevisions[1]);
        Assert.Equal("FW3", info.SlotRevisions[2]);
        Assert.Equal(2, info.SlotsWithRevision);
        Assert.Contains("槽1＝FW1、槽3＝FW3", info.DescribeSlots());
    }

    [Fact]
    public void AFI只展開已對準的槽位bits()
    {
        // bits 6:4＝2、bits 2:0＝3；bit3 是保留位，不得混進任何一欄。
        var info = NvmeFwSlot.Decode(Log(["", "", ""], afi: 0b0010_1011));
        Assert.NotNull(info);
        Assert.Equal(3, info!.ActiveSlot);
        Assert.Equal(2, info.PendingActivateSlot);
    }

    [Fact]
    public void 整片零與長度不足都是讀不到_不是七個空槽()
    {
        Assert.Null(NvmeFwSlot.Decode(null));
        Assert.Null(NvmeFwSlot.Decode(new byte[NvmeFwSlot.DataSize]));
        Assert.Null(NvmeFwSlot.Decode(new byte[NvmeFwSlot.DataSize - 1]));
    }

    [Fact]
    public void 八位元槽版本遇到NUL停止並去尾端空白()
    {
        var log = Log(["A B     "]);
        var info = NvmeFwSlot.Decode(log);
        Assert.Equal("A B", info!.SlotRevisions[0]);

        log = Log(["AB      "]);
        log[9] = 0;
        log[10] = (byte)'X';
        info = NvmeFwSlot.Decode(log);
        Assert.Equal("A", info!.SlotRevisions[0]);
    }

    [Fact]
    public void OACS韌體命令位只認bit2_讀不到時不冒充不支援()
    {
        Assert.Null(NvmeFwSlot.FirmwareCommandsSupported(null));
        Assert.Null(NvmeFwSlot.FirmwareCommandsSupported(new byte[257]));

        var identify = new byte[4096];
        Assert.False(NvmeFwSlot.FirmwareCommandsSupported(identify));
        identify[256] = 0x04;
        Assert.True(NvmeFwSlot.FirmwareCommandsSupported(identify));
        identify[256] = 0x08;   // bit3 不是 Firmware Commands，不得誤判
        Assert.False(NvmeFwSlot.FirmwareCommandsSupported(identify));
    }
}
