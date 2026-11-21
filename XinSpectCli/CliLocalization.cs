using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using XinSpect;

namespace XinSpectCli;

internal enum CliLanguage
{
    Traditional,
    Simplified,
    English,
}

internal static class CliLocalization
{
    private static SettingsService? _settings;
    private static readonly ConditionalWeakTable<TextWriter, TextWriter> MachineWriters = new();
    private static readonly HashSet<string> JsonCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "--json", "--compare-flash", "--verify-audit", "--integrity-baseline", "--sbom",
        "--disk-usage", "--errors", "--stdin-batch", "--diff-report", "--evidence-pack",
        "--compliance", "--compliance-diff", "--enterprise-baseline", "--merkle",
        "--speculation", "--fleet", "--redfish", CliService.AgentBridgeArg, CliService.SensorCrossCheckArg,
        "sensor-crosscheck",
    };

    // Fill the CLI's high-frequency shell/menu strings from the desktop translation catalog.
    // This keeps the REPL's primary navigation readable in English even where a detailed
    // command explanation has not yet been added to the shared catalog.
    private static readonly Dictionary<string, string> EnglishOverrides = new(StringComparer.Ordinal)
    {
        ["曦覽終端機"] = "XinSpect CLI", ["快速開始"] = "Quick start",
        ["指令書"] = "Command book", ["採集與查詢"] = "Collection & queries",
        ["即時監控"] = "Live monitoring", ["深層探針"] = "Deep probes",
        ["證據與合規"] = "Evidence & compliance", ["導覽"] = "Navigation",
        ["系統"] = "System", ["採集一輪全機硬體事實進快取"] = "Collect a full hardware snapshot into cache",
        ["重新採集事實快取"] = "Refresh the hardware facts cache",
        ["搜尋靜態事實鍵目錄"] = "Search the static fact-key catalog",
        ["健康摘要：六態分布與對帳矛盾"] = "Health summary: availability and consistency",
        ["即時一行 KPI 快照"] = "One-line live KPI snapshot",
        ["即時表格持續刷新"] = "Live table, continuously refreshed",
        ["全螢幕即時儀表板"] = "Full-screen live dashboard",
        ["唯讀"] = "Read-only", ["不寫硬體"] = "no hardware writes", ["不連網"] = "offline",
        ["目前語言：繁體中文。用 lang zh-T 或 zh-S 切換；按 K 可切換 English／简体中文。"] = "Current language: Traditional Chinese. Use lang zh-T or zh-S to switch; press K for English / Simplified Chinese.",
        ["目前語言：简体中文。用 lang zh-T 或 zh-S 切換；按 K 可切換 English／简体中文。"] = "Current language: Simplified Chinese. Use lang zh-T or zh-S to switch; press K for English / Simplified Chinese.",
        ["目前語言：English。用 lang zh-T 或 zh-S 切換；按 K 可切換 English／简体中文。"] = "Current language: English. Use lang zh-T or zh-S to switch; press K to toggle English / Simplified Chinese.",
        ["已切換至繁體中文。"] = "Switched to Traditional Chinese.",
        ["已切换到简体中文。"] = "Switched to Simplified Chinese.",
        ["已切換至 English。"] = "Switched to English.",
    };

    public static CliLanguage Current { get; private set; }

    public static void Initialize(SettingsService settings)
    {
        _settings = settings;
        Current = settings.IsEnglish ? CliLanguage.English
            : settings.SimplifiedChinese ? CliLanguage.Simplified : CliLanguage.Traditional;
    }

    public static bool TrySet(string? value)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "zh-tw":
            case "zh-hant":
            case "zh-t":
            case "traditional":
                Current = CliLanguage.Traditional;
                if (_settings is not null)
                {
                    _settings.SimplifiedChinese = false;
                    _settings.IsEnglish = false;
                }
                return true;
            case "zh-cn":
            case "zh-hans":
            case "simplified":
            case "zh-s":
                Current = CliLanguage.Simplified;
                if (_settings is not null)
                {
                    _settings.SimplifiedChinese = true;
                    _settings.IsEnglish = false;
                }
                return true;
            case "en":
            case "english":
                Current = CliLanguage.English;
                if (_settings is not null)
                {
                    _settings.SimplifiedChinese = false;
                    _settings.IsEnglish = true;
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>Keyboard cycle requested by the CLI: English → Simplified Chinese → English.</summary>
    public static void ToggleEnglishSimplified()
        => TrySet(Current == CliLanguage.English ? "zh-S" : "en");

    public static string LanguageLabel => Current switch
    {
        CliLanguage.English => "English",
        CliLanguage.Simplified => "简体中文",
        _ => "繁體中文",
    };

    public static string LanguageCycleLabel => Current switch
    {
        CliLanguage.English => "English · K switches to 简体中文",
        CliLanguage.Simplified => "简体中文 · K 切换到 English",
        _ => "繁體中文 · 按 K 切換 English／简体中文",
    };

    public static string FooterLabel => Current switch
    {
        CliLanguage.English => "● READY · READ-ONLY · " + LanguageCycleLabel,
        CliLanguage.Simplified => "● 就绪 · 只读模式 · " + LanguageCycleLabel,
        _ => "● READY · 唯讀模式 · " + LanguageCycleLabel,
    };

    public static string Translate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (Current == CliLanguage.Simplified) return LanguageService.ToSimplified(text);
        if (Current == CliLanguage.English)
        {
            if (EnglishOverrides.TryGetValue(text, out string? english)) return english;
            if (EnglishStrings.Lookup(text) is { } exact) return exact;
            return string.Join(" ・ ", text.Split(" ・ ").Select(part =>
                EnglishOverrides.TryGetValue(part, out string? item) ? item : EnglishStrings.Lookup(part) ?? part));
        }
        return text;
    }

    public static bool IsMachineFormat(IReadOnlyList<string> args)
    {
        if (args.Count > 0 && JsonCommands.Contains(args[0])) return true;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].Equals("--json", StringComparison.OrdinalIgnoreCase)) return true;
            if (args[i].Equals("--format", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count
                && (args[i + 1].Equals("json", StringComparison.OrdinalIgnoreCase)
                    || args[i + 1].Equals("sarif", StringComparison.OrdinalIgnoreCase)
                    || args[i + 1].Equals("junit", StringComparison.OrdinalIgnoreCase)
                    || args[i + 1].Equals("cef", StringComparison.OrdinalIgnoreCase)
                    || args[i + 1].Equals("otel", StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    internal static string RedactSettingsJson(string json)
    {
        JsonNode? root = JsonNode.Parse(json);
        Redact(root);
        return root?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
    }

    internal static string[] SplitCommandLine(string input)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        bool hasToken = false;
        foreach (char ch in input)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (!hasToken) continue;
                arguments.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(ch);
                hasToken = true;
            }
        }

        if (quoted) throw new FormatException("引數有未結束的雙引號。");
        if (hasToken) arguments.Add(current.ToString());
        return [.. arguments];
    }

    internal static string JoinCommandLine(IReadOnlyList<string> arguments)
        => string.Join(' ', arguments.Select(argument =>
            argument.Any(char.IsWhiteSpace) ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : argument));

    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (string name in obj.Select(pair => pair.Key).ToArray())
            {
                if (name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("password", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("token", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("secret", StringComparison.OrdinalIgnoreCase))
                    obj[name] = "[REDACTED]";
                else
                    Redact(obj[name]);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array) Redact(child);
        }
    }

    public static void InstallConsoleWriters(bool machineFormat)
    {
        if (machineFormat) return;
        var stdout = MachineWriter(Console.Out);
        var stderr = MachineWriter(Console.Error);
        Console.SetOut(WrapWriter(stdout));
        Console.SetError(WrapWriter(stderr));
        // Console.SetOut/SetError add a synchronized wrapper that hides LocalizedWriter.
        MachineWriters.Add(Console.Out, stdout);
        MachineWriters.Add(Console.Error, stderr);
    }

    internal static TextWriter WrapWriter(TextWriter writer) => new LocalizedWriter(writer);

    internal static TextWriter MachineWriter(TextWriter writer)
        => MachineWriters.TryGetValue(writer, out var original) ? MachineWriter(original)
            : writer is LocalizedWriter localized ? MachineWriter(localized.Inner) : writer;

    public static bool TryConsumeOption(string[] args, out string[] remaining, out string? error)
    {
        var output = new List<string>(args.Length);
        error = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals("--lang", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(args[i]);
                continue;
            }

            if (i + 1 >= args.Length)
            {
            error = "--lang requires en, zh-T or zh-S.";
                remaining = args;
                return false;
            }
            if (!TrySet(args[++i]))
            {
                error = "Unsupported language. Use en, zh-T or zh-S.";
                remaining = args;
                return false;
            }
        }

        remaining = output.ToArray();
        return true;
    }

    private sealed class LocalizedWriter(TextWriter inner) : TextWriter
    {
        internal TextWriter Inner => inner;
        public override Encoding Encoding => inner.Encoding;
        public override IFormatProvider FormatProvider => inner.FormatProvider;
        [AllowNull]
        public override string NewLine { get => inner.NewLine; set => inner.NewLine = value; }

        public override void Write(string? value) => inner.Write(Translate(value));
        public override void Write(char value) => inner.Write(Translate(value.ToString()));
        public override void Write(char[] buffer, int index, int count)
            => inner.Write(Translate(new string(buffer, index, count)));
        public override void Write(ReadOnlySpan<char> buffer)
            => inner.Write(Translate(buffer.ToString()));
        public override void WriteLine(string? value) => inner.WriteLine(Translate(value));
        public override void WriteLine(char value) => inner.WriteLine(Translate(value.ToString()));
        public override void WriteLine(char[]? buffer)
            => inner.WriteLine(buffer is null ? null : Translate(new string(buffer)));
        public override void WriteLine(char[] buffer, int index, int count)
            => inner.WriteLine(Translate(new string(buffer, index, count)));
        public override void WriteLine(ReadOnlySpan<char> buffer)
            => inner.WriteLine(Translate(buffer.ToString()));
        public override void WriteLine() => inner.WriteLine();
        public override void Flush() => inner.Flush();
    }
}
