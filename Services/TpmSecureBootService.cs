using System.Management;

namespace XinSpect;

/// <summary>TPM / Secure Boot 狀態列。</summary>
public sealed record TrustSecurityRow(string Label, string Value, string Status);

/// <summary>
/// TPM 2.0 與 Secure Boot 狀態直讀：
/// TPM 走 <c>Win32_Tpm</c> WMI（零特權，讀取 SpecVersion / Enabled / Activated）；
/// Secure Boot 走 registry <c>HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled</c>
/// （部分系統此鍵不存在→非 UEFI 或已關閉，兩者誠實區分）。
/// </summary>
public static class TpmSecureBootService
{
    public static List<TrustSecurityRow> Read()
    {
        var rows = new List<TrustSecurityRow>();

        // ── TPM ──
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2\Security\MicrosoftTpm",
                "SELECT * FROM Win32_Tpm");
            var found = false;
            foreach (var mo in searcher.Get().Cast<ManagementObject>())
            {
                found = true;
                string spec = mo["SpecVersion"]?.ToString() ?? "";
                bool enabled = ToBool(mo["Enabled_InitialValue"]) || ToBool(mo["IsEnabled_InitialValue"]);
                bool activated = ToBool(mo["Activated_InitialValue"]) || ToBool(mo["IsActivated_InitialValue"]);
                string status = !enabled ? "已停用" : !activated ? "未啟用" : "正常";
                rows.Add(new TrustSecurityRow("TPM", spec.Length > 0 ? spec : "存在", status));
                break;  // 通常只有一個 TPM
            }
            if (!found)
                rows.Add(new TrustSecurityRow("TPM", "未找到", "無"));
        }
        catch
        {
            // WMI namespace 不存在→無 TPM 或未提供，誠實回「未找到」
            rows.Add(new TrustSecurityRow("TPM", "未找到", "無"));
        }

        // ── Secure Boot ──
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key?.GetValue("UEFISecureBootEnabled") is int v)
                rows.Add(new TrustSecurityRow("Secure Boot", v != 0 ? "開啟" : "關閉",
                    v != 0 ? "正常" : "已關閉"));
            else
                rows.Add(new TrustSecurityRow("Secure Boot", "查無狀態鍵", "可能非 UEFI 開機"));
        }
        catch
        {
            rows.Add(new TrustSecurityRow("Secure Boot", "無法讀取", "—"));
        }

        return rows;
    }

    private static bool ToBool(object? v) => v is bool b && b || v is uint u && u != 0;
}
