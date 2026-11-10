using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

public partial class DeepBenchView : UserControl
{
    private CancellationTokenSource? _cancellationToken;
    private bool _targetSuggestionLoaded;

    public DeepBenchView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_targetSuggestionLoaded) return;
            MainViewModel? main = Shell.Vm;
            DeepBenchViewModel? viewModel = ViewModel;
            if (main is null || viewModel is null || main.Cpu.Name is "" or "—") return;
            viewModel.SuggestTarget(main.Cpu.Name, main.Cpu.Socket, main.System.BoardModel);
            _targetSuggestionLoaded = true;
        };
    }

    private DeepBenchViewModel? ViewModel => DataContext as DeepBenchViewModel ?? (DataContext as MainViewModel)?.DeepBench;

    private void Quick_Click(object sender, RoutedEventArgs e) => ViewModel?.SetProfileQuick();

    private void Full_Click(object sender, RoutedEventArgs e) => ViewModel?.SetProfileFull();

    // 儲存根瀏覽：選資料夾後寫回 StorageRoot；不強制存在——執行時 VM 會自動建立。
    private void BrowseStorageRoot_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "選擇深測暫存根（可寫的資料夾）",
            InitialDirectory = System.IO.Path.Exists(ViewModel.StorageRoot)
                ? ViewModel.StorageRoot
                : System.IO.Path.GetTempPath(),
        };
        if (dialog.ShowDialog() == true)
            ViewModel.StorageRoot = dialog.FolderName;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        DeepBenchViewModel? vm = ViewModel;
        if (vm is null) return;

        if (Shell.Vm is { } main)
            vm.SuggestTarget(main.Cpu.Name, main.Cpu.Socket, main.System.BoardModel);

        using var cancellation = new CancellationTokenSource();
        _cancellationToken = cancellation;
        try
        {
            await vm.StartAsync(cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_cancellationToken, cancellation)) _cancellationToken = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel?.Cancel();
}
