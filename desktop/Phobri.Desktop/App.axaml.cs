using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Phobri.Desktop.Infrastructure;
using Phobri.Desktop.Services;
using Phobri.Desktop.ViewModels;
using Phobri.Desktop.Views;

namespace Phobri.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = ConfigureServices();

            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();
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

        // --- Data ---
        var dataService = new DataService(Path.Combine(configDir, "data.db"));
        services.AddSingleton<IDataService>(dataService);

        // --- Pairing ---
        var pairingService = new PairingService(configManager);
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
                dataService: sp.GetRequiredService<IDataService>());
        });

        // --- ViewModels ---
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
