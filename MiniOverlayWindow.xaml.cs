using System.Windows;
using System.Windows.Input;

namespace XinSpect;

/// <summary>迷你浮動監視器：無邊框、置頂、半透明、可拖曳的即時精簡面板。可由主視窗「迷你」鈕或系統匣切換顯示 / 隱藏。</summary>
public partial class MiniOverlayWindow : Window
{
    public MiniOverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PlaceTopRight();
    }

    private void PlaceTopRight()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 24;
        Top = wa.Top + 24;
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { /* 非拖曳狀態呼叫時忽略 */ }
    }

    /// <summary>切換顯示 / 隱藏（首次顯示時重新貼齊右上角）。</summary>
    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        Show();
        PlaceTopRight();
        Activate();
    }
}
