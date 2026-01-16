using Avalonia.Controls;

namespace P2PFileTransfer.Desktop.Services;

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
