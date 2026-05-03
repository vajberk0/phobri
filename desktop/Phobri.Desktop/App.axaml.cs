using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Phobri.Desktop.Infrastructure;
using Phobri.Desktop.Services;
using Phobri.Desktop.ViewModels;
using Phobri.Desktop.Views;

namespace Phobri.Desktop;

public partial class App : Application
{
    internal ServiceProvider? Services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Services = ConfigureServices();

            // --- Global exception handling ---
            // Catch unhandled dispatcher exceptions (UI thread).
            Dispatcher.UIThread.UnhandledException += (sender, e) =>
            {
                e.Handled = true;
                ShowExceptionDialog("UI Thread Exception", e.Exception);
            };

            // Catch unobserved task exceptions (async void / fire-and-forget).
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                e.SetObserved();
                Dispatcher.UIThread.Post(() =>
                {
                    ShowExceptionDialog("Background Task Exception", e.Exception);
                });
            };

            // Catch final fallback exceptions.
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        ShowExceptionDialog("Fatal Exception", ex);
                    });
                }
            };

            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Unlock the database with the DEK after password is set/unlocked.
    /// </summary>
    internal static void UnlockDatabase(IDataService dataService, IPasswordManagerService passwordManager)
    {
        var dek = passwordManager.DataEncryptionKey;
        if (dek is not null)
        {
            dataService.UnlockAsync(dek).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception is not null)
                {
                    Dispatcher.UIThread.Post(() =>
                        ShowExceptionDialog("Database Unlock Error", t.Exception));
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Show an exception in a dialog so the user can see what happened.
    /// </summary>
    internal static void ShowExceptionDialog(string title, Exception ex)
    {
        try
        {
            // Flatten AggregateExceptions
            if (ex is AggregateException agg && agg.InnerExceptions.Count == 1)
                ex = agg.InnerExceptions[0];

            var message = $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace ?? "(no stack trace)"}";

            var dialog = new Window
            {
                Title = title,
                Width = 600,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = true,
                ShowInTaskbar = false
            };

            var stack = new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(16)
            };

            stack.Children.Add(new TextBlock
            {
                Text = "An unexpected error occurred:",
                FontSize = 14,
                FontWeight = Avalonia.Media.FontWeight.Bold
            });
            stack.Children.Add(new TextBlock
            {
                Text = ex.GetType().Name,
                FontSize = 12
            });
            stack.Children.Add(new TextBlock
            {
                Text = ex.Message,
                FontSize = 12,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });

            var scrollViewer = new ScrollViewer
            {
                Height = 200,
                Content = new TextBlock
                {
                    Text = message,
                    FontSize = 10,
                    FontFamily = "Courier New",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                }
            };
            stack.Children.Add(scrollViewer);

            var okButton = new Button
            {
                Content = "OK",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                MinWidth = 80
            };
            okButton.Click += (_, _) => dialog.Close();
            stack.Children.Add(okButton);

            dialog.Content = new Border { Padding = new Thickness(8), Child = stack };
            dialog.Show();
        }
        catch
        {
            // Last resort: nothing more we can do
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // --- Infrastructure ---
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".phobri");

        var configManager = new ConfigurationManager(configDir);
        services.AddSingleton(configManager);

        // --- Password Manager ---
        var passwordManager = new PasswordManagerService(configManager);
        services.AddSingleton<IPasswordManagerService>(passwordManager);

        // --- Data ---
        var dataService = new DataService(Path.Combine(configDir, "data.db"));
        services.AddSingleton<IDataService>(dataService);

        // --- Pairing ---
        var pairingService = new PairingService(configManager, passwordManager);
        services.AddSingleton<IPairingService>(pairingService);

        // --- Networking ---
        services.AddSingleton<IWebSocketHandler, WebSocketHandler>();
        services.AddSingleton<IUdpWakeService, UdpWakeService>();
        services.AddSingleton<IExternalIpService>(sp =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return new ExternalIpService(client);
        });

        // --- Server ---
        services.AddSingleton<SyncServer>(sp =>
        {
            return new SyncServer(
                port: 8765,
                wsHandler: sp.GetRequiredService<IWebSocketHandler>(),
                pairingService: sp.GetRequiredService<IPairingService>(),
                dataService: sp.GetRequiredService<IDataService>(),
                passwordManager: sp.GetRequiredService<IPasswordManagerService>());
        });

        // --- ViewModels ---
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
