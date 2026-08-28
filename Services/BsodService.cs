using System.Collections.ObjectModel;
using System.IO;

namespace XinSpect;

/// <summary>
/// 藍屏（BSOD）分析：掃描 %SystemRoot%\Minidump 內的核心小型傾印檔，
/// 直接解析傾印標頭（DUMP_HEADER64）取出停止代碼（BugCheckCode）與四個參數，
/// 對照常見停止代碼名稱與可能原因。純本機、無第三方相依。
/// 註：定位「肇事驅動程式」需符號解析（WinDbg），非本工具範圍；本工具提供代碼判讀與快速定位。
/// </summary>
public sealed class BsodRow
{
    public string FileName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public DateTime Time { get; init; }
    public long Size { get; init; }
    public uint Code { get; init; }
    public string CodeHex => "0x" + Code.ToString("X8");
    public string Name { get; init; } = "";
    public string Params { get; init; } = "";
    public string Hint { get; init; } = "";

    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeText => Size >= 1 << 20 ? $"{Size / 1024.0 / 1024.0:0.0} MB" : $"{Size / 1024.0:0} KB";
}

public sealed class BsodService
{
    public ObservableCollection<BsodRow> Rows { get; } = new();
    public string Status { get; private set; } = "";
    public bool HasDumps => Rows.Count > 0;

    private static string DumpDir =>
        Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "Minidump");

    public void Scan()
    {
        Rows.Clear();
        try
        {
            var dir = new DirectoryInfo(DumpDir);
            if (!dir.Exists)
            {
                Status = $"未找到傾印資料夾（{DumpDir}）。若系統從未發生藍屏，或未啟用小型記憶體傾印，此處會是空的。";
                return;
            }
            var files = dir.GetFiles("*.dmp").OrderByDescending(f => f.LastWriteTime).ToArray();
            if (files.Length == 0)
            {
                Status = $"傾印資料夾存在但沒有 .dmp 檔（{DumpDir}）。表示近期沒有記錄到藍屏。";
                return;
            }
            foreach (var f in files)
            {
                var (code, p1, p2, p3, p4, ok) = ReadHeader(f.FullName);
                var info = ok ? BugCheck.Lookup(code) : ("無法解析", "此檔可能非標準核心傾印，或已毀損。");
                Rows.Add(new BsodRow
                {
                    FileName = f.Name,
                    FullPath = f.FullName,
                    Time = f.LastWriteTime,
                    Size = f.Length,
                    Code = ok ? code : 0,
                    Name = info.Item1,
                    Params = ok ? $"1={p1:X} 2={p2:X} 3={p3:X} 4={p4:X}" : "—",
                    Hint = info.Item2,
                });
            }
            Status = $"共 {Rows.Count} 筆傾印 ・ 最近一次：{files[0].LastWriteTime:yyyy-MM-dd HH:mm}";
        }
        catch (Exception ex)
        {
            Status = "掃描失敗：" + ex.Message;
        }
    }

    // DUMP_HEADER64：Signature "PAGE"(0)、ValidDump "DU64"(4)、
    // BugCheckCode 於位移 0x38、四個 ULONG64 參數於 0x40/0x48/0x50/0x58。
    private static (uint code, ulong p1, ulong p2, ulong p3, ulong p4, bool ok) ReadHeader(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[0x60];
            if (fs.Read(buf, 0, buf.Length) < buf.Length) return (0, 0, 0, 0, 0, false);
            // "PAGE" = 0x50 0x41 0x47 0x45
            if (buf[0] != 0x50 || buf[1] != 0x41 || buf[2] != 0x47 || buf[3] != 0x45)
                return (0, 0, 0, 0, 0, false);
            uint code = BitConverter.ToUInt32(buf, 0x38);
            ulong p1 = BitConverter.ToUInt64(buf, 0x40);
            ulong p2 = BitConverter.ToUInt64(buf, 0x48);
            ulong p3 = BitConverter.ToUInt64(buf, 0x50);
            ulong p4 = BitConverter.ToUInt64(buf, 0x58);
            return (code, p1, p2, p3, p4, true);
        }
        catch { return (0, 0, 0, 0, 0, false); }
    }
}
