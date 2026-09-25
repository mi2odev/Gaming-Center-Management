using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using GamingCenter.App.Controls;

namespace GamingCenter.App.Localization;

/// <summary>
/// UI translations. English text is the key; Resources/Lang/{fr,ar}.json map it to the translation.
/// Anything without a translation stays in English, so a missing entry never breaks the screen.
/// </summary>
public static class L
{
    public static readonly (string Code, string Name)[] Languages = [("en", "English"), ("fr", "Français"), ("ar", "العربية")];

    private static Dictionary<string, string> _map = [];

    public static string Language { get; private set; } = "en";

    public static bool IsRtl => Language == "ar";

    public static FlowDirection Direction => IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public static void Load(string? language)
    {
        Language = Languages.Any(l => l.Code == language) ? language! : "en";
        _map = [];
        if (Language != "en")
        {
            try
            {
                var uri = new Uri($"pack://application:,,,/Resources/Lang/{Language}.json");
                var info = System.Windows.Application.GetResourceStream(uri);
                if (info is not null)
                {
                    using var reader = new StreamReader(info.Stream);
                    _map = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd()) ?? [];
                }
            }
            catch (Exception) { /* fall back to English */ }
        }

        // Dates and day names follow the language; money keeps its own fixed format.
        var culture = CultureInfo.GetCultureInfo(Language switch { "fr" => "fr-FR", "ar" => "ar-DZ", _ => "en-GB" });
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>Translate a fixed English text.</summary>
    public static string T(string english) =>
        english.Length > 0 && _map.TryGetValue(english, out var t) && !string.IsNullOrEmpty(t) ? t : english;

    /// <summary>Translate a format string ("Session started on {0}") and fill it in.</summary>
    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

    /// <summary>
    /// Translates fixed texts in XAML as elements load (TextBlock, Run, buttons, check boxes, tooltips, placeholders).
    /// Data-bound and style-driven values are left alone, so only literal texts are swapped.
    /// </summary>
    public static void RegisterAutoTranslation()
    {
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), true);
    }

    private static bool IsLiteral(DependencyObject d, DependencyProperty p)
    {
        if (BindingOperations.IsDataBound(d, p)) return false;
        var source = DependencyPropertyHelper.GetValueSource(d, p);
        if (source.IsExpression) return false;
        return source.BaseValueSource is BaseValueSource.Local or BaseValueSource.ParentTemplate;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Language == "en" || sender is not FrameworkElement fe) return;

        switch (fe)
        {
            case TextBlock tb:
                TranslateTextBlock(tb);
                break;
            case HeaderedItemsControl h when h.Header is string header && IsLiteral(h, HeaderedItemsControl.HeaderProperty):
                h.Header = T(header);
                break;
            case ContentControl cc when cc is not Window && cc.Content is string content && IsLiteral(cc, ContentControl.ContentProperty):
                cc.Content = T(content);
                break;
            case Window w when IsLiteral(w, Window.TitleProperty):
                w.Title = T(w.Title);
                break;
        }

        if (fe.ToolTip is string tip && IsLiteral(fe, FrameworkElement.ToolTipProperty)) fe.ToolTip = T(tip);
        var placeholder = Ui.GetPlaceholder(fe);
        if (!string.IsNullOrEmpty(placeholder) && IsLiteral(fe, Ui.PlaceholderProperty)) Ui.SetPlaceholder(fe, T(placeholder));
    }

    private static void TranslateTextBlock(TextBlock tb)
    {
        if (tb.Inlines.Count > 1 || tb.Inlines.FirstInline is not Run)
        {
            // Mixed text: translate each literal Run on its own.
            foreach (var run in tb.Inlines.OfType<Run>().ToList())
            {
                if (!IsLiteral(run, Run.TextProperty)) continue;
                var raw = run.Text;
                var trimmed = raw.Trim();
                if (trimmed.Length == 0) continue;
                var translated = T(trimmed);
                if (!ReferenceEquals(translated, trimmed)) run.Text = raw.Replace(trimmed, translated);
            }
            return;
        }
        if (IsLiteral(tb, TextBlock.TextProperty))
        {
            var text = tb.Text;
            var translated = T(text.Trim());
            if (translated != text.Trim()) tb.Text = translated;
        }
        else if (!BindingOperations.IsDataBound(tb, TextBlock.TextProperty) && tb.Inlines.FirstInline is Run single)
        {
            var translated = T(single.Text.Trim());
            if (translated != single.Text.Trim()) single.Text = translated;
        }
    }
}
