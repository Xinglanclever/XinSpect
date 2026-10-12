using System.Globalization;
using System.Management;

namespace XinSpect;

/// <summary>
/// BitLocker 卷級事實（Vol2 DS-001）：讀 <c>Win32_EncryptableVolume</c>（root\CIMV2\Security\MicrosoftVolumeEncryption）。
/// </summary>
/// <remarks>
/// <para><b>三態分明</b>：命名空間／提供者不存在＝NotSupported（此版本沒有 BitLocker 功能，例如部分 SKU 或
/// 精簡安裝）；查詢被拒＝InsufficientPrivilege；查得到卷但沒加密＝Present 的「未加密」事實。</para>
/// <para><b>界線</b>：只陳述保護狀態、轉換狀態、加密方法與百分比；<b>不讀復原金鑰、不讀 TPM 綁定細節、
/// 不變更任何設定</b>。保護器（protector）與卷的交叉對照屬第一組 FW-003，本項不重做。</para>
/// </remarks>
public static class VolumeEncryptionFactsService
{
    private const string Cat = "資料安全深化";
    public const string SummaryKey = "ds.bl.summary";

    /// <summary>一個可加密卷的狀態（不含任何金鑰或識別碼）。</summary>
    public readonly record struct VolumeState(string DriveLetter, uint ProtectionStatus, uint ConversionStatus,
        int? Method, uint? Percentage);

    /// <summary>探針結果：Volumes＝成功（可能 0 卷）；Error＝三態原因。</summary>
    public sealed record ProbeResult(IReadOnlyList<VolumeState>? Volumes, string? Error, FactAvailability Availability)
    {
        public static ProbeResult Ok(IReadOnlyList<VolumeState> v) => new(v, null, FactAvailability.Present);
        public static ProbeResult Fail(string reason, FactAvailability a) => new(null, reason, a);
    }

    public delegate ProbeResult Probe();

    public static HardwareFact Collect(DateTimeOffset at, Probe? probe = null)
    {
        ProbeResult r;
        try { r = (probe ?? QueryWmi)(); }
        catch (Exception ex)
        {
            r = ProbeResult.Fail("BitLocker 查詢拋出例外（" + ex.GetType().Name + "）——讀不到就是不猜", FactAvailability.ReadError);
        }

        if (r.Volumes is null)
            return new HardwareFact(SummaryKey, Cat, "BitLocker 卷級狀態", "", "",
                @"root\CIMV2\Security\MicrosoftVolumeEncryption（Win32_EncryptableVolume）",
                FactTrustLevel.Unknown, false, at, null, r.Availability,
                r.Error ?? "BitLocker 查詢不可用——不推測");

        if (r.Volumes.Count == 0)
            return new HardwareFact(SummaryKey, Cat, "BitLocker 卷級狀態",
                "提供者可用但回報 0 個可加密卷——本機沒有可加密的固定磁碟（或全為不支援的媒體）。",
                "", @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                FactTrustLevel.Reported, false, at, 0);

        int protectedCount = r.Volumes.Count(v => v.ProtectionStatus == 1);
        int unencrypted = r.Volumes.Count(v => v.ConversionStatus == 0);
        string detail = string.Join("；", r.Volumes.Select(v =>
            $"{v.DriveLetter}：保護 {(v.ProtectionStatus == 1 ? "開" : "關")}、轉換狀態 {Convert(v.ConversionStatus)}"
            + (v.Method is { } m ? $"、方法 {MethodText(m)}" : "")
            + (v.Percentage is { } pc ? $"、{pc}%" : "")));

        return new HardwareFact(SummaryKey, Cat, "BitLocker 卷級狀態",
            $"{r.Volumes.Count} 個卷（保護開啟 {protectedCount}、未加密 {unencrypted}）：{detail}。" +
            "界線：不讀復原金鑰、不讀 TPM 綁定細節、不改任何設定；保護器與卷的交叉對照在韌體安全頁（FW-003）。",
            "卷", @"root\CIMV2\Security\MicrosoftVolumeEncryption（不含金鑰）",
            FactTrustLevel.Reported, false, at, protectedCount);
    }

    private static string Convert(uint status) => status switch
    {
        0 => "完全解密",
        1 => "完全加密",
        2 => "加密中",
        3 => "解密中",
        4 => "加密暫停",
        5 => "解密暫停",
        _ => $"未收錄狀態 {status}",
    };

    private static string MethodText(int m) => m switch
    {
        0 => "無",
        1 => "AES-128 + Diffuser",
        2 => "AES-256 + Diffuser",
        3 => "AES-128",
        4 => "AES-256",
        5 => "硬體加密",
        6 => "XTS-AES-128",
        7 => "XTS-AES-256",
        _ => $"未收錄方法 {m}",
    };

    /// <summary>真實通路：WMI；命名空間不存在與權限不足分開回報。</summary>
    private static ProbeResult QueryWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                "SELECT DriveLetter, ProtectionStatus, ConversionStatus, EncryptionMethod, EncryptionPercentage FROM Win32_EncryptableVolume");
            var list = new List<VolumeState>();
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    list.Add(new VolumeState(
                        o["DriveLetter"] as string ?? "(無代號)",
                        o["ProtectionStatus"] is { } p ? System.Convert.ToUInt32(p) : 0u,
                        o["ConversionStatus"] is { } c ? System.Convert.ToUInt32(c) : 0u,
                        o["EncryptionMethod"] is { } m ? System.Convert.ToInt32(m) : null,
                        o["EncryptionPercentage"] is { } pc ? System.Convert.ToUInt32(pc) : null));
                }
            }
            return ProbeResult.Ok(list);
        }
        catch (ManagementException ex) when (ex.Message.Contains("無效", StringComparison.Ordinal)
                                             || ex.Message.Contains("Invalid", StringComparison.OrdinalIgnoreCase))
        {
            return ProbeResult.Fail("本機沒有 BitLocker WMI 提供者（命名空間不存在）——此版本／SKU 未提供，不適用不是錯誤",
                FactAvailability.NotSupported);
        }
        catch (UnauthorizedAccessException)
        {
            return ProbeResult.Fail("BitLocker 查詢被拒（需要系統管理員）——提權後可再試", FactAvailability.InsufficientPrivilege);
        }
        catch (ManagementException ex)
        {
            return ProbeResult.Fail("BitLocker WMI 查詢失敗：" + ex.Message, FactAvailability.ReadError);
        }
    }
}
