// Core Temp 共享記憶體讀取器
// 透過 CoreTemp_SharedData 讀取 Core Temp 即時感測器資料

using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

/// <summary>
/// 從 Core Temp 共享記憶體區段讀取感測器數值。
/// Core Temp 必須在執行中（預設即開啟共享記憶體）。
/// </summary>
public static class CoreTempSharedMem
{
    // ── 共享記憶體名稱 ──────────────────────────────────────────
    // Core Temp 不使用 Global\ 前綴
    // Core Temp SDK 的官方共享記憶體名稱。
    // 官方 .NET SDK (GetCoreTempInfoNET.dll) 用 "CoreTempMappingObject"，
    // 原生 SDK 與 Rainmeter 的讀取器用 "CoreTempMappingObjectEx"。
    // 先前寫成 "CoreTemp_SharedData" 是編出來的——GitHub 搜尋 0 筆命中，結果是永遠「未執行」。
    // 先嘗試較新的 Ex 版本，不行再試不帶 Ex 的。
    private const string SharedMemNameEx = "CoreTempMappingObjectEx";
    private const string SharedMemName = "CoreTempMappingObject";

    // ── Core Temp 共享資料結構 ──────────────────────────────────
    // 對應 Core Temp SDK 的 CORE_TEMP_SHARED_DATA
    private const int MaxCores = 256;
    private const int CpuNameLength = 100;

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct CoreTempSharedData
    {
        public uint uiLoad_0;           // 第一個核心的負載（後續透過偏移取得）
        // ── 因結構含變長陣列，改用手動偏移讀取 ──
    }

    // ── 結構欄位偏移量（基於 Core Temp SDK 文件） ──────────────
    // uint  uiLoad[256]       : offset 0,      size 1024
    // uint  uiTjMax[128]      : offset 1024,    size 512
    // uint  uiCoreCnt         : offset 1536,    size 4
    // uint  uiCPUCnt          : offset 1540,    size 4
    // float fTemp[256]         : offset 1544,    size 1024
    // float fVID              : offset 2568,    size 4
    // float fCPUSpeed         : offset 2572,    size 4
    // float fFSBSpeed         : offset 2576,    size 4
    // float fMultiplier       : offset 2580,    size 4
    // char  sCPUName[100]     : offset 2584,    size 100
    // 以下為 Core Temp 1.0+ 擴充欄位
    // bool  ucFahrenheit      : offset 2684,    size 1
    // bool  ucDeltaToTjMax    : offset 2685,    size 1

    private const int OffsetLoad       = 0;
    private const int OffsetTjMax      = 1024;
    private const int OffsetCoreCnt    = 1536;
    private const int OffsetCpuCnt     = 1540;
    private const int OffsetTemp       = 1544;
    private const int OffsetVid        = 2568;
    private const int OffsetCpuSpeed   = 2572;
    private const int OffsetFsbSpeed   = 2576;
    private const int OffsetMultiplier = 2580;
    private const int OffsetCpuName    = 2584;
    private const int OffsetFahrenheit = 2684;
    private const int OffsetDeltaTj    = 2685;
    private const int MinStructSize    = 2686;

    /// <summary>
    /// 嘗試從 Core Temp 共享記憶體讀取所有感測器讀數。
    /// </summary>
    /// <param name="readings">成功時回傳讀數清單。</param>
    /// <param name="error">失敗時的錯誤訊息。</param>
    /// <returns>是否成功開啟並解析共享記憶體。</returns>
    public static bool TryRead(out IReadOnlyList<ExternalReading> readings, out string? error)
    {
        readings = Array.Empty<ExternalReading>();
        error = null;

        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? accessor = null;
        try
        {
            // 先嘗試較新的 Ex 版本（原生 SDK），不行再試 .NET SDK 的名稱
            try { mmf = MemoryMappedFile.OpenExisting(SharedMemNameEx, MemoryMappedFileRights.Read); }
            catch (System.IO.FileNotFoundException)
            {
                mmf = MemoryMappedFile.OpenExisting(SharedMemName, MemoryMappedFileRights.Read);
            }
            accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            if (accessor.Capacity < MinStructSize)
            {
                error = "Core Temp 共享記憶體區段過小，可能版本不相容";
                return false;
            }

            // ── 讀取基本資訊 ──
            uint coreCnt = accessor.ReadUInt32(OffsetCoreCnt);
            uint cpuCnt  = accessor.ReadUInt32(OffsetCpuCnt);

            if (coreCnt == 0 || coreCnt > MaxCores || cpuCnt == 0)
            {
                error = $"Core Temp 核心數異常：coreCnt={coreCnt}, cpuCnt={cpuCnt}";
                return false;
            }

            // ── CPU 名稱 ──
            byte[] nameBytes = new byte[CpuNameLength];
            accessor.ReadArray(OffsetCpuName, nameBytes, 0, CpuNameLength);
            int nameEnd = Array.IndexOf(nameBytes, (byte)0);
            if (nameEnd < 0) nameEnd = CpuNameLength;
            string cpuName = Encoding.ASCII.GetString(nameBytes, 0, nameEnd).Trim();

            // ── 溫度單位判斷 ──
            byte fahrenheitFlag = accessor.ReadByte(OffsetFahrenheit);
            byte deltaFlag      = accessor.ReadByte(OffsetDeltaTj);
            bool isFahrenheit   = fahrenheitFlag != 0;
            bool isDelta        = deltaFlag != 0;

            string tempUnit = isFahrenheit ? "°F" : "°C";

            var result = new List<ExternalReading>();

            // ── 逐核心溫度與負載 ──
            uint totalCores = coreCnt; // Core Temp 的 coreCnt 已含所有 CPU 核心總數
            for (uint i = 0; i < totalCores && i < MaxCores; i++)
            {
                // 溫度。Core Temp 的「距 TjMax」模式（ucDeltaToTjMax）下 fTemp 是**差值**不是絕對溫度，
                // 所以另立一個 SensorType 而不是只加後綴字串——後綴會讓下游用 Contains("TjMax")
                // 找 TjMax 的程式把這些差值一起當成 TjMax 吃掉。
                float temp = ReadFloat(accessor, OffsetTemp + i * 4);
                result.Add(new ExternalReading(
                    Source:     "Core Temp",
                    SensorType: isDelta ? "TemperatureDelta" : "Temperature",
                    Label:      isDelta ? $"{cpuName} 核心 #{i} 距 TjMax" : $"{cpuName} 核心 #{i}",
                    Value:      Math.Round(temp, 1),
                    Unit:       tempUnit
                ));

                // 負載
                uint load = accessor.ReadUInt32(OffsetLoad + i * 4);
                result.Add(new ExternalReading(
                    Source:     "Core Temp",
                    SensorType: "Usage",
                    Label:      $"{cpuName} 核心 #{i} 負載",
                    Value:      load,
                    Unit:       "%"
                ));
            }

            // ── TjMax（各核心的溫度上限） ──
            // TjMax 陣列最多 128 個（對應每顆 CPU 的每個核心）。
            // 這個值在 SDK 裡一律是絕對攝氏，不受使用者的華氏設定影響，
            // 先前沿用 tempUnit 會在華氏模式下標成「100 °F」。
            uint tjMaxCount = Math.Min(totalCores, 128);
            for (uint i = 0; i < tjMaxCount; i++)
            {
                uint tjMax = accessor.ReadUInt32(OffsetTjMax + i * 4);
                if (tjMax > 0 && tjMax < 200) // 合理範圍
                {
                    result.Add(new ExternalReading(
                        Source:     "Core Temp",
                        SensorType: "Temperature",
                        Label:      $"{cpuName} 核心 #{i} TjMax",
                        Value:      tjMax,
                        Unit:       "°C"
                    ));
                }
            }

            // ── VID 電壓 ──
            float vid = ReadFloat(accessor, OffsetVid);
            if (vid > 0 && vid < 5) // 合理範圍
            {
                result.Add(new ExternalReading(
                    Source:     "Core Temp",
                    SensorType: "Voltage",
                    Label:      $"{cpuName} VID",
                    Value:      Math.Round(vid, 4),
                    Unit:       "V"
                ));
            }

            // ── CPU 時脈 ──
            float cpuSpeed = ReadFloat(accessor, OffsetCpuSpeed);
            if (cpuSpeed > 0)
            {
                result.Add(new ExternalReading(
                    Source:     "Core Temp",
                    SensorType: "Clock",
                    Label:      $"{cpuName} 時脈",
                    Value:      Math.Round(cpuSpeed, 1),
                    Unit:       "MHz"
                ));
            }

            // ── FSB 時脈 ──
            float fsbSpeed = ReadFloat(accessor, OffsetFsbSpeed);
            if (fsbSpeed > 0)
            {
                result.Add(new ExternalReading(
                    Source:     "Core Temp",
                    SensorType: "Clock",
                    Label:      $"{cpuName} FSB",
                    Value:      Math.Round(fsbSpeed, 1),
                    Unit:       "MHz"
                ));
            }

            // ── 倍頻 ──
            float multiplier = ReadFloat(accessor, OffsetMultiplier);
            if (multiplier > 0)
            {
                result.Add(new ExternalReading(
                    Source:     "Core Temp",
                    SensorType: "Other",
                    Label:      $"{cpuName} 倍頻",
                    Value:      Math.Round(multiplier, 1),
                    Unit:       "x"
                ));
            }

            readings = result;
            return true;
        }
        catch (System.IO.FileNotFoundException)
        {
            error = "Core Temp 未執行";
            return false;
        }
        catch (Exception ex)
        {
            error = $"讀取 Core Temp 共享記憶體失敗：{ex.Message}";
            return false;
        }
        finally
        {
            accessor?.Dispose();
            mmf?.Dispose();
        }
    }

    /// <summary>從指定偏移量讀取一個 float（4 bytes）。</summary>
    private static float ReadFloat(MemoryMappedViewAccessor accessor, long offset)
    {
        // 使用 ReadArray 以避免對齊問題
        byte[] buf = new byte[4];
        accessor.ReadArray(offset, buf, 0, 4);
        return BitConverter.ToSingle(buf, 0);
    }
}
