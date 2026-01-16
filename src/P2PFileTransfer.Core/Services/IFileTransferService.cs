using System;
using System.Threading;
using System.Threading.Tasks;
using P2PFileTransfer.Core.Models;

namespace P2PFileTransfer.Core.Services;

/// <summary>
/// Provides file transfer operations over TCP.
/// </summary>
public interface IFileTransferService : IAsyncDisposable
{
    /// <summary>
    /// Occurs when a transfer starts.
    /// </summary>
    event EventHandler<TransferStartedEventArgs>? TransferStarted;

    /// <summary>
    /// Occurs when transfer progress changes.
    /// </summary>
    event EventHandler<TransferProgressEventArgs>? TransferProgressChanged;

    /// <summary>
    /// Occurs when a transfer completes.
    /// </summary>
    event EventHandler<TransferCompletedEventArgs>? TransferCompleted;

    /// <summary>
    /// Occurs when a transfer fails.
    /// </summary>
    event EventHandler<TransferFailedEventArgs>? TransferFailed;

    /// <summary>
    /// The TCP listener port for inbound file transfers.
    /// </summary>
    int ListenerPort { get; }

    /// <summary>
    /// Starts the TCP listener for inbound transfers.
    /// </summary>
    /// <param name="port">The port to listen on. Use 0 for a dynamic port.</param>
    /// <param name="downloadDirectory">The directory where files are saved.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task StartListenerAsync(
        int port,
        string downloadDirectory,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Stops the TCP listener and cancels active accept loops.
    /// </summary>
    Task StopListenerAsync();

    /// <summary>
    /// Sends a file to the specified peer.
    /// </summary>
    /// <param name="filePath">The file path to send.</param>
    /// <param name="peer">The target peer.</param>
    /// <param name="progress">An optional progress reporter.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task SendFileAsync(
        string filePath,
        PeerInfo peer,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    );
}
