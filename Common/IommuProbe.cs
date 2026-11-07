namespace XinSpect.Common;

/// <summary>
/// IOMMU（VT-d／AMD-Vi）有無的單一事實來源（審計 P2-24）。
/// 讀 ACPI 韌體表清單裡有無 DMAR（Intel）／IVRS（AMD）——這才是 DMA 重對映硬體存在的直接證據。
/// <para>
/// <b>不得再用</b> <c>Win32_DeviceGuard.AvailableSecurityProperties</c> 屬性 1 冒充 IOMMU：
/// 那個位元代表「hypervisor 支援（VT-x/AMD-V）」，與 DMA 重對映是兩套不同的硬體功能。
/// 有 VT-x 但韌體沒開 VT-d 的機器，用屬性 1 會回 True，與 ACPI 讀法矛盾。
/// </para>
/// <para>
/// 此檔以 Compile Link 同時編進 XinSpect 本體（預設 glob）與 BlueSquadronBridge
/// （csproj 以 Compile Include Link 連入）——兩端共用同一份代碼，任何改動兩端同時生效，
/// 不再出現「主體改對、Bridge 副本漂移」的修復分叉。
/// </para>
/// 讀不到（EnumSystemFirmwareTables 失敗、例外）回 null＝未評估，與主體端三態口徑一致。
/// </summary>
public static class IommuProbe
{
    public static bool? IsPresent()
    {
        try
        {
            int size = EnumSystemFirmwareTables(AcpiSignature, null, 0);
            if (size <= 0) return null;

            var table = new byte[size];
            if (EnumSystemFirmwareTables(AcpiSignature, table, size) <= 0) return null;

            for (int i = 0; i + 4 <= size; i += 4)
            {
                uint sig = BitConverter.ToUInt32(table, i);
                if (sig == DmarSignature || sig == IvrsSignature) return true;
            }
            return false;
        }
        catch { return null; }
    }

    private const uint AcpiSignature = 0x41435049;  // 'ACPI'
    private const uint DmarSignature = 0x52414D44;  // 'DMAR'
    private const uint IvrsSignature = 0x53525649;  // 'IVRS'

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern int EnumSystemFirmwareTables(uint firmwareTableProviderSignature, byte[]? buffer, int bufferSize);
}
