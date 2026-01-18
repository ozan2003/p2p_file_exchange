using System;
using Avalonia;
using Avalonia.ReactiveUI;

namespace P2PFileExchange.Desktop;

/// <summary>
/// Application entry point for the P2P File Transfer desktop client.
/// </summary>
internal sealed class Program
{
    /// <summary>
    /// Application entry point. Initializes and starts the Avalonia application.
    /// </summary>
    /// <remarks>
    /// Do not use Avalonia, third-party APIs, or SynchronizationContext-reliant code
    /// before this method is called—the framework is not yet initialized.
    /// </remarks>
    /// <param name="args">Command-line arguments.</param>
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// Configures the Avalonia application builder with platform detection,
    /// font loading, logging, and ReactiveUI integration.
    /// </summary>
    /// <remarks>Also used by the Avalonia visual designer.</remarks>
    /// <returns>A configured <see cref="AppBuilder"/> instance.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
