using System.IO;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// PE 靜態檢視解碼器與驅動檢視服務的契約：PE 逐欄對規格、CTL_CODE 候選的形狀篩選與手算答案、
/// 裝置字串雙編碼（UTF-16LE／ASCII）掃描、解析失敗如實回原因；服務層的模組選取、檔案讀不到三態、
/// BYOVD 雙道比對（SHA-256 主、檔名輔）。全程不載入驅動、不呼叫 IOCTL。
/// </summary>
public class PeInspectTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    // ── 金標 PE 建構器：x64、NATIVE 子系統、.text＋.rdata、一條匯入 ─────────────

    private static byte[] BuildPe(
        ushort machine = 0x8664,
        ushort subsystem = 1,
        uint? ctlCode = 0x00222015,
        string? wideDevice = "\\Device\\FakeDrv",
        string? asciiDevice = "\\DosDevices\\FakeSym")
    {
        const int dosSize = 0x40;
        const int peSize = 24;          // 簽章 + file header
        const int optSize = 240;        // PE32+ 選擇性標頭（0xF0）
        const int numSections = 2;

        // 佈局：dos | pe | opt | sections(2×40) | .text | .rdata（字串 80 + 匯入名 13 + 描述符 20 + 終止項 20）
        int textPayload = 64, stringsLen = 80;
        byte[] importName = Encoding.ASCII.GetBytes("ntoskrnl.exe\0");
        int sectionsOff = dosSize + peSize + optSize;
        int textOff = sectionsOff + numSections * 40;
        int rdataOff = textOff + textPayload;
        int nameOff = rdataOff + stringsLen;
        int descOff = nameOff + importName.Length;
        int rdataPayload = stringsLen + importName.Length + 20 + 20; // 描述符後補全零終止項
        int total = descOff + 20 + 20;

        var pe = new byte[total];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z';
        BitConverter.GetBytes(dosSize).CopyTo(pe, 0x3C);
        pe[dosSize] = (byte)'P'; pe[dosSize + 1] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(pe, dosSize + 4);
        BitConverter.GetBytes((ushort)numSections).CopyTo(pe, dosSize + 6);
        BitConverter.GetBytes(0x68A30000u).CopyTo(pe, dosSize + 8); // TimeDateStamp（示意值）
        BitConverter.GetBytes((ushort)optSize).CopyTo(pe, dosSize + 20);
        int opt = dosSize + peSize;
        BitConverter.GetBytes((ushort)0x20B).CopyTo(pe, opt);       // PE32+ 魔術
        BitConverter.GetBytes((ushort)subsystem).CopyTo(pe, opt + 68);
        // 資料目錄（PE32+ 於 opt+112）：index 1 = 匯入表
        uint importRva = (uint)descOff; // RVA=檔案位移（區段 VirtualAddress＝檔案位移的簡化佈局）
        BitConverter.GetBytes(importRva).CopyTo(pe, opt + 112 + 8);
        BitConverter.GetBytes(40u).CopyTo(pe, opt + 112 + 12);

        void WriteSection(int i, string name, int payload, uint chars)
        {
            int s = sectionsOff + i * 40;
            var n = Encoding.ASCII.GetBytes(name);
            Array.Copy(n, 0, pe, s, n.Length);
            BitConverter.GetBytes((uint)payload).CopyTo(pe, s + 8);   // VirtualSize
            BitConverter.GetBytes(chars).CopyTo(pe, s + 12);          // VirtualAddress
            BitConverter.GetBytes((uint)payload).CopyTo(pe, s + 16);  // SizeOfRawData
            BitConverter.GetBytes(chars).CopyTo(pe, s + 20);          // PointerToRawData
        }
        WriteSection(0, ".text", textPayload, (uint)textOff);
        WriteSection(1, ".rdata", rdataPayload, (uint)rdataOff);

        // .text：放一個 CTL_CODE 形狀的值（其餘補 0）
        if (ctlCode is { } c) BitConverter.GetBytes(c).CopyTo(pe, textOff);
        // .rdata：UTF-16LE 與 ASCII 裝置字串；匯入名在描述符之前、描述符後補全零終止項
        if (wideDevice is { } w) Encoding.Unicode.GetBytes(w + "\0").CopyTo(pe, rdataOff);
        if (asciiDevice is { } a) Encoding.ASCII.GetBytes(a + "\0").CopyTo(pe, rdataOff + 40);
        Array.Copy(importName, 0, pe, nameOff, importName.Length);
        BitConverter.GetBytes(nameOff).CopyTo(pe, descOff + 12); // IMAGE_IMPORT_DESCRIPTOR.Name RVA
        return pe;
    }

    // ── 解碼器契約 ──────────────────────────────────────────────────────────

    [Fact]
    public void 金標PE_逐欄解出_機器子系統匯入()
    {
        var r = PeInspect.Inspect(BuildPe());
        Assert.True(r.Parsed, r.ParseError);
        Assert.Equal("x64", r.MachineText);
        Assert.Equal("NATIVE（核心驅動）", r.SubsystemText);
        Assert.Equal(2, r.Sections.Count);
        Assert.Equal(".text", r.Sections[0].Name);
        Assert.Equal(64u, r.Sections[0].RawSize);
        Assert.Contains("ntoskrnl.exe", r.ImportedDlls);
    }

    [Fact]
    public void CTL候選_手算答案逐欄對得上()
    {
        var c = PeInspect.DecodeCtlCode(0x00222015);
        Assert.NotNull(c);
        Assert.Equal(0x22u, c!.DeviceType);
        Assert.Equal(0, c.Access);
        Assert.Equal(0x805, c.Function);
        Assert.Equal(1, c.Method);
        Assert.Equal("METHOD_IN_DIRECT", c.MethodText);
        Assert.Equal("FILE_ANY_ACCESS", c.AccessText);
    }

    [Fact]
    public void CTL篩選_通用函數範圍與裝置型別守門()
    {
        Assert.Null(PeInspect.DecodeCtlCode(0x00001234)); // Function < 0x800（微軟保留）
        Assert.Null(PeInspect.DecodeCtlCode(0x00000000)); // DeviceType 0
        Assert.Null(PeInspect.DecodeCtlCode(0x0900_8804)); // DeviceType > 0x8FF
        var c = PeInspect.DecodeCtlCode(0x0033_2187);      // 0x33 裝置、Function 0x861、Method 3
        Assert.NotNull(c);
        Assert.Equal(0x33u, c!.DeviceType);
        Assert.Equal(0x861, c.Function);
        Assert.Equal(3, c.Method);
        Assert.Equal("METHOD_NEITHER", c.MethodText);
    }

    [Fact]
    public void 裝置字串_雙編碼都掃得到()
    {
        var r = PeInspect.Inspect(BuildPe());
        Assert.True(r.Parsed);
        Assert.Contains("\\Device\\FakeDrv", r.DeviceStrings);
        Assert.Contains("\\DosDevices\\FakeSym", r.DeviceStrings);
    }

    [Fact]
    public void 解析失敗_如實回原因不猜結構()
    {
        var bad = new byte[] { 0x4D, 0x5A, 0, 0 }; // MZ 但 e_lfanew 越界
        var r = PeInspect.Inspect(bad);
        Assert.False(r.Parsed);
        Assert.NotNull(r.ParseError);

        var notPe = new byte[0x100];
        notPe[0] = (byte)'M'; notPe[1] = (byte)'Z';
        var r2 = PeInspect.Inspect(notPe);
        Assert.False(r2.Parsed);

        Assert.False(PeInspect.Inspect(new byte[16]).Parsed);
    }

    [Fact]
    public void 非NATIVE子系統_如實報出()
    {
        var r = PeInspect.Inspect(BuildPe(subsystem: 3));
        Assert.Equal("Windows CUI", r.SubsystemText);
    }

    // ── 服務層：模組選取、三態、BYOVD 雙道比對 ─────────────────────────────

    private static KernelModuleEntry Win(string path) => new(path, true, "通過");
    private static KernelModuleEntry NonWin(string path) => new(path, null, "無法驗證");

    [Fact]
    public void 非系統目錄驅動_給出總數與逐顆摘要()
    {
        var bytes = BuildPe();
        var facts = DriverInspectionFactsService.Collect(
            At, new[] { Win("C:\\Windows\\System32\\drivers\\ntfs.sys"), NonWin("D:\\Tools\\FakeDrv.sys") },
            _ => bytes);

        var count = facts.Single(x => x.Key == DriverInspectionFactsService.CountKey);
        Assert.Equal(FactAvailability.Present, count.Availability);
        Assert.Equal(1, count.NumericValue);

        var summary = Assert.Single(facts, x => x.Key == "drvinsp.0.summary");
        Assert.Equal(FactAvailability.Present, summary.Availability);
        Assert.Contains("FakeDrv.sys", summary.Name);
        Assert.Contains("NATIVE", summary.Value);
        Assert.Contains("IOCTL 候選", summary.Value);
        Assert.Contains("候選不是確認", summary.Value);
        // Windows 目錄的模組不進檢視
        Assert.DoesNotContain(facts, x => x.Name?.Contains("ntfs.sys") == true);
    }

    [Fact]
    public void 模組清單讀不到_如實標讀取失敗()
    {
        var fact = Assert.Single(DriverInspectionFactsService.Collect(At, null, _ => null));
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("讀不到", fact.UnavailableReason);
    }

    [Fact]
    public void 全在Windows目錄_如實標不適用_不是錯誤()
    {
        var fact = Assert.Single(DriverInspectionFactsService.Collect(
            At, new[] { Win("C:\\Windows\\System32\\drivers\\ntfs.sys") }, _ => null));
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("不是錯誤", fact.UnavailableReason);
    }

    [Fact]
    public void 檔案讀不到_逐顆如實三態()
    {
        var facts = DriverInspectionFactsService.Collect(
            At, new[] { NonWin("D:\\Tools\\Missing.sys") }, _ => null);
        var summary = Assert.Single(facts, x => x.Key == "drvinsp.0.summary");
        Assert.Equal(FactAvailability.ReadError, summary.Availability);
        Assert.Contains("讀不到", summary.Value);
    }

    [Fact]
    public void BYOVD雙道比對_SHA256與檔名()
    {
        var bytes = BuildPe();
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var rules = new[]
        {
            new ByovdBlockRule(null, sha.ToLowerInvariant(), null, "KnownBad"),
            new ByovdBlockRule("byname.sys", "0".PadRight(64, '0'), null, "ByName"),
        };

        // SHA-256 命中
        var hit = DriverInspectionFactsService.MatchBlocklist(sha, "whatever.sys", rules);
        Assert.Equal("KnownBad", hit?.FriendlyName);
        // 檔名命中
        var hit2 = DriverInspectionFactsService.MatchBlocklist("AB".PadRight(64, '0'), "ByName.SYS", rules);
        Assert.Equal("ByName", hit2?.FriendlyName);
        // 都不命中
        Assert.Null(DriverInspectionFactsService.MatchBlocklist("F".PadRight(64, 'f'), "none.sys", rules));

        // 服務層：命中文字如實帶「攻擊面事實，不是中毒判決」
        var facts = DriverInspectionFactsService.Collect(
            At, new[] { NonWin("D:\\Tools\\FakeDrv.sys") }, _ => bytes,
            blocklistPath: WriteTempBlocklist(sha));
        var summary = Assert.Single(facts, x => x.Key == "drvinsp.0.summary");
        Assert.Contains("命中封鎖清單", summary.Value);
        Assert.Contains("不是中毒判決", summary.Value);
    }

    [Fact]
    public void 封鎖清單不存在_如實標注_不假裝比對過()
    {
        var bytes = BuildPe();
        var facts = DriverInspectionFactsService.Collect(
            At, new[] { NonWin("D:\\Tools\\FakeDrv.sys") }, _ => bytes,
            blocklistPath: Path.Combine(Path.GetTempPath(), "不存在-" + Guid.NewGuid() + ".xml"));
        var summary = Assert.Single(facts, x => x.Key == "drvinsp.0.summary");
        Assert.Contains("封鎖清單不存在", summary.Value);
    }

    [Fact]
    public void 裝置空間路徑正規化_四種形狀()
    {
        // \SystemRoot\ → %SystemRoot%
        string root = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        Assert.Equal(root + @"\System32\drivers\x.sys",
            DriverInspectionFactsService.NormalizeDriverPath(@"\SystemRoot\System32\drivers\x.sys"));
        // \??\ → 剝前綴
        Assert.Equal(@"C:\Tools\y.sys", DriverInspectionFactsService.NormalizeDriverPath(@"\??\C:\Tools\y.sys"));
        // \Device\HarddiskVolumeN\ → 磁碟代號（本機 HarddiskVolume1 可能對映到任一顆固定磁碟；
        // 對映不到時原樣回傳）
        string normalized = DriverInspectionFactsService.NormalizeDriverPath(
            @"\Device\HarddiskVolume1\Windows\z.sys");
        Assert.Matches(@"^[A-Z]:\\Windows\\z\.sys$|^\\Device\\", normalized);
        // 一般 Win32 路徑原樣
        Assert.Equal(@"D:\a.sys", DriverInspectionFactsService.NormalizeDriverPath(@"D:\a.sys"));
    }

    private static string WriteTempBlocklist(string sha)
    {
        string p = Path.Combine(Path.GetTempPath(), "peinsp-blocklist-" + Guid.NewGuid() + ".xml");
        File.WriteAllText(p,
            $"""<SiPolicy><FileRule Name="r1" FileName="x.sys" MinimumSHA256Hash="{sha}" FriendlyName="KnownBad" /></SiPolicy>""");
        return p;
    }
}
