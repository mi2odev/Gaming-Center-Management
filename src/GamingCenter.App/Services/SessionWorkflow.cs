using CommunityToolkit.Mvvm.Messaging;
using GamingCenter.App.ViewModels;
using GamingCenter.Application.Common;
using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace GamingCenter.App.Services;

/// <summary>
/// The operator's core flow, shared by dashboard cards, list rows, search and the header button:
/// station → start → consumption → bill → payment → station free again.
/// </summary>
public sealed class SessionWorkflow(
    IServiceProvider services,
    DialogService dialogs,
    LiveSessionStore store,
    IStationService stations,
    ISessionService sessions,
    ToastService toasts)
{
    private T Create<T>(params object[] args) where T : DialogViewModel => ActivatorUtilities.CreateInstance<T>(services, args);

    /// <summary>Card click: open the running session, or start one on a free station.</summary>
    public async Task OpenStationAsync(int stationId)
    {
        if (store.ForStation(stationId) is { } live)
        {
            await OpenSessionAsync(live.Id);
            return;
        }
        var station = await stations.GetAsync(stationId);
        if (station is null) return;
        if (!station.IsActive) { toasts.Info($"{station.Name} is disabled", "An administrator can enable it on the Gaming Stations page."); return; }
        if (station.State == StationState.Maintenance) { toasts.Info($"{station.Name} is under maintenance", "Right-click the card and choose \"Back in service\"."); return; }
        await StartSessionAsync(station);
    }

    public async Task<GamingSession?> StartSessionAsync(StationDto? station = null)
    {
        var session = await dialogs.ShowAsync<GamingSession>(Create<StartSessionViewModel>(new StartSessionArgs(station)));
        if (session is not null)
        {
            store.Upsert(session);
            toasts.Success($"Session started on {session.StationName}", session.Mode switch
            {
                SessionMode.FixedDuration => $"Fixed {Durations.Minutes(session.PlannedMinutes ?? 0)} · {Money.Format(session.GamingCost(session.StartTime))}",
                SessionMode.FixedBudget => $"Budget {Money.Format(session.Budget ?? 0)} · max {Durations.Clock(session.AllowedTime(session.StartTime) ?? TimeSpan.Zero)}",
                _ => $"Open session at {Money.Rate(session.HourlyRate)}",
            });
        }
        return session;
    }

    public async Task OpenSessionAsync(int sessionId, bool focusCatalog = false)
    {
        if (store.ById(sessionId) is null) await store.RefreshAsync();
        if (store.ById(sessionId) is null) { toasts.Info("This session has already ended."); return; }
        await dialogs.ShowAsync(Create<SessionDrawerViewModel>(new SessionDrawerArgs(sessionId, focusCatalog)));
    }

    /// <summary>Shows the final bill; returns true when paid.</summary>
    public async Task<bool> BillAsync(int sessionId)
    {
        if (store.ById(sessionId) is null) await store.RefreshAsync();
        if (store.ById(sessionId) is null) return false;
        var paid = await dialogs.ShowAsync<bool>(Create<BillViewModel>(new BillArgs(sessionId)));
        if (paid) WeakReferenceMessenger.Default.Send(new DataChangedMessage(DataArea.Sessions));
        return paid;
    }

    public async Task CounterSaleAsync()
    {
        var paid = await dialogs.ShowAsync<bool>(Create<CounterSaleViewModel>());
        if (paid) WeakReferenceMessenger.Default.Send(new DataChangedMessage(DataArea.Sessions));
    }

    public async Task TogglePauseAsync(int sessionId)
    {
        try
        {
            var s = store.ById(sessionId);
            if (s is null) return;
            var updated = s.Status == SessionStatus.Paused ? await sessions.ResumeAsync(sessionId) : await sessions.PauseAsync(sessionId);
            store.Upsert(updated);
            toasts.Info(updated.Status == SessionStatus.Paused ? $"{updated.StationName} paused" : $"{updated.StationName} resumed");
        }
        catch (Exception ex)
        {
            toasts.Error("Could not change the session", ErrorText.For(ex));
        }
    }
}
