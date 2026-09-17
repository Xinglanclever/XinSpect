using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

public partial class GpuRenderTestView : UserControl
{
    readonly GpuRenderTestService _svc = new();

    public GpuRenderTestView()
    {
        InitializeComponent();
        DataContext = _svc;
        _svc.PropertyChanged += OnServicePropertyChanged;
    }

    void OnStart(object sender, RoutedEventArgs e)  => _svc.Start();
    void OnCancel(object sender, RoutedEventArgs e) => _svc.Cancel();

    void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GpuRenderTestService.CompositeScore))
        {
            bool hasScore = _svc.CompositeScore.HasValue;
            CompositeCard.Visibility = hasScore ? Visibility.Visible : Visibility.Collapsed;
            CompositeText.Text = _svc.CompositeText;
        }
    }
}
