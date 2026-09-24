using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;

namespace GamingCenter.App.Services;

/// <summary>Sent through WeakReferenceMessenger when data other screens display has changed.</summary>
public sealed record DataChangedMessage(DataArea Area);

public enum DataArea { Stations, Products, Customers, Sessions, Settings, Users }

public sealed partial class CurrentUserService : ObservableObject, ICurrentUser
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdmin))]
    private UserDto? _user;

    public bool IsAdmin => User?.IsAdmin == true;
}

/// <summary>
/// One UI-thread timer for the whole app. Every timer on screen is recomputed from stored
/// timestamps on each tick, so 50+ stations cost one timer, not one thread each.
/// </summary>
public sealed class TickService
{
    private readonly DispatcherTimer _timer;

    public event EventHandler<DateTime>? Tick;

    public TickService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        DateTime last = DateTime.MinValue;
        _timer.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            // Fire once per wall-clock second so all timers change together.
            if (now.Second == last.Second && now - last < TimeSpan.FromSeconds(1)) return;
            last = now;
            Tick?.Invoke(this, now);
        };
        _timer.Start();
    }
}

public enum ToastKind { Info, Success, Warning, Error }

public sealed record Toast(ToastKind Kind, string Title, string? Message);

public sealed class ToastService
{
    public ObservableCollection<Toast> Items { get; } = [];

    public void Show(ToastKind kind, string title, string? message = null, int seconds = 5)
    {
        void Add()
        {
            var toast = new Toast(kind, title, message);
            Items.Add(toast);
            while (Items.Count > 4) Items.RemoveAt(0);
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            t.Tick += (_, _) => { t.Stop(); Items.Remove(toast); };
            t.Start();
        }
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Add(); else d.BeginInvoke(Add);
    }

    public void Success(string title, string? message = null) => Show(ToastKind.Success, title, message);
    public void Info(string title, string? message = null) => Show(ToastKind.Info, title, message);
    public void Warning(string title, string? message = null) => Show(ToastKind.Warning, title, message, 8);
    public void Error(string title, string? message = null) => Show(ToastKind.Error, title, message, 8);

    public void Dismiss(Toast toast) => Items.Remove(toast);
}

public sealed class ThemeService(ISettingsService settings)
{
    private static readonly Uri Dark = new("pack://application:,,,/Resources/Theme/Colors.Dark.xaml");
    private static readonly Uri Light = new("pack://application:,,,/Resources/Theme/Colors.Light.xaml");

    public event EventHandler? ThemeChanged;

    public string Current { get; private set; } = "Dark";

    public void ApplyFromSettings() => Apply(settings.Current.Theme);

    public void Apply(string theme)
    {
        theme = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        var dicts = System.Windows.Application.Current.Resources.MergedDictionaries;
        var existing = dicts.FirstOrDefault(d => d.Source == Dark || d.Source == Light);
        var next = new ResourceDictionary { Source = theme == "Light" ? Light : Dark };
        if (existing is null) dicts.Insert(0, next);
        else dicts[dicts.IndexOf(existing)] = next;
        bool changed = Current != theme;
        Current = theme;
        if (changed) ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}
