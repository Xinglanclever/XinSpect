namespace XinSpect;

/// <summary>總覽儀表板的一塊磁貼：顯示與否、擺放順序由使用者決定。</summary>
/// <remarks>
/// 磁貼「長什麼樣」仍寫在 <c>OverviewView.xaml</c> 的 <c>DataTemplate</c> 裡（資源鍵為 <c>Tile.{Id}</c>），
/// 這個型別只負責身分、標題與狀態——版面是宣告式的，順序才是資料。
/// <para>
/// <see cref="Vm"/> 是給樣板用的：磁貼放進 <c>ItemsControl</c> 後，各層的 DataContext 會變成磁貼本身，
/// 原本 <c>{Binding Live.CpuLoad}</c> 之類的路徑就接不上了；樣板根元素改綁 <c>DataContext="{Binding Vm}"</c>
/// 便能沿用既有繫結，不必為了搬進磁貼而把每一條路徑改寫成 <c>Vm.Live.CpuLoad</c>。
/// </para>
/// </remarks>
public sealed class DashboardTile : ObservableObject
{
    /// <summary>穩定識別碼，也是樣板資源鍵的後半段。<b>已發佈的識別碼不得更名</b>：它會寫進設定檔。</summary>
    public string Id { get; }

    /// <summary>自訂清單上顯示的名稱。</summary>
    public string Title { get; }

    /// <summary>自訂清單上的一行說明（這塊磁貼會放什麼）。</summary>
    public string Hint { get; }

    /// <summary>樣板內層繫結用的主檢視模型。</summary>
    public MainViewModel Vm { get; }

    internal DashboardTile(string id, string title, string hint, bool visible, MainViewModel vm)
    {
        Id = id; Title = title; Hint = hint; Vm = vm;
        _visible = visible;
    }

    private bool _visible;
    /// <summary>是否顯示在總覽頁。變更會由 <see cref="DashboardLayout"/> 立即存檔。</summary>
    public bool Visible
    {
        get => _visible;
        set { if (SetProperty(ref _visible, value)) VisibleChanged?.Invoke(); }
    }

    /// <summary>供 <see cref="DashboardLayout"/> 掛勾：核取方塊是雙向繫結，改動不會經過任何方法。</summary>
    internal Action? VisibleChanged;

    private double _revealDelay;
    /// <summary>進場動畫的交錯延遲（毫秒）；由所在順位算出，隱藏的磁貼不佔位。</summary>
    public double RevealDelay { get => _revealDelay; internal set => SetProperty(ref _revealDelay, value); }

    private bool _canMoveUp;
    private bool _canMoveDown;
    /// <summary>是否還能往上／往下挪（自訂清單直接綁在 ▲ ▼ 鈕的 IsEnabled 上，端點自然變灰）。</summary>
    public bool CanMoveUp { get => _canMoveUp; internal set => SetProperty(ref _canMoveUp, value); }
    public bool CanMoveDown { get => _canMoveDown; internal set => SetProperty(ref _canMoveDown, value); }
}
