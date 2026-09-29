using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Aksara.Desktop.Controls;

public partial class MdiChild : UserControl
{
    private const double TitleHeight = 32;
    private bool _isMaximized;
    private bool _isMinimized;
    private Rect _restoreBounds;

    public MdiChild(string windowKey, string title, FrameworkElement body)
    {
        InitializeComponent();
        WindowKey = windowKey;
        Title = title;
        BodyPresenter.Content = body;
    }

    public string WindowKey { get; }
    public object? Body => BodyPresenter.Content;

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public bool IsMaximized => _isMaximized;
    public bool IsMinimized => _isMinimized;
    public event EventHandler? Activated;
    public event EventHandler? CloseRequested;

    public void SetActive(bool active)
    {
        TitleBorder.Background = active
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(124, 22, 34))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(163, 91, 101));
        TitleText.Foreground = System.Windows.Media.Brushes.White;
        TitleText.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    public void RestoreIfMinimized()
    {
        if (!_isMinimized) return;
        RestoreFromMinimized();
    }

    public void FitToWorkspace(Size size)
    {
        if (!_isMaximized) return;
        Canvas.SetLeft(this, 0);
        Canvas.SetTop(this, 0);
        Width = Math.Max(MinWidth, size.Width);
        Height = Math.Max(TitleHeight, size.Height);
    }

    public void ToggleMaximize()
    {
        if (_isMinimized) RestoreFromMinimized();
        if (_isMaximized) RestoreFromMaximized();
        else Maximize();
    }

    public void Minimize()
    {
        if (_isMinimized) return;
        if (!_isMaximized)
            _restoreBounds = CurrentBounds();

        _isMinimized = true;
        _isMaximized = false;
        ContentBorder.Visibility = Visibility.Collapsed;
        SetResizeHandles(false);
        Height = TitleHeight;
        Width = Math.Max(260, Math.Min(360, _restoreBounds.Width > 0 ? _restoreBounds.Width : Width));
        MaximizeButton.Content = "□";
    }

    private void RestoreFromMinimized()
    {
        _isMinimized = false;
        ContentBorder.Visibility = Visibility.Visible;
        SetResizeHandles(true);
        if (_restoreBounds.Width > 0)
        {
            Canvas.SetLeft(this, _restoreBounds.Left);
            Canvas.SetTop(this, _restoreBounds.Top);
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
        }
    }

    private void Maximize()
    {
        if (Parent is not Canvas workspace) return;
        _restoreBounds = CurrentBounds();
        _isMaximized = true;
        Canvas.SetLeft(this, 0);
        Canvas.SetTop(this, 0);
        Width = Math.Max(MinWidth, workspace.ActualWidth);
        Height = Math.Max(TitleHeight, workspace.ActualHeight);
        SetResizeHandles(false);
        MaximizeButton.Content = "❐";
    }

    private void RestoreFromMaximized()
    {
        _isMaximized = false;
        Canvas.SetLeft(this, _restoreBounds.Left);
        Canvas.SetTop(this, _restoreBounds.Top);
        Width = _restoreBounds.Width;
        Height = _restoreBounds.Height;
        SetResizeHandles(true);
        MaximizeButton.Content = "□";
    }

    private Rect CurrentBounds()
    {
        var left = Canvas.GetLeft(this);
        var top = Canvas.GetTop(this);
        return new Rect(double.IsNaN(left) ? 0 : left, double.IsNaN(top) ? 0 : top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
    }

    private void SetResizeHandles(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RightResizeThumb.Visibility = visibility;
        BottomResizeThumb.Visibility = visibility;
        CornerResizeThumb.Visibility = visibility;
    }

    private void Root_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Activated?.Invoke(this, EventArgs.Empty);

    private void TitleDragThumb_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
    }

    private void TitleDragThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_isMaximized || _isMinimized || Parent is not Canvas workspace) return;
        var currentLeft = double.IsNaN(Canvas.GetLeft(this)) ? 0 : Canvas.GetLeft(this);
        var currentTop = double.IsNaN(Canvas.GetTop(this)) ? 0 : Canvas.GetTop(this);
        var left = currentLeft + e.HorizontalChange;
        var top = currentTop + e.VerticalChange;
        Canvas.SetLeft(this, Math.Clamp(left, -Width + 120, Math.Max(0, workspace.ActualWidth - 120)));
        Canvas.SetTop(this, Math.Clamp(top, 0, Math.Max(0, workspace.ActualHeight - TitleHeight)));
    }

    private void TitleDragThumb_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ToggleMaximize();
        e.Handled = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => Minimize();
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void RightResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) => ResizeBy(e.HorizontalChange, 0);
    private void BottomResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) => ResizeBy(0, e.VerticalChange);
    private void CornerResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) => ResizeBy(e.HorizontalChange, e.VerticalChange);

    private void ResizeBy(double horizontal, double vertical)
    {
        if (_isMaximized || _isMinimized || Parent is not Canvas workspace) return;
        var left = double.IsNaN(Canvas.GetLeft(this)) ? 0 : Canvas.GetLeft(this);
        var top = double.IsNaN(Canvas.GetTop(this)) ? 0 : Canvas.GetTop(this);
        var maxWidth = Math.Max(MinWidth, workspace.ActualWidth - Math.Max(0, left));
        var maxHeight = Math.Max(260, workspace.ActualHeight - Math.Max(0, top));
        Width = Math.Clamp(Width + horizontal, MinWidth, maxWidth);
        Height = Math.Clamp(Height + vertical, 260, maxHeight);
    }

}
