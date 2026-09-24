using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GamingCenter.App.Services;
using GamingCenter.Application.Common;
using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Enums;

namespace GamingCenter.App.ViewModels;

public sealed record StationRow(StationDto Station, bool HasSession)
{
    public string StatusText => !Station.IsActive ? "Disabled" : Station.State switch
    {
        StationState.Maintenance => "Maintenance",
        StationState.Reserved => "Reserved",
        _ => HasSession ? "In use" : "Active",
    };
    public string StatusKey => !Station.IsActive || Station.State == StationState.Maintenance ? "Status.Offline"
        : Station.State == StationState.Reserved ? "Status.Reserved"
        : HasSession ? "Status.Occupied" : "Status.Available";
    public string ToggleLabel => Station.IsActive ? "Disable" : "Enable";
    public string RateLabel => Money.Rate(Station.HourlyRate);
}

/// <summary>Gaming Stations admin (design 1h): table plus edit panel.</summary>
public sealed partial class StationsViewModel : PageViewModel
{
    private readonly IStationService _stations;
    private readonly IImageStore _images;
    private readonly FileDialogService _files;
    private readonly DialogService _dialogs;
    private readonly LiveSessionStore _store;
    private List<StationRow> _all = [];

    public StationsViewModel(IStationService stations, IImageStore images, FileDialogService files, DialogService dialogs,
        LiveSessionStore store, ToastService toasts) : base(toasts)
    {
        _stations = stations;
        _images = images;
        _files = files;
        _dialogs = dialogs;
        _store = store;
    }

    public override string Title => "Gaming Stations";

    public ObservableCollection<StationRow> Rows { get; } = [];
    public ObservableCollection<StationTypeDto> Types { get; } = [];
    public ObservableCollection<string> Locations { get; } = [];
    public StationState[] States { get; } = [StationState.Available, StationState.Reserved, StationState.Maintenance];

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private StationRow? _selected;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _focusPrice;

    // Editor fields
    [ObservableProperty] private int? _editId;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editNumber = "";
    [ObservableProperty] private StationTypeDto? _editType;
    [ObservableProperty] private string _editBrand = "";
    [ObservableProperty] private string _editModel = "";
    [ObservableProperty] private string _editRate = "";
    [ObservableProperty] private string _editControllers = "";
    [ObservableProperty] private string _editLocation = "";
    [ObservableProperty] private StationState _editState;
    [ObservableProperty] private bool _editActive = true;
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private string? _editImage;
    [ObservableProperty] private string? _priceNotice;
    [ObservableProperty] private string? _editError;
    [ObservableProperty] private string _editTag = "";
    public ObservableCollection<PriceHistoryDto> PriceHistory { get; } = [];

    public string EditorTitle => EditId is null ? "Add station" : "Edit station";

    public override async Task OnNavigatedToAsync() => await LoadAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        await _store.RefreshAsync();
        var list = await _stations.GetAllAsync();
        _all = list.Select(s => new StationRow(s, _store.ForStation(s.Id) is not null)).ToList();
        var types = await _stations.GetTypesAsync();
        var keepTypeId = EditType?.Id;
        Types.Clear();
        foreach (var t in types.Where(t => t.IsActive || t.Id == keepTypeId || list.Any(s => s.StationTypeId == t.Id))) Types.Add(t);
        if (keepTypeId is not null) EditType = Types.FirstOrDefault(t => t.Id == keepTypeId);
        Locations.Clear();
        foreach (var l in list.Select(s => s.Location).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct().Order()) Locations.Add(l!);
        Summary = $"{_all.Count} stations · {_all.Count(r => !r.Station.IsActive)} disabled";
        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var t = FilterText.Trim();
        Rows.Clear();
        foreach (var r in _all.Where(r => t.Length == 0
                     || r.Station.Name.Contains(t, StringComparison.CurrentCultureIgnoreCase)
                     || r.Station.TypeName.Contains(t, StringComparison.CurrentCultureIgnoreCase)
                     || (r.Station.Model?.Contains(t, StringComparison.CurrentCultureIgnoreCase) ?? false)
                     || (r.Station.Location?.Contains(t, StringComparison.CurrentCultureIgnoreCase) ?? false)))
            Rows.Add(r);
    }

    partial void OnSelectedChanged(StationRow? value)
    {
        if (value is not null) _ = EditAsync(value.Station, focusPrice: false);
    }

    private async Task EditAsync(StationDto s, bool focusPrice)
    {
        EditError = null;
        EditId = s.Id;
        EditName = s.Name;
        EditNumber = s.Number?.ToString() ?? "";
        EditType = Types.FirstOrDefault(t => t.Id == s.StationTypeId);
        EditBrand = s.Brand ?? "";
        EditModel = s.Model ?? "";
        EditRate = Money.Number(s.HourlyRate).Replace(",", "");
        EditControllers = s.ControllerCount?.ToString() ?? "";
        EditLocation = s.Location ?? "";
        EditState = s.State;
        EditActive = s.IsActive;
        EditDescription = s.Description ?? "";
        EditImage = s.ImagePath;
        EditTag = s.Tag;
        IsEditing = true;
        OnPropertyChanged(nameof(EditorTitle));
        FocusPrice = false;
        FocusPrice = focusPrice;
        UpdatePriceNotice();
        PriceHistory.Clear();
        await TryAsync(async () =>
        {
            foreach (var h in await _stations.GetPriceHistoryAsync(s.Id)) PriceHistory.Add(h);
        });
    }

    partial void OnEditRateChanged(string value) => UpdatePriceNotice();
    partial void OnEditModelChanged(string value) => EditTag = !string.IsNullOrWhiteSpace(value) && value.Length <= 5 ? value.ToUpperInvariant() : EditType?.Tag ?? "";

    partial void OnEditTypeChanged(StationTypeDto? value)
    {
        if (value is null) return;
        if (EditId is null && string.IsNullOrWhiteSpace(EditRate)) EditRate = Money.Number(value.DefaultHourlyRate).Replace(",", "");
        OnEditModelChanged(EditModel);
    }

    private void UpdatePriceNotice()
    {
        PriceNotice = null;
        if (EditId is null || !Money.TryParse(EditRate, out var rate)) return;
        var current = _all.FirstOrDefault(r => r.Station.Id == EditId);
        if (current is null || current.Station.HourlyRate == rate) return;
        var session = _store.ForStation(current.Station.Id);
        PriceNotice = $"Price {Money.Number(current.Station.HourlyRate)} → {Money.Number(rate)} {Money.CurrencySymbol}/h. Applies to new sessions only."
            + (session is not null ? $" {current.Station.Name}'s running session keeps {Money.Rate(session.HourlyRate)}." : "")
            + " Change is logged in price history.";
    }

    [RelayCommand]
    private void Add()
    {
        Selected = null;
        EditError = null;
        EditId = null;
        EditName = "";
        EditNumber = "";
        EditType = Types.FirstOrDefault();
        EditBrand = "";
        EditModel = "";
        EditRate = EditType is null ? "" : Money.Number(EditType.DefaultHourlyRate).Replace(",", "");
        EditControllers = "";
        EditLocation = Locations.FirstOrDefault() ?? "";
        EditState = StationState.Available;
        EditActive = true;
        EditDescription = "";
        EditImage = null;
        PriceNotice = null;
        PriceHistory.Clear();
        IsEditing = true;
        OnPropertyChanged(nameof(EditorTitle));
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        Selected = null;
    }

    [RelayCommand]
    private async Task ChooseImage()
    {
        var file = _files.OpenImage();
        if (file is null) return;
        await TryAsync(async () => EditImage = await _images.ImportAsync(file, "stations"));
    }

    [RelayCommand]
    private void RemoveImage() => EditImage = null;

    [RelayCommand]
    private async Task Save()
    {
        EditError = null;
        if (EditType is null) { EditError = "Choose a station type."; return; }
        if (!Money.TryParse(EditRate, out var rate)) { EditError = "Enter a valid hourly price."; return; }
        int? number = null, controllers = null;
        if (!string.IsNullOrWhiteSpace(EditNumber)) { if (!int.TryParse(EditNumber, out var n)) { EditError = "Number must be a whole number."; return; } number = n; }
        if (!string.IsNullOrWhiteSpace(EditControllers)) { if (!int.TryParse(EditControllers, out var c)) { EditError = "Controllers must be a whole number."; return; } controllers = c; }

        try
        {
            var saved = await _stations.SaveAsync(new SaveStationRequest(EditId, EditName, number, EditType.Id, EditBrand, EditModel, EditImage,
                rate, EditDescription, EditLocation, controllers, EditState, EditActive));
            Toasts.Success(EditId is null ? $"{saved.Name} added" : $"{saved.Name} saved");
            WeakReferenceMessenger.Default.Send(new DataChangedMessage(DataArea.Stations));
            await ReloadAsync();
            Selected = Rows.FirstOrDefault(r => r.Station.Id == saved.Id);
            if (Selected is null) await EditAsync(saved, false);
        }
        catch (Exception ex)
        {
            EditError = ErrorText.For(ex);
        }
    }

    [RelayCommand]
    private async Task EditPrice(StationRow row)
    {
        Selected = row;
        await EditAsync(row.Station, focusPrice: true);
    }

    [RelayCommand]
    private async Task ToggleActive(StationRow row)
    {
        bool enable = !row.Station.IsActive;
        if (!enable && !await _dialogs.ConfirmAsync($"Disable {row.Station.Name}?",
                "It will no longer be available for new sessions. Its history stays in reports.", "Disable", danger: true)) return;
        if (await TryAsync(() => _stations.SetActiveAsync(row.Station.Id, enable), enable ? $"{row.Station.Name} enabled" : $"{row.Station.Name} disabled"))
        {
            WeakReferenceMessenger.Default.Send(new DataChangedMessage(DataArea.Stations));
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task Delete(StationRow row)
    {
        if (!await _dialogs.ConfirmAsync($"Delete {row.Station.Name}?",
                "The station is removed from the dashboard and admin list. Completed sessions and receipts keep their data.", "Delete", danger: true)) return;
        if (await TryAsync(() => _stations.DeleteAsync(row.Station.Id), $"{row.Station.Name} deleted"))
        {
            if (EditId == row.Station.Id) CancelEdit();
            WeakReferenceMessenger.Default.Send(new DataChangedMessage(DataArea.Stations));
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task ManageTypes()
    {
        await _dialogs.ShowAsync(new StationTypesViewModel(_stations, _dialogs));
        await ReloadAsync();
    }
}

/// <summary>Editable list of station categories (PlayStation, PC, Xbox…).</summary>
public sealed partial class StationTypesViewModel : DialogViewModel
{
    private readonly IStationService _stations;
    private readonly DialogService _dialogs;

    public StationTypesViewModel(IStationService stations, DialogService dialogs)
    {
        _stations = stations;
        _dialogs = dialogs;
        _ = LoadAsync();
    }

    public ObservableCollection<StationTypeDto> Types { get; } = [];

    [ObservableProperty] private int? _editId;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _tag = "";
    [ObservableProperty] private string _rate = "";
    [ObservableProperty] private bool _isActive = true;

    private async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            Types.Clear();
            foreach (var t in await _stations.GetTypesAsync()) Types.Add(t);
        });
    }

    [RelayCommand]
    private void Edit(StationTypeDto t)
    {
        EditId = t.Id;
        Name = t.Name;
        Tag = t.Tag;
        Rate = Money.Number(t.DefaultHourlyRate).Replace(",", "");
        IsActive = t.IsActive;
    }

    [RelayCommand]
    private void New()
    {
        EditId = null;
        Name = "";
        Tag = "";
        Rate = "";
        IsActive = true;
    }

    [RelayCommand]
    private async Task Save()
    {
        if (!Money.TryParse(string.IsNullOrWhiteSpace(Rate) ? "0" : Rate, out var rate)) { Error = "Enter a valid default price."; return; }
        if (await RunAsync(() => _stations.SaveTypeAsync(new SaveStationTypeRequest(EditId, Name, Tag, rate, IsActive))))
        {
            New();
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task Delete(StationTypeDto t)
    {
        if (!await _dialogs.ConfirmAsync($"Delete type {t.Name}?", "Only types that were never used can be deleted.", "Delete", true)) return;
        if (await RunAsync(() => _stations.DeleteTypeAsync(t.Id))) await LoadAsync();
    }
}
