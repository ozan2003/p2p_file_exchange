using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using P2PFileExchange.Core.Services.Discovery;
using P2PFileExchange.Core.Services.Security;
using P2PFileExchange.Core.Services.Transfer;
using P2PFileExchange.Desktop.Services;
using P2PFileExchange.Desktop.Settings;
using P2PFileExchange.Desktop.ViewModels;
using P2PFileExchange.Desktop.Views;

namespace P2PFileExchange.Desktop;

/// <summary>
/// Application bootstrapper.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? m_serviceProvider;

    public override void Initialize()
    {
        // Initialize libsodium for cryptographic operations
        IdentityKeyManager.Initialize();

        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (
            this.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop
        )
        {
            // Show a splash or loading state while initializing identity
            MainWindow mainWindow = new();
            desktop.MainWindow = mainWindow;

            // Configure services (partial - identity service needs window)
            (
                ServiceProvider serviceProvider,
                IdentityService identityService
            ) = ConfigureServices(mainWindow);
            this.m_serviceProvider = serviceProvider;

            IWindowProvider windowProvider =
                this.m_serviceProvider.GetRequiredService<IWindowProvider>();
            windowProvider.MainWindow = mainWindow;

            // Initialize identity key (prompts for password if needed)
            IdentityInitResult initResult = await identityService
                .InitializeAsync()
                .ConfigureAwait(true);

            if (
                initResult
                is IdentityInitResult.Cancelled
                    or IdentityInitResult.TooManyAttempts
            )
            {
                // User cancelled or too many failed attempts - shut down
                desktop.Shutdown(1);
                return;
            }

            if (initResult == IdentityInitResult.CorruptedFile)
            {
                // Show error and offer to regenerate
                var errorDialog = new Window
                {
                    Title = "Identity Key Error",
                    Width = 400,
                    Height = 150,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Content = new TextBlock
                    {
                        Text =
                            "Your identity key file is corrupted. Please delete it and restart the application.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Margin = new Thickness(20),
                    },
                };
                await errorDialog.ShowDialog(mainWindow).ConfigureAwait(true);
                desktop.Shutdown(2);
                return;
            }

            // Identity initialized successfully - set up the main view model
            MainViewModel mainViewModel =
                this.m_serviceProvider.GetRequiredService<MainViewModel>();
            mainWindow.DataContext = mainViewModel;

            _ = mainViewModel.InitializeAsync();

            desktop.Exit += async (_, _) =>
                await this.ShutdownAsync().ConfigureAwait(false);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Registers application services and view models.
    /// </summary>
    private static (ServiceProvider, IdentityService) ConfigureServices(
        Window mainWindow
    )
    {
        ServiceCollection services = new();
        SettingsStore settingsStore = new();
        AppSettings appSettings = settingsStore.Load();

        services.AddSingleton(settingsStore);
        services.AddSingleton(appSettings);
        services.AddSingleton(appSettings.Discovery);
        services.AddSingleton(appSettings.Transfer);

        // Create identity key manager and service
        IdentityKeyManager identityKeyManager = new();
        WindowProvider windowProvider = new() { MainWindow = mainWindow };
        DialogPasswordProvider passwordProvider = new(windowProvider);
        IdentityService identityService = new(
            identityKeyManager,
            passwordProvider,
            appSettings.Security.IdentityKeyPath,
            appSettings.Security.RequirePasswordOnStartup
        );

        services.AddSingleton(identityKeyManager);
        services.AddSingleton(identityService);

        services.AddSingleton<IPeerDiscoveryService>(
            provider => new PeerDiscoveryService(
                provider.GetRequiredService<PeerDiscoveryOptions>()
            )
        );
        services.AddSingleton<IFileTransferService>(
            provider => new FileTransferService(
                provider.GetRequiredService<FileTransferOptions>()
            )
        );
        services.AddSingleton<IWindowProvider>(windowProvider);
        services.AddSingleton<IPasswordProvider>(passwordProvider);
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        return (services.BuildServiceProvider(), identityService);
    }

    /// <summary>
    /// Resolves a required service from the application service provider.
    /// </summary>
    internal T GetRequiredService<T>()
        where T : notnull
    {
        if (this.m_serviceProvider == null)
        {
            throw new InvalidOperationException("Services are not available.");
        }

        return this.m_serviceProvider.GetRequiredService<T>();
    }

    private async Task ShutdownAsync()
    {
        if (this.m_serviceProvider == null)
        {
            return;
        }

        MainViewModel? mainViewModel =
            this.m_serviceProvider.GetService<MainViewModel>();
        mainViewModel?.Dispose();

        IPeerDiscoveryService? discoveryService =
            this.m_serviceProvider.GetService<IPeerDiscoveryService>();
        if (discoveryService != null)
        {
            await discoveryService.StopAsync().ConfigureAwait(false);
        }

        IFileTransferService? transferService =
            this.m_serviceProvider.GetService<IFileTransferService>();
        if (transferService != null)
        {
            await transferService.StopListenerAsync().ConfigureAwait(false);
        }

        // Clear identity keys from memory
        IdentityService? identityService =
            this.m_serviceProvider.GetService<IdentityService>();
        identityService?.Dispose();

        await this.m_serviceProvider.DisposeAsync().ConfigureAwait(false);
        this.m_serviceProvider = null;
    }
}
