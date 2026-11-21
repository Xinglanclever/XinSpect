using System.Windows;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>Confirms entry into high-risk diagnostic and tuning features.</summary>
public static class CatFeatureGate
{
    /// <summary>Shows the cat confirmation and its language-specific response. Returns false on decline.</summary>
    public static bool Confirm(Window? owner = null)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher ?? owner?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return false;
            try { return dispatcher.Invoke(() => ConfirmOnUiThread(owner), DispatcherPriority.Send); }
            catch (TaskCanceledException) { return false; }
            catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) { return false; }
        }

        return ConfirmOnUiThread(owner);
    }

    private static bool ConfirmOnUiThread(Window? owner)
    {
        Window? dialogOwner = owner ?? Shell.Main;
        var dialog = new CatFeatureGateWindow();
        if (dialogOwner is not null) dialog.Owner = dialogOwner;
        dialog.ShowDialog();
        if (!dialog.Proceed) return false;

        string reaction = ReactionText(LanguageService.Language);
        if (dialogOwner is null)
            MessageBox.Show(reaction, "喵！", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(dialogOwner, reaction, "喵！", MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

    internal static string ReactionText(AppLanguage language) => language switch
    {
        AppLanguage.Simplified => "哈——！",
        AppLanguage.English => "Hiss—!",
        _ => "嘶——！",
    };
}
