using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Phobri.Desktop.Services;
using Phobri.Desktop.ViewModels;

namespace Phobri.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded; // Only fire once

        var app = App.Current as App;
        var services = app?.Services;
        if (services is null) return;

        var passwordManager = services.GetRequiredService<IPasswordManagerService>();
        var dataService = services.GetRequiredService<IDataService>();

        if (!passwordManager.IsConfigured)
        {
            // First run: show setup dialog
            var setupDialog = new PasswordDialog(passwordManager, isSetupMode: true);
            setupDialog.ShowDialog(this).ContinueWith(_ =>
            {
                if (setupDialog.Success)
                {
                    App.UnlockDatabase(dataService, passwordManager);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        else if (!passwordManager.IsUnlocked)
        {
            // Subsequent run: show unlock dialog
            var unlockDialog = new PasswordDialog(passwordManager, isSetupMode: false);
            unlockDialog.ShowDialog(this).ContinueWith(_ =>
            {
                if (unlockDialog.Success)
                {
                    App.UnlockDatabase(dataService, passwordManager);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>
    /// Handle the Browse button for the FCM service account key file.
    /// Opens a file picker dialog and sets the path in the ViewModel.
    /// </summary>
    private async void BrowseFcmKey_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Firebase Service Account Key",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON Files")
                {
                    Patterns = new[] { "*.json" }
                }
            }
        });

        if (files.Count > 0)
        {
            var vm = DataContext as MainWindowViewModel;
            if (vm is not null)
            {
                vm.PairingViewModel.FcmServiceAccountPath = files[0].Path.LocalPath;
            }
        }
    }
}
