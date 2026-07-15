using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Konscious.Security.Cryptography;
using P2PFileExchange.Core.Utilities;
using Sodium;

namespace P2PFileExchange.Core.Security;

/// <summary>
/// Manages Ed25519 identity keys for persistent peer authentication using TOFU (Trust-On-First-Use).
/// Keys are encrypted at rest using Argon2id for key derivation and XChaCha20-Poly1305 for encryption.
/// </summary>
public sealed class IdentityKeyManager : IDisposable
{
    /// <summary>
    /// Default filename for the identity key file.
    /// </summary>
    public const string DefaultIdentityKeyFileName = "identity.key";

    /// <summary>
    /// Ed25519 public key length in bytes.
    /// </summary>
    public const int PublicKeyLength = 32;

    /// <summary>
    /// Ed25519 private key length in bytes (seed + public key).
    /// </summary>
    public const int PrivateKeyLength = 64;

    /// <summary>
    /// Argon2id salt length in bytes.
    /// </summary>
    private const int SaltLength = 32;

    /// <summary>
    /// XChaCha20-Poly1305 nonce length in bytes.
    /// </summary>
    private const int NonceLength = 24;

    /// <summary>
    /// XChaCha20-Poly1305 authentication tag length in bytes.
    /// </summary>
    private const int TagLength = 16;

    /// <summary>
    /// Derived encryption key length in bytes.
    /// </summary>
    private const int DerivedKeyLength = 32;

    /// <summary>
    /// Total encrypted file size: salt(32) + nonce(24) + ciphertext(64) + tag(16)
    /// </summary>
    public const int EncryptedFileSize =
        SaltLength + NonceLength + PrivateKeyLength + TagLength;

    /// <summary>
    /// Argon2id memory cost in KB (64 MB).
    /// </summary>
    private const int Argon2MemoryKB = 65536;

    /// <summary>
    /// Argon2id iteration count.
    /// </summary>
    private const int Argon2Iterations = 3;

    /// <summary>
    /// Argon2id parallelism (lanes).
    /// </summary>
    private const int Argon2Parallelism = 4;

    /// <summary>
    /// Maximum password retry attempts.
    /// </summary>
    public const int MaxPasswordAttempts = 3;

    private byte[]? m_publicKey;
    private byte[]? m_privateKey;
    private bool m_isDisposed;

    /// <summary>
    /// Gets the default path for the identity key file.
    /// Platform-specific: ~/.local/share/P2PFileExchange/identity.key (Linux/Mac)
    /// or %LOCALAPPDATA%/P2PFileExchange/identity.key (Windows).
    /// </summary>
    public static string DefaultIdentityKeyPath
    {
        get
        {
            string basePath = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData
            );
            return Path.Combine(
                basePath,
                AppConstants.AppDataDirectoryName,
                DefaultIdentityKeyFileName
            );
        }
    }

    /// <summary>
    /// Gets whether an identity key has been loaded.
    /// </summary>
    public bool IsLoaded =>
        this.m_publicKey is not null && this.m_privateKey is not null;

    /// <summary>
    /// Gets the Ed25519 public key (32 bytes).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if no key is loaded.</exception>
    public ImmutableArray<byte> PublicKey
    {
        get
        {
            this.ThrowIfDisposed();
            if (this.m_publicKey is null)
            {
                throw new InvalidOperationException(
                    "No identity key is loaded."
                );
            }
            return ImmutableArray.Create(this.m_publicKey);
        }
    }

    /// <summary>
    /// Gets the peer ID derived from the public key (first 16 bytes of SHA-256 hash).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if no key is loaded.</exception>
    public Guid PeerId
    {
        get
        {
            this.ThrowIfDisposed();
            return ComputePeerId(this.PublicKey.AsSpan());
        }
    }

    /// <summary>
    /// Gets the fingerprint of the public key for display (formatted hex groups).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if no key is loaded.</exception>
    public string Fingerprint
    {
        get
        {
            this.ThrowIfDisposed();
            return ComputeFingerprint(this.PublicKey.AsSpan());
        }
    }

    /// <summary>
    /// Gets the public key as a Base64-encoded string for network transmission.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if no key is loaded.</exception>
    public string PublicKeyBase64
    {
        get
        {
            this.ThrowIfDisposed();
            return Convert.ToBase64String(this.PublicKey.AsSpan());
        }
    }

    /// <summary>
    /// Initializes the libsodium library. Must be called once on application startup.
    /// </summary>
    public static void Initialize()
    {
        SodiumCore.Init();
    }

    /// <summary>
    /// Generates a new Ed25519 keypair.
    /// </summary>
    /// <returns>A tuple containing the public key (32 bytes) and private key (64 bytes).</returns>
    public static (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
    {
        KeyPair keyPair = PublicKeyAuth.GenerateKeyPair();
        return (keyPair.PublicKey, keyPair.PrivateKey);
    }

    /// <summary>
    /// Computes a peer ID from an Ed25519 public key.
    /// The peer ID is derived from the first 16 bytes of the SHA-256 hash of the public key.
    /// </summary>
    /// <param name="publicKey">The Ed25519 public key (32 bytes).</param>
    /// <returns>A GUID representing the peer ID.</returns>
    public static Guid ComputePeerId(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException(
                $"Public key must be {PublicKeyLength} bytes.",
                nameof(publicKey)
            );
        }

        byte[] hash = SHA256.HashData(publicKey);
        ReadOnlySpan<byte> guidBytes = hash.AsSpan(0, 16);
        return new Guid(guidBytes);
    }

    /// <summary>
    /// Computes a human-readable fingerprint from an Ed25519 public key.
    /// Format: "F3A7 B82C 91D4 E6F5 2C8A 4E91 7B3D 6F2E" (4-char groups).
    /// </summary>
    /// <param name="publicKey">The Ed25519 public key (32 bytes).</param>
    /// <returns>A formatted fingerprint string.</returns>
    public static string ComputeFingerprint(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException(
                $"Public key must be {PublicKeyLength} bytes.",
                nameof(publicKey)
            );
        }

        byte[] hash = SHA256.HashData(publicKey);
        string hex = Convert.ToHexString(hash);

        // Format as 4-character groups separated by spaces
        StringBuilder fingerprint = new(hex.Length + hex.Length / 4);

        for (int i = 0; i < hex.Length; i += 4)
        {
            if (i > 0)
            {
                fingerprint.Append(' ');
            }
            fingerprint.Append(hex, i, 4);
        }

        return fingerprint.ToString();
    }

    /// <summary>
    /// Computes a short fingerprint for compact display (first 8 chars).
    /// </summary>
    /// <param name="publicKey">The Ed25519 public key (32 bytes).</param>
    /// <returns>A short fingerprint string.</returns>
    public static string ComputeShortFingerprint(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (publicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException(
                $"Public key must be {PublicKeyLength} bytes.",
                nameof(publicKey)
            );
        }

        byte[] hash = SHA256.HashData(publicKey);
        return Convert.ToHexString(hash, 0, 4);
    }

    /// <summary>
    /// Derives an encryption key from a password using Argon2id.
    /// </summary>
    /// <param name="password">The password to derive from.</param>
    /// <param name="salt">The salt (32 bytes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The derived key (32 bytes).</returns>
    public static async Task<byte[]> DeriveKeyAsync(
        ReadOnlyMemory<char> password,
        byte[] salt,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length != SaltLength)
        {
            throw new ArgumentException(
                $"Salt must be {SaltLength} bytes.",
                nameof(salt)
            );
        }

        byte[] passwordBytes = GetUtf8Bytes(password.Span);
        try
        {
            // Run Argon2id in a background task to avoid blocking
            return await Task.Run(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using Argon2id argon2 = new(passwordBytes);

                        argon2.Salt = salt;
                        argon2.MemorySize = Argon2MemoryKB;
                        argon2.Iterations = Argon2Iterations;
                        argon2.DegreeOfParallelism = Argon2Parallelism;

                        return argon2.GetBytes(DerivedKeyLength);
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// Encrypts an Ed25519 private key using XChaCha20-Poly1305.
    /// </summary>
    /// <param name="privateKey">The private key to encrypt (64 bytes).</param>
    /// <param name="encryptionKey">The encryption key (32 bytes).</param>
    /// <param name="nonce">The nonce (24 bytes).</param>
    /// <returns>The encrypted ciphertext with authentication tag (80 bytes).</returns>
    private static byte[] EncryptPrivateKey(
        byte[] privateKey,
        byte[] encryptionKey,
        byte[] nonce
    )
    {
        // Using SecretBox which provides XChaCha20-Poly1305 authenticated encryption
        return SecretBox.Create(privateKey, nonce, encryptionKey);
    }

    /// <summary>
    /// Decrypts an Ed25519 private key using XChaCha20-Poly1305.
    /// </summary>
    /// <param name="ciphertext">The encrypted ciphertext with tag (80 bytes).</param>
    /// <param name="encryptionKey">The encryption key (32 bytes).</param>
    /// <param name="nonce">The nonce (24 bytes).</param>
    /// <returns>The decrypted private key (64 bytes).</returns>
    /// <exception cref="CryptographicException">Thrown if decryption or authentication fails.</exception>
    private static byte[] DecryptPrivateKey(
        byte[] ciphertext,
        byte[] encryptionKey,
        byte[] nonce
    )
    {
        try
        {
            return SecretBox.Open(ciphertext, nonce, encryptionKey);
        }
        catch (Exception ex)
        {
            throw new CryptographicException(
                "Decryption failed. Invalid password or corrupted file.",
                ex
            );
        }
    }

    /// <summary>
    /// Generates a new identity key and saves it encrypted to the specified path.
    /// </summary>
    /// <param name="filePath">The path to save the encrypted key file.</param>
    /// <param name="password">The password to encrypt the key with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task GenerateAndSaveAsync(
        string filePath,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        if (password.IsEmpty)
        {
            throw new ArgumentException(
                "Password cannot be empty.",
                nameof(password)
            );
        }

        (byte[] publicKey, byte[] privateKey) = GenerateKeyPair();
        try
        {
            await SaveEncryptedKeyAsync(
                    filePath,
                    privateKey,
                    password,
                    cancellationToken
                )
                .ConfigureAwait(false);

            // Store keys in memory
            this.m_publicKey = publicKey;
            this.m_privateKey = privateKey;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(publicKey);
            CryptographicOperations.ZeroMemory(privateKey);
            throw;
        }
    }

    /// <summary>
    /// Saves an encrypted private key to a file.
    /// File format: [salt(32)][nonce(24)][ciphertext_with_tag(80)]
    /// </summary>
    /// <param name="filePath">The path to save the encrypted key file.</param>
    /// <param name="privateKey">The private key to encrypt and save.</param>
    /// <param name="password">The password to encrypt the key with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task SaveEncryptedKeyAsync(
        string filePath,
        byte[] privateKey,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(privateKey);
        if (password.IsEmpty)
        {
            throw new ArgumentException(
                "Password cannot be empty.",
                nameof(password)
            );
        }

        if (privateKey.Length != PrivateKeyLength)
        {
            throw new ArgumentException(
                $"Private key must be {PrivateKeyLength} bytes.",
                nameof(privateKey)
            );
        }

        // Generate random salt and nonce
        byte[] salt = SodiumCore.GetRandomBytes(SaltLength);
        byte[] nonce = SodiumCore.GetRandomBytes(NonceLength);
        byte[]? encryptionKey = null;

        try
        {
            // Derive encryption key from password
            encryptionKey = await DeriveKeyAsync(
                    password,
                    salt,
                    cancellationToken
                )
                .ConfigureAwait(false);

            // Encrypt private key
            byte[] ciphertext = EncryptPrivateKey(
                privateKey,
                encryptionKey,
                nonce
            );

            // Combine: [salt][nonce][ciphertext]
            byte[] fileData = new byte[EncryptedFileSize];
            Buffer.BlockCopy(salt, 0, fileData, 0, SaltLength);
            Buffer.BlockCopy(nonce, 0, fileData, SaltLength, NonceLength);
            Buffer.BlockCopy(
                ciphertext,
                0,
                fileData,
                SaltLength + NonceLength,
                ciphertext.Length
            );

            // Ensure directory exists
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            // Write file atomically (write to temp, then rename)
            string tempPath = filePath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, fileData, cancellationToken)
                .ConfigureAwait(false);
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
            if (encryptionKey is not null)
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }
        }
    }

    /// <summary>
    /// Loads and decrypts an identity key from a file.
    /// </summary>
    /// <param name="filePath">The path to the encrypted key file.</param>
    /// <param name="password">The password to decrypt the key with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="CryptographicException">Thrown if decryption fails (wrong password).</exception>
    /// <exception cref="InvalidDataException">Thrown if the file format is invalid.</exception>
    public async Task LoadAsync(
        string filePath,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        if (password.IsEmpty)
        {
            throw new ArgumentException(
                "Password cannot be empty.",
                nameof(password)
            );
        }

        byte[] fileData = await File.ReadAllBytesAsync(
                filePath,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (fileData.Length != EncryptedFileSize)
        {
            throw new InvalidDataException(
                $"Invalid identity key file. Expected {EncryptedFileSize} bytes, got {fileData.Length}."
            );
        }

        byte[] salt = new byte[SaltLength];
        byte[] nonce = new byte[NonceLength];
        byte[] ciphertext = new byte[PrivateKeyLength + TagLength];

        Buffer.BlockCopy(fileData, 0, salt, 0, SaltLength);
        Buffer.BlockCopy(fileData, SaltLength, nonce, 0, NonceLength);
        Buffer.BlockCopy(
            fileData,
            SaltLength + NonceLength,
            ciphertext,
            0,
            ciphertext.Length
        );

        byte[]? encryptionKey = null;
        byte[]? privateKey = null;

        try
        {
            // Derive decryption key from password
            encryptionKey = await DeriveKeyAsync(
                    password,
                    salt,
                    cancellationToken
                )
                .ConfigureAwait(false);

            // Decrypt private key
            privateKey = DecryptPrivateKey(ciphertext, encryptionKey, nonce);

            // Extract public key from private key (last 32 bytes of Ed25519 private key)
            byte[] publicKey = new byte[PublicKeyLength];
            Buffer.BlockCopy(
                privateKey,
                PublicKeyLength,
                publicKey,
                0,
                PublicKeyLength
            );

            // Clear any existing keys
            this.ClearKeys();

            // Store keys in memory
            this.m_publicKey = publicKey;
            this.m_privateKey = privateKey;
            privateKey = null; // Prevent cleanup in finally block
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(fileData);
            if (encryptionKey is not null)
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }
            if (privateKey is not null)
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }
    }

    /// <summary>
    /// Checks whether an identity key file exists at the specified path.
    /// </summary>
    /// <param name="filePath">The path to check.</param>
    /// <returns>True if the file exists, false otherwise.</returns>
    public static bool KeyExists(string filePath)
    {
        return File.Exists(filePath);
    }

    /// <summary>
    /// Signs data using the loaded Ed25519 private key.
    /// </summary>
    /// <param name="data">The data to sign.</param>
    /// <returns>The signature (64 bytes).</returns>
    /// <exception cref="InvalidOperationException">Thrown if no key is loaded.</exception>
    public byte[] Sign(byte[] data)
    {
        this.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(data);

        if (this.m_privateKey is null)
        {
            throw new InvalidOperationException("No identity key is loaded.");
        }

        return PublicKeyAuth.SignDetached(data, this.m_privateKey);
    }

    /// <summary>
    /// Verifies a signature using an Ed25519 public key.
    /// </summary>
    /// <param name="data">The signed data.</param>
    /// <param name="signature">The signature to verify (64 bytes).</param>
    /// <param name="publicKey">The public key to verify against (32 bytes).</param>
    /// <returns>True if the signature is valid, false otherwise.</returns>
    public static bool Verify(byte[] data, byte[] signature, byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(publicKey);

        if (publicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException(
                $"Public key must be {PublicKeyLength} bytes.",
                nameof(publicKey)
            );
        }

        try
        {
            return PublicKeyAuth.VerifyDetached(signature, data, publicKey);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Exports the encrypted identity key to a new location (for backup purposes).
    /// </summary>
    /// <param name="sourcePath">The source encrypted key file path.</param>
    /// <param name="destinationPath">The destination path for the backup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task ExportKeyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                "Identity key file not found.",
                sourcePath
            );
        }

        byte[] fileData = await File.ReadAllBytesAsync(
                sourcePath,
                cancellationToken
            )
            .ConfigureAwait(false);

        // Ensure directory exists
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(
                destinationPath,
                fileData,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Re-encrypts the identity key with a new password.
    /// </summary>
    /// <param name="filePath">The path to the encrypted key file.</param>
    /// <param name="currentPassword">The current password.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ChangePasswordAsync(
        string filePath,
        ReadOnlyMemory<char> currentPassword,
        ReadOnlyMemory<char> newPassword,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        if (currentPassword.IsEmpty)
        {
            throw new ArgumentException(
                "Current password cannot be empty.",
                nameof(currentPassword)
            );
        }
        if (newPassword.IsEmpty)
        {
            throw new ArgumentException(
                "New password cannot be empty.",
                nameof(newPassword)
            );
        }

        // Ensure the key is loaded with the current password
        if (!this.IsLoaded)
        {
            await this.LoadAsync(filePath, currentPassword, cancellationToken)
                .ConfigureAwait(false);
        }

        // Save with the new password (generates new salt and nonce)
        await SaveEncryptedKeyAsync(
                filePath,
                this.m_privateKey!,
                newPassword,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Regenerates the identity key, replacing any existing key.
    /// Warning: This will change the peer's identity and require re-verification by all peers.
    /// </summary>
    /// <param name="filePath">The path to save the new encrypted key file.</param>
    /// <param name="password">The password to encrypt the new key with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RegenerateAsync(
        string filePath,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        if (password.IsEmpty)
        {
            throw new ArgumentException(
                "Password cannot be empty.",
                nameof(password)
            );
        }

        // Clear existing keys
        this.ClearKeys();

        // Delete existing file if present
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        // Generate and save new keys
        await this.GenerateAndSaveAsync(filePath, password, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the private key from memory. Call on application shutdown.
    /// </summary>
    public void ClearKeys()
    {
        if (this.m_privateKey is not null)
        {
            CryptographicOperations.ZeroMemory(this.m_privateKey);
            this.m_privateKey = null;
        }

        if (this.m_publicKey is not null)
        {
            CryptographicOperations.ZeroMemory(this.m_publicKey);
            this.m_publicKey = null;
        }
    }

    /// <summary>
    /// Gets UTF-8 bytes from a character span.
    /// </summary>
    /// <param name="chars">The character span to convert.</param>
    /// <returns>
    /// A byte array containing the UTF-8 encoded bytes of the input characters.
    /// </returns>
    private static byte[] GetUtf8Bytes(ReadOnlySpan<char> chars)
    {
        int byteCount = Encoding.UTF8.GetByteCount(chars);
        byte[] bytes = new byte[byteCount];
        Encoding.UTF8.GetBytes(chars, bytes);
        return bytes;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this.m_isDisposed, this);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.m_isDisposed)
        {
            return;
        }

        this.ClearKeys();
        this.m_isDisposed = true;
    }
}

/// <summary>
/// Result of an identity key loading operation.
/// </summary>
public enum IdentityKeyLoadResult
{
    /// <summary>
    /// Key loaded successfully.
    /// </summary>
    Success,

    /// <summary>
    /// Key file not found, new key needs to be generated.
    /// </summary>
    NotFound,

    /// <summary>
    /// Invalid password provided.
    /// </summary>
    InvalidPassword,

    /// <summary>
    /// Key file is corrupted or invalid format.
    /// </summary>
    CorruptedFile,

    /// <summary>
    /// Operation was cancelled.
    /// </summary>
    Cancelled,
}
