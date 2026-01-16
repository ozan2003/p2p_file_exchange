using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace P2PFileTransfer.Desktop.Services;

/// <summary>
/// Opens file dialogs using Avalonia's storage provider.
/// </summary>
public sealed class FileDialogService : IFileDialogService
{
    private readonly IWindowProvider m_windowProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileDialogService"/> class.
    /// </summary>
    /// <param name="windowProvider">The window provider.</param>
    public FileDialogService(IWindowProvider windowProvider)
    {
        this.m_windowProvider = windowProvider;
    }

    /// <inheritdoc />
    public async Task<string?> PickFileAsync(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        Avalonia.Controls.Window? window = this.m_windowProvider.MainWindow;
        if (window == null)
        {
            return null;
        }

        FilePickerOpenOptions options = new()
        {
            AllowMultiple = false,
            Title = "Select a file to send",
        };

        System.Collections.Generic.IReadOnlyList<IStorageFile> files =
            await window.StorageProvider.OpenFilePickerAsync(options);
        IStorageFile? file = files.FirstOrDefault();
        return file?.Path.LocalPath;
    }
}
