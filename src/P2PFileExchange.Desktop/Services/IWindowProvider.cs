using Avalonia.Controls;

namespace P2PFileExchange.Desktop.Services;

/// <summary>
/// Provides access to the main window.
/// </summary>
public interface IWindowProvider
{
    /// <summary>
    /// The main window.
    /// </summary>
    Window? MainWindow { get; set; }
}
