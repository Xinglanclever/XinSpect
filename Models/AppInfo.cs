using System.Reflection;

namespace XinSpect;

/// <summary>
/// 版本與品名的單一來源：一律從組件版本讀取，避免版號散落在各個畫面的字面字串裡。
/// 版號本身定義於 XinSpect.csproj 的 &lt;Version&gt;，改一處即可全體同步。
/// </summary>
public static class AppInfo
{
    /// <summary>純版號（如 2.0.0；已去除 Git 雜湊後綴）。</summary>
    public static string Version { get; } = Read();

    /// <summary>短版號（僅主版與次版，如 2.0），供標題列等空間有限處使用。</summary>
    public static string ShortVersion
    {
        get
        {
            var p = Version.Split('.');
            return p.Length >= 2 ? $"{p[0]}.{p[1]}" : Version;
        }
    }

    public static string Name => "曦覽 XinSpect";
    public static string VersionText => $"版本 {ShortVersion}";

    private static string Read()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }
            var v = asm.GetName().Version;
            return v is null ? "—" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return "—"; }
    }
}
