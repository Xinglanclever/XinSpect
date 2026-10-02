using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>
/// CLI 模式（V7 WP32／A42）：帶引數啟動時不走 WPF，headless 收集事實後以 JSON 輸出。
/// <b>退出碼語意</b>：0＝全部 Present；2＝收集完成但部分事實讀不到（三態如實反映在輸出裡）；
/// 1＝致命錯誤（參數錯、收集拋例外）。自動化管線靠退出碼分岔，不必解析人話。
/// 誠實界線：本模式只輸出驅動相依證據組（韌體安全稽核範圍）；全機快照（SMBIOS／磁碟／GPU）
/// 依賴 WPF 服務層，未納入——幫助文字如實標明，不假裝全機都掃了。
/// </summary>
public static class CliService
{
    public const string JsonArg = "--json";
    public const string EvidenceScope = "evidence";
    public const string QueryArg = "--query";
    public const string OutArg = "--out";
    public const string HelpArg = "--help";
    public const string CompareArg = "--compare-flash";

    public const int ExitOk = 0;
    public const int ExitPartial = 2;
    public const int ExitError = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static int Run(string[] args, Func<IReadOnlyList<HardwareFact>> collect, TextWriter stdout, TextWriter stderr,
        Func<byte[], HardwareFact>? compare = null)
    {
        if (args.Length == 0 || args.Contains(HelpArg))
        {
            PrintHelp(stdout);
            return ExitOk;
        }
        if (args[0] == CompareArg)
            return RunCompare(args, compare, stdout, stderr);
        if (args[0] != JsonArg)
        {
            stderr.WriteLine($"未知引數「{args[0]}」。用 --help 看用法。");
            return ExitError;
        }
        if (args.Length < 2 || args[1] != EvidenceScope)
        {
            stderr.WriteLine($"--json 之後要接範圍，目前唯一支援：「{EvidenceScope}」。");
            return ExitError;
        }
        string? query = OptionValue(args, QueryArg);
        string? outPath = OptionValue(args, OutArg);

        IReadOnlyList<HardwareFact> facts;
        try
        {
            facts = collect();
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"收集失敗：{ex.GetType().Name}：{ex.Message}");
            return ExitError;
        }

        if (query is not null)
            facts = facts.Where(f => f.Key.StartsWith(query, StringComparison.Ordinal)).ToList();

        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            scope = EvidenceScope,
            count = facts.Count,
            allPresent = facts.All(f => f.Availability == FactAvailability.Present),
            facts = facts.Select(FactJson),
        };
        string json = JsonSerializer.Serialize(payload, JsonOptions);

        try
        {
            if (outPath is not null)
                File.WriteAllText(outPath, json);
            else
                stdout.WriteLine(json);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"寫出失敗：{ex.Message}");
            return ExitError;
        }

        return facts.All(f => f.Availability == FactAvailability.Present) ? ExitOk : ExitPartial;
    }

    /// <summary>
    /// BIOS 區 vs 參考映像比對模式（WP4 第二層的 CLI 面）。
    /// 退出碼：0＝一致；2＝有差異或三態（比對沒能完成／大小不符——細節在輸出）；1＝致命（讀檔失敗等）。
    /// </summary>
    private static int RunCompare(string[] args, Func<byte[], HardwareFact>? compare, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2)
        {
            stderr.WriteLine("--compare-flash 之後要接參考映像路徑。");
            return ExitError;
        }
        string? outPath = OptionValue(args, OutArg);

        byte[] reference;
        try
        {
            reference = File.ReadAllBytes(args[1]);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"讀取參考映像失敗：{ex.Message}");
            return ExitError;
        }

        HardwareFact fact;
        try
        {
            fact = (compare ?? EvidenceCollection.CompareFlashWithReference)(reference);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"比對失敗：{ex.GetType().Name}：{ex.Message}");
            return ExitError;
        }

        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            scope = "flashcompare",
            reference = new { Path = args[1], Bytes = reference.Length },
            fact = FactJson(fact),
        };
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        try
        {
            if (outPath is not null)
                File.WriteAllText(outPath, json);
            else
                stdout.WriteLine(json);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"寫出失敗：{ex.Message}");
            return ExitError;
        }

        return fact.Availability == FactAvailability.Present && fact.NumericValue == 0 ? ExitOk : ExitPartial;
    }

    private static object FactJson(HardwareFact f) => new
    {
        f.Key, f.Category, f.Name, f.Value, f.Unit, f.Source, f.Trust,
        availability = f.Availability.ToString(),
        f.UnavailableReason, f.NumericValue, f.MeasuredAtUtc,
    };

    private static string? OptionValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void PrintHelp(TextWriter w)
    {
        w.WriteLine("""
            曦覽 XinSpect CLI（V7 WP32）・ headless 事實收集

            用法：
              XinSpect --json evidence [--query <key 前綴>] [--out <檔案>]
              XinSpect --compare-flash <參考映像> [--out <檔案>]
              XinSpect --help

            範圍：
              evidence        驅動相依證據組：晶片組安全、SPI 快閃、平台安全 MSR、MCHBAR、
                              PCIe AER、後端與環境、CPU 韌體身分、I/O 埠、CMOS、SMBus、
                              UEFI 開機設定、Super I/O、PCI 裝置盤點、交叉對帳、ACPI 表清單。
                              （全機快照的 SMBIOS／磁碟／GPU 面依賴 WPF 服務層，本模式未涵蓋。）
              compare-flash   BIOS 區 vs 參考映像逐 4KB 塊比對（映像＝原廠或信任來源的 BIOS 區 dump）。

            選項：
              --query <前綴>   只輸出 key 以該前綴開頭的事實（例：--query platform.）
              --out <檔案>     寫入檔案而非 stdout

            退出碼：
              --json evidence   0＝全部 Present；2＝部分讀不到（三態細節在輸出）；1＝致命錯誤。
              --compare-flash   0＝一致；2＝有差異或無法完成比對（三態細節在輸出）；1＝致命錯誤。
            讀不到的事實如實帶 availability 與原因，絕不以 0／典型值頂替。
            """);
    }
}
