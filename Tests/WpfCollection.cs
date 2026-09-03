using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 會建立 WPF <see cref="System.Windows.Application"/> 或在 STA 執行緒上算繪的測試類別集合。
/// </summary>
/// <remarks>
/// <para>
/// xunit 預設讓不同測試類別平行跑，而 WPF 有兩個行程層級的限制撞在一起：
/// 一個 AppDomain 只能有一個 <c>Application</c> 執行個體（<see cref="WpfEnv"/> 用鎖擋住重複建立），
/// 而它的資源字典與 Dispatcher 又<b>綁在建立它的那條執行緒上</b>。兩個以上的類別各自開一條 STA
/// 執行緒去讀同一份 <c>Application.Current.Resources</c>、或在非擁有者執行緒上呼叫
/// <c>RenderTargetBitmap.Render</c>，就會出現算繪卡住不返回、最後整條測試逾時的狀況。
/// </para>
/// <para>
/// 症狀很好認也很容易誤判：失敗的是「逾時」而不是斷言，而且失敗的組合每次不一樣，
/// 單獨跑每一個類別又都會過。歸進同一個集合序列化執行即可——這是 WPF 全域狀態的代價，
/// 不是測試寫壞了。<see cref="CrashFolderCollection"/> 是同一個道理的另一個例子。
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfCollection
{
    public const string Name = "WPF Application 與算繪";
}
