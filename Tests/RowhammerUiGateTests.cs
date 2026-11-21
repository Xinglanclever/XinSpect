using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Real XAML state with an in-memory SettingsService. No click, worker, VM startup or hardware.</summary>
[Collection(HardwareOperationPolicyCollection.Name)]
[Trait("Category", "InjectedUiState")]
public sealed class RowhammerUiGateTests
{
    [Fact]
    public void ActualButtonsRequireTwoLevelsAndFreshConsent()
    {
        OnSta(() =>
        {
            WpfEnv.Ensure();
            var settings = new SettingsService(loadFromDisk: false);
            var view = new FirmwareSecurityView { DataContext = new { Settings = settings } };
            Refresh(view);
            var consent = (CheckBox)view.FindName("RowhammerConsentCheckBox");
            var single = (Button)view.FindName("RowhammerRunButton");
            var multi = (Button)view.FindName("RowhammerMultiButton");
            Assert.False(consent.IsEnabled);
            Assert.False(single.IsEnabled);
            Assert.False(multi.IsEnabled);
            settings.AdvancedDiagnosticsEnabled = true;
            Refresh(view);
            Assert.False(consent.IsEnabled);
            Assert.False(single.IsEnabled);
            settings.HighRiskHardwareOperationsEnabled = true;
            Refresh(view);
            Assert.True(consent.IsEnabled);
            Assert.False(single.IsEnabled);
            Assert.False(multi.IsEnabled);
            consent.IsChecked = true;
            Refresh(view);
            Assert.True(single.IsEnabled);
            Assert.True(multi.IsEnabled);
            settings.AdvancedDiagnosticsEnabled = false;
            Refresh(view);
            Assert.False(settings.HighRiskHardwareOperationsEnabled);
            Assert.False(consent.IsChecked == true);
            Assert.False(single.IsEnabled);
            Assert.False(multi.IsEnabled);
            settings.AdvancedDiagnosticsEnabled = true;
            settings.HighRiskHardwareOperationsEnabled = true;
            Refresh(view);
            Assert.True(consent.IsEnabled);
            Assert.False(single.IsEnabled);
            Assert.False(multi.IsEnabled);
        });
    }

    [Fact]
    public void BusyEndAndReenabledModeCannotRestoreConsumedConsentOrRestart()
    {
        OnSta(() =>
        {
            WpfEnv.Ensure();
            var settings = new SettingsService(loadFromDisk: false)
            { AdvancedDiagnosticsEnabled = true, HighRiskHardwareOperationsEnabled = true };
            var view = new FirmwareSecurityView { DataContext = new { Settings = settings } };
            Refresh(view);
            var consent = (CheckBox)view.FindName("RowhammerConsentCheckBox");
            var single = (Button)view.FindName("RowhammerRunButton");
            var multi = (Button)view.FindName("RowhammerMultiButton");
            consent.IsChecked = true;
            Refresh(view);
            Assert.True(single.IsEnabled);
            view.SetValue(FirmwareSecurityView.RowhammerBusyProperty, true);
            Refresh(view);
            Assert.False(consent.IsChecked == true);
            Assert.False(consent.IsEnabled);
            Assert.False(single.IsEnabled);
            Assert.False(multi.IsEnabled);
            settings.HighRiskHardwareOperationsEnabled = false;
            Refresh(view);
            view.SetValue(FirmwareSecurityView.RowhammerBusyProperty, false);
            Refresh(view);
            Assert.False(single.IsEnabled);
            Assert.False(multi.IsEnabled);
            settings.HighRiskHardwareOperationsEnabled = true;
            Refresh(view);
            Assert.False(consent.IsChecked == true);
            Assert.False(single.IsEnabled);
            Assert.False(multi.IsEnabled);
            consent.IsChecked = true;
            Refresh(view);
            Assert.True(single.IsEnabled);
            Assert.True(multi.IsEnabled);
        });
    }

    private static void Refresh(FrameworkElement view)
    {
        view.Measure(new Size(1280, 800));
        view.Arrange(new Rect(0, 0, 1280, 800));
        view.UpdateLayout();
        view.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
    }
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Rowhammer XAML state fixture timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
