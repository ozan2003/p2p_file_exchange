using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using P2PFileTransfer.Core.Models;
using P2PFileTransfer.Core.Utilities;

namespace P2PFileTransfer.Core.Services;

/// <summary>
/// Provides TCP-based file transfer functionality.
/// </summary>
public sealed class FileTransferService : IFileTransferService
{
    private readonly SemaphoreSlim m_listenerLock = new(1, 1);
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

        await m_listenerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (m_listener != null)
            {
                return;
            }

            this.m_downloadDirectory = downloadDirectory;
            Directory.CreateDirectory(downloadDirectory);

            m_listener = new TcpListener(IPAddress.Any, port);
            m_listener.Server.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true
            );
            m_listener.Start();

            ListenerPort = ((IPEndPoint)m_listener.LocalEndpoint).Port;
            m_listenerCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            m_acceptLoopTask = AcceptLoopAsync(m_listener, m_listenerCts.Token);
        }
        finally
        {
            m_listenerLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopListenerAsync()
    {
        await m_listenerLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (m_listener == null)
            {
                return;
            }

            m_listenerCts?.Cancel();
            m_listener.Stop();

            if (m_acceptLoopTask != null)
            {
                await m_acceptLoopTask
                    .ContinueWith(_ => { })
                    .ConfigureAwait(false);
            }

            m_listenerCts?.Dispose();
            m_listener = null;
            m_listenerCts = null;
            m_acceptLoopTask = null;
            ListenerPort = 0;
        }
        finally
        {
            m_listenerLock.Release();
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
        if (peer == null)
        {
            throw new ArgumentNullException(nameof(peer));
        }

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
        int totalChunks = FileChunker.CalculateTotalChunks(
            fileInfo.Length,
            FileChunker.DefaultChunkSize
        );
        FileMetadata? metadata = new()
        {
            FileName = Path.GetFileName(filePath),
            FileSize = fileInfo.Length,
            TotalChunks = totalChunks,
            ChunkSize = FileChunker.DefaultChunkSize,
        };

        Guid transferId = Guid.NewGuid();
        TransferStarted?.Invoke(
            this,
            new TransferStartedEventArgs(
                transferId,
                TransferDirection.Send,
                metadata,
                $"{peer.IPAddress}:{peer.TcpPort}",
                filePath
            )
        );

        try
        {
            using TcpClient? client = new();
            await client
                .ConnectAsync(remoteAddress, peer.TcpPort, cancellationToken)
                .ConfigureAwait(false);
            client.NoDelay = true;

            await using NetworkStream networkStream = client.GetStream();
            await FileTransferProtocol
                .WriteMetadataAsync(networkStream, metadata, cancellationToken)
                .ConfigureAwait(false);

            await using FileStream? fileStream = new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true
            );

            int chunkIndex = 0;
            await foreach (
                FileChunk chunk in FileChunker.ReadChunksAsync(
                    fileStream,
                    metadata.ChunkSize,
                    cancellationToken
                )
            )
            {
                await FileTransferProtocol
                    .WriteChunkAsync(networkStream, chunk, cancellationToken)
                    .ConfigureAwait(false);

                chunkIndex++;
                int progressPercent = CalculateProgressPercent(
                    chunkIndex,
                    metadata.TotalChunks
                );
                progress?.Report(progressPercent);
                TransferProgressChanged?.Invoke(
                    this,
                    new TransferProgressEventArgs(transferId, progressPercent)
                );
            }

            TransferCompleted?.Invoke(
                this,
                new TransferCompletedEventArgs(
                    transferId,
                    TransferDirection.Send,
                    filePath
                )
            );
        }
        catch (OperationCanceledException)
        {
            TransferFailed?.Invoke(
                this,
                new TransferFailedEventArgs(
                    transferId,
                    TransferDirection.Send,
                    "Transfer canceled."
                )
            );
        }
        catch (Exception ex)
        {
            TransferFailed?.Invoke(
                this,
                new TransferFailedEventArgs(
                    transferId,
                    TransferDirection.Send,
                    $"Transfer failed: {ex.Message}"
                )
            );
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopListenerAsync().ConfigureAwait(false);
        m_listenerLock.Dispose();
    }

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
                _ = HandleIncomingAsync(client, cancellationToken);
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
            using TcpClient _ = client;
            await using NetworkStream networkStream = client.GetStream();
            FileMetadata metadata = await FileTransferProtocol
                .ReadMetadataAsync(networkStream, cancellationToken)
                .ConfigureAwait(false);

            metadata.FileName = SanitizeFileName(metadata.FileName);

            string remoteEndpoint =
                client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
            destinationPath = FilePathUtilities.GetUniquePath(
                Path.Combine(m_downloadDirectory, metadata.FileName)
            );

            TransferStarted?.Invoke(
                this,
                new TransferStartedEventArgs(
                    transferId,
                    TransferDirection.Receive,
                    metadata,
                    remoteEndpoint,
                    destinationPath
                )
            );

            await using FileStream? fileStream = new(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true
            );

            for (
                int expectedIndex = 0;
                expectedIndex < metadata.TotalChunks;
                expectedIndex++
            )
            {
                FileChunk chunk = await FileTransferProtocol
                    .ReadChunkAsync(networkStream, cancellationToken)
                    .ConfigureAwait(false);

                if (chunk.ChunkIndex != expectedIndex)
                {
                    throw new InvalidDataException("Chunk index mismatch.");
                }

                byte[] hash = SHA256.HashData(chunk.Data);
                if (!hash.AsSpan().SequenceEqual(chunk.Hash))
                {
                    throw new InvalidDataException("Chunk hash mismatch.");
                }

                await fileStream
                    .WriteAsync(chunk.Data, cancellationToken)
                    .ConfigureAwait(false);

                int progressPercent = CalculateProgressPercent(
                    expectedIndex + 1,
                    metadata.TotalChunks
                );
                TransferProgressChanged?.Invoke(
                    this,
                    new TransferProgressEventArgs(transferId, progressPercent)
                );
            }

            await fileStream
                .FlushAsync(cancellationToken)
                .ConfigureAwait(false);
            TransferCompleted?.Invoke(
                this,
                new TransferCompletedEventArgs(
                    transferId,
                    TransferDirection.Receive,
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
                    TransferDirection.Receive,
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
                    TransferDirection.Receive,
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

    private static string SanitizeFileName(string? fileName)
    {
        string sanitized = Path.GetFileName(fileName ?? string.Empty);
        return string.IsNullOrWhiteSpace(sanitized)
            ? "received-file"
            : sanitized;
    }
}
