using System.Reflection;
using Xunit;

namespace XinSpect.Tests;

public class HistoryPollingTests
{
    [Fact]
    public void 慢查詢進行中自動心跳不移動視窗或排入取代請求()
    {
        // 模擬尚未完成的查詢，避免為重現兩秒計時器而真的查磁碟與事件記錄。
        var vm = new HistoryViewModel(null!, null!, null!);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(HistoryViewModel).GetField("_reloadRunning", flags)!.SetValue(vm, true);
        var from = vm.FromUtc;
        var to = vm.ToUtc;
        for (int i = 0; i < 5; i++) vm.Tick();
        Assert.Equal(from, vm.FromUtc);
        Assert.Equal(to, vm.ToUtc);
        Assert.False((bool)typeof(HistoryViewModel).GetField("_reloadPending", flags)!.GetValue(vm)!);
    }
}
