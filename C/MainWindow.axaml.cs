using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Threading.Tasks;

namespace SWBF_C_build;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnInstallButtonClick(object sender, RoutedEventArgs e)
    {
        // Disable buttons during execution
        SetActionButtonsEnabled(false);
        StatusText.Text = "Checking GitHub for updates...";

        for (int i = 0; i <= 100; i += 10)
        {
            InstallProgressBar.Value = i;
            await Task.Delay(120); // Simulates progress
        }

        StatusText.Text = "All mods up to date!";
        SetActionButtonsEnabled(true);
    }

    private void OnVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Guard against designer instantiation and uninitialized UI controls
        if (Design.IsDesignMode || StatusText == null) return;

        if (VersionComboBox?.SelectedItem is ComboBoxItem selectedItem)
        {
            string selectedVersion = selectedItem.Content?.ToString() ?? "";
            StatusText.Text = $"Switched build target to {selectedVersion}";
        }
    }

    // Folder picker button handlers
    private async void OnBrowseGamePathClick(object? sender, RoutedEventArgs e)
    {
        await PickFolderAndSetPathAsync(GamePathTextBox, "Select Game Installation Folder");
    }

    private async void OnBrowseModPathClick(object? sender, RoutedEventArgs e)
    {
        await PickFolderAndSetPathAsync(ModPathTextBox, "Select Mod Installation Folder");
    }

    private async void OnBrowseAppdataPathClick(object? sender, RoutedEventArgs e)
    {
        await PickFolderAndSetPathAsync(AppdataPathTextBox, "Select AppData Folder");
    }

    // Reusable native OS folder picker helper
    private async Task PickFolderAndSetPathAsync(TextBox targetTextBox, string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            targetTextBox.Text = folders[0].Path.LocalPath;
        }
    }

    // UI state helper to lock buttons during async tasks
    private void SetActionButtonsEnabled(bool isEnabled)
    {
        if (InstallButton != null) InstallButton.IsEnabled = isEnabled;
        if (RepairButton != null) RepairButton.IsEnabled = isEnabled;
        if (UpdateButton != null) UpdateButton.IsEnabled = isEnabled;
    }
}