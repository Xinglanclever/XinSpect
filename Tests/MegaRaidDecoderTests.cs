using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// A23 RAID OOB 的契約：MFI 訊框標頭解碼（跨 FreeBSD/Linux 一致欄位）、狀態碼、
/// Redfish 包封結構解碼，與 OOB 三態分離（無 RAID → NotApplicable／有 → NotSupported）。
/// 解碼器只吃位元組緩衝與 JSON 字串——不碰 SMBus。
/// </summary>
public class MegaRaidDecoderTests
{
    [Fact]
    public void MFI訊框_金標向量_DCMD狀態OK傳輸256位元組()
    {
        // 手算向量：cmd=0x05（DCMD）、sense_len=0x20、cmd_status=0x00（OK）、flags=0x00、
        // reserved=0x0000、data_xfer_len=0x00000100（256，LE）。
        var frame = new byte[]
        {
            0x05, 0x20, 0x00, 0x00,
            0x00, 0x00,
            0x00, 0x01, 0x00, 0x00,
        };

        var f = MegaRaidDecoder.DecodeMfiFrame(frame);
        Assert.NotNull(f);
        Assert.Equal(0x05, f!.Command);
        Assert.Equal("DCMD", f.CommandName);
        Assert.Equal(0x20, f.SenseLength);
        Assert.Equal("OK", f.CommandStatusName);
        Assert.Equal(256u, f.DataTransferLength);
    }

    [Fact]
    public void MFI訊框_過短拒解_狀態非OK如實帶碼_未知命令如實標()
    {
        Assert.Null(MegaRaidDecoder.DecodeMfiFrame(new byte[9]));

        var frame = new byte[] { 0x99, 0x00, 0x42, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00 };
        var f = MegaRaidDecoder.DecodeMfiFrame(frame);
        Assert.NotNull(f);
        Assert.Contains("0x99", f!.CommandName);
        Assert.Contains("0x42", f.CommandStatusName);
    }

    [Fact]
    public void MFI命令與狀態名_收錄子集查得到_未收錄如實標()
    {
        Assert.Equal("PD_SCSI_IO", MegaRaidDecoder.CommandName(0x04));
        Assert.Equal("OK", MegaRaidDecoder.CommandStatusName(0x00));
        Assert.Contains("未收錄", MegaRaidDecoder.CommandName(0xFE));
        Assert.Contains("未收錄", MegaRaidDecoder.CommandStatusName(0xFE));
    }

    [Fact]
    public void Redfish包封_集合成員數與錯誤訊息與非JSON()
    {
        var collection = RedfishSchemaDecoder.DecodeEnvelope("""
            { "@odata.id": "/redfish/v1/Chassis", "@odata.type": "#ChassisCollection",
              "Members": [ { "@odata.id": "/redfish/v1/Chassis/1" } ], "Members@odata.count": 1 }
            """);
        Assert.NotNull(collection);
        Assert.Equal("/redfish/v1/Chassis", collection!.OdataId);
        Assert.Equal(1, collection.MemberCount);

        var error = RedfishSchemaDecoder.DecodeEnvelope("""
            { "error": { "code": "Base.1.0.GeneralError", "message": "一般錯誤" } }
            """);
        Assert.NotNull(error);
        Assert.Contains("一般錯誤", error.ErrorBrief);

        Assert.Null(RedfishSchemaDecoder.DecodeEnvelope("不是 JSON"));
    }
}
