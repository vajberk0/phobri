using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Phobri.Desktop.Infrastructure;
using Phobri.Desktop.Services;
using System;

namespace Phobri.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--headless")
        {
            RunHeadless(args);
        }
        else
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }

    /// <summary>
    /// Run the sync server in headless mode (no GUI).
    /// Useful for servers, CI, and automated testing.
    /// </summary>
    private static void RunHeadless(string[] args)
    {
        Console.WriteLine("Phobri Desktop — Headless Server Mode");
        Console.WriteLine("========================================");

        // Parse optional --port argument
        int port = 8765;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
            {
                port = p;
                break;
            }
        }

        var services = ConfigureHeadlessServices(port);
        var server = services.GetRequiredService<SyncServer>();
        var pairingService = services.GetRequiredService<IPairingService>();

        // Print pairing info
        Console.WriteLine($"  Port:       {port} (WSS), {port + 1} (HTTP health)");
        Console.WriteLine($"  Paired:     {(pairingService.IsPaired ? "Yes" : "No — waiting for pair request")}");
        Console.WriteLine($"  Cert FP:    {pairingService.CertificateFingerprint}");
        Console.WriteLine($"  Data dir:   {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/.phobri/");
        Console.WriteLine();

        if (!pairingService.IsPaired)
        {
            var token = pairingService.GeneratePairingToken();
            Console.WriteLine($"  Pairing token: {token}");
            Console.WriteLine($"  (automatically confirmed on first Android connection)");
            Console.WriteLine();
        }

        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        // Handle Ctrl+C for graceful shutdown
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutting down...");
            cts.Cancel();
        };

        try
        {
            server.StartAsync().GetAwaiter().GetResult();
            Console.WriteLine($"Server listening on 0.0.0.0:{port} (WSS) and 127.0.0.1:{port + 1} (HTTP)");

            // Block until Ctrl+C
            cts.Token.WaitHandle.WaitOne();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal: {ex.Message}");
        }
        finally
        {
            server.StopAsync().GetAwaiter().GetResult();
            server.Dispose();
            Console.WriteLine("Server stopped.");
        }
    }

    /// <summary>
    /// Configure DI services for headless server operation.
    /// </summary>
    private static ServiceProvider ConfigureHeadlessServices(int port)
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

        // --- Server ---
        services.AddSingleton(sp => new SyncServer(
            port: port,
            wsHandler: sp.GetRequiredService<IWebSocketHandler>(),
            pairingService: sp.GetRequiredService<IPairingService>(),
            dataService: sp.GetRequiredService<IDataService>()));

        return services.BuildServiceProvider();
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
