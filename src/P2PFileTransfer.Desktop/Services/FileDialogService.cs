using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using P2PFileTransfer.Desktop.Views;

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

        Window? window = this.m_windowProvider.MainWindow;
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

    /// <inheritdoc />
    public async Task<bool> ShowTransferConfirmationAsync(
        string senderName,
        string fileName,
        long fileSize
    )
    {
        Window? parentWindow = this.m_windowProvider.MainWindow;
        if (parentWindow == null)
        {
            // No window available - reject by default for safety.
            return false;
        }

        TransferConfirmationDialog dialog = new();
        dialog.SetContent(senderName, fileName, fileSize);

        await dialog.ShowDialog(parentWindow).ConfigureAwait(false);
        return dialog.IsAccepted;
    }
}
