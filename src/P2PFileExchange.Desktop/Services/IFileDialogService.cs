using System.Threading;
using System.Threading.Tasks;

namespace P2PFileExchange.Desktop.Services;

/// <summary>
/// Provides file dialog operations.
/// </summary>
public interface IFileDialogService
{
    /// <summary>
    /// Opens a file picker for selecting a file.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<string?> PickFileAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Shows a confirmation dialog for an incoming file transfer.
    /// </summary>
    /// <param name="senderName">The display name of the sender.</param>
    /// <param name="fileName">The name of the file being sent.</param>
    /// <param name="fileSize">The size of the file in bytes.</param>
    /// <returns>True if the user accepts the transfer; otherwise, false.</returns>
    Task<bool> ShowTransferConfirmationAsync(
        string senderName,
        string fileName,
        long fileSize
    );
}
