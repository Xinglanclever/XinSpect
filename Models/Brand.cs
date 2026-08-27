using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>可辨識的硬體品牌（用於繪製自繪向量徽章，非官方商標重製）。</summary>
public enum Brand
{
    Unknown,
    Intel, Amd, Nvidia,
    Samsung, WesternDigital, Seagate, SkHynix, Kingston, Crucial, Micron,
    Toshiba, Kioxia, Adata, Corsair, SanDisk, GSkill, TeamGroup,
    Asus, Gigabyte, Msi, AsRock, Realtek,
    Evga, Biostar, Supermicro,
}

/// <summary>徽章上的類別字形（處理器 / 顯示卡 / 硬碟 / 記憶體 / 網路 / 主機板）。</summary>
public enum BadgeKind { Generic, Cpu, Gpu, Disk, Ram, Net, Board }

/// <summary>品牌顯示資訊：文字標記 + 由淺到深的漸層兩色（皆為原創配色）。</summary>
public readonly record struct BrandInfo(string Name, string Color1, string Color2);

/// <summary>品牌對照表與偵測器。徽章以品牌色 + 自繪字形呈現，屬原創向量圖，非商標圖樣重製。</summary>
public static partial class Brands
{
    public static BrandInfo Info(Brand b) => b switch
    {
        Brand.Intel          => new("intel",   "#1B9AF0", "#0060B4"),
        Brand.Amd            => new("AMD",     "#ED3B33", "#8C0D12"),
        Brand.Nvidia         => new("NVIDIA",  "#86D01A", "#4A7A00"),
        Brand.Samsung        => new("SAMSUNG", "#2E5BE8", "#12279E"),
        Brand.WesternDigital => new("WD",      "#2C6BE0", "#06227A"),
        Brand.Seagate        => new("SEAGATE", "#7ED957", "#3C8A2E"),
        Brand.SkHynix        => new("SK hynix","#F5443E", "#AF0018"),
        Brand.Kingston       => new("Kingston","#D6373F", "#8E0E17"),
        Brand.Crucial        => new("Crucial", "#2AA0E0", "#0A5C94"),
        Brand.Micron         => new("Micron",  "#2277C4", "#0A4A87"),
        Brand.Toshiba        => new("TOSHIBA", "#FF4A4A", "#B00000"),
        Brand.Kioxia         => new("KIOXIA",  "#9A5BC4", "#552587"),
        Brand.Adata          => new("ADATA",   "#F0453B", "#A81419"),
        Brand.Corsair        => new("CORSAIR", "#E0A400", "#7E5C00"),
        Brand.SanDisk        => new("SanDisk", "#EE4036", "#A31217"),
        Brand.GSkill         => new("G.SKILL", "#C22026", "#7A0E12"),
        Brand.TeamGroup      => new("TEAM",    "#E23B3B", "#96181C"),
        Brand.Asus           => new("ASUS",    "#2AA5D6", "#0A5C87"),
        Brand.Gigabyte       => new("GIGABYTE","#F58220", "#A8530A"),
        Brand.Msi            => new("MSI",     "#E23B2E", "#9A130C"),
        Brand.AsRock         => new("ASRock",  "#2E77C4", "#0A3E7A"),
        Brand.Realtek        => new("REALTEK", "#2E6BC4", "#0A3A7A"),
        Brand.Evga           => new("EVGA",    "#C8202B", "#4A0508"),
        Brand.Biostar        => new("BIOSTAR", "#12A0A0", "#075858"),
        Brand.Supermicro     => new("SUPERMICRO","#3C8A2E", "#14532D"),
        _                    => new("",        "#5C5C58", "#343432"),
    };

    /// <summary>從一組提示字串（型號 / 製造商 / 描述）推斷品牌，取第一個命中的。</summary>
    public static Brand Resolve(params string?[] hints)
    {
        foreach (var raw in hints)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.ToLowerInvariant();

            if (s.Contains("intel")) return Brand.Intel;
            if (s.Contains("nvidia") || s.Contains("geforce") || s.Contains("rtx ") ||
                s.Contains("gtx ") || s.Contains("quadro") || s.Contains("tesla") ||
                s.Contains("titan") || s.StartsWith("rtx") || s.StartsWith("gtx"))
                return Brand.Nvidia;
            if (s.Contains("amd") || s.Contains("advanced micro") || s.Contains("ryzen") ||
                s.Contains("radeon") || s.Contains("athlon") || s.Contains("threadripper") || s.Contains("epyc"))
                return Brand.Amd;

            if (s.Contains("samsung")) return Brand.Samsung;
            if (s.Contains("western digital") || s.Contains("wdc") ||
                Regex.IsMatch(s, @"\bwd[\s_-]?[a-z0-9]")) return Brand.WesternDigital;
            if (s.Contains("sandisk")) return Brand.SanDisk;
            if (s.Contains("seagate") || Regex.IsMatch(s, @"\bst\d{3,}")) return Brand.Seagate;
            if (s.Contains("hynix")) return Brand.SkHynix;
            if (s.Contains("kingston")) return Brand.Kingston;
            if (s.Contains("crucial")) return Brand.Crucial;
            if (s.Contains("micron")) return Brand.Micron;
            if (s.Contains("kioxia")) return Brand.Kioxia;
            if (s.Contains("toshiba")) return Brand.Toshiba;
            if (s.Contains("adata") || s.Contains("a-data") || s.Contains("xpg")) return Brand.Adata;
            if (s.Contains("corsair")) return Brand.Corsair;
            if (s.Contains("g.skill") || s.Contains("gskill") || s.Contains("g skill")) return Brand.GSkill;
            if (s.Contains("teamgroup") || s.Contains("team group") || s.Contains("t-force")) return Brand.TeamGroup;

            if (s.Contains("asrock")) return Brand.AsRock;
            if (s.Contains("asus") || s.Contains("asustek")) return Brand.Asus;
            if (s.Contains("gigabyte") || s.Contains("aorus")) return Brand.Gigabyte;
            if (s.Contains("micro-star") || s.Contains("msi")) return Brand.Msi;
            if (s.Contains("evga")) return Brand.Evga;
            if (s.Contains("biostar")) return Brand.Biostar;
            if (s.Contains("supermicro") || s.Contains("super micro")) return Brand.Supermicro;
            if (s.Contains("realtek")) return Brand.Realtek;
        }
        return Brand.Unknown;
    }

    /// <summary>主機板廠商的原創字母標記（用於徽章；無對應者回 null → 沿用通用主機板字形）。</summary>
    public static string? BoardMonogram(Brand b) => b switch
    {
        Brand.Asus       => "A",
        Brand.AsRock     => "AR",
        Brand.Gigabyte   => "G",
        Brand.Msi        => "MSI",
        Brand.Evga       => "E",
        Brand.Biostar    => "B",
        Brand.Supermicro => "SM",
        _ => null,
    };
}
