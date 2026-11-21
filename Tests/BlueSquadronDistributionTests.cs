using System.IO;
using Xunit;

namespace XinSpect.Tests;

public sealed class BlueSquadronDistributionTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, Path.Combine(parts));
    }

    [Fact]
    public void 部署器安裝的同目錄Bridge優先於舊版快取()
    {
        string? resolved = BlueSquadronBootstrap.ResolveLocalPath(
            new[] { @"C:\XinSpect\BlueSquadronBridge.exe", @"C:\XinSpect\bluesquadron\BlueSquadronBridge.exe" },
            @"C:\Users\User\AppData\Local\XinSpect\bluesquadron\BlueSquadronBridge.exe",
            path => path.Contains(@"C:\XinSpect\", StringComparison.Ordinal));

        Assert.Equal(@"C:\XinSpect\BlueSquadronBridge.exe", resolved);
    }

    [Fact]
    public void 找不到已安裝Bridge時回傳缺少狀態供上層提供引導()
    {
        string? resolved = BlueSquadronBootstrap.ResolveLocalPath(
            Array.Empty<string>(), @"C:\Users\User\AppData\Local\XinSpect\bluesquadron\BlueSquadronBridge.exe",
            _ => false);

        Assert.Null(resolved);
    }

    [Fact]
    public void Release本體不內嵌Bridge而發行腳本仍保留獨立橋接資產()
    {
        string project = File.ReadAllText(RepoFile("XinSpect.csproj"));
        string release = File.ReadAllText(RepoFile("Tools", "release.ps1"));

        Assert.DoesNotContain("BuildAndEmbedBlueSquadronBridge", project, StringComparison.Ordinal);
        Assert.DoesNotContain("XinSpect.bridge.BlueSquadronBridge.exe", project, StringComparison.Ordinal);
        Assert.Contains("BlueSquadronBridge.exe", release, StringComparison.Ordinal);
    }

    [Fact]
    public void 部署器把BlueSquadron標示為獨立且未完工()
    {
        string installer = File.ReadAllText(RepoFile("Installer", "Install.cs"));

        Assert.DoesNotContain("含 LHM＋WinRing0 驅動＋BlueSquadron", installer, StringComparison.Ordinal);
        Assert.Contains("BlueSquadron 為獨立橋接，尚未完工", installer, StringComparison.Ordinal);
    }
}
