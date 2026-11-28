using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Injected observations only: creates no SensorService/MainViewModel and never accesses hardware.</summary>
[Collection(WpfCollection.Name)]
public sealed class SensorPanelUiTests
{
    [Fact]
    public void InjectedPanelSupportsDeviceSelectionAllTileKindsDragResizeButtonsScaleAndSave()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "XinSpect-PanelUI-" + Guid.NewGuid().ToString("N"));
            SensorPanelWindow? window = null;
            var priorLanguage = (LanguageService.IsSimplified, LanguageService.IsEnglish, LanguageService.IsCatLanguage);
            try
            {
                WpfEnv.Ensure();
                LanguageService.Initialize(false, false, false);
                var now = DateTimeOffset.UtcNow;
                var frame = new SensorFrame(1, Stopwatch.GetTimestamp(), now,
                [
                    new("/gpu/0/temp/0", "/gpu/0", "GPU same name", "Core", "Temperature", "°C", 41,
                        FactAvailability.Present, FactTrustLevel.Reported, "injected", now, null, ""),
                    new("/gpu/1/temp/0", "/gpu/1", "GPU 原始記憶體", "核心自訂", "Temperature", "°C", 73,
                        FactAvailability.Present, FactTrustLevel.Reported, "injected", now, null, ""),
                    new("/disk/0/temp/0", "/disk/0", "Disk", "Composite", "Temperature", "°C", null,
                        FactAvailability.ReadError, FactTrustLevel.Reported, "injected", now, null, "offline 記憶體 raw-id"),
                ]);
                var store = new SensorPanelLayoutStore(Path.Combine(directory, "layout.json"));
                window = new SensorPanelWindow(() => frame, store);
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                window.Measure(new Size(1180, 780));
                window.Arrange(new Rect(0, 0, 1180, 780));
                var search = (TextBox)window.FindName("SensorSearch");
                var picker = (ListBox)window.FindName("SensorPicker");
                Assert.Equal(3, picker.Items.Count);
                search.Text = "/gpu/1";
                Assert.Single(picker.Items.Cast<object>());
                var kind = (ComboBox)window.FindName("AddKind");
                var board = (Canvas)window.FindName("Board");
                for (int i = 0; i < 3; i++)
                {
                    kind.SelectedIndex = i; Click(window, "AddTileButton");
                }
                Assert.Equal(3, board.Children.Count);
                Assert.All(board.Children.OfType<Border>(), tile =>
                    Assert.Contains(Descendants<TextBlock>(tile), text => text.Text.Contains("73")));
                Click(window, "AddTileButton"); // same sensor + display kind is rejected
                Assert.Equal(3, board.Children.Count);

                // Exercise the actual routed drag/resize handlers rather than duplicating their formulas.
                var selected = (Border)board.Children[2];
                var thumbs = Descendants<Thumb>(selected).ToArray();
                Assert.Equal(2, thumbs.Length);
                double x = Canvas.GetLeft(selected), y = Canvas.GetTop(selected);
                thumbs[0].RaiseEvent(new DragDeltaEventArgs(21, 17) { RoutedEvent = Thumb.DragDeltaEvent });
                Assert.Equal(x + 21, Canvas.GetLeft(selected));
                Assert.Equal(y + 17, Canvas.GetTop(selected));
                thumbs[1].RaiseEvent(new DragDeltaEventArgs(31, 23) { RoutedEvent = Thumb.DragDeltaEvent });
                Assert.Equal(331, selected.Width); Assert.Equal(263, selected.Height);
                // The sidebar's ScrollViewer need not materialize every offscreen visual in this fixture.
                // Resolve the named XAML button and still exercise its actual routed click handler.
                var right = (Button)window.FindName("MoveRightButton");
                right.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(x + 37, Canvas.GetLeft(selected));

                ((TextBox)window.FindName("ScaleMin")).Text = "20";
                ((TextBox)window.FindName("ScaleMax")).Text = "10";
                Click(window, "ApplyScaleButton"); Click(window, "SaveLayoutButton");
                Assert.Equal(100, store.LoadLocal().Tiles[2].ScaleMaximum);
                ((TextBox)window.FindName("ScaleMin")).Text = "-20";
                ((TextBox)window.FindName("ScaleMax")).Text = "120";
                Click(window, "ApplyScaleButton"); Click(window, "SaveLayoutButton");
                var saved = store.LoadLocal();
                Assert.Equal(3, saved.Tiles.Count);
                Assert.All(saved.Tiles, t => Assert.Equal("/gpu/1", t.DeviceId));
                Assert.Equal(120, saved.Tiles[2].ScaleMaximum);
                Assert.Equal(331, saved.Tiles[2].Width);

                // Offline source can be selected, but its numeric tile displays a dash and ReadError.
                search.Text = "/disk/0"; kind.SelectedIndex = 0; Click(window, "AddTileButton");
                var offlineTexts = Descendants<TextBlock>((Border)board.Children[3]).Select(t => t.Text).ToArray();
                Assert.Contains("—", offlineTexts);
                Assert.Contains(offlineTexts, t => t.Contains("ReadError"));
                var settings = new SettingsService(loadFromDisk: false);
                foreach (var language in new[] { AppLanguage.English, AppLanguage.Simplified, AppLanguage.Traditional })
                {
                    LanguageService.SetLanguage(language, settings);
                    Assert.Contains(Descendants<TextBlock>((Border)board.Children[0]),
                        text => text.Text == "GPU 原始記憶體 / 核心自訂");
                    Assert.Contains(Descendants<TextBlock>((Border)board.Children[3]),
                        text => text.Text.Contains("ReadError") && text.Text.Contains("offline 記憶體 raw-id"));
                    Assert.All(board.Children.OfType<Border>().Take(3), tile =>
                        Assert.Contains(Descendants<TextBlock>(tile), text => text.Text.Contains("73")));
                }
                Click(window, "RemoveTileButton");
                Assert.Equal(3, board.Children.Count);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { window?.Close(); } catch (Exception ex) { failure ??= ex; }
                LanguageService.Initialize(priorLanguage.IsSimplified, priorLanguage.IsEnglish, priorLanguage.IsCatLanguage);
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
                catch (Exception ex) { failure ??= ex; }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Injected sensor-panel UI test exceeded 30 seconds.");
        Assert.Null(failure);
    }

    private static void Click(SensorPanelWindow window, string name)
        => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
