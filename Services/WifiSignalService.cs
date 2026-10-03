using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

/// <summary>一列 Wi-Fi 訊號資訊：介面名稱、SSID、RSSI、速率、認證。頻道 0＝讀不到不猜。</summary>
public sealed record WifiSignalRow(
    string InterfaceName, string Ssid, string Bssid,
    int Rssi, int Channel, string LinkSpeed, string Auth, bool Connected);

/// <summary>
/// Wi-Fi 即時訊號診斷：Native WiFi API（wlanapi.dll）直讀目前連線的 RSSI／速率／認證。
/// 原生資料以位元組平移解析，避免 Managed struct layout 差一個 padding 就讀越界。
/// 全部零特權、不啟動子行程、不上網。
/// </summary>
public static class WifiSignalService
{
    private const int DescriptionChars = 256;
    private const int InterfaceInfoSize = 16 + DescriptionChars * 2 + 4; // 532
    private const int ConnectionAttributesSize = 608;

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr listPtr);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern int WlanQueryInterface(IntPtr handle, ref Guid guid, uint queryType, IntPtr reserved,
        out uint dataSize, out IntPtr dataPtr, IntPtr opCode);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern int WlanGetNetworkBssList(IntPtr handle, ref Guid guid, IntPtr ssid, uint bssType,
        bool securityEnabled, IntPtr reserved, out IntPtr listPtr);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern void WlanFreeMemory(IntPtr p);

    private const uint WlanQueryConnection = 7;

    /// <summary>列舉所有無線介面的目前連線狀態。無 Wi-Fi 或 API 失敗回空列表。</summary>
    public static List<WifiSignalRow> Read()
    {
        var rows = new List<WifiSignalRow>();
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr h) != 0)
        {
            // 舊平台退回 client version 1；兩者都失敗時才是真的讀不到。
            if (WlanOpenHandle(1, IntPtr.Zero, out _, out h) != 0) return rows;
        }
        try
        {
            if (WlanEnumInterfaces(h, IntPtr.Zero, out IntPtr listPtr) != 0) return rows;
            try
            {
                uint count = ReadUInt32(listPtr, 0);
                // C header 的[1]陣列是 inline：指標欄位不是 pointer，資料就在 list header 後面。
                IntPtr items = listPtr + 8;

                for (uint i = 0; i < count; i++)
                {
                    var info = ReadInterfaceInfo(items + (int)(i * InterfaceInfoSize));
                    var guid = info.Guid;

                    // 未連線的介面也如實列一列（SSID 空、RSSI 0）——「介面存在但沒連線」是有用的資訊，
                    // 跟「沒有介面」不一樣，不能混在一起報「未找到」。
                    bool connected = info.State is 1 or 2; // wlan_interface_state：connected / ad_hoc
                    if (!connected)
                    {
                        rows.Add(new WifiSignalRow(info.Description, "", "", 0, 0, "—", "—", Connected: false));
                        continue;
                    }

                    if (WlanQueryInterface(h, ref guid, WlanQueryConnection, IntPtr.Zero,
                        out uint dataSize, out IntPtr connPtr, IntPtr.Zero) != 0 ||
                        dataSize < ConnectionAttributesSize)
                    {
                        if (connPtr != IntPtr.Zero) WlanFreeMemory(connPtr);
                        continue;
                    }
                    try
                    {
                        rows.Add(ParseConnection(info.Description, connPtr, h, guid));
                    }
                    finally { WlanFreeMemory(connPtr); }
                }
            }
            finally { WlanFreeMemory(listPtr); }
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
        return rows;
    }

    private readonly record struct NativeInterfaceInfo(Guid Guid, string Description, uint State);

    private static NativeInterfaceInfo ReadInterfaceInfo(IntPtr p)
    {
        var guidBytes = new byte[16];
        Marshal.Copy(p, guidBytes, 0, 16);
        var descriptionBytes = new byte[DescriptionChars * 2];
        Marshal.Copy(p + 16, descriptionBytes, 0, descriptionBytes.Length);
        int end = Array.IndexOf(descriptionBytes, 0);
        if (end < 0) end = descriptionBytes.Length;
        string description = Encoding.Unicode.GetString(descriptionBytes, 0, end).TrimEnd('\0');
        return new(new Guid(guidBytes), description, ReadUInt32(p, 16 + DescriptionChars * 2));
    }

    private static WifiSignalRow ParseConnection(string interfaceName, IntPtr p, IntPtr handle, Guid guid)
    {
        // WLAN_CONNECTION_ATTRIBUTES 開頭是 ULONG state，後面緊接 WLAN_INTERFACE_INFO。
        int info = 4;
        int assoc = info + InterfaceInfoSize; // 536
        int security = assoc + 56;            // 584
        uint ssidLength = Math.Min(ReadUInt32(p, assoc), 32u);
        var ssidBytes = new byte[ssidLength];
        for (uint i = 0; i < ssidLength; i++) ssidBytes[i] = Marshal.ReadByte(p, assoc + 4 + (int)i);
        string ssid = Encoding.UTF8.GetString(ssidBytes);
        uint signalQuality = ReadUInt32(p, assoc + 48);
        uint txRate = ReadUInt32(p, assoc + 52);
        uint securityEnabled = ReadUInt32(p, security);
        uint authAlgorithm = ReadUInt32(p, security + 4);

        // 頻道與 BSSID 來自 BSS list：頻率是量到的、換算是查表公式（WifiBssDecoder），
        // 找不到對應項就回 0／空——不拿連線名稱或 RSSI 冒充。
        string bssid = "";
        int channel = 0;
        foreach (var bss in EnumBssList(handle, guid))
        {
            if (bss.Ssid == ssid)
            {
                bssid = bss.Bssid;
                channel = bss.Channel;
                break;
            }
        }

        return new(
            interfaceName,
            ssid,
            bssid,
            (int)signalQuality - 100,
            channel,
            txRate > 0 ? $"{txRate / 1000.0:F0} Mbps" : "—",
            securityEnabled != 0 ? AuthText(authAlgorithm) : "Open",
            Connected: true);
    }

    /// <summary>列舉 BSS list（現有基地台掃描結果）；無線電關閉或查詢失敗回空——呼叫方以 0／空如實呈現。</summary>
    private static List<WifiBssEntry> EnumBssList(IntPtr handle, Guid guid)
    {
        var entries = new List<WifiBssEntry>();
        // 參數：pDot11Ssid=null（不過濾）、dot11BssType=infrastructure(1)、bSecurityEnabled=false。
        if (WlanGetNetworkBssList(handle, ref guid, IntPtr.Zero, 1, false, IntPtr.Zero, out IntPtr listPtr) != 0)
            return entries;
        try
        {
            uint count = ReadUInt32(listPtr, 0);
            IntPtr first = listPtr + 8;
            for (uint i = 0; i < count; i++)
            {
                var buf = new byte[WifiBssDecoder.EntrySize];
                Marshal.Copy(first + (int)(i * WifiBssDecoder.EntrySize), buf, 0, buf.Length);
                if (WifiBssDecoder.DecodeEntry(buf) is { } e) entries.Add(e);
            }
        }
        finally { WlanFreeMemory(listPtr); }
        return entries;
    }

    private static uint ReadUInt32(IntPtr p, int offset)
    {
        int value = Marshal.ReadInt32(p, offset);
        return unchecked((uint)value);
    }

    /// <summary>一句話解讀 RSSI。誠實：中間值不硬貼等級。</summary>
    public static string Interpret(int rssi) => rssi switch
    {
        >= -40 => "極佳",
        >= -55 => "良好",
        >= -67 => "可用（適合一般瀏覽；串流或傳檔可能受限）",
        >= -75 => "偏弱（容易斷線或降速）",
        >= -85 => "微弱",
        _ => "極弱或讀取失敗",
    };

    private static string AuthText(uint auth) => auth switch
    {
        1 => "Open",
        2 => "Shared",
        3 => "WPA-Enterprise",
        4 => "WPA-Personal",
        5 => "WPA2-Enterprise",
        6 => "WPA2-Personal",
        7 => "WPA3-Enterprise",
        8 => "WPA3-Personal",
        9 => "WPA3-Enterprise 192-bit",
        _ => $"Unknown({auth})",
    };
}

