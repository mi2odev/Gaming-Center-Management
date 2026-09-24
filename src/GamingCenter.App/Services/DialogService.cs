using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GamingCenter.App.Services;

public enum DialogPlacement { Center, Right }

/// <summary>Base for in-window dialogs (modals and drawers) shown by <see cref="DialogService"/>.</summary>
public abstract partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<object?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public virtual DialogPlacement Placement => DialogPlacement.Center;

    /// <summary>Closing by Esc or clicking the backdrop is allowed unless the dialog is busy.</summary>
    public virtual bool CanDismiss => !IsBusy;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    public Task<object?> Result => _tcs.Task;

    internal event EventHandler? Closed;

    public void Close(object? result = null)
    {
        if (_tcs.TrySetResult(result))
        {
            OnClosed();
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    protected virtual void OnClosed() { }

    [RelayCommand]
    private void Cancel()
    {
        if (CanDismiss) Close();
    }

    /// <summary>Runs work with a busy flag and shows business errors inline instead of crashing.</summary>
    protected async Task<bool> RunAsync(Func<Task> work)
    {
        if (IsBusy) return false;
        IsBusy = true;
        Error = null;
        try
        {
            await work();
            return true;
        }
        catch (Exception ex)
        {
            Error = ErrorText.For(ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class DialogService
{
    public ObservableCollection<DialogViewModel> Open { get; } = [];

    public async Task<object?> ShowAsync(DialogViewModel dialog)
    {
        dialog.Closed += (_, _) => Open.Remove(dialog);
        Open.Add(dialog);
        return await dialog.Result;
    }

    public async Task<T?> ShowAsync<T>(DialogViewModel dialog)
    {
        var result = await ShowAsync(dialog);
        return result is T t ? t : default;
    }

    public DialogViewModel? Top => Open.Count > 0 ? Open[^1] : null;

    public void CloseAll()
    {
        foreach (var d in Open.ToList()) d.Close();
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "Confirm", bool danger = false) =>
        ShowAsync<bool>(new ViewModels.ConfirmViewModel(title, message, confirmText, danger));

    public Task<string?> PromptAsync(string title, string label, string? initial = null, string confirmText = "Save", string? placeholder = null) =>
        ShowAsync<string>(new ViewModels.PromptViewModel(title, label, initial, confirmText, placeholder));
}

public static class ErrorText
{
    public static string For(Exception ex) => ex switch
    {
        Application.Interfaces.BusinessException b => b.Message,
        Microsoft.EntityFrameworkCore.DbUpdateException => "The database rejected the change. Nothing was saved.",
        IOException io => "File error: " + io.Message,
        UnauthorizedAccessException => "Access denied to the file or folder.",
        _ => "Unexpected error: " + ex.Message,
    };
}
