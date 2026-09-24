using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace GamingCenter.App.Controls;

/// <summary>Draws a 24×24 stroke icon geometry in the inherited foreground color.</summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    public Icon()
    {
        Width = 16;
        Height = 16;
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Data is null || ActualWidth <= 0) return;
        double scale = Math.Min(ActualWidth, ActualHeight) / 24.0;
        // Stroke thickness is in 24-unit icon space, like an SVG viewBox.
        var pen = new Pen(Foreground, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, pen, Data);
        dc.Pop();
        dc.Pop();
    }
}

/// <summary>
/// Uniform grid whose column count follows the available width (station cards: 5 columns at 1920, 4 at 1366).
/// </summary>
public sealed class ResponsiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(ResponsiveGrid), new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(ResponsiveGrid), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(ResponsiveGrid), new FrameworkPropertyMetadata(8, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }

    private int Columns(double width)
    {
        if (double.IsInfinity(width) || width <= 0) return 1;
        int cols = (int)Math.Floor((width + Gap) / (MinItemWidth + Gap));
        return Math.Clamp(cols, 1, Math.Max(1, MaxColumns));
    }

    protected override Size MeasureOverride(Size available)
    {
        int cols = Columns(available.Width);
        double width = double.IsInfinity(available.Width) ? MinItemWidth : (available.Width - Gap * (cols - 1)) / cols;
        double total = 0, rowHeight = 0;
        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++i % cols == 0) { total += rowHeight + Gap; rowHeight = 0; }
        }
        if (i % cols != 0) total += rowHeight; else if (i > 0) total -= Gap;
        return new Size(double.IsInfinity(available.Width) ? width * cols : available.Width, Math.Max(0, total));
    }

    protected override Size ArrangeOverride(Size final)
    {
        int cols = Columns(final.Width);
        double width = (final.Width - Gap * (cols - 1)) / cols;
        double y = 0;
        var children = InternalChildren.Cast<UIElement>().ToList();
        for (int row = 0; row * cols < children.Count; row++)
        {
            var items = children.Skip(row * cols).Take(cols).ToList();
            double h = items.Max(c => c.DesiredSize.Height);
            for (int c = 0; c < items.Count; c++)
                items[c].Arrange(new Rect(c * (width + Gap), y, width, h));
            y += h + Gap;
        }
        return final;
    }
}

/// <summary>Attached helpers used from XAML.</summary>
public static class Ui
{
    /// <summary>Executes a command when the element is clicked (for Borders used as cards/rows).</summary>
    public static readonly DependencyProperty ClickCommandProperty = DependencyProperty.RegisterAttached(
        "ClickCommand", typeof(ICommand), typeof(Ui), new PropertyMetadata(null, OnClickCommandChanged));
    public static readonly DependencyProperty ClickParameterProperty = DependencyProperty.RegisterAttached(
        "ClickParameter", typeof(object), typeof(Ui), new PropertyMetadata(null));

    public static ICommand? GetClickCommand(DependencyObject d) => (ICommand?)d.GetValue(ClickCommandProperty);
    public static void SetClickCommand(DependencyObject d, ICommand? value) => d.SetValue(ClickCommandProperty, value);
    public static object? GetClickParameter(DependencyObject d) => d.GetValue(ClickParameterProperty);
    public static void SetClickParameter(DependencyObject d, object? value) => d.SetValue(ClickParameterProperty, value);

    private static void OnClickCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.MouseLeftButtonUp -= OnClick;
        if (e.NewValue is not null)
        {
            el.MouseLeftButtonUp += OnClick;
            if (el is FrameworkElement fe) fe.Cursor = Cursors.Hand;
        }
    }

    private static void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DependencyObject d) return;
        var cmd = GetClickCommand(d);
        var param = GetClickParameter(d) ?? (d as FrameworkElement)?.DataContext;
        if (cmd?.CanExecute(param) == true)
        {
            cmd.Execute(param);
            e.Handled = true;
        }
    }

    /// <summary>Lets the user drop an image file from Explorer; executes the command with the file path.</summary>
    public static readonly DependencyProperty ImageDropCommandProperty = DependencyProperty.RegisterAttached(
        "ImageDropCommand", typeof(ICommand), typeof(Ui), new PropertyMetadata(null, (d, e) =>
        {
            if (d is not UIElement el) return;
            el.AllowDrop = e.NewValue is not null;
            el.DragOver -= OnImageDragOver;
            el.Drop -= OnImageDrop;
            if (e.NewValue is null) return;
            el.DragOver += OnImageDragOver;
            el.Drop += OnImageDrop;
        }));
    public static ICommand? GetImageDropCommand(DependencyObject d) => (ICommand?)d.GetValue(ImageDropCommandProperty);
    public static void SetImageDropCommand(DependencyObject d, ICommand? value) => d.SetValue(ImageDropCommandProperty, value);

    private static void OnImageDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ImageDrop.CanAccept(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static async void OnImageDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not DependencyObject d || GetImageDropCommand(d) is not { } cmd) return;
        var toasts = App.Services?.GetService(typeof(Services.ToastService)) as Services.ToastService;
        try
        {
            var result = ImageDrop.Read(e.Data);
            if (result is null)
            {
                toasts?.Warning("No picture found", "Drag the picture itself (not the page), or save it first and drop the file.");
                return;
            }
            var file = result.File;
            if (file is null && result.Url is { } url)
            {
                toasts?.Info("Downloading picture…");
                file = await ImageDrop.DownloadAsync(url);
            }
            if (file is not null && cmd.CanExecute(file)) cmd.Execute(file);
        }
        catch (Exception ex)
        {
            toasts?.Error("Could not use this picture", ex is HttpRequestException or TaskCanceledException
                ? "No internet or the website did not answer. Save the picture, then drop the file."
                : ex.Message);
        }
    }

    /// <summary>Selects all text when a TextBox gets keyboard focus (fast numeric entry).</summary>
    public static readonly DependencyProperty SelectAllOnFocusProperty = DependencyProperty.RegisterAttached(
        "SelectAllOnFocus", typeof(bool), typeof(Ui), new PropertyMetadata(false, (d, e) =>
        {
            if (d is TextBox tb && (bool)e.NewValue)
            {
                tb.GotKeyboardFocus += (_, _) => tb.SelectAll();
                tb.PreviewMouseLeftButtonDown += (s, args) =>
                {
                    if (!tb.IsKeyboardFocusWithin) { tb.Focus(); args.Handled = true; }
                };
            }
        }));
    public static bool GetSelectAllOnFocus(DependencyObject d) => (bool)d.GetValue(SelectAllOnFocusProperty);
    public static void SetSelectAllOnFocus(DependencyObject d, bool value) => d.SetValue(SelectAllOnFocusProperty, value);

    /// <summary>Focuses the element when it is loaded (first field of a dialog).</summary>
    public static readonly DependencyProperty FocusOnLoadProperty = DependencyProperty.RegisterAttached(
        "FocusOnLoad", typeof(bool), typeof(Ui), new PropertyMetadata(false, (d, e) =>
        {
            if (d is FrameworkElement fe && (bool)e.NewValue)
                fe.Loaded += (_, _) => fe.Dispatcher.BeginInvoke(() => { fe.Focus(); Keyboard.Focus(fe); }, System.Windows.Threading.DispatcherPriority.Input);
        }));
    public static bool GetFocusOnLoad(DependencyObject d) => (bool)d.GetValue(FocusOnLoadProperty);
    public static void SetFocusOnLoad(DependencyObject d, bool value) => d.SetValue(FocusOnLoadProperty, value);

    /// <summary>Placeholder text for TextBox/ComboBox templates.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));
    public static string GetPlaceholder(DependencyObject d) => (string)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string value) => d.SetValue(PlaceholderProperty, value);

    /// <summary>Unit suffix shown inside inputs ("DA", "min").</summary>
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.RegisterAttached(
        "Suffix", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));
    public static string GetSuffix(DependencyObject d) => (string)d.GetValue(SuffixProperty);
    public static void SetSuffix(DependencyObject d, string value) => d.SetValue(SuffixProperty, value);

    /// <summary>Icon geometry for buttons/nav items.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(Ui), new FrameworkPropertyMetadata(null));
    public static Geometry? GetIcon(DependencyObject d) => (Geometry?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, Geometry? value) => d.SetValue(IconProperty, value);

    /// <summary>Corner radius for templated controls.</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(8)));
    public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject d, CornerRadius value) => d.SetValue(CornerRadiusProperty, value);

    /// <summary>Binds PasswordBox.Password one-way to a view model string (PasswordBox is not bindable).</summary>
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));
    // Default is null (not "") so the first bound value always triggers the callback that hooks PasswordChanged.
    public static string? GetBoundPassword(DependencyObject d) => (string?)d.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject d, string? value) => d.SetValue(BoundPasswordProperty, value);

    private static readonly DependencyProperty IsUpdatingProperty = DependencyProperty.RegisterAttached("IsUpdating", typeof(bool), typeof(Ui));

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        box.PasswordChanged -= OnPasswordChanged;
        if (!(bool)box.GetValue(IsUpdatingProperty) && box.Password != ((string?)e.NewValue ?? "")) box.Password = (string?)e.NewValue ?? "";
        box.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetValue(IsUpdatingProperty, true);
        SetBoundPassword(box, box.Password);
        box.SetValue(IsUpdatingProperty, false);
    }
}
