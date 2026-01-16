using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using P2PFileTransfer.Core.Models;
using P2PFileTransfer.Core.Utilities;

namespace P2PFileTransfer.Core.Services;

/// <summary>
/// Provides TCP-based file transfer functionality.
/// </summary>
public sealed class FileTransferService : IFileTransferService
{
    private const int DefaultBufferSize = 80 * 1024; // 80 KiB

    /// <summary>
    /// Lock for TCP listener.
    /// </summary>
    private readonly SemaphoreSlim m_listenerLock = new(1, 1);

    /// <summary>
    /// The TCP listener for file transfers.
    /// </summary>
    private TcpListener? m_listener;
    private CancellationTokenSource? m_listenerCts;
    private Task? m_acceptLoopTask;
    private string m_downloadDirectory = string.Empty;

    /// <inheritdoc />
    public event EventHandler<TransferStartedEventArgs>? TransferStarted;

    /// <inheritdoc />
    public event EventHandler<TransferProgressEventArgs>? TransferProgressChanged;

    /// <inheritdoc />
    public event EventHandler<TransferCompletedEventArgs>? TransferCompleted;

    /// <inheritdoc />
    public event EventHandler<TransferFailedEventArgs>? TransferFailed;

    /// <inheritdoc />
    public int ListenerPort { get; private set; }

    /// <inheritdoc />
    public async Task StartListenerAsync(
        int port,
        string downloadDirectory,
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
            Directory.CreateDirectory(downloadDirectory);

            this.m_listener = new TcpListener(IPAddress.Any, port);
            this.m_listener.Server.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true
            );
            this.m_listener.Start();

            this.ListenerPort = (
                (IPEndPoint)this.m_listener.LocalEndpoint
            ).Port;

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

            this.m_listenerCts?.Dispose();
            this.m_listener = null;
            this.m_listenerCts = null;
            this.m_acceptLoopTask = null;
            this.ListenerPort = 0;
        }
        finally
        {
            this.m_listenerLock.Release();
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

        if (!IPAddress.TryParse(peer.IPAddress, out IPAddress? remoteAddress))
        {
            throw new InvalidOperationException("Peer IP address is invalid.");
        }

        FileInfo fileInfo = new(filePath);
        int totalChunks = FileChunker.CalculateTotalChunkNumber(
            fileInfo.Length
        );
        FileMetadata metadata = new()
        {
            FileName = Path.GetFileName(filePath),
            FileSize = fileInfo.Length,
            TotalChunksNumber = totalChunks,
            ChunkSize = FileChunker.DefaultChunkSize,
        };

        // Create a unique transfer ID and notify the UI that the transfer has started.
        Guid transferId = Guid.NewGuid();
        TransferStarted?.Invoke(
            this,
            new TransferStartedEventArgs(
                transferId,
                TransferMode.Send,
                metadata,
                $"{peer.IPAddress}:{peer.TcpPort}",
                filePath
            )
        );

        try
        {
            // Connect to the peer via TCP.
            using TcpClient client = new();
            await client
                .ConnectAsync(remoteAddress, peer.TcpPort, cancellationToken)
                .ConfigureAwait(false);
            client.NoDelay = true;

            // Send metadata to the peer.
            await using NetworkStream networkStream = client.GetStream();
            await FileTransferProtocol
                .WriteMetadataAsync(networkStream, metadata, cancellationToken)
                .ConfigureAwait(false);

            // Read the file chunks and send them to the peer.
            await using FileStream fileStream = new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: DefaultBufferSize,
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
                    .WriteChunkAsync(networkStream, chunk, cancellationToken)
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
                    new TransferProgressEventArgs(transferId, progressPercent)
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
    /// Handles an incoming file transfer connection. Reads metadata and chunks from the
    /// network stream, verifies chunk integrity via SHA256, and writes data to disk.
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

        try
        {
            // Set up the network stream for the incoming connection.
            using TcpClient _ = client;
            await using NetworkStream networkStream = client.GetStream();
            FileMetadata metadata = await FileTransferProtocol
                .ReadMetadataAsync(networkStream, cancellationToken)
                .ConfigureAwait(false);

            metadata.FileName = SanitizeFileName(metadata.FileName);

            string remoteEndpoint =
                client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
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
                bufferSize: DefaultBufferSize,
                useAsync: true
            );

            for (
                int expectedIndex = 0;
                expectedIndex < metadata.TotalChunksNumber;
                ++expectedIndex
            )
            {
                FileChunk chunk = await FileTransferProtocol
                    .ReadChunkAsync(networkStream, cancellationToken)
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
                    new TransferProgressEventArgs(transferId, progressPercent)
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
