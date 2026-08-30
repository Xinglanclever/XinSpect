using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 共用 <see cref="CrashLog.Folder"/> 這個靜態狀態的測試類別集合。
/// </summary>
/// <remarks>
/// xunit 預設讓不同測試類別平行跑，而 <see cref="CrashLog"/> 與 <see cref="Diag"/> 的
/// 落地資料夾是<b>行程層級的靜態屬性</b>：兩個類別同時把它指向自己的暫存目錄，就會互相把
/// 路徑抽走，變成偶發失敗。歸進同一個集合即可序列化執行——這是靜態狀態的代價，
/// 不是測試寫壞了。
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CrashFolderCollection
{
    public const string Name = "CrashLog 落地資料夾";
}
