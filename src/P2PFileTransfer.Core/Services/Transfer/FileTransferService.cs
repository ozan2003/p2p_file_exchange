using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using P2PFileTransfer.Core.Models;
using P2PFileTransfer.Core.Models.TransferEvents;
using P2PFileTransfer.Core.Utilities;

namespace P2PFileTransfer.Core.Services.Transfer;

/// <summary>
/// Provides TCP-based file transfer functionality.
/// </summary>
public sealed class FileTransferService : IFileTransferService
{
    private readonly FileTransferOptions m_options;

    /// <summary>
    /// Lock for TCP listener.
    /// </summary>
    private readonly SemaphoreSlim m_listenerLock = new(1, 1);

    /// <summary>
    /// Pending transfer requests awaiting user response.
    /// Maps requestId to a TaskCompletionSource that will be completed with the response.
    /// </summary>
    private readonly ConcurrentDictionary<
        Guid,
        TaskCompletionSource<TransferResponse>
    > m_pendingRequests = new();

    /// <summary>
    /// The TCP listener for file transfers.
    /// </summary>
    private TcpListener? m_listener;
    private CancellationTokenSource? m_listenerCts;
    private Task? m_acceptLoopTask;
    private string m_downloadDirectory = string.Empty;
    private X509Certificate2? m_certificate;
    private Func<IPAddress, string?>? m_fingerprintLookup;
    private Func<IPAddress, string?>? m_displayNameLookup;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTransferService"/> class.
    /// </summary>
    public FileTransferService()
        : this(new FileTransferOptions()) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTransferService"/> class with options.
    /// </summary>
    /// <param name="options">The file transfer options.</param>
    public FileTransferService(FileTransferOptions options)
    {
        this.m_options =
            options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public event EventHandler<TransferRequestEventArgs>? TransferRequestReceived;

    /// <inheritdoc />
    public event EventHandler<TransferStartedEventArgs>? TransferStarted;

    /// <inheritdoc />
    public event EventHandler<TransferProgressEventArgs>? TransferProgressChanged;

    /// <inheritdoc />
    public event EventHandler<TransferCompletedEventArgs>? TransferCompleted;

    /// <inheritdoc />
    public event EventHandler<TransferFailedEventArgs>? TransferFailed;

    /// <inheritdoc />
    public ushort ListenerPort { get; private set; }

    /// <inheritdoc />
    public async Task StartListenerAsync(
        ushort port,
        string downloadDirectory,
        X509Certificate2 certificate,
        Func<IPAddress, string?> fingerprintLookup,
        CancellationToken cancellationToken
    )
    {
        await this.StartListenerAsync(
                port,
                downloadDirectory,
                certificate,
                fingerprintLookup,
                displayNameLookup: null,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

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
    /// <param name="displayNameLookup">
    /// A function to look up peer display names by IP address.
    /// Returns null if the peer is unknown.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task StartListenerAsync(
        ushort port,
        string downloadDirectory,
        X509Certificate2 certificate,
        Func<IPAddress, string?> fingerprintLookup,
        Func<IPAddress, string?>? displayNameLookup,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(downloadDirectory))
        {
            throw new ArgumentException(
                "Download directory is required.",
                nameof(downloadDirectory)
            );
        }

        ArgumentNullException.ThrowIfNull(certificate, nameof(certificate));
        ArgumentNullException.ThrowIfNull(
            fingerprintLookup,
            nameof(fingerprintLookup)
        );

        await this
            .m_listenerLock.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (this.m_listener != null)
            {
                return;
            }

            this.m_downloadDirectory = downloadDirectory;
            this.m_certificate = certificate;
            this.m_fingerprintLookup = fingerprintLookup;
            this.m_displayNameLookup = displayNameLookup;
            Directory.CreateDirectory(downloadDirectory);

            this.m_listener = new TcpListener(IPAddress.Any, port);
            this.m_listener.Server.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true
            );
            this.m_listener.Start();

            this.ListenerPort = (ushort)
                ((IPEndPoint)this.m_listener.LocalEndpoint).Port;

            this.m_listenerCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );

            this.m_acceptLoopTask = this.AcceptLoopAsync(
                this.m_listener,
                this.m_listenerCts.Token
            );
        }
        finally
        {
            this.m_listenerLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopListenerAsync()
    {
        await this.m_listenerLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (this.m_listener == null)
            {
                return;
            }

            this.m_listenerCts?.Cancel();
            this.m_listener.Stop();

            if (this.m_acceptLoopTask != null)
            {
                await this
                    .m_acceptLoopTask.ContinueWith(_ => { })
                    .ConfigureAwait(false);
            }

            // Cancel all pending transfer requests.
            foreach (var kvp in this.m_pendingRequests)
            {
                kvp.Value.TrySetCanceled();
            }

            this.m_pendingRequests.Clear();

            this.m_listenerCts?.Dispose();
            this.m_listener = null;
            this.m_listenerCts = null;
            this.m_acceptLoopTask = null;
            this.m_certificate = null;
            this.m_fingerprintLookup = null;
            this.m_displayNameLookup = null;
            this.ListenerPort = 0;
        }
        finally
        {
            this.m_listenerLock.Release();
        }
    }

    /// <summary>
    /// Updates the download directory used for inbound transfers.
    /// </summary>
    /// <param name="downloadDirectory">The target download directory.</param>
    public void UpdateDownloadDirectory(string downloadDirectory)
    {
        if (string.IsNullOrWhiteSpace(downloadDirectory))
        {
            throw new ArgumentException(
                "Download directory is required.",
                nameof(downloadDirectory)
            );
        }

        Directory.CreateDirectory(downloadDirectory);
        this.m_downloadDirectory = downloadDirectory;
    }

    /// <inheritdoc />
    public void RespondToTransferRequest(
        Guid requestId,
        TransferResponse response
    )
    {
        if (
            this.m_pendingRequests.TryRemove(
                requestId,
                out TaskCompletionSource<TransferResponse>? tcs
            )
        )
        {
            tcs.TrySetResult(response);
        }
    }

    /// <inheritdoc />
    public async Task SendFileAsync(
        string filePath,
        PeerInfo peer,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException(
                "File path is required.",
                nameof(filePath)
            );
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found.", filePath);
        }

        FileInfo fileInfo = new(filePath);
        int totalChunks = FileChunker.CalculateTotalChunkNumber(
            fileInfo.Length,
            this.m_options.ChunkSize
        );
        FileMetadata metadata = new()
        {
            FileName = Path.GetFileName(filePath),
            FileSize = fileInfo.Length,
            TotalChunksNumber = totalChunks,
            ChunkSize = this.m_options.ChunkSize,
        };

        // Create a unique transfer ID and notify the UI that the transfer has started.
        Guid transferId = Guid.NewGuid();
        TransferStarted?.Invoke(
            this,
            new TransferStartedEventArgs(
                transferId,
                TransferMode.Send,
                metadata,
                new IPEndPoint(peer.IPAddress, peer.TcpPort),
                filePath
            )
        );

        try
        {
            // Connect to the peer via TCP.
            using TcpClient client = new();
            await client
                .ConnectAsync(peer.IPAddress, peer.TcpPort, cancellationToken)
                .ConfigureAwait(false);
            client.NoDelay = true;

            await using NetworkStream networkStream = client.GetStream();

            // Set up TLS with certificate pinning validation.
            await using SslStream sslStream = new(
                networkStream,
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, certificate, _, _) =>
                    ValidatePeerCertificate(certificate, peer)
            );

            // Authenticate as client with timeout.
            using CancellationTokenSource tlsTimeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
            tlsTimeoutCts.CancelAfter(this.m_options.TlsHandshakeTimeout);

            await sslStream
                .AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = peer.DisplayName,
                        ClientCertificates = null,
                        CertificateRevocationCheckMode =
                            X509RevocationMode.NoCheck,
                    },
                    tlsTimeoutCts.Token
                )
                .ConfigureAwait(false);

            // Send metadata to the peer over TLS.
            await FileTransferProtocol
                .WriteMetadataAsync(sslStream, metadata, cancellationToken)
                .ConfigureAwait(false);

            // Wait for the receiver to accept or reject the transfer.
            TransferResponse response = await FileTransferProtocol
                .ReadResponseAsync(sslStream, cancellationToken)
                .ConfigureAwait(false);

            if (response == TransferResponse.Rejected)
            {
                TransferFailed?.Invoke(
                    this,
                    new TransferFailedEventArgs(
                        transferId,
                        TransferMode.Send,
                        $"Transfer rejected by {peer.DisplayName}."
                    )
                );
                return;
            }

            // Read the file chunks and send them to the peer.
            await using FileStream fileStream = new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: this.m_options.BufferSize,
                useAsync: true
            );

            int chunkIndex = 0;
            IAsyncEnumerable<FileChunk> chunks = FileChunker.ReadChunksAsync(
                fileStream,
                metadata.ChunkSize,
                cancellationToken
            );
            await foreach (FileChunk chunk in chunks)
            {
                await FileTransferProtocol
                    .WriteChunkAsync(sslStream, chunk, cancellationToken)
                    .ConfigureAwait(false);

                ++chunkIndex;
                int progressPercent = CalculateProgressPercent(
                    chunkIndex,
                    metadata.TotalChunksNumber
                );
                // Update the progress bar in the UI.
                progress?.Report(progressPercent);
                TransferProgressChanged?.Invoke(
                    this,
                    new TransferProgressEventArgs(
                        transferId,
                        TransferMode.Send,
                        progressPercent
                    )
                );
            }

            // Notify the UI that the transfer has completed if everything went right.
            TransferCompleted?.Invoke(
                this,
                new TransferCompletedEventArgs(
                    transferId,
                    TransferMode.Send,
                    filePath
                )
            );
        }
        catch (AuthenticationException)
        {
            TransferFailed?.Invoke(
                this,
                new TransferFailedEventArgs(
                    transferId,
                    TransferMode.Send,
                    $"Failed to establish secure connection with {peer.DisplayName}. Certificate verification failed."
                )
            );
        }
        catch (OperationCanceledException)
        {
            // Notify the UI that the transfer has failed.
            TransferFailed?.Invoke(
                this,
                new TransferFailedEventArgs(
                    transferId,
                    TransferMode.Send,
                    "Transfer canceled."
                )
            );
        }
        catch (Exception exc)
        {
            // Notify any exception that occurred during the transfer.
            TransferFailed?.Invoke(
                this,
                new TransferFailedEventArgs(
                    transferId,
                    TransferMode.Send,
                    $"Transfer failed: {exc.Message}"
                )
            );
        }
    }

    /// <summary>
    /// Validates the peer's certificate by comparing its SHA-256 fingerprint
    /// against the expected fingerprint from PeerInfo.
    /// </summary>
    /// <param name="certificate">The certificate presented by the peer.</param>
    /// <param name="peer">The target peer with expected fingerprint.</param>
    /// <returns>True if the certificate matches; otherwise, false.</returns>
    private static bool ValidatePeerCertificate(
        X509Certificate? certificate,
        PeerInfo peer
    )
    {
        if (certificate == null)
        {
            return false;
        }

        // Compute the SHA-256 fingerprint of the presented certificate.
        byte[] certBytes = certificate.GetRawCertData();
        byte[] hashBytes = SHA256.HashData(certBytes);
        string presentedFingerprint = Convert.ToHexString(hashBytes);

        // Get expected fingerprint from peer info.
        string expectedFingerprint = peer.CertificateFingerprint;

        // If no expected fingerprint is known, allow the connection.
        if (string.IsNullOrWhiteSpace(expectedFingerprint))
        {
            return true;
        }

        // Compare fingerprints (case-insensitive).
        bool isValid = string.Equals(
            presentedFingerprint,
            expectedFingerprint,
            StringComparison.OrdinalIgnoreCase
        );

        if (!isValid)
        {
            // Log possible MITM attack.
            Console.Error.WriteLine(
                $"[SECURITY] Possible MITM attack detected for peer {peer.DisplayName} ({peer.IPAddress}): expected fingerprint {expectedFingerprint}, got {presentedFingerprint}"
            );
        }

        return isValid;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.StopListenerAsync().ConfigureAwait(false);
        this.m_listenerLock.Dispose();
    }

    /// <summary>
    /// Continuously accepts incoming TCP connections and dispatches them for handling.
    /// Runs until cancellation is requested or the listener is stopped.
    /// </summary>
    /// <param name="tcpListener">The TCP listener to accept connections from.</param>
    /// <param name="cancellationToken">A token to signal loop termination.</param>
    private async Task AcceptLoopAsync(
        TcpListener tcpListener,
        CancellationToken cancellationToken
    )
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await tcpListener
                    .AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                _ = this.HandleIncomingAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                return;
            }
            catch (ObjectDisposedException)
            {
                client?.Dispose();
                return;
            }
            catch (SocketException)
            {
                client?.Dispose();
            }
        }
    }

    /// <summary>
    /// Handles an incoming file transfer connection with TLS. Reads metadata and chunks from the
    /// SSL stream, verifies chunk integrity via SHA256, and writes data to disk.
    /// Deletes partial files on failure or cancellation.
    /// </summary>
    /// <param name="client">The accepted TCP client connection.</param>
    /// <param name="cancellationToken">A token to cancel the transfer.</param>
    private async Task HandleIncomingAsync(
        TcpClient client,
        CancellationToken cancellationToken
    )
    {
        Guid transferId = Guid.NewGuid();
        string destinationPath = string.Empty;
        bool shouldDeleteFile = false;
        IPAddress remoteIpAddress = IPAddress.None;

        try
        {
            using TcpClient tcpClient = client;

            // Extract remote IP for fingerprint lookup.
            if (tcpClient.Client.RemoteEndPoint is IPEndPoint remoteEndPoint)
            {
                remoteIpAddress = remoteEndPoint.Address;
            }

            IPEndPoint? remoteEndpoint = (IPEndPoint?)
                tcpClient.Client.RemoteEndPoint;

            ArgumentNullException.ThrowIfNull(
                remoteEndpoint,
                nameof(remoteEndpoint)
            );

            await using NetworkStream networkStream = tcpClient.GetStream();

            // Wrap `networkStream` with TLS.
            await using SslStream encryptedStream = new(
                networkStream,
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, certificate, _, _) =>
                    this.ValidateRemoteCertificate(
                        certificate,
                        remoteIpAddress,
                        remoteEndpoint
                    )
            );

            // Authenticate as server with our certificate.
            using CancellationTokenSource tlsTimeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
            tlsTimeoutCts.CancelAfter(this.m_options.TlsHandshakeTimeout);
            await encryptedStream
                .AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = this.m_certificate,
                        ClientCertificateRequired = false,
                        CertificateRevocationCheckMode =
                            X509RevocationMode.NoCheck,
                    },
                    tlsTimeoutCts.Token
                )
                .ConfigureAwait(false);

            FileMetadata metadata = await FileTransferProtocol
                .ReadMetadataAsync(encryptedStream, cancellationToken)
                .ConfigureAwait(false);

            metadata.FileName = SanitizeFileName(metadata.FileName);

            // Look up the sender's display name if available.
            string? senderDisplayName = this.m_displayNameLookup?.Invoke(
                remoteIpAddress
            );

            // Raise the transfer request event and wait for user decision.
            Guid requestId = transferId;
            TaskCompletionSource<TransferResponse> responseTcs = new();
            this.m_pendingRequests[requestId] = responseTcs;

            TransferRequestReceived?.Invoke(
                this,
                new TransferRequestEventArgs(
                    requestId,
                    metadata,
                    remoteEndpoint,
                    senderDisplayName
                )
            );

            // Wait for user response with timeout.
            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
            timeoutCts.CancelAfter(this.m_options.TransferRequestTimeout);

            TransferResponse userResponse;
            try
            {
                userResponse = await responseTcs
                    .Task.WaitAsync(timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Timeout or cancellation - reject the transfer.
                this.m_pendingRequests.TryRemove(
                    requestId,
                    out TaskCompletionSource<TransferResponse>? _
                );
                userResponse = TransferResponse.Rejected;
            }

            // Send the response back to the sender.
            await FileTransferProtocol
                .WriteResponseAsync(
                    encryptedStream,
                    userResponse,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (userResponse == TransferResponse.Rejected)
            {
                // User rejected - nothing more to do.
                return;
            }

            // Set up the destination path for the received file.
            destinationPath = FilePathUtilities.GetUniquePath(
                Path.Combine(this.m_downloadDirectory, metadata.FileName)
            );

            TransferStarted?.Invoke(
                this,
                new TransferStartedEventArgs(
                    transferId,
                    TransferMode.Receive,
                    metadata,
                    remoteEndpoint,
                    destinationPath
                )
            );

            await using FileStream fileStream = new(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: this.m_options.BufferSize,
                useAsync: true
            );

            for (
                int expectedIndex = 0;
                expectedIndex < metadata.TotalChunksNumber;
                ++expectedIndex
            )
            {
                FileChunk chunk = await FileTransferProtocol
                    .ReadChunkAsync(encryptedStream, cancellationToken)
                    .ConfigureAwait(false);

                // Match the chunk index.
                if (chunk.ChunkIndex != expectedIndex)
                {
                    throw new InvalidDataException("Chunk index mismatch.");
                }

                // Verify the chunk hash.
                byte[] hash = SHA256.HashData(chunk.Data);
                if (!hash.AsSpan().SequenceEqual(chunk.Hash))
                {
                    throw new InvalidDataException("Chunk hash mismatch.");
                }

                // Write to disk as we receive the chunks.
                await fileStream
                    .WriteAsync(chunk.Data, cancellationToken)
                    .ConfigureAwait(false);

                // Update the progress bar in the UI.
                int progressPercent = CalculateProgressPercent(
                    expectedIndex + 1,
                    metadata.TotalChunksNumber
                );
                TransferProgressChanged?.Invoke(
                    this,
                    new TransferProgressEventArgs(
                        transferId,
                        TransferMode.Receive,
                        progressPercent
                    )
                );
            }

            await fileStream
                .FlushAsync(cancellationToken)
                .ConfigureAwait(false);
            // Report the progress as 100% when the transfer is complete.
            TransferCompleted?.Invoke(
                this,
                new TransferCompletedEventArgs(
                    transferId,
                    TransferMode.Receive,
                    destinationPath
                )
            );
        }
        catch (AuthenticationException ex)
        {
            shouldDeleteFile = true;
            TransferFailed?.Invoke(
                this,
                new TransferFailedEventArgs(
                    transferId,
                    TransferMode.Receive,
                    $"TLS authentication failed: {ex.Message}"
                )
            );
        }
        catch (OperationCanceledException)
        {
            shouldDeleteFile = true;
            TransferFailed?.Invoke(
                this,
                new TransferFailedEventArgs(
                    transferId,
                    TransferMode.Receive,
                    "Transfer canceled."
                )
            );
        }
        catch (Exception ex)
        {
            shouldDeleteFile = true;
            TransferFailed?.Invoke(
                this,
                new TransferFailedEventArgs(
                    transferId,
                    TransferMode.Receive,
                    $"Transfer failed: {ex.Message}"
                )
            );
        }
        finally
        {
            bool shouldAttemptDelete =
                shouldDeleteFile && !string.IsNullOrWhiteSpace(destinationPath);
            if (shouldAttemptDelete && File.Exists(destinationPath))
            {
                try
                {
                    File.Delete(destinationPath);
                }
                catch (IOException)
                {
                    // Ignore cleanup failures.
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore cleanup failures.
                }
            }
        }
    }

    /// <summary>
    /// Validates the remote peer's certificate by comparing its SHA-256 fingerprint
    /// against the expected fingerprint from peer discovery.
    /// </summary>
    /// <param name="certificate">The certificate presented by the remote peer.</param>
    /// <param name="remoteIpAddress">The IP address of the remote peer.</param>
    /// <param name="remoteEndpoint">The full remote endpoint for logging.</param>
    /// <returns>True if the certificate is valid; otherwise, false.</returns>
    private bool ValidateRemoteCertificate(
        X509Certificate? certificate,
        IPAddress remoteIpAddress,
        IPEndPoint remoteEndpoint
    )
    {
        // If no certificate is presented, allow (server mode with ClientCertificateRequired=false).
        if (certificate == null)
        {
            return true;
        }

        // Compute the SHA-256 fingerprint of the presented certificate.
        byte[] certBytes = certificate.GetRawCertData();
        byte[] hashBytes = SHA256.HashData(certBytes);
        string presentedFingerprint = Convert.ToHexString(hashBytes);

        // Look up the expected fingerprint for this peer.
        string? expectedFingerprint = this.m_fingerprintLookup?.Invoke(
            remoteIpAddress
        );

        // If no expected fingerprint is known, allow the connection (unknown peer).
        if (string.IsNullOrWhiteSpace(expectedFingerprint))
        {
            return true;
        }

        // Compare fingerprints (case-insensitive).
        bool isValid = string.Equals(
            presentedFingerprint,
            expectedFingerprint,
            StringComparison.OrdinalIgnoreCase
        );

        if (!isValid)
        {
            // Log rejection reason via TransferFailed event with a temporary ID.
            TransferFailed?.Invoke(
                this,
                new TransferFailedEventArgs(
                    Guid.Empty,
                    TransferMode.Receive,
                    $"Certificate validation failed for {remoteEndpoint}: {expectedFingerprint}, got {presentedFingerprint}"
                )
            );
        }

        return isValid;
    }

    /// <summary>
    /// Calculates the transfer progress as a percentage clamped to 0–100.
    /// </summary>
    /// <param name="completedChunks">The number of chunks transferred so far.</param>
    /// <param name="totalChunks">The total number of chunks in the transfer.</param>
    /// <returns>The progress percentage (0–100).</returns>
    private static int CalculateProgressPercent(
        int completedChunks,
        int totalChunks
    )
    {
        if (totalChunks <= 0)
        {
            return 0;
        }

        int percent = (int)
            Math.Round(completedChunks / (double)totalChunks * 100);
        return Math.Clamp(percent, 0, 100);
    }

    /// <summary>
    /// Sanitizes a file name by stripping directory components to prevent path traversal attacks.
    /// Returns a default name if the input is null or empty.
    /// </summary>
    /// <param name="fileName">The raw file name from the transfer metadata.</param>
    /// <returns>A safe file name without directory separators.</returns>
    private static string SanitizeFileName(string? fileName)
    {
        string sanitized = Path.GetFileName(fileName ?? string.Empty);
        return string.IsNullOrWhiteSpace(sanitized)
            ? "received-file"
            : sanitized;
    }
}
