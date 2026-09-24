using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamingCenter.App.Converters;

namespace GamingCenter.App.Views.Parts;

/// <summary>The center's logo: a custom image from Settings, or the bundled mi2o logo.</summary>
public sealed class Logo : Image
{
    private static readonly ImageSource Default = LoadDefault();
    private static readonly ImagePathConverter Loader = new() { DecodeWidth = 600 };

    public static readonly DependencyProperty LogoPathProperty = DependencyProperty.Register(
        nameof(LogoPath), typeof(string), typeof(Logo), new PropertyMetadata(null, (d, _) => ((Logo)d).Update()));

    public string? LogoPath { get => (string?)GetValue(LogoPathProperty); set => SetValue(LogoPathProperty, value); }

    public Logo()
    {
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Update();
    }

    private void Update() =>
        Source = Loader.Convert(LogoPath, typeof(ImageSource), null, System.Globalization.CultureInfo.InvariantCulture) as ImageSource ?? Default;

    private static ImageSource LoadDefault()
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri("pack://application:,,,/Resources/Images/mi2o-logo.png");
        bmp.DecodePixelWidth = 600;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>Station/product picture, or a gradient placeholder with a short tag ("PS5") when no image is set.</summary>
public sealed class Thumb : Border
{
    private static readonly ImagePathConverter Loader = new() { DecodeWidth = 720 };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, Margin = new Thickness(6) };
    // Blurred copy that fills the empty sides, so the whole picture stays visible and sharp in the middle.
    private readonly Image _backdrop = new()
    {
        Stretch = Stretch.UniformToFill,
        Opacity = 0.45,
        Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 24, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance },
    };
    private readonly TextBlock _tag = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold };

    public static readonly DependencyProperty ImagePathProperty = DependencyProperty.Register(
        nameof(ImagePath), typeof(string), typeof(Thumb), new PropertyMetadata(null, (d, _) => ((Thumb)d).Update()));
    public static readonly DependencyProperty TagTextProperty = DependencyProperty.Register(
        nameof(TagText), typeof(string), typeof(Thumb), new PropertyMetadata("", (d, _) => ((Thumb)d).Update()));
    public static readonly DependencyProperty TagSizeProperty = DependencyProperty.Register(
        nameof(TagSize), typeof(double), typeof(Thumb), new PropertyMetadata(26.0, (d, _) => ((Thumb)d).Update()));

    public string? ImagePath { get => (string?)GetValue(ImagePathProperty); set => SetValue(ImagePathProperty, value); }
    public string TagText { get => (string)GetValue(TagTextProperty); set => SetValue(TagTextProperty, value); }
    public double TagSize { get => (double)GetValue(TagSizeProperty); set => SetValue(TagSizeProperty, value); }

    public Thumb()
    {
        ClipToBounds = true;
        var grid = new Grid();
        grid.Children.Add(_tag);
        grid.Children.Add(_backdrop);
        grid.Children.Add(_image);
        Child = grid;
        SetResourceReference(BackgroundProperty, "Bg.ImageArea");
        _tag.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tag");
        _tag.SetResourceReference(TextBlock.FontFamilyProperty, "Font.UI");
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(_backdrop, BitmapScalingMode.LowQuality);
        SizeChanged += (_, e) =>
        {
            Clip = RoundedRect(new Rect(RenderSize), CornerRadius);
            UpdateStretch(e.NewSize.Height);
        };
        Update();
    }

    /// <summary>Tiny thumbnails (list rows) are filled edge to edge; bigger ones show the whole picture.</summary>
    private void UpdateStretch(double height)
    {
        bool small = height is > 0 and < 50;
        _image.Stretch = small ? Stretch.UniformToFill : Stretch.Uniform;
        _image.Margin = small ? new Thickness(0) : new Thickness(6);
        _backdrop.Visibility = small || _image.Source is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Clip to the rounded corners so pictures don't poke out of rounded cards.</summary>
    private static Geometry RoundedRect(Rect r, CornerRadius c)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            double tl = Math.Min(c.TopLeft, r.Height / 2), tr = Math.Min(c.TopRight, r.Height / 2);
            double br = Math.Min(c.BottomRight, r.Height / 2), bl = Math.Min(c.BottomLeft, r.Height / 2);
            ctx.BeginFigure(new Point(r.Left + tl, r.Top), true, true);
            ctx.LineTo(new Point(r.Right - tr, r.Top), false, false);
            if (tr > 0) ctx.ArcTo(new Point(r.Right, r.Top + tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(r.Right, r.Bottom - br), false, false);
            if (br > 0) ctx.ArcTo(new Point(r.Right - br, r.Bottom), new Size(br, br), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(r.Left + bl, r.Bottom), false, false);
            if (bl > 0) ctx.ArcTo(new Point(r.Left, r.Bottom - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(r.Left, r.Top + tl), false, false);
            if (tl > 0) ctx.ArcTo(new Point(r.Left + tl, r.Top), new Size(tl, tl), 0, false, SweepDirection.Clockwise, false, false);
        }
        g.Freeze();
        return g;
    }

    private void Update()
    {
        var src = Loader.Convert(ImagePath, typeof(ImageSource), null, System.Globalization.CultureInfo.InvariantCulture) as ImageSource;
        _image.Source = src;
        _backdrop.Source = src;
        _image.Visibility = src is null ? Visibility.Collapsed : Visibility.Visible;
        _backdrop.Visibility = _image.Visibility;
        UpdateStretch(ActualHeight);
        _tag.Visibility = src is null ? Visibility.Visible : Visibility.Collapsed;
        _tag.Text = TagText;
        _tag.FontSize = TagSize;
    }
}
