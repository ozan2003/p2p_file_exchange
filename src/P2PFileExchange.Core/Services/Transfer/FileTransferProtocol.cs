using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using P2PFileExchange.Core.Models;

namespace P2PFileExchange.Core.Services.Transfer;

/// <summary>
/// Implements the wire protocol for metadata and chunk payloads.
/// Not to be confused with the actual File Transfer Protocol (FTP).
/// </summary>
/// <remarks>
/// <para><b>Wire Protocol</b></para>
/// <para>
/// All integers are encoded as <b>little-endian 32-bit signed integers</b>.
/// A single file transfer consists of one metadata frame followed by N chunk frames.
/// </para>
///
/// <para><b>Metadata Frame</b></para>
/// <code>
///  0                   1                   2                   3
///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                          Payload Length                       |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                           JSON Payload                        |
/// +                               ...                             |
/// |                               ...                             |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// </code>
///
/// <para><b>Chunk Frame</b> (repeated <c>totalChunksNumber</c> times)</para>
/// <code>
///  0                   1                   2                   3
///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                          Chunk Index                          |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                          Data Length                          |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                          Hash Length                          |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                           Chunk Data                          |
/// +                               ...                             |
/// |                               ...                             |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                              Hash                             |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// </code>
/// </remarks>
internal static class FileTransferProtocol
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(
        JsonSerializerDefaults.Web
    );

    /// <summary>
    /// Writes a metadata frame to the stream (length-prefixed JSON).
    /// </summary>
    /// <param name="stream">The network stream to write to.</param>
    /// <param name="metadata">The file metadata to serialize.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task WriteMetadataAsync(
        Stream stream,
        FileMetadata metadata,
        CancellationToken cancellationToken
    )
    {
        byte[] jsonPayload = JsonSerializer.SerializeToUtf8Bytes(
            metadata,
            s_jsonOptions
        );
        // Write length first.
        await WriteInt32Async(stream, jsonPayload.Length, cancellationToken)
            .ConfigureAwait(false);
        await stream
            .WriteAsync(jsonPayload, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a metadata frame from the stream.
    /// </summary>
    /// <param name="stream">The network stream to read from.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The deserialized file metadata.</returns>
    /// <exception cref="InvalidDataException">Thrown when the metadata payload is malformed.</exception>
    public static async Task<FileMetadata> ReadMetadataAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        // read length then read the payload.
        int length = await ReadInt32Async(stream, cancellationToken)
            .ConfigureAwait(false);
        byte[] jsonPayload = await ReadExactAsync(
                stream,
                length,
                cancellationToken
            )
            .ConfigureAwait(false);

        // Create metadata object from JSON payload.
        FileMetadata? metadata = JsonSerializer.Deserialize<FileMetadata>(
            jsonPayload,
            s_jsonOptions
        );

        if (metadata == null)
        {
            throw new InvalidDataException("Metadata payload is invalid.");
        }

        return metadata;
    }

    /// <summary>
    /// Writes a transfer response to the stream.
    /// </summary>
    /// <param name="stream">The network stream to write to.</param>
    /// <param name="response">The transfer response.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task WriteResponseAsync(
        Stream stream,
        TransferResponse response,
        CancellationToken cancellationToken
    )
    {
        byte[] buffer = [(byte)response];
        await stream
            .WriteAsync(buffer, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a transfer response from the stream.
    /// </summary>
    /// <param name="stream">The network stream to read from.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The transfer response from the receiver.</returns>
    /// <exception cref="InvalidDataException">Thrown when the response is invalid.</exception>
    public static async Task<TransferResponse> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        byte[] buffer = await ReadExactAsync(stream, 1, cancellationToken)
            .ConfigureAwait(false);
        byte value = buffer[0];

        return value switch
        {
            (byte)TransferResponse.Accepted => TransferResponse.Accepted,
            (byte)TransferResponse.Rejected => TransferResponse.Rejected,
            _ => throw new InvalidDataException(
                $"Invalid transfer response value: {value}"
            ),
        };
    }

    /// <summary>
    /// Writes a chunk frame to the stream (index + lengths + data + hash).
    /// </summary>
    /// <param name="stream">The network stream to write to.</param>
    /// <param name="chunk">The file chunk to transmit.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
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

    /// <summary>
    /// Reads a chunk frame from the stream.
    /// </summary>
    /// <param name="stream">The network stream to read from.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The deserialized file chunk (index, data, and hash).</returns>
    public static async Task<FileChunk> ReadChunkAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        // read the index, data length, and hash length.
        int chunkIndex = await ReadInt32Async(stream, cancellationToken)
            .ConfigureAwait(false);
        int dataLength = await ReadInt32Async(stream, cancellationToken)
            .ConfigureAwait(false);
        int hashLength = await ReadInt32Async(stream, cancellationToken)
            .ConfigureAwait(false);

        // read the data and hash.
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

    /// <summary>
    /// Writes a 32-bit integer to the stream in little-endian byte order.
    /// </summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="value">The integer value to write.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    private static async Task WriteInt32Async(
        Stream stream,
        int value,
        CancellationToken cancellationToken
    )
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(sizeof(int));
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            await stream
                .WriteAsync(buffer.AsMemory(0, sizeof(int)), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads a 32-bit integer from the stream in little-endian byte order.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The integer value read from the stream.</returns>
    private static async Task<int> ReadInt32Async(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(sizeof(int));
        try
        {
            await ReadExactIntoBufferAsync(
                    stream,
                    buffer,
                    sizeof(int),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return BinaryPrimitives.ReadInt32LittleEndian(buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads exactly the specified number of bytes from the stream into a provided buffer.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="length">The exact number of bytes to read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="EndOfStreamException">Thrown when the stream ends before all bytes are read.</exception>
    private static async Task ReadExactIntoBufferAsync(
        Stream stream,
        byte[] buffer,
        int length,
        CancellationToken cancellationToken
    )
    {
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
    }

    /// <summary>
    /// Reads exactly the specified number of bytes from the stream.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="length">The exact number of bytes to read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A byte array containing the read data.</returns>
    /// <exception cref="InvalidDataException">Thrown when length is negative.</exception>
    /// <exception cref="EndOfStreamException">Thrown when the stream ends before all bytes are read.</exception>
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

        // Use uninitialized array for performance since we'll overwrite all bytes.
        byte[] buffer = GC.AllocateUninitializedArray<byte>(length);
        await ReadExactIntoBufferAsync(
                stream,
                buffer,
                length,
                cancellationToken
            )
            .ConfigureAwait(false);

        return buffer;
    }
}
