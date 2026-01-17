using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using P2PFileTransfer.Core.Models;

namespace P2PFileTransfer.Core.Services;

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
    /// Reads file chunks asynchronously.
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
        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        }

        byte[] buffer = new byte[chunkSize]; // Repeteadly fill the chunks to here.
        int chunkIndex = 0;

        while (true)
        {
            // Fill each chunks completely.
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

            byte[] chunkData = buffer.AsSpan(0, totalRead).ToArray();
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
}
