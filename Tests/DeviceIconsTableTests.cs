using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>徽章對照表的整表不變式（形狀正確，而非某顆晶片抓不抓得到）。</summary>
/// <remarks>
/// 個別型號的命中與誤判防線寫在 <see cref="DeviceIconsTests"/>；這一組管的是那些
/// 「寫錯了不會拋例外、也不會有人察覺」的欄位：
///   · Glyph 打錯字（"stem"）只會安靜地畫出預設方塊；
///   · 規則裡出現大寫字母則永遠不可能命中（比對前已 ToLowerInvariant）；
///   · 新規則被排在它前面的通則遮蔽——徽章看起來永遠「很合理」，只是掛錯了家；
///   · Tier2 給了但 Chip2 忘了給，第二枚膠囊就靜靜地不畫。
/// 每往對照表加一列，都必須同時在 <see cref="Samples"/> 補一個代表字串，否則本組會紅。
/// 那個字串就是「這條規則真的抓得到它自己」的憑證，也是防遮蔽的唯一辦法。
/// </remarks>
public class DeviceIconsTableTests
{
    /// <summary>已實作的中心字形代號（見 BrandBadge.CenterGlyph／SpecialGlyph）；空字串＝不畫字形。</summary>
    private static readonly string[] KnownGlyphs =
        { "", "x", "star", "cpu", "gpu", "disk", "board", "steam" };

    private static readonly Regex Hex = new(@"^#[0-9a-fA-F]{6}$");

    [Theory]
    [InlineData(BadgeKind.Cpu)]
    [InlineData(BadgeKind.Gpu)]
    [InlineData(BadgeKind.Disk)]
    [InlineData(BadgeKind.Board)]
    public void 同一張表內的Id不得重複(BadgeKind kind)
    {
        var ids = DeviceIcons.Table(kind).Select(i => i.Id).ToList();
        var dup = ids.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(dup);
    }

    [Theory]
    [InlineData(BadgeKind.Cpu)]
    [InlineData(BadgeKind.Gpu)]
    [InlineData(BadgeKind.Disk)]
    [InlineData(BadgeKind.Board)]
    public void 每條規則都要能編譯_且不得出現大寫字母(BadgeKind kind)
    {
        foreach (var icon in DeviceIcons.Table(kind))
        {
            Regex.Match("", icon.Regex);   // 語法錯誤在此拋出，而不是等到某人的機器上
            // 比對前輸入已轉小寫，故規則裡的大寫字母永遠不可能命中（跳過 \d \s \b 之類的轉義）
            Assert.DoesNotMatch("[A-Z]", Regex.Replace(icon.Regex, @"\\.", ""));
        }
    }

    [Theory]
    [InlineData(BadgeKind.Cpu)]
    [InlineData(BadgeKind.Gpu)]
    [InlineData(BadgeKind.Disk)]
    [InlineData(BadgeKind.Board)]
    public void 中心字形代號必須是已實作的那幾個(BadgeKind kind)
    {
        foreach (var icon in DeviceIcons.Table(kind))
            Assert.Contains(icon.Glyph, KnownGlyphs);
    }

    [Theory]
    [InlineData(BadgeKind.Cpu)]
    [InlineData(BadgeKind.Gpu)]
    [InlineData(BadgeKind.Disk)]
    [InlineData(BadgeKind.Board)]
    public void 中心文字與字形不得同時給(BadgeKind kind)
    {
        foreach (var icon in DeviceIcons.Table(kind))
            Assert.False(icon.Text.Length > 0 && icon.Glyph.Length > 0,
                $"{icon.Id}：Text 非空時 Glyph 會被忽略，兩個都給表示有一個是死設定");
    }
    [Theory]
    [InlineData(BadgeKind.Cpu)]
    [InlineData(BadgeKind.Gpu)]
    [InlineData(BadgeKind.Disk)]
    [InlineData(BadgeKind.Board)]
    public void 膠囊必須有字_第二枚膠囊的文字與配色須成對(BadgeKind kind)
    {
        foreach (var icon in DeviceIcons.Table(kind))
        {
            Assert.False(string.IsNullOrWhiteSpace(icon.Tier), $"{icon.Id}：膠囊沒有字");
            // 只給文字不給配色（或反之）＝第二枚膠囊靜靜地不畫，是最難察覺的那種錯
            Assert.Equal(icon.Tier2.Length > 0, icon.Chip2 is not null);
        }
    }

    [Theory]
    [InlineData(BadgeKind.Cpu)]
    [InlineData(BadgeKind.Gpu)]
    [InlineData(BadgeKind.Disk)]
    [InlineData(BadgeKind.Board)]
    public void 所有配色都必須是可解析的十六進位色(BadgeKind kind)
    {
        foreach (var icon in DeviceIcons.Table(kind))
        {
            Assert.NotEmpty(icon.Emblem);
            Assert.NotEmpty(icon.Chip);
            foreach (var c in icon.Emblem.Concat(icon.Chip)
                                         .Concat(icon.Ring ?? [])
                                         .Concat(icon.Frame ?? [])
                                         .Concat(icon.Chip2 ?? [])
                                         .Concat(icon.TopColor ?? [])
                                         .Append(icon.Ink).Append(icon.MarkInk))
                Assert.Matches(Hex, c);
        }
    }
    /// <summary>
    /// 每一列的代表字串（韌體／WMI 會回報的樣子）。加規則就要加一筆，這是防遮蔽的唯一辦法：
    /// 只要新規則被排在它前面的通則吃掉，這裡就會指向別人的 Id 而讓測試變紅。
    /// </summary>
    private static readonly (BadgeKind Kind, string Id, string Sample)[] Samples =
    {
        (BadgeKind.Cpu, "i7-8086k",        "Intel(R) Core(TM) i7-8086K CPU @ 4.00GHz"),
        (BadgeKind.Cpu, "xeon-x5698",      "Intel(R) Xeon(R) CPU X5698 @ 4.40GHz"),
        (BadgeKind.Cpu, "everest",         "Intel JKT EVEREST SS 4.4GHZ INTERNAL USE ONLY"),
        (BadgeKind.Cpu, "blackops",        "Intel BlackOps 6 Core 4.60GHz"),
        (BadgeKind.Cpu, "xeon-e5-2602v4",  "Intel(R) Xeon(R) CPU E5-2602 v4"),
        (BadgeKind.Cpu, "cc150",           "Intel(R) Core(TM) CC150 CPU @ 3.50GHz"),
        (BadgeKind.Cpu, "ryzen-x3d",       "AMD Ryzen 7 9800X3D 8-Core Processor"),
        (BadgeKind.Cpu, "threadripper",    "AMD Ryzen Threadripper 3990X 64-Core Processor"),
        (BadgeKind.Cpu, "ryzen",           "AMD Ryzen 9 5950X 16-Core Processor"),
        (BadgeKind.Cpu, "epyc",            "AMD EPYC 9754 128-Core Processor"),
        (BadgeKind.Cpu, "opteron",         "AMD Opteron(tm) Processor 6276"),
        (BadgeKind.Cpu, "pentium-gold",    "Intel(R) Pentium(R) Gold G7400 @ 3.70GHz"),
        (BadgeKind.Cpu, "i9-9990xe",       "Intel(R) Core(TM) i9-9990XE CPU @ 4.00GHz"),
        (BadgeKind.Cpu, "i9-extreme",      "Intel(R) Core(TM) i9-10980XE CPU @ 3.00GHz"),
        (BadgeKind.Cpu, "core-x",          "Intel(R) Core(TM) i9-9900X CPU @ 3.50GHz"),
        (BadgeKind.Cpu, "i7-extreme-blue", "Intel(R) Core(TM) i7-5960X CPU @ 3.00GHz"),
        (BadgeKind.Cpu, "i7-extreme",      "Intel(R) Core(TM)2 Extreme CPU QX9650 @ 3.00GHz"),
        (BadgeKind.Cpu, "xeon-platinum",   "Intel(R) Xeon(R) Platinum 8480+"),
        (BadgeKind.Cpu, "xeon-gold",       "Intel(R) Xeon(R) Gold 6248R"),
        (BadgeKind.Cpu, "xeon-silver",     "Intel(R) Xeon(R) Silver 4214"),
        (BadgeKind.Cpu, "xeon-bronze",     "Intel(R) Xeon(R) Bronze 3204"),
        (BadgeKind.Cpu, "xeon-w",          "Intel(R) Xeon(R) W-2295 CPU @ 3.00GHz"),
        (BadgeKind.Cpu, "xeon-e7",         "Intel(R) Xeon(R) CPU E7-8890 v4 @ 2.20GHz"),
        (BadgeKind.Cpu, "xeon-e5-1680v2",  "Intel(R) Xeon(R) CPU E5-1680 v2 @ 3.00GHz"),
        (BadgeKind.Cpu, "xeon-e5",         "Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz"),
        (BadgeKind.Cpu, "xeon-e3",         "Intel(R) Xeon(R) CPU E3-1230 v6 @ 3.50GHz"),
        (BadgeKind.Cpu, "xeon-e",          "Intel(R) Xeon(R) E-2288G CPU @ 3.70GHz"),
        (BadgeKind.Cpu, "xeon",            "Intel(R) Xeon(R) CPU 5160 @ 3.00GHz"),
        (BadgeKind.Cpu, "es-sample",       "Genuine Intel(R) CPU 0000 @ 2.00GHz"),
        (BadgeKind.Gpu, "titan-ceo",       "NVIDIA TITAN V CEO Edition"),
        (BadgeKind.Gpu, "titan-v",         "NVIDIA TITAN V"),
        (BadgeKind.Gpu, "titan",           "NVIDIA TITAN RTX"),
        (BadgeKind.Gpu, "tesla",           "NVIDIA Tesla V100-SXM2-16GB"),
        (BadgeKind.Gpu, "nv-datacenter",   "NVIDIA H100 80GB HBM3"),
        (BadgeKind.Gpu, "nv-quadro",       "NVIDIA Quadro P5000"),
        (BadgeKind.Gpu, "nv-rtx-pro",      "NVIDIA RTX A6000"),
        (BadgeKind.Gpu, "amd-instinct",    "AMD Instinct MI300X"),
        (BadgeKind.Gpu, "amd-radeon-pro",  "AMD Radeon Pro W7900"),
        (BadgeKind.Disk, "optane",         "INTEL SSDPED1D480GA"),
        (BadgeKind.Board, "board-apex",    "ROG MAXIMUS Z790 APEX"),
        (BadgeKind.Board, "board-dark",    "EVGA Z790 DARK KINGPIN"),
        (BadgeKind.Board, "board-godlike", "MEG Z790 GODLIKE"),
        (BadgeKind.Board, "board-tachyon", "Z790 AORUS TACHYON"),
        (BadgeKind.Cpu, "i9-ks",           "Intel(R) Core(TM) i9-14900KS CPU @ 3.20GHz"),
        (BadgeKind.Cpu, "xeon-w3175x",     "Intel(R) Xeon(R) W-3175X CPU @ 3.10GHz"),
        (BadgeKind.Cpu, "xeon-oem",        "Intel(R) Xeon(R) CPU E5-2696 v4 @ 2.20GHz"),
        (BadgeKind.Cpu, "xeon-phi",        "Intel(R) Xeon Phi(TM) CPU 7250 @ 1.40GHz"),
        (BadgeKind.Cpu, "itanium",         "Intel(R) Itanium(R) Processor 9750 @ 2.53GHz"),
        (BadgeKind.Cpu, "athlon-fx",       "AMD Athlon(tm) 64 FX-60 Processor"),
        (BadgeKind.Cpu, "fx-9590",         "AMD FX(tm)-9590 Eight-Core Processor"),
        (BadgeKind.Gpu, "dual-gpu",        "NVIDIA GeForce GTX 690"),
        (BadgeKind.Gpu, "china-d",         "NVIDIA GeForce RTX 4090 D"),
        (BadgeKind.Gpu, "nv-cmp",          "NVIDIA CMP 90HX"),
// PLACEHOLDER-SAMPLES
    };
    [Fact]
    public void 每一列都要有代表字串_且不得多出對不上的樣本()
    {
        foreach (var kind in new[] { BadgeKind.Cpu, BadgeKind.Gpu, BadgeKind.Disk, BadgeKind.Board })
        {
            var ids = DeviceIcons.Table(kind).Select(i => i.Id).ToHashSet();
            var sampled = Samples.Where(s => s.Kind == kind).Select(s => s.Id).ToHashSet();
            Assert.Equal(ids, sampled);   // 加了規則沒補樣本、或樣本指向已刪的規則，都在此攔下
        }
    }

    [Fact]
    public void 每一列的代表字串都必須命中自己_而非前面的通則()
    {
        foreach (var (kind, id, sample) in Samples)
            Assert.Equal(id, DeviceIcons.Resolve(kind, sample)?.Id);
    }

    [Fact]
    public void 官方logo資料夾都要被csproj的Resource收進去()
    {
        // TryLoadLogo 以 pack URI 讀 Assets\{folder}\{id}.png；沒被 Resource 收進組件的話，
        // 圖檔放了也讀不到，而且不會有任何錯誤訊息——只是徽章永遠停在向量圖。
        var csproj = ReadRepoFile("XinSpect.csproj");
        foreach (var kind in new[] { BadgeKind.Cpu, BadgeKind.Gpu, BadgeKind.Disk, BadgeKind.Board })
        {
            var folder = DeviceIcons.AssetFolder(kind);
            Assert.NotNull(folder);
            Assert.Contains($@"Assets\{folder}\*.png", csproj);
        }
    }

    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
                return File.ReadAllText(Path.Combine(dir.FullName, relativePath));
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到原始碼樹（往上找不到 XinSpect.csproj）");
    }
}
