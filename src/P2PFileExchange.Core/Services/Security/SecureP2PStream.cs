using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using P2PFileExchange.Core.Models;
using Sodium;

namespace P2PFileExchange.Core.Services.Security;

/// <summary>
/// Provides an encrypted stream wrapper using X25519 key exchange, Ed25519 authentication,
/// and ChaCha20-Poly1305 for symmetric encryption. Replaces TLS with a custom protocol
/// that supports TOFU (Trust-On-First-Use) identity verification.
/// </summary>
/// <remarks>
/// Protocol:
/// 1. Ephemeral X25519 key exchange
/// 2. HKDF session key derivation (separate TX/RX keys)
/// 3. Ed25519 mutual authentication with identity keys
/// 4. ChaCha20-Poly1305 frame encryption with replay protection
/// </remarks>
public sealed class SecureP2PStream : Stream
{
    #region Constants

    /// <summary>
    /// X25519 public key length in bytes.
    /// </summary>
    private const int X25519PublicKeyLength = 32;

    /// <summary>
    /// Ed25519 signature length in bytes.
    /// </summary>
    private const int Ed25519SignatureLength = 64;

    /// <summary>
    /// ChaCha20-Poly1305 key length in bytes.
    /// </summary>
    private const int SessionKeyLength = 32;

    /// <summary>
    /// ChaCha20-Poly1305 nonce length in bytes.
    /// </summary>
    private const int NonceLength = 12;

    /// <summary>
    /// ChaCha20-Poly1305 authentication tag length in bytes.
    /// </summary>
    private const int TagLength = 16;

    /// <summary>
    /// Frame number length in bytes (uint64 big-endian).
    /// </summary>
    private const int FrameNumberLength = 8;

    /// <summary>
    /// Payload length field size in bytes (uint16 big-endian).
    /// </summary>
    private const int PayloadLengthFieldSize = 2;

    /// <summary>
    /// Maximum plaintext payload size per frame (16 KB).
    /// </summary>
    private const int MaxPayloadSize = 16384;

    /// <summary>
    /// Frame header size: frame number (8) + payload length (2).
    /// </summary>
    private const int FrameHeaderSize =
        FrameNumberLength + PayloadLengthFieldSize;

    /// <summary>
    /// HKDF info string for session key derivation.
    /// </summary>
    private const string HkdfInfo = "P2PFileTransfer-v1-session";

    /// <summary>
    /// Default handshake timeout.
    /// </summary>
    private static readonly TimeSpan DefaultHandshakeTimeout =
        TimeSpan.FromSeconds(10);

    #endregion Constants

    #region Fields

    private readonly NetworkStream m_baseStream;
    private readonly IdentityKeyManager m_localIdentity;
    private readonly bool m_leaveOpen;

    private byte[]? m_txKey;
    private byte[]? m_rxKey;
    private ulong m_txFrameNumber;
    private ulong m_rxExpectedFrameNumber;

    private byte[]? m_remoteIdentityPublicKey;
    private bool m_isHandshakeComplete;
    private bool m_disposed;

    // Read buffering for partial frame reads
    private byte[]? m_readBuffer;
    private int m_readBufferOffset;
    private int m_readBufferCount;

    #endregion Fields

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="SecureP2PStream"/> class.
    /// </summary>
    /// <param name="baseStream">The underlying network stream.</param>
    /// <param name="localIdentity">The local identity key manager (must be loaded).</param>
    /// <param name="leaveOpen">Whether to leave the base stream open when disposing.</param>
    /// <exception cref="ArgumentNullException">Thrown if arguments are null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if identity key is not loaded.</exception>
    public SecureP2PStream(
        NetworkStream baseStream,
        IdentityKeyManager localIdentity,
        bool leaveOpen = false
    )
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        ArgumentNullException.ThrowIfNull(localIdentity);

        if (!localIdentity.IsLoaded)
        {
            throw new InvalidOperationException(
                "Identity key must be loaded before creating secure stream."
            );
        }

        this.m_baseStream = baseStream;
        this.m_localIdentity = localIdentity;
        this.m_leaveOpen = leaveOpen;
    }

    #endregion Constructor

    #region Properties

    /// <summary>
    /// Gets the remote peer's Ed25519 identity public key after successful handshake.
    /// </summary>
    public byte[]? RemoteIdentityPublicKey => this.m_remoteIdentityPublicKey;

    /// <summary>
    /// Gets the remote peer's identity fingerprint after successful handshake.
    /// </summary>
    public string? RemoteFingerprint =>
        this.m_remoteIdentityPublicKey is not null
            ? IdentityKeyManager.ComputeFingerprint(
                this.m_remoteIdentityPublicKey
            )
            : null;

    /// <summary>
    /// Gets the remote peer's ID derived from their identity public key.
    /// </summary>
    public Guid? RemotePeerId =>
        this.m_remoteIdentityPublicKey is not null
            ? IdentityKeyManager.ComputePeerId(this.m_remoteIdentityPublicKey)
            : null;

    /// <summary>
    /// Gets whether the handshake has been completed.
    /// </summary>
    public bool IsHandshakeComplete => this.m_isHandshakeComplete;

    /// <inheritdoc />
    public override bool CanRead =>
        this.m_isHandshakeComplete && this.m_baseStream.CanRead;

    /// <inheritdoc />
    public override bool CanWrite =>
        this.m_isHandshakeComplete && this.m_baseStream.CanWrite;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    #endregion Properties

    #region Handshake

    /// <summary>
    /// Performs the secure handshake with the remote peer.
    /// </summary>
    /// <param name="expectedRemotePeer">
    /// The expected remote peer info for TOFU verification. If null, any peer is accepted (first contact).
    /// </param>
    /// <param name="isInitiator">Whether this side initiated the connection (client=true, server=false).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the handshake operation.</returns>
    /// <exception cref="SecureP2PException">Thrown if handshake fails.</exception>
    public async Task HandshakeAsync(
        PeerInfo? expectedRemotePeer,
        bool isInitiator,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        if (this.m_isHandshakeComplete)
        {
            throw new InvalidOperationException("Handshake already completed.");
        }

        using CancellationTokenSource timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DefaultHandshakeTimeout);
        CancellationToken token = timeoutCts.Token;

        try
        {
            // Step 1: Generate ephemeral X25519 keypair using Curve25519
            KeyPair ephemeralKeyPair = PublicKeyBox.GenerateKeyPair();
            byte[] myEphemeralPublic = ephemeralKeyPair.PublicKey;
            byte[] myEphemeralPrivate = ephemeralKeyPair.PrivateKey;

            byte[] theirEphemeralPublic;

            try
            {
                // Step 2: Exchange ephemeral public keys
                if (isInitiator)
                {
                    // Initiator sends first, then receives
                    await this.WriteRawAsync(myEphemeralPublic, token)
                        .ConfigureAwait(false);
                    theirEphemeralPublic = await this.ReadRawAsync(
                            X25519PublicKeyLength,
                            token
                        )
                        .ConfigureAwait(false);
                }
                else
                {
                    // Responder receives first, then sends
                    theirEphemeralPublic = await this.ReadRawAsync(
                            X25519PublicKeyLength,
                            token
                        )
                        .ConfigureAwait(false);
                    await this.WriteRawAsync(myEphemeralPublic, token)
                        .ConfigureAwait(false);
                }

                // Step 3: Compute shared secret using X25519
                byte[] sharedSecret = ScalarMult.Mult(
                    myEphemeralPrivate,
                    theirEphemeralPublic
                );

                // Step 4: Derive session keys using HKDF
                // Salt: concatenation of both ephemeral public keys (initiator's first for consistency)
                byte[] salt = new byte[X25519PublicKeyLength * 2];
                if (isInitiator)
                {
                    Buffer.BlockCopy(
                        myEphemeralPublic,
                        0,
                        salt,
                        0,
                        X25519PublicKeyLength
                    );
                    Buffer.BlockCopy(
                        theirEphemeralPublic,
                        0,
                        salt,
                        X25519PublicKeyLength,
                        X25519PublicKeyLength
                    );
                }
                else
                {
                    Buffer.BlockCopy(
                        theirEphemeralPublic,
                        0,
                        salt,
                        0,
                        X25519PublicKeyLength
                    );
                    Buffer.BlockCopy(
                        myEphemeralPublic,
                        0,
                        salt,
                        X25519PublicKeyLength,
                        X25519PublicKeyLength
                    );
                }

                // Derive TX and RX keys (64 bytes total, split into two 32-byte keys)
                byte[] derivedKeys = DeriveSessionKeys(sharedSecret, salt);

                // Assign keys based on role (ensures each direction uses different key)
                if (isInitiator)
                {
                    this.m_txKey = derivedKeys[..SessionKeyLength];
                    this.m_rxKey = derivedKeys[SessionKeyLength..];
                }
                else
                {
                    // Responder uses reversed assignment
                    this.m_rxKey = derivedKeys[..SessionKeyLength];
                    this.m_txKey = derivedKeys[SessionKeyLength..];
                }

                // Step 5: Mutual authentication with Ed25519 signatures
                // Auth data: their_ephemeral_public || my_ephemeral_public
                byte[] authData = new byte[X25519PublicKeyLength * 2];
                Buffer.BlockCopy(
                    theirEphemeralPublic,
                    0,
                    authData,
                    0,
                    X25519PublicKeyLength
                );
                Buffer.BlockCopy(
                    myEphemeralPublic,
                    0,
                    authData,
                    X25519PublicKeyLength,
                    X25519PublicKeyLength
                );

                // Sign the auth data with our identity key
                byte[] mySignature = this.m_localIdentity.Sign(authData);
                byte[] myIdentityPublic = this.m_localIdentity.PublicKey;

                // Prepare auth message: [identity_public (32)][signature (64)]
                byte[] myAuthMessage = new byte[
                    IdentityKeyManager.PublicKeyLength + Ed25519SignatureLength
                ];
                Buffer.BlockCopy(
                    myIdentityPublic,
                    0,
                    myAuthMessage,
                    0,
                    IdentityKeyManager.PublicKeyLength
                );
                Buffer.BlockCopy(
                    mySignature,
                    0,
                    myAuthMessage,
                    IdentityKeyManager.PublicKeyLength,
                    Ed25519SignatureLength
                );

                byte[] theirAuthMessage;

                // Exchange auth messages
                if (isInitiator)
                {
                    await this.WriteRawAsync(myAuthMessage, token)
                        .ConfigureAwait(false);
                    theirAuthMessage = await this.ReadRawAsync(
                            IdentityKeyManager.PublicKeyLength
                                + Ed25519SignatureLength,
                            token
                        )
                        .ConfigureAwait(false);
                }
                else
                {
                    theirAuthMessage = await this.ReadRawAsync(
                            IdentityKeyManager.PublicKeyLength
                                + Ed25519SignatureLength,
                            token
                        )
                        .ConfigureAwait(false);
                    await this.WriteRawAsync(myAuthMessage, token)
                        .ConfigureAwait(false);
                }

                // Extract their identity public key and signature
                byte[] theirIdentityPublic = theirAuthMessage[
                    ..IdentityKeyManager.PublicKeyLength
                ];
                byte[] theirSignature = theirAuthMessage[
                    IdentityKeyManager.PublicKeyLength..
                ];

                // Verify their signature
                // Their auth data is reversed: my_ephemeral_public || their_ephemeral_public
                byte[] theirAuthData = new byte[X25519PublicKeyLength * 2];
                Buffer.BlockCopy(
                    myEphemeralPublic,
                    0,
                    theirAuthData,
                    0,
                    X25519PublicKeyLength
                );
                Buffer.BlockCopy(
                    theirEphemeralPublic,
                    0,
                    theirAuthData,
                    X25519PublicKeyLength,
                    X25519PublicKeyLength
                );

                if (
                    !IdentityKeyManager.Verify(
                        theirAuthData,
                        theirSignature,
                        theirIdentityPublic
                    )
                )
                {
                    throw new SecureP2PException(
                        SecureP2PErrorCode.AuthenticationFailed,
                        "Remote peer signature verification failed."
                    );
                }

                // Step 6: TOFU verification
                if (
                    expectedRemotePeer is not null
                    && !string.IsNullOrEmpty(
                        expectedRemotePeer.IdentityPublicKey
                    )
                )
                {
                    byte[] expectedPublicKey = Convert.FromBase64String(
                        expectedRemotePeer.IdentityPublicKey
                    );
                    if (
                        !CryptographicOperations.FixedTimeEquals(
                            theirIdentityPublic,
                            expectedPublicKey
                        )
                    )
                    {
                        string expectedFingerprint =
                            IdentityKeyManager.ComputeFingerprint(
                                expectedPublicKey
                            );
                        string actualFingerprint =
                            IdentityKeyManager.ComputeFingerprint(
                                theirIdentityPublic
                            );
                        throw new SecureP2PException(
                            SecureP2PErrorCode.IdentityMismatch,
                            $"TOFU violation: expected identity {expectedFingerprint}, got {actualFingerprint}. "
                                + "Possible impersonation attack."
                        );
                    }
                }

                this.m_remoteIdentityPublicKey = theirIdentityPublic;
                this.m_isHandshakeComplete = true;
            }
            finally
            {
                // Clear ephemeral private key from memory
                CryptographicOperations.ZeroMemory(myEphemeralPrivate);
            }
        }
        catch (OperationCanceledException)
            when (timeoutCts.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
            )
        {
            throw new SecureP2PException(
                SecureP2PErrorCode.HandshakeTimeout,
                "Handshake timed out."
            );
        }
        catch (SecureP2PException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SecureP2PException(
                SecureP2PErrorCode.HandshakeFailed,
                $"Handshake failed: {ex.Message}",
                ex
            );
        }
    }

    /// <summary>
    /// Derives session keys from the shared secret using HKDF-SHA256.
    /// </summary>
    private static byte[] DeriveSessionKeys(byte[] sharedSecret, byte[] salt)
    {
        byte[] info = System.Text.Encoding.UTF8.GetBytes(HkdfInfo);
        byte[] derivedKeys = new byte[SessionKeyLength * 2]; // TX + RX keys

        // Use HKDF to derive the session keys
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            derivedKeys,
            salt,
            info
        );

        return derivedKeys;
    }

    #endregion Handshake

    #region Stream Operations

    /// <inheritdoc />
    public override void Flush()
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotHandshaked();
        this.m_baseStream.Flush();
    }

    /// <inheritdoc />
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotHandshaked();
        await this
            .m_baseStream.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        return this.ReadAsync(buffer, offset, count, CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    /// <inheritdoc />
    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotHandshaked();

        if (count == 0)
        {
            return 0;
        }

        // If we have buffered data from a previous partial read, return from buffer first
        if (this.m_readBufferCount > 0)
        {
            int toCopy = Math.Min(count, this.m_readBufferCount);
            Buffer.BlockCopy(
                this.m_readBuffer!,
                this.m_readBufferOffset,
                buffer,
                offset,
                toCopy
            );
            this.m_readBufferOffset += toCopy;
            this.m_readBufferCount -= toCopy;
            return toCopy;
        }

        // Read and decrypt the next frame
        byte[]? plaintext = await this.ReadFrameAsync(cancellationToken)
            .ConfigureAwait(false);
        if (plaintext is null || plaintext.Length == 0)
        {
            return 0; // End of stream
        }

        // If the decrypted data fits in the requested buffer, copy directly
        if (plaintext.Length <= count)
        {
            Buffer.BlockCopy(plaintext, 0, buffer, offset, plaintext.Length);
            return plaintext.Length;
        }

        // Otherwise, buffer the excess for subsequent reads
        Buffer.BlockCopy(plaintext, 0, buffer, offset, count);
        this.m_readBuffer = plaintext;
        this.m_readBufferOffset = count;
        this.m_readBufferCount = plaintext.Length - count;
        return count;
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        this.WriteAsync(buffer, offset, count, CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    /// <inheritdoc />
    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotHandshaked();

        if (count == 0)
        {
            return;
        }

        // Split data into frames and encrypt each
        int remaining = count;
        int currentOffset = offset;

        while (remaining > 0)
        {
            int chunkSize = Math.Min(remaining, MaxPayloadSize);
            await this.WriteFrameAsync(
                    buffer.AsMemory(currentOffset, chunkSize),
                    cancellationToken
                )
                .ConfigureAwait(false);

            currentOffset += chunkSize;
            remaining -= chunkSize;
        }
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException();

    #endregion Stream Operations

    #region Frame Encryption/Decryption

    /// <summary>
    /// Writes an encrypted frame to the stream.
    /// </summary>
    private async Task WriteFrameAsync(
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken
    )
    {
        if (plaintext.Length > MaxPayloadSize)
        {
            throw new ArgumentException(
                $"Payload exceeds maximum size of {MaxPayloadSize} bytes."
            );
        }

        // Build nonce: frame_number (8 bytes) + 4 zero bytes = 12 bytes total
        byte[] nonce = new byte[NonceLength];
        BinaryPrimitives.WriteUInt64BigEndian(
            nonce.AsSpan(0, 8),
            this.m_txFrameNumber
        );

        // Additional authenticated data: just the frame number
        byte[] aad = new byte[FrameNumberLength];
        BinaryPrimitives.WriteUInt64BigEndian(aad, this.m_txFrameNumber);

        // Encrypt using ChaCha20-Poly1305
        byte[] ciphertext = SecretAeadChaCha20Poly1305.Encrypt(
            plaintext.ToArray(),
            nonce,
            this.m_txKey!,
            aad
        );

        // Build frame: [frame_number (8)][length (2)][ciphertext+tag]
        int frameSize = FrameHeaderSize + ciphertext.Length;
        byte[] frame = new byte[frameSize];

        BinaryPrimitives.WriteUInt64BigEndian(
            frame.AsSpan(0, 8),
            this.m_txFrameNumber
        );
        BinaryPrimitives.WriteUInt16BigEndian(
            frame.AsSpan(8, 2),
            (ushort)plaintext.Length
        );
        Buffer.BlockCopy(
            ciphertext,
            0,
            frame,
            FrameHeaderSize,
            ciphertext.Length
        );

        await this
            .m_baseStream.WriteAsync(frame, cancellationToken)
            .ConfigureAwait(false);

        this.m_txFrameNumber++;
    }

    /// <summary>
    /// Reads and decrypts a frame from the stream.
    /// </summary>
    /// <returns>The decrypted plaintext, or null/empty if stream ended.</returns>
    private async Task<byte[]?> ReadFrameAsync(
        CancellationToken cancellationToken
    )
    {
        // Read frame header: [frame_number (8)][length (2)]
        byte[] header = new byte[FrameHeaderSize];
        int headerRead = await this.ReadExactAsync(
                header,
                0,
                FrameHeaderSize,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null; // End of stream
        }
        if (headerRead < FrameHeaderSize)
        {
            throw new SecureP2PException(
                SecureP2PErrorCode.ProtocolViolation,
                "Incomplete frame header received."
            );
        }

        ulong frameNumber = BinaryPrimitives.ReadUInt64BigEndian(
            header.AsSpan(0, 8)
        );
        ushort payloadLength = BinaryPrimitives.ReadUInt16BigEndian(
            header.AsSpan(8, 2)
        );

        // Validate frame number for replay protection
        if (frameNumber != this.m_rxExpectedFrameNumber)
        {
            throw new SecureP2PException(
                SecureP2PErrorCode.ReplayDetected,
                $"Out-of-order frame: expected {this.m_rxExpectedFrameNumber}, got {frameNumber}. "
                    + "Possible replay attack."
            );
        }

        if (payloadLength > MaxPayloadSize)
        {
            throw new SecureP2PException(
                SecureP2PErrorCode.ProtocolViolation,
                $"Payload length {payloadLength} exceeds maximum of {MaxPayloadSize}."
            );
        }

        // Read ciphertext + tag
        int ciphertextLength = payloadLength + TagLength;
        byte[] ciphertext = new byte[ciphertextLength];
        int ciphertextRead = await this.ReadExactAsync(
                ciphertext,
                0,
                ciphertextLength,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (ciphertextRead < ciphertextLength)
        {
            throw new SecureP2PException(
                SecureP2PErrorCode.ProtocolViolation,
                "Incomplete ciphertext received."
            );
        }

        // Build nonce: frame_number (8 bytes) + 4 zero bytes = 12 bytes total
        byte[] nonce = new byte[NonceLength];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(0, 8), frameNumber);

        // Additional authenticated data: just the frame number
        byte[] aad = new byte[FrameNumberLength];
        BinaryPrimitives.WriteUInt64BigEndian(aad, frameNumber);

        // Decrypt using ChaCha20-Poly1305
        byte[] plaintext;
        try
        {
            plaintext = SecretAeadChaCha20Poly1305.Decrypt(
                ciphertext,
                nonce,
                this.m_rxKey!,
                aad
            );
        }
        catch (CryptographicException ex)
        {
            throw new SecureP2PException(
                SecureP2PErrorCode.TamperingDetected,
                "Frame authentication failed. Data may have been tampered with.",
                ex
            );
        }

        this.m_rxExpectedFrameNumber++;
        return plaintext;
    }

    #endregion Frame Encryption/Decryption

    #region Raw I/O Helpers

    /// <summary>
    /// Writes raw bytes to the base stream (used during handshake).
    /// </summary>
    private async Task WriteRawAsync(
        byte[] data,
        CancellationToken cancellationToken
    )
    {
        await this
            .m_baseStream.WriteAsync(data, cancellationToken)
            .ConfigureAwait(false);
        await this
            .m_baseStream.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads exactly the specified number of bytes from the base stream (used during handshake).
    /// </summary>
    private async Task<byte[]> ReadRawAsync(
        int length,
        CancellationToken cancellationToken
    )
    {
        byte[] buffer = new byte[length];
        int totalRead = 0;
        while (totalRead < length)
        {
            int read = await this
                .m_baseStream.ReadAsync(
                    buffer.AsMemory(totalRead, length - totalRead),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new SecureP2PException(
                    SecureP2PErrorCode.HandshakeFailed,
                    "Connection closed during handshake."
                );
            }
            totalRead += read;
        }
        return buffer;
    }

    /// <summary>
    /// Reads exactly the specified number of bytes, handling partial reads.
    /// </summary>
    /// <returns>The number of bytes read (0 means end of stream, less than count means incomplete).</returns>
    private async Task<int> ReadExactAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await this
                .m_baseStream.ReadAsync(
                    buffer.AsMemory(offset + totalRead, count - totalRead),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (read == 0)
            {
                return totalRead; // End of stream
            }
            totalRead += read;
        }
        return totalRead;
    }

    #endregion Raw I/O Helpers

    #region Validation Helpers

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this.m_disposed, this);
    }

    private void ThrowIfNotHandshaked()
    {
        if (!this.m_isHandshakeComplete)
        {
            throw new InvalidOperationException(
                "Handshake must be completed before performing stream operations."
            );
        }
    }

    #endregion Validation Helpers

    #region Disposal

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (this.m_disposed)
        {
            return;
        }

        if (disposing)
        {
            // Clear sensitive key material
            if (this.m_txKey is not null)
            {
                CryptographicOperations.ZeroMemory(this.m_txKey);
            }
            if (this.m_rxKey is not null)
            {
                CryptographicOperations.ZeroMemory(this.m_rxKey);
            }

            if (!this.m_leaveOpen)
            {
                this.m_baseStream.Dispose();
            }
        }

        this.m_disposed = true;
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (this.m_disposed)
        {
            return;
        }

        // Clear sensitive key material
        if (this.m_txKey is not null)
        {
            CryptographicOperations.ZeroMemory(this.m_txKey);
        }
        if (this.m_rxKey is not null)
        {
            CryptographicOperations.ZeroMemory(this.m_rxKey);
        }

        if (!this.m_leaveOpen)
        {
            await this.m_baseStream.DisposeAsync().ConfigureAwait(false);
        }

        this.m_disposed = true;
        GC.SuppressFinalize(this);
    }

    #endregion Disposal
}

/// <summary>
/// Error codes for secure P2P stream operations.
/// </summary>
public enum SecureP2PErrorCode
{
    /// <summary>
    /// Generic handshake failure.
    /// </summary>
    HandshakeFailed,

    /// <summary>
    /// Handshake timed out.
    /// </summary>
    HandshakeTimeout,

    /// <summary>
    /// Remote peer failed signature verification.
    /// </summary>
    AuthenticationFailed,

    /// <summary>
    /// Remote peer's identity doesn't match expected TOFU identity.
    /// </summary>
    IdentityMismatch,

    /// <summary>
    /// Frame authentication tag verification failed (tampering detected).
    /// </summary>
    TamperingDetected,

    /// <summary>
    /// Out-of-order or duplicate frame detected (replay attack).
    /// </summary>
    ReplayDetected,

    /// <summary>
    /// Protocol violation (malformed data).
    /// </summary>
    ProtocolViolation,
}

/// <summary>
/// Exception thrown for secure P2P stream errors.
/// </summary>
public class SecureP2PException : Exception
{
    /// <summary>
    /// Gets the error code.
    /// </summary>
    public SecureP2PErrorCode ErrorCode { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SecureP2PException"/> class.
    /// </summary>
    public SecureP2PException(SecureP2PErrorCode errorCode, string message)
        : base(message)
    {
        this.ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SecureP2PException"/> class.
    /// </summary>
    public SecureP2PException(
        SecureP2PErrorCode errorCode,
        string message,
        Exception innerException
    )
        : base(message, innerException)
    {
        this.ErrorCode = errorCode;
    }
}
