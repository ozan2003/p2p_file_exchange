using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using P2PFileExchange.Core.Models;
using P2PFileExchange.Core.Models.TransferEvents;

namespace P2PFileExchange.Core.Services.Transfer;

/// <summary>
/// Provides file transfer operations over TCP.
/// </summary>
public interface IFileTransferService : IAsyncDisposable
{
    /// <summary>
    /// Occurs when an incoming transfer request is received and awaits user approval.
    /// The handler should call <see cref="RespondToTransferRequestAsync"/> to accept or reject.
    /// </summary>
    event EventHandler<TransferRequestEventArgs>? TransferRequestReceived;

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
    ushort ListenerPort { get; }

    /// <summary>
    /// Starts the TCP listener for inbound transfers with TLS support.
    /// </summary>
    /// <param name="port">The port to listen on. Use 0 for a dynamic port.</param>
    /// <param name="downloadDirectory">The directory where files are saved.</param>
    /// <param name="certificate">The local TLS certificate with private key.</param>
    /// <param name="fingerprintLookup">
    /// A function to look up expected certificate fingerprints by IP address.
    /// Returns null if the peer is unknown.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task StartListenerAsync(
        ushort port,
        string downloadDirectory,
        X509Certificate2 certificate,
        Func<IPAddress, string?> fingerprintLookup,
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

    /// <summary>
    /// Responds to a pending transfer request by accepting or rejecting it.
    /// </summary>
    /// <param name="requestId">The request ID from <see cref="TransferRequestEventArgs"/>.</param>
    /// <param name="response">The response to send (Accepted or Rejected).</param>
    void RespondToTransferRequest(Guid requestId, TransferResponse response);
}
