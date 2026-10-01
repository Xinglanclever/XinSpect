using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

public partial class DeepBenchView : UserControl
{
    private CancellationTokenSource? _cancellationToken;

    public DeepBenchView() => InitializeComponent();

    private DeepBenchViewModel? ViewModel => (DataContext as MainViewModel)?.DeepBench;

    private void Quick_Click(object sender, RoutedEventArgs e) => ViewModel?.SetProfileQuick();

    private void Full_Click(object sender, RoutedEventArgs e) => ViewModel?.SetProfileFull();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        DeepBenchViewModel? vm = ViewModel;
        if (vm is null) return;

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
