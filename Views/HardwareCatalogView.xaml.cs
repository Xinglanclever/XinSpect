using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace XinSpect;

public partial class HardwareCatalogView : UserControl
{
    public HardwareCatalogView()
    {
        InitializeComponent();
        Loaded += (_, _) => Search();
    }

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Search();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => Search();

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "XinSpect catalog JSON (*.json)|*.json|JSON files (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false,
            Title = "匯入已確認可使用的 CPU/GPU JSON 索引"
        };
        if (dialog.ShowDialog() != true || Vm is null) return;
        try
        {
            Vm.HardwareCatalog.LoadFile(dialog.FileName);
            Search();
        }
        catch (Exception ex)
        {
            StatusText.Text = "匯入失敗：" + ex.Message;
        }
    }

    private void Search()
    {
        var catalog = Vm?.HardwareCatalog;
        if (catalog is null) return;
        string? kind = (KindBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        Results.ItemsSource = catalog.Search(SearchBox.Text, kind);
        StatusText.Text = catalog.Status + "；目前顯示 " + Results.Items.Count + " 筆。";
        AttributionText.Text = catalog.Attribution;
        LimitationsText.Text = catalog.Limitations;
    }
}
