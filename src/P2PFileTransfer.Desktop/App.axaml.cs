using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using P2PFileTransfer.Core.Services;
using P2PFileTransfer.Desktop.Services;
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

    private static ServiceProvider ConfigureServices()
    {
        ServiceCollection services = new();
        services.AddSingleton<IPeerDiscoveryService, PeerDiscoveryService>();
        services.AddSingleton<IFileTransferService, FileTransferService>();
        services.AddSingleton<IWindowProvider, WindowProvider>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider();
    }

    private async Task ShutdownAsync()
    {
        if (this.m_serviceProvider == null)
        {
            return;
        }

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
