using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using P2PFileExchange.Core.Models;
using P2PFileExchange.Core.Models.TransferEvents;
using P2PFileExchange.Core.Services.Security;
using P2PFileExchange.Core.Utilities;

namespace P2PFileExchange.Core.Services.Transfer;

/// <summary>
/// Provides TCP-based file transfer functionality.
///
/// <list type="bullet">
/// <item>Hosts a TCP listener and negotiates TLS with a local certificate.</item>
/// <item>Sends files by connecting to a peer, performing TLS, sending metadata, then streaming chunks.</item>
/// <item>Receives files by authenticating TLS, awaiting user approval, then writing chunks to disk.</item>
/// <item>Validates chunk order and SHA-256 hashes, sanitizes file names, and cleans up partial files on failure.</item>
/// <item>Supports certificate pinning via fingerprint lookups from peer discovery.</item>
/// </list>
/// </summary>
public sealed class FileTransferService : IFileTransferService
{
    #region Configuration
    /// <summary>Transfer configuration options.</summary>
    private readonly FileTransferOptions m_options;
    #endregion Configuration

    #region Synchronization
    /// <summary>Lock for TCP listener.</summary>
    private readonly SemaphoreSlim m_listenerLock = new(1, 1);
    #endregion Synchronization

    #region Pending Requests
    /// <summary>Pending transfer requests awaiting user response.</summary>
    private readonly ConcurrentDictionary<
        Guid,
        TaskCompletionSource<TransferResponse>
    > m_pendingRequests = new();
    #endregion Pending Requests

    #region Listener State
    /// <summary>The TCP listener for file transfers.</summary>
    private TcpListener? m_listener;

    /// <summary>Cancellation source for listener loop.</summary>
    private CancellationTokenSource? m_listenerCts;

    /// <summary>Task running the accept loop.</summary>
    private Task? m_acceptLoopTask;
    #endregion Listener State

    #region Transfer Settings
    /// <summary>Download directory for inbound files.</summary>
    private string m_downloadDirectory = string.Empty;

    /// <summary>Local Ed25519 identity key manager for SecureP2PStream.</summary>
    private IdentityKeyManager? m_identityKeyManager;

    /// <summary>Lookup for known peer info by IP address for TOFU verification.</summary>
    private Func<IPAddress, PeerInfo?>? m_peerLookup;

    /// <summary>Lookup for peer display names.</summary>
    private Func<IPAddress, string?>? m_displayNameLookup;
    #endregion Transfer Settings

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
        IdentityKeyManager identityKeyManager,
        Func<IPAddress, PeerInfo?> peerLookup,
        CancellationToken cancellationToken
    )
    {
        await this.StartListenerAsync(
                port,
                downloadDirectory,
                identityKeyManager,
                peerLookup,
                displayNameLookup: null,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the TCP listener for inbound transfers with secure P2P transport.
    /// </summary>
    /// <param name="port">The port to listen on. Use 0 for a dynamic port.</param>
    /// <param name="downloadDirectory">The directory where files are saved.</param>
    /// <param name="identityKeyManager">The local Ed25519 identity key manager (must be loaded).</param>
    /// <param name="peerLookup">
    /// A function to look up known peer info by IP address for TOFU verification.
    /// Returns null if the peer is unknown (first contact).
    /// </param>
    /// <param name="displayNameLookup">
    /// A function to look up peer display names by IP address.
    /// Returns null if the peer is unknown.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task StartListenerAsync(
        ushort port,
        string downloadDirectory,
        IdentityKeyManager identityKeyManager,
        Func<IPAddress, PeerInfo?> peerLookup,
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

        ArgumentNullException.ThrowIfNull(
            identityKeyManager,
            nameof(identityKeyManager)
        );
        if (!identityKeyManager.IsLoaded)
        {
            throw new InvalidOperationException(
                "Identity key must be loaded before starting the listener."
            );
        }
        ArgumentNullException.ThrowIfNull(peerLookup, nameof(peerLookup));

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
            this.m_identityKeyManager = identityKeyManager;
            this.m_peerLookup = peerLookup;
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
            foreach (
                TaskCompletionSource<TransferResponse> tcs in this.m_pendingRequests.Values
            )
            {
                tcs.TrySetCanceled();
            }

            this.m_pendingRequests.Clear();

            this.m_listenerCts?.Dispose();
            this.m_listener = null;
            this.m_listenerCts = null;
            this.m_acceptLoopTask = null;
            this.m_identityKeyManager = null;
            this.m_peerLookup = null;
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
            // Ensure identity key is loaded
            if (
                this.m_identityKeyManager is null
                || !this.m_identityKeyManager.IsLoaded
            )
            {
                throw new InvalidOperationException(
                    "Identity key must be loaded before sending files."
                );
            }

            // Connect to the peer via TCP.
            using TcpClient client = new();
            await client
                .ConnectAsync(peer.IPAddress, peer.TcpPort, cancellationToken)
                .ConfigureAwait(false);
            client.NoDelay = true;

            await using NetworkStream networkStream = client.GetStream();

            // Create secure P2P stream with X25519 key exchange and ChaCha20-Poly1305 encryption.
            await using SecureP2PStream secureStream = new(
                networkStream,
                this.m_identityKeyManager,
                leaveOpen: false
            );

            // Perform handshake as initiator (client).
            // Pass peer info for TOFU verification.
            using CancellationTokenSource handshakeTimeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
            handshakeTimeoutCts.CancelAfter(this.m_options.TlsHandshakeTimeout);

            await secureStream
                .HandshakeAsync(
                    peer,
                    isInitiator: true,
                    handshakeTimeoutCts.Token
                )
                .ConfigureAwait(false);

            // Send metadata to the peer over the secure stream.
            await FileTransferProtocol
                .WriteMetadataAsync(secureStream, metadata, cancellationToken)
                .ConfigureAwait(false);

            // Transfer response is prompted when the peer receives metadata.
            TransferResponse response = await FileTransferProtocol
                .ReadResponseAsync(secureStream, cancellationToken)
                .ConfigureAwait(false);

            // If the peer rejected the transfer, notify and exit.
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
                // Send each chunk to the peer.
                await FileTransferProtocol
                    .WriteChunkAsync(secureStream, chunk, cancellationToken)
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
        catch (SecureP2PException ex)
        {
            string errorMessage = ex.ErrorCode switch
            {
                SecureP2PErrorCode.IdentityMismatch =>
                    $"Identity verification failed for {peer.DisplayName}: {ex.Message}",
                SecureP2PErrorCode.AuthenticationFailed =>
                    $"Authentication failed with {peer.DisplayName}: {ex.Message}",
                SecureP2PErrorCode.HandshakeTimeout =>
                    $"Secure connection timed out with {peer.DisplayName}.",
                _ =>
                    $"Secure connection failed with {peer.DisplayName}: {ex.Message}",
            };
            TransferFailed?.Invoke(
                this,
                new TransferFailedEventArgs(
                    transferId,
                    TransferMode.Send,
                    errorMessage
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
    /// Handles an incoming file transfer connection with secure P2P transport.
    /// Reads metadata and chunks from the encrypted stream, verifies chunk integrity
    /// via SHA256, and writes data to disk. Deletes partial files on failure or cancellation.
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
            // Ensure identity key is loaded
            if (
                this.m_identityKeyManager is null
                || !this.m_identityKeyManager.IsLoaded
            )
            {
                throw new InvalidOperationException(
                    "Identity key must be loaded to accept incoming transfers."
                );
            }

            using TcpClient tcpClient = client;

            // Extract remote IP for peer lookup.
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

            // Create secure P2P stream with X25519 key exchange and ChaCha20-Poly1305 encryption.
            await using SecureP2PStream secureStream = new(
                networkStream,
                this.m_identityKeyManager,
                leaveOpen: false
            );

            // Look up expected peer info for TOFU verification.
            PeerInfo? expectedPeer = this.m_peerLookup?.Invoke(remoteIpAddress);

            // Perform handshake as responder (server).
            using CancellationTokenSource handshakeTimeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
            handshakeTimeoutCts.CancelAfter(this.m_options.TlsHandshakeTimeout);

            await secureStream
                .HandshakeAsync(
                    expectedPeer,
                    isInitiator: false,
                    handshakeTimeoutCts.Token
                )
                .ConfigureAwait(false);

            FileMetadata metadata = await FileTransferProtocol
                .ReadMetadataAsync(secureStream, cancellationToken)
                .ConfigureAwait(false);

            metadata.FileName = SanitizeFileName(metadata.FileName);

            // Transfer request handling
            // Name lookup for UI display
            string? senderDisplayName = this.m_displayNameLookup?.Invoke(
                remoteIpAddress
            );

            // Raise the transfer request event and wait for user decision.
            Guid requestId = transferId;
            TaskCompletionSource<TransferResponse> responseTcs = new();
            this.m_pendingRequests[requestId] = responseTcs;

            // 1. Notify the UI of the incoming transfer request.
            TransferRequestReceived?.Invoke(
                this,
                new TransferRequestEventArgs(
                    requestId,
                    metadata,
                    remoteEndpoint,
                    senderDisplayName
                )
            );

            // 2. Wait for user response with timeout.
            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
            timeoutCts.CancelAfter(this.m_options.TransferRequestTimeout);

            // 3. Get the user's response or timeout.
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

            // 4. Send the response back to the sender.
            await FileTransferProtocol
                .WriteResponseAsync(
                    secureStream,
                    userResponse,
                    cancellationToken
                )
                .ConfigureAwait(false);

            // 5. If rejected, exit early.
            // Else, proceed to receive the file.
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
                    .ReadChunkAsync(secureStream, cancellationToken)
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
        catch (SecureP2PException ex)
        {
            shouldDeleteFile = true;
            string errorMessage = ex.ErrorCode switch
            {
                SecureP2PErrorCode.IdentityMismatch =>
                    $"Identity verification failed: {ex.Message}",
                SecureP2PErrorCode.AuthenticationFailed =>
                    $"Authentication failed: {ex.Message}",
                SecureP2PErrorCode.HandshakeTimeout =>
                    "Secure connection timed out.",
                SecureP2PErrorCode.TamperingDetected =>
                    $"Data tampering detected: {ex.Message}",
                SecureP2PErrorCode.ReplayDetected =>
                    $"Replay attack detected: {ex.Message}",
                _ => $"Secure connection failed: {ex.Message}",
            };
            TransferFailed?.Invoke(
                this,
                new TransferFailedEventArgs(
                    transferId,
                    TransferMode.Receive,
                    errorMessage
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
