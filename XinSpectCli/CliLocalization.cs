using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics.CodeAnalysis;
using XinSpect;

namespace XinSpectCli;

internal enum CliLanguage
{
    Traditional,
    Simplified,
}

internal static class CliLocalization
{
    private static SettingsService? _settings;
    private static readonly HashSet<string> JsonCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "--json", "--compare-flash", "--verify-audit", "--integrity-baseline", "--sbom",
        "--disk-usage", "--errors", "--stdin-batch", "--diff-report", "--evidence-pack",
        "--compliance", "--compliance-diff", "--enterprise-baseline", "--merkle",
        "--speculation", "--fleet", "--redfish",
    };

    public static CliLanguage Current { get; private set; }

    public static void Initialize(SettingsService settings)
    {
        _settings = settings;
        Current = settings.SimplifiedChinese ? CliLanguage.Simplified : CliLanguage.Traditional;
    }

    public static bool TrySet(string? value)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "zh-tw":
            case "zh-hant":
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
                Current = CliLanguage.Simplified;
                if (_settings is not null)
                {
                    _settings.SimplifiedChinese = true;
                    _settings.IsEnglish = false;
                }
                return true;
            default:
                return false;
        }
    }

    public static string Translate(string? text)
        => Current == CliLanguage.Simplified && !string.IsNullOrEmpty(text)
            ? LanguageService.ToSimplified(text)
            : text ?? "";

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
        Console.SetOut(WrapWriter(Console.Out));
        Console.SetError(WrapWriter(Console.Error));
    }

    internal static TextWriter WrapWriter(TextWriter writer) => new LocalizedWriter(writer);

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
                error = "--lang requires zh-TW or zh-CN.";
                remaining = args;
                return false;
            }
            if (!TrySet(args[++i]))
            {
                error = "Unsupported language. Use zh-TW or zh-CN.";
                remaining = args;
                return false;
            }
        }

        remaining = output.ToArray();
        return true;
    }

    private sealed class LocalizedWriter(TextWriter inner) : TextWriter
    {
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
