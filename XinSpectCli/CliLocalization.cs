using System.IO;
using System.Text;
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
    private static readonly HashSet<string> JsonCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "--json", "--compare-flash", "--verify-audit", "--integrity-baseline", "--sbom",
        "--disk-usage", "--errors", "--stdin-batch", "--diff-report", "--evidence-pack",
        "--compliance", "--compliance-diff", "--enterprise-baseline", "--merkle",
        "--speculation", "--fleet", "--redfish",
    };

    public static CliLanguage Current { get; private set; }

    public static bool TrySet(string? value)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "zh-tw":
            case "zh-hant":
            case "traditional":
                Current = CliLanguage.Traditional;
                return true;
            case "zh-cn":
            case "zh-hans":
            case "simplified":
                Current = CliLanguage.Simplified;
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
        public override void Write(char value) => inner.Write(value);
        public override void Write(char[] buffer, int index, int count)
            => inner.Write(Translate(new string(buffer, index, count)));
        public override void WriteLine(string? value) => inner.WriteLine(Translate(value));
        public override void WriteLine() => inner.WriteLine();
        public override void Flush() => inner.Flush();
    }
}
