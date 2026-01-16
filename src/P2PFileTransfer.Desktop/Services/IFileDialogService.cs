using System.Threading;
using System.Threading.Tasks;

namespace P2PFileTransfer.Desktop.Services;

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
}
