using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using Phobri.Desktop.Services;

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
}
