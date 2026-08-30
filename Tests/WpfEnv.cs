using System.Windows;

namespace XinSpect.Tests;

/// <summary>
/// WPF 測試環境的共用入口。<see cref="Application"/> 一個 AppDomain 只能有一個執行個體，
/// 而 xunit 會平行跑不同測試類別——兩個類別同時建立就會拋
/// 「無法在 AppDomain 中建立多個 System.Windows.Application 執行個體」。
/// 因此所有需要 WPF 環境的測試都必須經過這裡，由同一把鎖序列化建立動作。
/// </summary>
internal static class WpfEnv
{
    private static readonly object Gate = new();

    /// <summary>取得（必要時建立）帶有佈景資源字典的 Application；資源內容與 App.xaml 相同。</summary>
    public static Application Ensure()
    {
        lock (Gate)
        {
            if (Application.Current is { } existing) return existing;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/XinSpect;component/Themes/Theme.xaml"),
            });
            return app;
        }
    }
}
