using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using P2PFileTransfer.Core.Services.Discovery;
using P2PFileTransfer.Core.Services.Transfer;
using P2PFileTransfer.Desktop.Services;
using P2PFileTransfer.Desktop.Settings;
using P2PFileTransfer.Desktop.ViewModels;
using P2PFileTransfer.Desktop.Views;

namespace P2PFileTransfer.Desktop;

/// <summary>
/// Application bootstrapper.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? m_serviceProvider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (
            this.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop
        )
        {
            this.m_serviceProvider = ConfigureServices();

            MainWindow mainWindow = new();
            IWindowProvider windowProvider =
                this.m_serviceProvider.GetRequiredService<IWindowProvider>();
            windowProvider.MainWindow = mainWindow;

            MainViewModel mainViewModel =
                this.m_serviceProvider.GetRequiredService<MainViewModel>();
            mainWindow.DataContext = mainViewModel;

            desktop.MainWindow = mainWindow;
            _ = mainViewModel.InitializeAsync();

            desktop.Exit += async (_, _) =>
                await this.ShutdownAsync().ConfigureAwait(false);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Registers application services and view models.
    /// </summary>
    private static ServiceProvider ConfigureServices()
    {
        ServiceCollection services = new();
        SettingsStore settingsStore = new();
        AppSettings appSettings = settingsStore.Load();

        services.AddSingleton(settingsStore);
        services.AddSingleton(appSettings);
        services.AddSingleton(appSettings.Discovery);
        services.AddSingleton(appSettings.Transfer);

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
        services.AddSingleton<IWindowProvider, WindowProvider>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        return services.BuildServiceProvider();
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

        await this.m_serviceProvider.DisposeAsync().ConfigureAwait(false);
        this.m_serviceProvider = null;
    }
}
