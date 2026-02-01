using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using P2PFileExchange.Core.Models;
using P2PFileExchange.Core.Utilities;

namespace P2PFileExchange.Core.Services.Security;

/// <summary>
/// Manages the TOFU (Trust-On-First-Use) trust database for peer identity verification.
/// Stores Ed25519 public keys and trust status in an SQLite database.
/// </summary>
public sealed class PeerTrustManager : IAsyncDisposable
{
    #region Constants

    /// <summary>
    /// Default filename for the trust database.
    /// </summary>
    public const string DefaultDatabaseFileName = "trust.db";

    /// <summary>
    /// Ed25519 public key length in bytes.
    /// </summary>
    private const int PublicKeyLength = 32;

    /// <summary>
    /// Current database schema version for migrations.
    /// </summary>
    private const int SchemaVersion = 1;

    #endregion Constants

    #region Fields

    private readonly SemaphoreSlim m_dbLock = new(1, 1);
    private SqliteConnection? m_connection;
    private bool m_disposed;

    #endregion Fields

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerTrustManager"/> class.
    /// </summary>
    /// <param name="databasePath">
    /// The path to the SQLite database file.
    /// If null, uses the default path in the app data directory.
    /// </param>
    public PeerTrustManager(string? databasePath = null)
    {
        this.DatabasePath = databasePath ?? DefaultDatabasePath;
    }

    #endregion Constructor

    #region Properties

    /// <summary>
    /// Gets the default path for the trust database.
    /// Platform-specific: ~/.local/share/P2PFileExchange/trust.db (Linux/Mac)
    /// or %LOCALAPPDATA%/P2PFileExchange/trust.db (Windows).
    /// </summary>
    public static string DefaultDatabasePath
    {
        get
        {
            string basePath = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData
            );
            return Path.Combine(
                basePath,
                AppConstants.AppDataDirectoryName,
                DefaultDatabaseFileName
            );
        }
    }

    /// <summary>
    /// Gets the path to the database file.
    /// </summary>
    public string DatabasePath { get; }

    /// <summary>
    /// Gets whether the database has been initialized.
    /// </summary>
    public bool IsInitialized => this.m_connection is not null;

    #endregion Properties

    #region Database Initialization

    /// <summary>
    /// Initializes the trust database, creating the schema if necessary.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task InitializeDatabaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.m_connection is not null)
            {
                return; // Already initialized
            }

            // Ensure directory exists
            string? directory = Path.GetDirectoryName(this.DatabasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Create and open connection
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = this.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            }.ToString();

            this.m_connection = new SqliteConnection(connectionString);
            await this
                .m_connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);

            // Enable WAL mode for better concurrent access
            await using (
                SqliteCommand walCommand = this.m_connection.CreateCommand()
            )
            {
                walCommand.CommandText = "PRAGMA journal_mode=WAL;";
                _ = await walCommand
                    .ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            // Create schema
            await this.CreateSchemaAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    /// <summary>
    /// Creates the database schema.
    /// </summary>
    private async Task CreateSchemaAsync(CancellationToken cancellationToken)
    {
        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS TrustedPeers (
                PeerId TEXT PRIMARY KEY,
                DisplayName TEXT NOT NULL,
                Ed25519PublicKey BLOB NOT NULL,
                PublicKeyFingerprint TEXT NOT NULL,
                TrustLevel INTEGER NOT NULL DEFAULT 0,
                FirstSeen INTEGER NOT NULL,
                LastSeen INTEGER NOT NULL,
                TransferCount INTEGER NOT NULL DEFAULT 0,
                FailedTransferCount INTEGER NOT NULL DEFAULT 0,
                Notes TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_fingerprint ON TrustedPeers(PublicKeyFingerprint);
            CREATE INDEX IF NOT EXISTS idx_trust_level ON TrustedPeers(TrustLevel);

            CREATE TABLE IF NOT EXISTS SchemaVersion (
                Version INTEGER PRIMARY KEY
            );

            INSERT OR IGNORE INTO SchemaVersion (Version) VALUES (@version);
            """;

        await using SqliteCommand command = this.m_connection!.CreateCommand();
        command.CommandText = createTableSql;
        command.Parameters.AddWithValue("@version", SchemaVersion);
        _ = await command
            .ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    #endregion Database Initialization

    #region Trust Operations

    /// <summary>
    /// Trusts a new peer, storing their identity in the database.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="displayName">The peer's display name.</param>
    /// <param name="ed25519PublicKey">The peer's Ed25519 public key (32 bytes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown if the public key is invalid.</exception>
    public async Task TrustPeerAsync(
        Guid peerId,
        string displayName,
        byte[] ed25519PublicKey,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();
        ValidatePublicKey(ed25519PublicKey);

        string fingerprint = IdentityKeyManager.ComputeFingerprint(
            ed25519PublicKey
        );
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        const string sql = """
            INSERT INTO TrustedPeers (
                PeerId, DisplayName, Ed25519PublicKey, PublicKeyFingerprint,
                TrustLevel, FirstSeen, LastSeen, TransferCount, FailedTransferCount
            ) VALUES (
                @peerId, @displayName, @publicKey, @fingerprint,
                @trustLevel, @firstSeen, @lastSeen, 0, 0
            )
            ON CONFLICT(PeerId) DO UPDATE SET
                DisplayName = @displayName,
                TrustLevel = @trustLevel,
                LastSeen = @lastSeen
            """;

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@peerId", peerId.ToString());
            command.Parameters.AddWithValue("@displayName", displayName);
            command.Parameters.AddWithValue("@publicKey", ed25519PublicKey);
            command.Parameters.AddWithValue("@fingerprint", fingerprint);
            command.Parameters.AddWithValue(
                "@trustLevel",
                (int)TrustLevel.Trusted
            );
            command.Parameters.AddWithValue("@firstSeen", now);
            command.Parameters.AddWithValue("@lastSeen", now);

            _ = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    /// <summary>
    /// Checks if a peer is trusted.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the peer is trusted; otherwise, false.</returns>
    public async Task<bool> IsTrustedAsync(
        Guid peerId,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql =
            "SELECT TrustLevel FROM TrustedPeers WHERE PeerId = @peerId";

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@peerId", peerId.ToString());

            object? result = await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (result is long trustLevel)
            {
                return trustLevel == (int)TrustLevel.Trusted;
            }
            return false;
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    /// <summary>
    /// Gets a peer's trust level.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The trust level, or null if the peer is not in the database.</returns>
    public async Task<TrustLevel?> GetTrustLevelAsync(
        Guid peerId,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql =
            "SELECT TrustLevel FROM TrustedPeers WHERE PeerId = @peerId";

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@peerId", peerId.ToString());

            object? result = await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (result is long trustLevel)
            {
                return (TrustLevel)trustLevel;
            }
            return null;
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    /// <summary>
    /// Gets the stored public key for a peer.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored public key, or null if the peer is not in the database.</returns>
    public async Task<byte[]?> GetPublicKeyAsync(
        Guid peerId,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql =
            "SELECT Ed25519PublicKey FROM TrustedPeers WHERE PeerId = @peerId";

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@peerId", peerId.ToString());

            object? result = await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            return result as byte[];
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    /// <summary>
    /// Verifies that a received public key matches the stored key for a peer.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="receivedPublicKey">The public key received from the peer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the keys match; false if they don't match or peer is unknown.</returns>
    public async Task<bool> VerifyPublicKeyAsync(
        Guid peerId,
        byte[] receivedPublicKey,
        CancellationToken cancellationToken = default
    )
    {
        ValidatePublicKey(receivedPublicKey);

        byte[]? storedKey = await this.GetPublicKeyAsync(
                peerId,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (storedKey is null)
        {
            return false; // Unknown peer
        }

        // Constant-time comparison to prevent timing attacks
        return CryptographicOperations.FixedTimeEquals(
            storedKey,
            receivedPublicKey
        );
    }

    /// <summary>
    /// Detects a key mismatch and returns both fingerprints for user review.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="receivedPublicKey">The public key received from the peer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A tuple containing the old (stored) fingerprint and new (received) fingerprint,
    /// or null if the peer is unknown or keys match.
    /// </returns>
    public async Task<(
        string OldFingerprint,
        string NewFingerprint
    )?> DetectKeyMismatchAsync(
        Guid peerId,
        byte[] receivedPublicKey,
        CancellationToken cancellationToken = default
    )
    {
        ValidatePublicKey(receivedPublicKey);

        byte[]? storedKey = await this.GetPublicKeyAsync(
                peerId,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (storedKey is null)
        {
            return null; // Unknown peer, no mismatch possible
        }

        // Check if keys match
        if (
            CryptographicOperations.FixedTimeEquals(
                storedKey,
                receivedPublicKey
            )
        )
        {
            return null; // Keys match, no mismatch
        }

        // Keys don't match - compute both fingerprints
        string oldFingerprint = IdentityKeyManager.ComputeFingerprint(
            storedKey
        );
        string newFingerprint = IdentityKeyManager.ComputeFingerprint(
            receivedPublicKey
        );

        return (oldFingerprint, newFingerprint);
    }

    /// <summary>
    /// Approves a key change for a peer (e.g., after device reinstall verification).
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="newPublicKey">The new public key to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ApproveKeyChangeAsync(
        Guid peerId,
        byte[] newPublicKey,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();
        ValidatePublicKey(newPublicKey);

        string newFingerprint = IdentityKeyManager.ComputeFingerprint(
            newPublicKey
        );
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        const string sql = """
            UPDATE TrustedPeers
            SET Ed25519PublicKey = @publicKey,
                PublicKeyFingerprint = @fingerprint,
                LastSeen = @lastSeen
            WHERE PeerId = @peerId
            """;

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@peerId", peerId.ToString());
            command.Parameters.AddWithValue("@publicKey", newPublicKey);
            command.Parameters.AddWithValue("@fingerprint", newFingerprint);
            command.Parameters.AddWithValue("@lastSeen", now);

            _ = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    /// <summary>
    /// Sets the trust level for a peer.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="trustLevel">The new trust level.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetTrustLevelAsync(
        Guid peerId,
        TrustLevel trustLevel,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql =
            "UPDATE TrustedPeers SET TrustLevel = @trustLevel WHERE PeerId = @peerId";

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@peerId", peerId.ToString());
            command.Parameters.AddWithValue("@trustLevel", (int)trustLevel);

            _ = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    /// <summary>
    /// Updates the last seen timestamp for a peer.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task UpdateLastSeenAsync(
        Guid peerId,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string sql =
            "UPDATE TrustedPeers SET LastSeen = @lastSeen WHERE PeerId = @peerId";

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@peerId", peerId.ToString());
            command.Parameters.AddWithValue("@lastSeen", now);

            _ = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    /// <summary>
    /// Increments the transfer count for a peer.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="success">True for successful transfer, false for failed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task IncrementTransferCountAsync(
        Guid peerId,
        bool success,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        string column = success ? "TransferCount" : "FailedTransferCount";
        string sql =
            $"UPDATE TrustedPeers SET {column} = {column} + 1 WHERE PeerId = @peerId";

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@peerId", peerId.ToString());

            _ = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    /// <summary>
    /// Updates notes for a peer.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="notes">The notes to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task UpdateNotesAsync(
        Guid peerId,
        string? notes,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql =
            "UPDATE TrustedPeers SET Notes = @notes WHERE PeerId = @peerId";

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@peerId", peerId.ToString());
            command.Parameters.AddWithValue(
                "@notes",
                notes is null ? DBNull.Value : notes
            );

            _ = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    /// <summary>
    /// Removes a peer from the trust database.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the peer was removed; false if not found.</returns>
    public async Task<bool> RemovePeerAsync(
        Guid peerId,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql = "DELETE FROM TrustedPeers WHERE PeerId = @peerId";

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@peerId", peerId.ToString());

            int rowsAffected = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            return rowsAffected > 0;
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    #endregion Trust Operations

    #region Query Operations

    /// <summary>
    /// Gets information about a specific peer.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The peer information, or null if not found.</returns>
    public async Task<TrustedPeerInfo?> GetPeerAsync(
        Guid peerId,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql = """
            SELECT PeerId, DisplayName, Ed25519PublicKey, PublicKeyFingerprint,
                   TrustLevel, FirstSeen, LastSeen, TransferCount, FailedTransferCount, Notes
            FROM TrustedPeers
            WHERE PeerId = @peerId
            """;

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@peerId", peerId.ToString());

            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return ReadPeerInfo(reader);
            }

            return null;
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    /// <summary>
    /// Gets all trusted peers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of all trusted peer information.</returns>
    public async Task<List<TrustedPeerInfo>> GetTrustedPeersAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await this.GetPeersByTrustLevelAsync(
                TrustLevel.Trusted,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets all peers with a specific trust level.
    /// </summary>
    /// <param name="trustLevel">The trust level to filter by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of peer information matching the trust level.</returns>
    public async Task<List<TrustedPeerInfo>> GetPeersByTrustLevelAsync(
        TrustLevel trustLevel,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql = """
            SELECT PeerId, DisplayName, Ed25519PublicKey, PublicKeyFingerprint,
                   TrustLevel, FirstSeen, LastSeen, TransferCount, FailedTransferCount, Notes
            FROM TrustedPeers
            WHERE TrustLevel = @trustLevel
            ORDER BY LastSeen DESC
            """;

        List<TrustedPeerInfo> peers = [];

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@trustLevel", (int)trustLevel);

            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            )
            {
                peers.Add(ReadPeerInfo(reader));
            }

            return peers;
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    /// <summary>
    /// Gets all peers in the database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of all peer information.</returns>
    public async Task<List<TrustedPeerInfo>> GetAllPeersAsync(
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql = """
            SELECT PeerId, DisplayName, Ed25519PublicKey, PublicKeyFingerprint,
                   TrustLevel, FirstSeen, LastSeen, TransferCount, FailedTransferCount, Notes
            FROM TrustedPeers
            ORDER BY LastSeen DESC
            """;

        List<TrustedPeerInfo> peers = [];

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;

            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            )
            {
                peers.Add(ReadPeerInfo(reader));
            }

            return peers;
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    /// <summary>
    /// Searches for peers by display name or fingerprint.
    /// </summary>
    /// <param name="searchTerm">The search term.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of matching peer information.</returns>
    public async Task<List<TrustedPeerInfo>> SearchPeersAsync(
        string searchTerm,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql = """
            SELECT PeerId, DisplayName, Ed25519PublicKey, PublicKeyFingerprint,
                   TrustLevel, FirstSeen, LastSeen, TransferCount, FailedTransferCount, Notes
            FROM TrustedPeers
            WHERE DisplayName LIKE @search OR PublicKeyFingerprint LIKE @search
            ORDER BY LastSeen DESC
            """;

        List<TrustedPeerInfo> peers = [];
        string searchPattern = $"%{searchTerm}%";

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@search", searchPattern);

            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            )
            {
                peers.Add(ReadPeerInfo(reader));
            }

            return peers;
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    /// <summary>
    /// Checks if a peer exists in the database.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the peer exists; otherwise, false.</returns>
    public async Task<bool> PeerExistsAsync(
        Guid peerId,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql =
            "SELECT 1 FROM TrustedPeers WHERE PeerId = @peerId LIMIT 1";

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@peerId", peerId.ToString());

            object? result = await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            return result is not null;
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    #endregion Query Operations

    #region Helper Methods

    /// <summary>
    /// Reads a TrustedPeerInfo from a data reader.
    /// </summary>
    private static TrustedPeerInfo ReadPeerInfo(SqliteDataReader reader)
    {
        return new TrustedPeerInfo
        {
            PeerId = Guid.Parse(reader.GetString(0)),
            CachedDisplayName = reader.GetString(1),
            Ed25519PublicKey = (byte[])reader.GetValue(2),
            PublicKeyFingerprint = reader.GetString(3),
            TrustLevel = (TrustLevel)reader.GetInt32(4),
            FirstTrusted = DateTimeOffset.FromUnixTimeSeconds(
                reader.GetInt64(5)
            ),
            LastSeen = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
            TransferCount = reader.GetInt32(7),
            FailedTransferCount = reader.GetInt32(8),
            Notes = reader.IsDBNull(9) ? null : reader.GetString(9),
        };
    }

    /// <summary>
    /// Validates an Ed25519 public key.
    /// </summary>
    private static void ValidatePublicKey(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (publicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException(
                $"Ed25519 public key must be exactly {PublicKeyLength} bytes.",
                nameof(publicKey)
            );
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this.m_disposed, this);
    }

    private void ThrowIfNotInitialized()
    {
        if (this.m_connection is null)
        {
            throw new InvalidOperationException(
                "Database not initialized. Call InitializeDatabaseAsync first."
            );
        }
    }

    #endregion Helper Methods

    #region Disposal

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this.m_disposed)
        {
            return;
        }

        this.m_disposed = true;

        if (this.m_connection is not null)
        {
            await this.m_connection.CloseAsync().ConfigureAwait(false);
            await this.m_connection.DisposeAsync().ConfigureAwait(false);
            this.m_connection = null;
        }

        this.m_dbLock.Dispose();
    }

    #endregion Disposal
}
