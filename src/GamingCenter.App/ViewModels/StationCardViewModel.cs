using CommunityToolkit.Mvvm.ComponentModel;
using GamingCenter.Application.Common;
using GamingCenter.Application.DTOs;
using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;

namespace GamingCenter.App.ViewModels;

/// <summary>
/// Everything a station card or list row shows. <see cref="Refresh"/> is called once per second
/// and recomputes timer, cost and status from the session's stored timestamps.
/// </summary>
public sealed partial class StationCardViewModel(StationDto station) : ObservableObject
{
    public StationDto Station { get; private set; } = station;
    public GamingSession? Session { get; private set; }

    public int Id => Station.Id;
    public string Name => Station.Name;
    public string Tag => Station.Tag;
    public string? ImagePath => Station.ImagePath;
    public string Room => Station.Location ?? "—";
    public string TypeLine => string.Join(" · ", new[] { Station.TypeName, Station.Model, Station.Location }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
    public string RateLabel => Money.Rate(Station.HourlyRate);

    [ObservableProperty] private StationDisplayStatus _status;
    [ObservableProperty] private string _statusLabel = "";
    [ObservableProperty] private string _timerText = "";
    [ObservableProperty] private string _timerBrush = "Text";
    [ObservableProperty] private string _line = "";
    [ObservableProperty] private string _costText = "";
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressBrush = "Accent";
    [ObservableProperty] private bool _isEndingSoon;

    public bool IsActive => Session is not null;
    public bool IsAvailable => Session is null && Station.IsActive && Station.State != StationState.Maintenance;
    public bool IsPaused => Session?.Status == SessionStatus.Paused;
    public bool IsRunning => Session?.Status == SessionStatus.Running;
    public bool IsReserved => Session is null && Station.State == StationState.Reserved && Station.IsActive;
    public bool IsOffline => Status == StationDisplayStatus.Offline;
    public bool IsMaintenance => Station.State == StationState.Maintenance;
    public bool CanPause => Session?.Status is SessionStatus.Running or SessionStatus.Paused;

    public void Update(StationDto station, GamingSession? session, DateTime now, int warnMinutes)
    {
        Station = station;
        Session = session;
        OnPropertyChanged(string.Empty); // station fields may all have changed
        Refresh(now, warnMinutes);
    }

    public void Refresh(DateTime now, int warnMinutes)
    {
        var s = Session;
        if (s is not null)
        {
            var remaining = s.RemainingTime(now);
            bool timeUp = s.Status == SessionStatus.AwaitingPayment || s.IsTimeUp(now);
            bool endingSoon = !timeUp && remaining is { } r && warnMinutes > 0 && r <= TimeSpan.FromMinutes(warnMinutes);

            Status = timeUp ? StationDisplayStatus.TimeUp
                : s.Status == SessionStatus.Paused ? StationDisplayStatus.Paused
                : StationDisplayStatus.Occupied;
            StatusLabel = Status switch
            {
                StationDisplayStatus.TimeUp => s.Status == SessionStatus.AwaitingPayment ? "Awaiting payment" : "Time up",
                StationDisplayStatus.Paused => "Paused",
                _ => "Occupied",
            };
            TimerText = remaining is { } rem && !timeUp ? Durations.Clock(rem) : Durations.Clock(s.PlayedTime(now));
            TimerBrush = timeUp ? "Danger" : s.Status == SessionStatus.Paused ? "Info" : endingSoon ? "Warning" : "Text";
            IsEndingSoon = endingSoon || timeUp;

            var who = s.Customer?.Name ?? "Walk-in";
            Line = s.Mode switch
            {
                SessionMode.FixedDuration => $"Fixed {Durations.Minutes(s.PlannedMinutes ?? 0)} · {who}" + (endingSoon ? " · ending soon" : ""),
                SessionMode.FixedBudget => $"Budget {Money.Format(s.Budget ?? 0)} · {who}",
                _ => s.Status == SessionStatus.Paused && s.OpenPause is { } p ? $"Paused {p.StartTime:HH:mm} · {who}" : $"Open · {who}",
            };
            CostText = Money.Format(s.TotalCost(now));
            HasProgress = s.AllowedTime is not null;
            Progress = s.Progress(now);
            ProgressBrush = timeUp ? "Danger" : endingSoon ? "Warning" : "Status.Occupied";
        }
        else
        {
            HasProgress = false;
            Progress = 0;
            IsEndingSoon = false;
            if (!Station.IsActive || Station.State == StationState.Maintenance)
            {
                Status = StationDisplayStatus.Offline;
                StatusLabel = Station.IsActive ? "Maintenance" : "Disabled";
                TimerText = "—";
                TimerBrush = "Text.Faint";
                Line = Station.IsActive ? "Under maintenance" : "Disabled by admin";
                CostText = "";
            }
            else if (Station.State == StationState.Reserved)
            {
                Status = StationDisplayStatus.Reserved;
                StatusLabel = "Reserved";
                TimerText = Station.ReservedAt?.ToString("HH:mm") ?? "Reserved";
                TimerBrush = "Warning";
                Line = string.IsNullOrWhiteSpace(Station.ReservedFor) ? "Reserved" : $"Reserved · {Station.ReservedFor}";
                CostText = RateLabel;
            }
            else
            {
                Status = StationDisplayStatus.Available;
                StatusLabel = "Available";
                TimerText = "Ready";
                TimerBrush = "Accent";
                Line = "Tap to start";
                CostText = RateLabel;
            }
        }
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsReserved));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(CanPause));
    }

    /// <summary>Sort key for "Status" ordering: things needing attention first.</summary>
    public int StatusOrder => Status switch
    {
        StationDisplayStatus.TimeUp => 0,
        StationDisplayStatus.Occupied => IsEndingSoon ? 1 : 2,
        StationDisplayStatus.Paused => 3,
        StationDisplayStatus.Reserved => 4,
        StationDisplayStatus.Available => 5,
        _ => 6,
    };
}
