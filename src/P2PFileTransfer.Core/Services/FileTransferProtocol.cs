using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using P2PFileTransfer.Core.Models;

namespace P2PFileTransfer.Core.Services;

/// <summary>
/// Implements the wire protocol for metadata and chunk payloads.
///
/// Not to be confused with the actual File Transfer Protocol (FTP).
/// </summary>
internal static class FileTransferProtocol
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(
        JsonSerializerDefaults.Web
    );

    public static async Task WriteMetadataAsync(
        Stream stream,
        FileMetadata metadata,
        CancellationToken cancellationToken
    )
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            metadata,
            s_jsonOptions
        );
        await WriteInt32Async(stream, payload.Length, cancellationToken)
            .ConfigureAwait(false);
        await stream
            .WriteAsync(payload, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<FileMetadata> ReadMetadataAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        int length = await ReadInt32Async(stream, cancellationToken)
            .ConfigureAwait(false);
        byte[] payload = await ReadExactAsync(stream, length, cancellationToken)
            .ConfigureAwait(false);
        FileMetadata? metadata = JsonSerializer.Deserialize<FileMetadata>(
            payload,
            s_jsonOptions
        );

        if (metadata == null)
        {
            throw new InvalidDataException("Metadata payload is invalid.");
        }

        return metadata;
    }

    public static async Task WriteChunkAsync(
        Stream stream,
        FileChunk chunk,
        CancellationToken cancellationToken
    )
    {
        await WriteInt32Async(stream, chunk.ChunkIndex, cancellationToken)
            .ConfigureAwait(false);
        await WriteInt32Async(stream, chunk.Data.Length, cancellationToken)
            .ConfigureAwait(false);
        await WriteInt32Async(stream, chunk.Hash.Length, cancellationToken)
            .ConfigureAwait(false);
        await stream
            .WriteAsync(chunk.Data, cancellationToken)
            .ConfigureAwait(false);
        await stream
            .WriteAsync(chunk.Hash, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<FileChunk> ReadChunkAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        int chunkIndex = await ReadInt32Async(stream, cancellationToken)
            .ConfigureAwait(false);
        int dataLength = await ReadInt32Async(stream, cancellationToken)
            .ConfigureAwait(false);
        int hashLength = await ReadInt32Async(stream, cancellationToken)
            .ConfigureAwait(false);
        byte[] data = await ReadExactAsync(
                stream,
                dataLength,
                cancellationToken
            )
            .ConfigureAwait(false);
        byte[] hash = await ReadExactAsync(
                stream,
                hashLength,
                cancellationToken
            )
            .ConfigureAwait(false);

        return new FileChunk
        {
            ChunkIndex = chunkIndex,
            Data = data,
            Hash = hash,
        };
    }

    private static async Task WriteInt32Async(
        Stream stream,
        int value,
        CancellationToken cancellationToken
    )
    {
        byte[] buffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        await stream
            .WriteAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<int> ReadInt32Async(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        byte[] buffer = await ReadExactAsync(
                stream,
                sizeof(int),
                cancellationToken
            )
            .ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static async Task<byte[]> ReadExactAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken
    )
    {
        if (length < 0)
        {
            throw new InvalidDataException("Invalid length.");
        }

        byte[] buffer = new byte[length];
        int totalRead = 0;

        while (totalRead < length)
        {
            int bytesRead = await stream
                .ReadAsync(
                    buffer.AsMemory(totalRead, length - totalRead),
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (bytesRead == 0)
            {
                throw new EndOfStreamException("Unexpected end of stream.");
            }

            totalRead += bytesRead;
        }

        return buffer;
    }
}
