using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 讀 UEFI 變數的唯一通路與「為什麼讀不到」的三分（2026-10-18）。
///
/// <para>
/// <b>這一組來自一次實測查證：</b>專案裡原本有<b>四份</b>啟用
/// <c>SeSystemEnvironmentPrivilege</c> 的實作，而其中兩份是壞的——
/// 一份少查 <c>LastError</c>（假成功），一份<b>根本沒啟用</b>。
/// 後者讓 <c>uefi.db</c>／<c>dbx</c>／<c>kek</c>／<c>pk</c>／<c>uefi.boot_entries.count</c>
/// 在真機上全部讀不到，而理由欄寫的是一個沒查證的推測（「可能未進入 Setup Mode」）。
/// </para>
///
/// <para>
/// 本機實測（同一次查證）：<c>GetFirmwareEnvironmentVariableExW("BootOrder")</c>
/// 在未啟用權限時回 0 bytes／<c>1314 ERROR_PRIVILEGE_NOT_HELD</c>；
/// 啟用之後同一個呼叫回 <b>18 bytes</b>、<c>PK</c> 回 <b>886 bytes</b>。
/// </para>
/// </summary>
public class FirmwareVariableAccessTests
{
    private static FirmwareVariableAccess.VariableReadResult Failed(uint error, bool privileged = true)
        => new(null, error, 0, privileged);

    [Fact]
    public void 權限不足的時候要說是權限而不是變數不存在()
    {
        string reason = FirmwareVariableAccess.DescribeFailure("PK", Failed(FirmwareVariableAccess.ErrorPrivilegeNotHeld));

        Assert.Contains("行程權杖沒有 SeSystemEnvironmentPrivilege", reason);
        Assert.Contains("1314", reason);
        Assert.Contains("不是「變數不存在」", reason);
        Assert.Contains("不是「這台機器不支援 UEFI」", reason);
    }

    [Fact]
    public void 權限沒啟用時_就算錯誤碼看起來正常也要說權限()
    {
        string reason = FirmwareVariableAccess.DescribeFailure("db", new(null, 0, 0, PrivilegeEnabled: false));

        Assert.Contains("SeSystemEnvironmentPrivilege", reason);
    }

    [Fact]
    public void 變數不存在與讀取失敗是兩句話()
    {
        string missing = FirmwareVariableAccess.DescribeFailure("KEK", Failed(FirmwareVariableAccess.ErrorFileNotFound));

        Assert.Contains("不存在", missing);
        Assert.Contains("這是韌體狀態，不是讀取失敗", missing);
        Assert.DoesNotContain("SeSystemEnvironmentPrivilege", missing);
    }

    [Fact]
    public void 長度零與不存在也是兩句話()
    {
        string empty = FirmwareVariableAccess.DescribeFailure("db", Failed(0));

        Assert.Contains("長度是 0 bytes", empty);
        Assert.Contains("不是「變數不存在」", empty);
    }

    [Fact]
    public void 其他錯誤要帶出錯誤碼與系統訊息_不吞掉()
    {
        string other = FirmwareVariableAccess.DescribeFailure("dbx", Failed(87));

        Assert.Contains("Win32 87", other);
        Assert.DoesNotContain("不存在", other.Replace("不是「變數不存在」", ""));
    }

    [Fact]
    public void 屬性只列原始值且明說位元名未對準出處()
    {
        string text = FirmwareVariableAccess.DescribeAttributes(0x27);

        Assert.Contains("0x00000027", text);
        Assert.Contains("位元名未對準出處前不展開", text);
        Assert.DoesNotContain("NON_VOLATILE", text);   // 沒有出處就不展開位元名
    }

    /// <summary>
    /// <b>簽章資料庫的 GUID 要是實測出來的那一個。</b>
    ///
    /// <para>
    /// 2026-10-18 實測（同一支 API、同一個變數名，只換 GUID）：
    /// <c>{a7719a85-…}</c>（舊值）→ <c>db</c>／<c>dbx</c>／<c>PK</c>／<c>KEK</c> <b>全部</b>
    /// 0 bytes、Win32 203；<c>{d719b2cb-…}</c> → <c>db</c> <b>8188</b> bytes、<c>dbx</c> <b>3724</b>；
    /// <c>{8be4df61-…}</c>（Global）→ <c>PK</c> <b>886</b>、<c>KEK</c> <b>3955</b>。
    /// </para>
    /// <para>
    /// 這條測試把那個結果釘住：<b>改回舊值會讓 db／dbx 再一次永遠讀不到</b>，
    /// 而那不會有任何錯誤訊息——只會讓 Secure Boot 的撤銷清單從畫面上消失。
    /// </para>
    /// </summary>
    [Fact]
    public void 簽章資料庫的GUID是實測出來的那一個()
    {
        Assert.Equal("{d719b2cb-3d3a-4596-a3bc-dad00e67656f}", FirmwareVariableAccess.ImageSecurityGuid);
        Assert.Equal("{8be4df61-93ca-11d2-aa0d-00e098032b8c}", FirmwareVariableAccess.GlobalVariableGuid);
        Assert.NotEqual(FirmwareVariableAccess.WrongImageSecurityGuidUsedUntil20261018,
            FirmwareVariableAccess.ImageSecurityGuid);
    }

    /// <summary>
    /// <b>守門：啟用這個權限的呼叫只能有一處。</b>
    ///
    /// <para>
    /// 同一個判斷寫四份的結果是「兩個地方各自漂移」（一份少查 <c>LastError</c>、一份根本沒寫）。
    /// 這一條掃的是<b>呼叫樣式</b>而不是「這幾個字有沒有出現」——註解裡提到這件事的地方很多，
    /// 而註解不該讓守門變綠（同接線守門踩過的「守門可被文字欺騙」）。
    /// </para>
    /// </summary>
    [Fact]
    public void 啟用SeSystemEnvironmentPrivilege的呼叫只能有一處()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "XinSpect.csproj")))
            root = root.Parent;
        Assert.NotNull(root);

        var call = new Regex(@"LookupPrivilegeValue\w*\(\s*null\s*,\s*""SeSystemEnvironmentPrivilege""", RegexOptions.Compiled);
        var hits = Directory.EnumerateFiles(Path.Combine(root!.FullName, "Services"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (File: Path.GetFileName(f), Match: call.IsMatch(File.ReadAllText(f))))
            .Where(x => x.Match)
            .Select(x => x.File)
            .ToList();

        Assert.Equal(["FirmwareVariableAccess.cs"], hits);
    }
}
