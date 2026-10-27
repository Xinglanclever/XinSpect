using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 例外訊息給人看之前要先整理：拿掉 .NET 接上的參數名尾巴，必要時補一句「接下來怎麼辦」。
/// </summary>
/// <remarks>
/// 2026-10-26 使用者回報的畫面是「儲存失敗／<c>UnavailableReason 超過 512 字元。(Parameter
/// 'UnavailableReason')</c>」。前半已經改成中文字段名；後半是這一支負責的——
/// 那串字對使用者沒有意義，而且會被原樣貼進回報單。
/// </remarks>
public class UserFacingErrorTests
{
    [Fact]
    public void 參數名尾巴要不見()
    {
        // 這是真實形狀：.NET 會自己接上 " (Parameter 'x')"。
        var ex = new ArgumentException("存檔路徑不可空白。", "path");
        Assert.Contains("Parameter", ex.Message);                 // 前提：原始訊息真的帶尾巴
        Assert.Equal("存檔路徑不可空白。", UserFacingError.Clean(ex.Message));
    }

    [Fact]
    public void 雙引號形式的參數名也要拿掉()
    {
        Assert.Equal("格式不對。", UserFacingError.Clean("格式不對。 (Parameter \"value\")"));
    }

    [Fact]
    public void 一般括號的內容不可以被吃掉()
    {
        const string text = "讀不到（需要系統管理員權限）。";
        Assert.Equal(text, UserFacingError.Clean(text));
    }

    [Fact]
    public void 訊息為空時要說出沒有原因而不是空白()
    {
        Assert.Equal("沒有提供原因。", UserFacingError.Clean(null));
        Assert.Equal("沒有提供原因。", UserFacingError.Clean("   "));
    }

    [Fact]
    public void 沒有權限時要補一句下一步()
    {
        string text = UserFacingError.Describe(new UnauthorizedAccessException("存取被拒。"));
        Assert.Contains("存取被拒。", text);
        Assert.Contains("寫入權限", text);
    }

    [Fact]
    public void 寫檔失敗要說明是寫不進去()
    {
        string text = UserFacingError.Describe(new IOException("磁碟已滿。"));
        Assert.Contains("寫不進去", text);
        Assert.Contains("磁碟已滿。", text);
    }

    [Fact]
    public void 其他例外原樣轉述不加料()
    {
        Assert.Equal("時間膠囊已損毀。",
            UserFacingError.Describe(new InvalidDataException("時間膠囊已損毀。")));
    }
}
