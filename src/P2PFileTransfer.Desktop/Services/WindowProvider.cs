using Avalonia.Controls;

namespace P2PFileTransfer.Desktop.Services;

/// <summary>
/// Default window provider implementation.
/// </summary>
public sealed class WindowProvider : IWindowProvider
{
    /// <inheritdoc />
    public Window? MainWindow { get; set; }
}
