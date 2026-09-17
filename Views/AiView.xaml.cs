using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>獨立的 AI 助手分頁：以使用者自選的 AI 模型評價本機硬體並進行問答對話。</summary>
public partial class AiView : UserControl
{
    private readonly ObservableCollection<AttachmentViewModel> _attachments = new();

    public AiView()
    {
        InitializeComponent();
        icAttachments.ItemsSource = _attachments;
        // 有新訊息時自動捲到底
        Loaded += (_, _) =>
        {
            if (Vm?.Ai is { } ai)
                ai.Messages.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd());
        };
    }

    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async void Evaluate_Click(object sender, RoutedEventArgs e)
    {
        if (Vm?.Ai is { } ai) await ai.EvaluateAsync();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Vm?.Ai.Clear();
        _attachments.Clear();
    }

    // 停止目前的請求（已串流出的文字會留在畫面上並註明是中途停止）。
    private void Stop_Click(object sender, RoutedEventArgs e) => Vm?.Ai.Cancel();

    // 快問按鈕：Tag 帶的是實際送出的完整問題（按鈕上只顯示短標籤）
    private async void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string prompt } || prompt.Length == 0) return;
        var ai = Vm?.Ai;
        if (ai is null || !ai.CanSend) return;
        await ai.SendAsync(prompt);
    }

    // Enter 送出、Shift+Enter 換行。
    private async void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendAsync();
        }

        // Ctrl+V 貼上剪貼簿圖片
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (Clipboard.ContainsImage())
            {
                var bmpSrc = Clipboard.GetImage();
                if (bmpSrc is not null)
                {
                    var ai = Vm?.Ai;
                    if (ai is null) return;
                    var pngBytes = BitmapSourceToPng(bmpSrc);
                    ai.AttachClipboardImage(pngBytes);
                    _attachments.Add(new AttachmentViewModel
                    {
                        FileName = "clipboard.png",
                        Thumbnail = bmpSrc,
                        Source = ai.PendingAttachments[^1]
                    });
                    e.Handled = true;
                }
            }
        }
    }

    private async Task SendAsync()
    {
        var ai = Vm?.Ai;
        if (ai is null || !ai.CanSend) return;
        var text = Input.Text;
        if (string.IsNullOrWhiteSpace(text) && _attachments.Count == 0) return;
        Input.Clear();
        _attachments.Clear();
        await ai.SendAsync(text);
    }

    // ── 附件相關 ──────────────────────────────────────────

    private static readonly string[] _imageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];
    private static readonly string[] _textExtensions = [".txt", ".log", ".csv", ".json", ".xml", ".yaml", ".yml", ".md", ".ini", ".cfg"];

    private void BtnAttach_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "選擇要附加的圖片或檔案",
            Filter = "圖片檔 (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|" +
                     "文字檔 (*.txt;*.log;*.csv;*.json;*.xml;*.yaml;*.yml;*.md)|*.txt;*.log;*.csv;*.json;*.xml;*.yaml;*.yml;*.md|" +
                     "所有檔案 (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() == true)
            foreach (var path in dlg.FileNames)
                _ = AttachFileByPathAsync(path);
    }

    private void BtnRemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is AttachmentViewModel vm)
        {
            _attachments.Remove(vm);
            if (vm.Source is not null)
                Vm?.Ai.RemoveAttachment(vm.Source);
        }
    }

    // 拖放
    private void SvChat_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void SvChat_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            foreach (var file in files)
                _ = AttachFileByPathAsync(file);
        }
        e.Handled = true;
    }

    private async Task AttachFileByPathAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;
        var ai = Vm?.Ai;
        if (ai is null) return;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (_imageExtensions.Contains(ext))
        {
            await ai.AttachImageAsync(filePath);
            var att = ai.PendingAttachments[^1];
            var thumb = new BitmapImage();
            thumb.BeginInit();
            thumb.CacheOption = BitmapCacheOption.OnLoad;
            thumb.DecodePixelHeight = 56;
            thumb.StreamSource = new MemoryStream(att.ImageBytes!);
            thumb.EndInit();
            thumb.Freeze();
            _attachments.Add(new AttachmentViewModel
            {
                FileName = att.FileName,
                Thumbnail = thumb,
                Source = att
            });
        }
        else if (_textExtensions.Contains(ext))
        {
            await ai.AttachFileAsync(filePath);
            var att = ai.PendingAttachments[^1];
            _attachments.Add(new AttachmentViewModel
            {
                FileName = att.FileName,
                Source = att
            });
        }
    }

    private static byte[] BitmapSourceToPng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}

/// <summary>附件預覽用 ViewModel。</summary>
public sealed class AttachmentViewModel
{
    public required string FileName { get; init; }
    public BitmapSource? Thumbnail { get; init; }
    public PendingAttachment? Source { get; init; }
}
