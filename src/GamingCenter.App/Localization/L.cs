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
        if (Language != "en") _map = ReadTranslations(Language);

        // Dates and day names follow the language; money keeps its own fixed format.
        var culture = CultureInfo.GetCultureInfo(Language switch { "fr" => "fr-FR", "ar" => "ar-DZ", _ => "en-GB" });
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>Last problem reading a translation file, shown in Settings when a language fails to load.</summary>
    public static string? LoadError { get; private set; }

    /// <summary>
    /// Lang\{code}.json next to the exe wins (lets a shop fix a word without rebuilding);
    /// otherwise the copy embedded in the program.
    /// </summary>
    private static Dictionary<string, string> ReadTranslations(string code)
    {
        LoadError = null;
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "Lang", code + ".json");
            if (File.Exists(file)) return Parse(File.ReadAllText(file));

            var asm = typeof(L).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + code + ".json", StringComparison.OrdinalIgnoreCase));
            if (name is null) { LoadError = $"Translation {code}.json not found."; return []; }
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            LoadError = $"Could not read {code}.json: {ex.Message}";
            return [];
        }
    }

    private static Dictionary<string, string> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip }) ?? [];

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
        // WPF only sends Loaded to elements that have their own Loaded handler, so a class handler on Loaded
        // misses most text. SizeChanged is raised for every element the first time it is laid out.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, e) => OnLoaded(s, e)), true);
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), true);
    }

    /// <summary>Set once an element has been translated, so resizing does not redo the work.</summary>
    private static readonly DependencyProperty DoneProperty =
        DependencyProperty.RegisterAttached("Done", typeof(bool), typeof(L), new PropertyMetadata(false));

    private static bool IsLiteral(DependencyObject d, DependencyProperty p)
    {
        if (BindingOperations.IsDataBound(d, p)) return false;
        var source = DependencyPropertyHelper.GetValueSource(d, p);
        if (source.IsExpression) return false;
        return source.BaseValueSource is BaseValueSource.Local or BaseValueSource.ParentTemplate;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Language == "en" || sender is not FrameworkElement fe || (bool)fe.GetValue(DoneProperty)) return;
        fe.SetValue(DoneProperty, true);

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
