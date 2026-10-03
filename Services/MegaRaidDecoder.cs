using System.Text.Json.Nodes;

namespace XinSpect;

/// <summary>MFI 訊框標頭的解碼結果（跨 FreeBSD mfi.h 與 Linux megaraid_sas 一致的欄位）。</summary>
public sealed record MegaRaidMfiFrame(
    byte Command,
    string CommandName,      // 未收錄＝「Command 0x..（未收錄）」
    byte SenseLength,
    byte CommandStatus,
    string CommandStatusName, // 0x00＝OK；其餘如實帶原始碼
    byte Flags,
    uint DataTransferLength);

/// <summary>
/// MegaRAID MFI／DCMD 的純解碼器（V7 WP24／A23）。
/// <b>本解碼器只吃位元組緩衝，不碰 SMBus／任何通路</b>——本機無 MegaRAID 控制器，無法驗證的通路不出貨。
/// 界線聲明：本層只解 MFI 訊框標頭中跨 FreeBSD mfi.h 與 Linux megaraid_sas 一致的欄位；
/// BBU／VD／PD／foreign config 的詳細佈局<b>未驗證</b>（文獻版本分歧，無硬體可逐項確認），刻意不解碼。
/// </summary>
public static class MegaRaidDecoder
{
    /// <summary>MFI 訊框標頭解碼。</summary>
    [SpecRef("Broadcom MegaRAID MFI 介面與 Linux megaraid_sas／FreeBSD mfi(4)：訊框標頭 cmd/sense_len/cmd_status/flags（各 1 byte）＋reserved u16＋data_xfer_len u32 LE")]
    public static MegaRaidMfiFrame? DecodeMfiFrame(byte[] frame)
    {
        if (frame.Length < 10) return null; // 標頭欄位（0–9）不足——不解碼垃圾
        byte cmd = frame[0];
        uint dataLen = (uint)(frame[6] | (frame[7] << 8) | (frame[8] << 16) | (frame[9] << 24));
        return new MegaRaidMfiFrame(
            cmd,
            CommandName(cmd),
            frame[1],
            frame[2],
            CommandStatusName(frame[2]),
            frame[3],
            dataLen);
    }

    /// <summary>MFI 命令碼（FreeBSD mfi.h，跨實作一致子集）。</summary>
    [SpecRef("FreeBSD mfi.h：MFI_CMD_INIT 0x00／LD_READ 0x01／LD_WRITE 0x02／LD_SCSI_IO 0x03／PD_SCSI_IO 0x04／DCMD 0x05／ABORT 0x06")]
    public static string CommandName(byte cmd) => cmd switch
    {
        0x00 => "INIT",
        0x01 => "LD_READ",
        0x02 => "LD_WRITE",
        0x03 => "LD_SCSI_IO",
        0x04 => "PD_SCSI_IO",
        0x05 => "DCMD",
        0x06 => "ABORT",
        _ => $"Command 0x{cmd:X2}（未收錄）",
    };

    /// <summary>MFI 狀態碼（0x00＝OK 有據；其餘各文件版本分歧，如實帶原始碼不硬解）。</summary>
    [SpecRef("FreeBSD mfi.h：MFI_STAT_OK＝0x00；其餘狀態碼跨文件版本分歧——只收錄 OK，其餘如實帶原始碼")]
    public static string CommandStatusName(byte status) => status == 0x00 ? "OK" : $"狀態碼 0x{status:X2}（未收錄）";
}

/// <summary>
/// Redfish 回應的結構解碼（V7 WP18／A18 的 Redfish 面）：Redfish 是 JSON over HTTP——
/// 本解碼器只解析<b>已取得的 JSON 文件結構</b>（服務根／集合／錯誤），不發起任何網路請求。
/// </summary>
public static class RedfishSchemaDecoder
{
    /// <summary>Redfish JSON 的結構摘要：@odata.id、類型、集合成員數、錯誤訊息。</summary>
    public sealed record RedfishEnvelope(string? OdataId, string? OdataType, int? MemberCount, string? ErrorBrief);

    [SpecRef("Redfish Specification 1.x（DSP0266）：資源含 @odata.id／@odata.type；集合含 Members 與 Members@odata.count；錯誤含 error[0].message")]
    public static RedfishEnvelope? DecodeEnvelope(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is not JsonObject obj) return null;
            string? odataId = (string?)obj["@odata.id"]?.GetValue<string>();
            string? odataType = (string?)obj["@odata.type"]?.GetValue<string>();
            int? members = obj["Members@odata.count"] is { } countNode
                ? (int?)countNode.GetValue<int>()
                : obj["Members"] is JsonArray arr2 ? arr2.Count : null;
            string? errorBrief = null;
            if (obj["error"] is JsonObject err)
            {
                var messages = err["message"] is { } m ? (string?)m.GetValue<string>() : null;
                var extended = err["@Message.ExtendedInfo"] as JsonArray;
                errorBrief = messages
                    ?? (extended is { Count: > 0 } && extended[0]?["Message"] is { } em ? (string?)em.GetValue<string>() : null);
            }
            return new RedfishEnvelope(odataId, odataType, members, errorBrief);
        }
        catch { return null; } // 非 JSON——如實回 null，不解碼垃圾
    }
}
