// AIDA64 共享記憶體讀取器
// 透過 Global\AIDA64_SensorValues 讀取 AIDA64 即時感測器資料

using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// 從 AIDA64 共享記憶體區段讀取感測器數值。
/// AIDA64 必須在執行中並啟用「外部應用程式」→「共享記憶體」。
/// 共享記憶體內容為類 XML 格式：&lt;key&gt;value&lt;/key&gt;
/// </summary>
public static partial class Aida64SharedMem
{
    // ── 共享記憶體名稱 ──────────────────────────────────────────
    private const string SharedMemName = "Global\\AIDA64_SensorValues";

    // ── 共享記憶體最大讀取長度（AIDA64 預設配置約數 KB） ────────
    private const int MaxReadBytes = 65536;

    // ── 解析用正規表達式：擷取 <KEY>VALUE</KEY> ────────────────
    [GeneratedRegex(@"<(?<key>[^/>]+)>(?<val>[^<]*)</\k<key>>", RegexOptions.Compiled)]
    private static partial Regex TagPattern();

    // ── AIDA64 鍵名前綴 → 感測器類型與單位的對映 ───────────────
    // 鍵名格式範例：TCPU (CPU 溫度)、FCPU (CPU 風扇)、VCPU (CPU 電壓)
    //               PCPU (CPU 功耗)、SCPUCLK (CPU 時脈)、CCPU (CPU 電流)
    //               UCPU (CPU 使用率)
    private static (string Type, string Unit) ClassifyKey(string key)
    {
        if (key.Length < 2) return ("Other", "");

        return key[0] switch
        {
            'T' => ("Temperature", "°C"),  // 溫度
            'F' => ("Fan",         "RPM"),      // 風扇轉速
            'V' => ("Voltage",     "V"),        // 電壓
            'P' => ("Power",       "W"),        // 功耗
            'C' => ("Current",     "A"),        // 電流
            'S' => ("Clock",       "MHz"),      // 時脈（Speed）
            'U' => ("Usage",       "%"),        // 使用率
            _   => ("Other",       ""),
        };
    }

    /// <summary>
    /// 嘗試從 AIDA64 共享記憶體讀取所有感測器讀數。
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
            mmf = MemoryMappedFile.OpenExisting(SharedMemName, MemoryMappedFileRights.Read);
            accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            // 讀取原始位元組並轉為字串
            long capacity = accessor.Capacity;
            int readLen = (int)Math.Min(capacity, MaxReadBytes);
            byte[] buffer = new byte[readLen];
            accessor.ReadArray(0, buffer, 0, readLen);

            // 找到有效內容結尾（null terminator 或緩衝區尾端）
            int endIdx = Array.IndexOf(buffer, (byte)0);
            if (endIdx < 0) endIdx = readLen;

            string content = Encoding.UTF8.GetString(buffer, 0, endIdx);

            if (string.IsNullOrWhiteSpace(content))
            {
                readings = Array.Empty<ExternalReading>();
                return true;
            }

            // 解析類 XML 標籤
            var matches = TagPattern().Matches(content);
            var result = new List<ExternalReading>(matches.Count);

            foreach (Match match in matches)
            {
                string key      = match.Groups["key"].Value;
                string valueStr = match.Groups["val"].Value;

                // 嘗試將值解析為數字
                if (!double.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture,
                                     out double value))
                {
                    continue; // 非數值條目（例如系統名稱），略過
                }

                var (sensorType, unit) = ClassifyKey(key);

                // 以 AIDA64 鍵名作為標籤（去掉類型前綴會失去語境，保留完整鍵名）
                result.Add(new ExternalReading(
                    Source:     "AIDA64",
                    SensorType: sensorType,
                    Label:      key,
                    Value:      value,
                    Unit:       unit
                ));
            }

            readings = result;
            return true;
        }
        catch (System.IO.FileNotFoundException)
        {
            error = "AIDA64 未執行或未啟用共享記憶體";
            return false;
        }
        catch (Exception ex)
        {
            error = $"讀取 AIDA64 共享記憶體失敗：{ex.Message}";
            return false;
        }
        finally
        {
            accessor?.Dispose();
            mmf?.Dispose();
        }
    }
}
