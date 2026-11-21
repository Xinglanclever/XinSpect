using System.Windows;

namespace XinSpect;

public partial class CatFeatureGateWindow : Window
{
    public bool Proceed { get; private set; }

    public CatFeatureGateWindow()
    {
        InitializeComponent();
        ApplyLanguage();
        LanguageService.Changed += ApplyLanguage;
        Closed += (_, _) => LanguageService.Changed -= ApplyLanguage;
    }

    private void ApplyLanguage()
    {
        Title = LanguageService.T("喵！先確認一下");
        PromptText.Text = LanguageService.T("我呀只是一隻可愛的小貓，你也不能指望貓貓幹這種事情吧！");
        ProceedButton.Content = LanguageService.T("我就是要你幹");
        DeclineButton.Content = LanguageService.T("那算了吧");
    }

    private void Proceed_Click(object sender, RoutedEventArgs e)
    {
        Proceed = true;
        DialogResult = true;
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        Proceed = false;
        DialogResult = false;
    }
}
