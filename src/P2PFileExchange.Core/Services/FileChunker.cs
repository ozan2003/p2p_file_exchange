using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using P2PFileExchange.Core.Models;

namespace P2PFileExchange.Core.Services;

/// <summary>
/// Handles file chunking operations.
/// </summary>
internal static class FileChunker
{
    /// <summary>
    /// The default chunk size in bytes.
    /// </summary>
    public const int DefaultChunkSize = 256 * 1024; // 256 KiB

    /// <summary>
    /// Calculates the total number of chunks for a file.
    /// </summary>
    /// <param name="fileSize">The file size in bytes.</param>
    /// <param name="chunkSize">The chunk size in bytes.</param>
    public static int CalculateTotalChunkNumber(
        long fileSize,
        int chunkSize = DefaultChunkSize
    )
    {
        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        }

        return (int)Math.Ceiling(fileSize / (double)chunkSize);
    }

    /// <summary>
    /// Reads file chunks asynchronously using pooled buffers for reduced memory allocation.
    /// </summary>
    /// <param name="stream">The file stream.</param>
    /// <param name="chunkSize">The chunk size in bytes.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async IAsyncEnumerable<FileChunk> ReadChunksAsync(
        Stream stream,
        int chunkSize,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            chunkSize,
            nameof(chunkSize)
        );

        // Rent a buffer from the shared pool to reduce allocations.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        try
        {
            int chunkIndex = 0;

            while (true)
            {
                // Fill each chunk completely.
                int totalRead = 0;
                while (totalRead < chunkSize)
                {
                    int bytesRead = await stream
                        .ReadAsync(
                            buffer.AsMemory(totalRead, chunkSize - totalRead),
                            cancellationToken
                        )
                        .ConfigureAwait(false);

                    if (bytesRead == 0)
                    {
                        break; // End of stream
                    }

                    totalRead += bytesRead;
                }

                if (totalRead == 0)
                {
                    // Stream is exhausted.
                    yield break;
                }

                // Create chunk data array only with the actual size needed.
                // Note: We must copy here because the buffer is reused and pooled.
                byte[] chunkData = GC.AllocateUninitializedArray<byte>(
                    totalRead
                );
                buffer.AsSpan(0, totalRead).CopyTo(chunkData);
                byte[] hash = SHA256.HashData(chunkData);

                yield return new FileChunk
                {
                    ChunkIndex = chunkIndex,
                    Data = chunkData,
                    Hash = hash,
                };

                ++chunkIndex;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
