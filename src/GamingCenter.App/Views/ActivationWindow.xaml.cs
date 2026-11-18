using System.Windows;
using GamingCenter.App.Licensing;
using GamingCenter.App.Localization;

namespace GamingCenter.App.Views;

/// <summary>Shown at start until a license key valid for this PC has been entered.</summary>
public partial class ActivationWindow : Window
{
    private readonly LicenseService _license;

    public ActivationWindow(LicenseService license, string? message = null)
    {
        _license = license;
        InitializeComponent();
        MachineCodeBox.Text = license.MachineCode;
        ErrorText.Text = message ?? "";
        Loaded += (_, _) => KeyBox.Focus();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_license.MachineCode);
            CopyButton.Content = L.T("Copied");
        }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy: the code can still be selected */ }
    }

    private void OnActivate(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = _license.Activate(KeyBox.Text);
            if (result.IsValid)
            {
                DialogResult = true;
                return;
            }
            ErrorText.Text = LicenseService.Explain(result);
        }
        catch (Exception ex)
        {
            ErrorText.Text = L.F("The license could not be saved: {0}", ex.Message);
        }
    }

    private void OnQuit(object sender, RoutedEventArgs e) => DialogResult = false;
}
