using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using P2PFileExchange.Core.Utilities;

namespace P2PFileExchange.Core.Services.Security;

/// <summary>
/// Audit log for security-related events.
/// Records trust decisions, key changes, and other security events.
/// </summary>
public sealed class SecurityAuditLog : IAsyncDisposable
{
    #region Constants

    /// <summary>
    /// Default filename for the audit log database.
    /// </summary>
    public const string DefaultAuditLogFileName = "security_audit.db";

    /// <summary>
    /// Maximum age of log entries before automatic cleanup (30 days).
    /// </summary>
    private const int DefaultRetentionDays = 30;

    #endregion Constants

    #region Audit Event Types

    /// <summary>
    /// Audit event types for security logging.
    /// </summary>
    public static class EventTypes
    {
        /// <summary>
        /// A new peer was trusted for the first time.
        /// </summary>
        public const string NewPeerTrusted = "NEW_PEER_TRUSTED";

        /// <summary>
        /// A key mismatch was detected (possible MITM attack).
        /// </summary>
        public const string KeyMismatchDetected = "KEY_MISMATCH_DETECTED";

        /// <summary>
        /// A key change was approved after user verification.
        /// </summary>
        public const string KeyChangeApproved = "KEY_CHANGE_APPROVED";

        /// <summary>
        /// A peer was blocked by the user.
        /// </summary>
        public const string PeerBlocked = "PEER_BLOCKED";

        /// <summary>
        /// A peer was unblocked by the user.
        /// </summary>
        public const string PeerUnblocked = "PEER_UNBLOCKED";

        /// <summary>
        /// A peer was removed from the trust database.
        /// </summary>
        public const string PeerRemoved = "PEER_REMOVED";

        /// <summary>
        /// A secure connection was established.
        /// </summary>
        public const string SecureConnectionEstablished =
            "SECURE_CONNECTION_ESTABLISHED";

        /// <summary>
        /// A transfer was completed successfully.
        /// </summary>
        public const string TransferCompleted = "TRANSFER_COMPLETED";

        /// <summary>
        /// A transfer failed.
        /// </summary>
        public const string TransferFailed = "TRANSFER_FAILED";

        /// <summary>
        /// A transfer was rejected by the user.
        /// </summary>
        public const string TransferRejected = "TRANSFER_REJECTED";

        /// <summary>
        /// The identity key was unlocked.
        /// </summary>
        public const string IdentityKeyUnlocked = "IDENTITY_KEY_UNLOCKED";

        /// <summary>
        /// The identity key was created.
        /// </summary>
        public const string IdentityKeyCreated = "IDENTITY_KEY_CREATED";
    }

    #endregion Audit Event Types

    #region Fields

    private readonly string m_databasePath;
    private readonly int m_retentionDays;
    private readonly SemaphoreSlim m_dbLock = new(1, 1);
    private SqliteConnection? m_connection;
    private bool m_disposed;

    #endregion Fields

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="SecurityAuditLog"/> class.
    /// </summary>
    /// <param name="databasePath">
    /// The path to the SQLite database file.
    /// If null, uses the default path in the app data directory.
    /// </param>
    /// <param name="retentionDays">
    /// Number of days to retain log entries before automatic cleanup.
    /// </param>
    public SecurityAuditLog(
        string? databasePath = null,
        int retentionDays = DefaultRetentionDays
    )
    {
        this.m_databasePath = databasePath ?? DefaultAuditLogPath;
        this.m_retentionDays = retentionDays;
    }

    #endregion Constructor

    #region Properties

    /// <summary>
    /// Gets the default path for the audit log database.
    /// </summary>
    public static string DefaultAuditLogPath
    {
        get
        {
            string basePath = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData
            );
            return Path.Combine(
                basePath,
                AppConstants.AppDataDirectoryName,
                DefaultAuditLogFileName
            );
        }
    }

    /// <summary>
    /// Gets the path to the database file.
    /// </summary>
    public string DatabasePath => this.m_databasePath;

    /// <summary>
    /// Gets whether the audit log has been initialized.
    /// </summary>
    public bool IsInitialized => this.m_connection is not null;

    #endregion Properties

    #region Initialization

    /// <summary>
    /// Initializes the audit log database, creating the schema if necessary.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task InitializeAsync(
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
            string? directory = Path.GetDirectoryName(this.m_databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Create and open connection
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = this.m_databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            }.ToString();

            this.m_connection = new SqliteConnection(connectionString);
            await this
                .m_connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);

            // Enable WAL mode
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

            // Cleanup old entries
            await this.CleanupOldEntriesAsync(cancellationToken)
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
            CREATE TABLE IF NOT EXISTS AuditLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp INTEGER NOT NULL,
                EventType TEXT NOT NULL,
                PeerId TEXT,
                PeerName TEXT,
                Details TEXT,
                IPAddress TEXT,
                Success INTEGER
            );

            CREATE INDEX IF NOT EXISTS idx_timestamp ON AuditLog(Timestamp);
            CREATE INDEX IF NOT EXISTS idx_event_type ON AuditLog(EventType);
            CREATE INDEX IF NOT EXISTS idx_peer_id ON AuditLog(PeerId);
            """;

        await using SqliteCommand command = this.m_connection!.CreateCommand();
        command.CommandText = createTableSql;
        _ = await command
            .ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Removes old log entries based on retention policy.
    /// </summary>
    private async Task CleanupOldEntriesAsync(
        CancellationToken cancellationToken
    )
    {
        long cutoffTime = DateTimeOffset
            .UtcNow.AddDays(-this.m_retentionDays)
            .ToUnixTimeSeconds();

        const string sql = "DELETE FROM AuditLog WHERE Timestamp < @cutoff";

        await using SqliteCommand command = this.m_connection!.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@cutoff", cutoffTime);
        _ = await command
            .ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    #endregion Initialization

    #region Logging Methods

    /// <summary>
    /// Logs a security event.
    /// </summary>
    /// <param name="eventType">The type of event (use <see cref="EventTypes"/> constants).</param>
    /// <param name="peerId">The peer ID associated with the event (optional).</param>
    /// <param name="peerName">The peer name associated with the event (optional).</param>
    /// <param name="details">Additional details about the event (optional).</param>
    /// <param name="ipAddress">The IP address associated with the event (optional).</param>
    /// <param name="success">Whether the operation was successful (optional).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task LogEventAsync(
        string eventType,
        Guid? peerId = null,
        string? peerName = null,
        string? details = null,
        string? ipAddress = null,
        bool? success = null,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        const string sql = """
            INSERT INTO AuditLog (Timestamp, EventType, PeerId, PeerName, Details, IPAddress, Success)
            VALUES (@timestamp, @eventType, @peerId, @peerName, @details, @ipAddress, @success)
            """;

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@timestamp", timestamp);
            command.Parameters.AddWithValue("@eventType", eventType);
            command.Parameters.AddWithValue(
                "@peerId",
                peerId?.ToString() ?? (object)DBNull.Value
            );
            command.Parameters.AddWithValue(
                "@peerName",
                peerName ?? (object)DBNull.Value
            );
            command.Parameters.AddWithValue(
                "@details",
                details ?? (object)DBNull.Value
            );
            command.Parameters.AddWithValue(
                "@ipAddress",
                ipAddress ?? (object)DBNull.Value
            );
            command.Parameters.AddWithValue(
                "@success",
                success.HasValue ? (success.Value ? 1 : 0) : DBNull.Value
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
    /// Logs that a new peer was trusted.
    /// </summary>
    public Task LogNewPeerTrustedAsync(
        Guid peerId,
        string peerName,
        string fingerprint,
        string? ipAddress = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.NewPeerTrusted,
            peerId,
            peerName,
            $"Fingerprint: {fingerprint}",
            ipAddress,
            true,
            cancellationToken
        );
    }

    /// <summary>
    /// Logs that a key mismatch was detected.
    /// </summary>
    public Task LogKeyMismatchDetectedAsync(
        Guid peerId,
        string peerName,
        string oldFingerprint,
        string newFingerprint,
        string? ipAddress = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.KeyMismatchDetected,
            peerId,
            peerName,
            $"Old fingerprint: {oldFingerprint}, New fingerprint: {newFingerprint}",
            ipAddress,
            false,
            cancellationToken
        );
    }

    /// <summary>
    /// Logs that a key change was approved.
    /// </summary>
    public Task LogKeyChangeApprovedAsync(
        Guid peerId,
        string peerName,
        string newFingerprint,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.KeyChangeApproved,
            peerId,
            peerName,
            $"New fingerprint: {newFingerprint}",
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs that a peer was blocked.
    /// </summary>
    public Task LogPeerBlockedAsync(
        Guid peerId,
        string peerName,
        string? reason = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.PeerBlocked,
            peerId,
            peerName,
            reason,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs that a peer was unblocked.
    /// </summary>
    public Task LogPeerUnblockedAsync(
        Guid peerId,
        string peerName,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.PeerUnblocked,
            peerId,
            peerName,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs that a peer was removed from the trust database.
    /// </summary>
    public Task LogPeerRemovedAsync(
        Guid peerId,
        string peerName,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.PeerRemoved,
            peerId,
            peerName,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs a successful secure connection.
    /// </summary>
    public Task LogSecureConnectionAsync(
        Guid peerId,
        string peerName,
        string? ipAddress = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.SecureConnectionEstablished,
            peerId,
            peerName,
            ipAddress: ipAddress,
            success: true,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs a completed transfer.
    /// </summary>
    public Task LogTransferCompletedAsync(
        Guid peerId,
        string peerName,
        string fileName,
        long fileSize,
        bool isIncoming,
        CancellationToken cancellationToken = default
    )
    {
        string direction = isIncoming ? "received from" : "sent to";
        return this.LogEventAsync(
            EventTypes.TransferCompleted,
            peerId,
            peerName,
            $"File '{fileName}' ({fileSize} bytes) {direction} peer",
            success: true,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs a failed transfer.
    /// </summary>
    public Task LogTransferFailedAsync(
        Guid peerId,
        string peerName,
        string fileName,
        string? errorMessage = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.TransferFailed,
            peerId,
            peerName,
            $"File '{fileName}' failed: {errorMessage ?? "Unknown error"}",
            success: false,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs that the identity key was unlocked.
    /// </summary>
    public Task LogIdentityKeyUnlockedAsync(
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.IdentityKeyUnlocked,
            success: true,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs that the identity key was created.
    /// </summary>
    public Task LogIdentityKeyCreatedAsync(
        string fingerprint,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.IdentityKeyCreated,
            details: $"Fingerprint: {fingerprint}",
            success: true,
            cancellationToken: cancellationToken
        );
    }

    #endregion Logging Methods

    #region Query Methods

    /// <summary>
    /// Gets audit log entries for a specific peer.
    /// </summary>
    /// <param name="peerId">The peer ID to query.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of audit log entries.</returns>
    public async Task<System.Collections.Generic.List<AuditLogEntry>> GetEntriesForPeerAsync(
        Guid peerId,
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql = """
            SELECT Id, Timestamp, EventType, PeerId, PeerName, Details, IPAddress, Success
            FROM AuditLog
            WHERE PeerId = @peerId
            ORDER BY Timestamp DESC
            LIMIT @limit
            """;

        return await this.ExecuteQueryAsync(
                sql,
                limit,
                cancellationToken,
                ("@peerId", peerId.ToString())
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets recent audit log entries.
    /// </summary>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of audit log entries.</returns>
    public async Task<System.Collections.Generic.List<AuditLogEntry>> GetRecentEntriesAsync(
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql = """
            SELECT Id, Timestamp, EventType, PeerId, PeerName, Details, IPAddress, Success
            FROM AuditLog
            ORDER BY Timestamp DESC
            LIMIT @limit
            """;

        return await this.ExecuteQueryAsync(sql, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets audit log entries by event type.
    /// </summary>
    /// <param name="eventType">The event type to filter by.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of audit log entries.</returns>
    public async Task<System.Collections.Generic.List<AuditLogEntry>> GetEntriesByTypeAsync(
        string eventType,
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql = """
            SELECT Id, Timestamp, EventType, PeerId, PeerName, Details, IPAddress, Success
            FROM AuditLog
            WHERE EventType = @eventType
            ORDER BY Timestamp DESC
            LIMIT @limit
            """;

        return await this.ExecuteQueryAsync(
                sql,
                limit,
                cancellationToken,
                ("@eventType", eventType)
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a query and returns audit log entries.
    /// </summary>
    private async Task<System.Collections.Generic.List<AuditLogEntry>> ExecuteQueryAsync(
        string sql,
        int limit,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters
    )
    {
        System.Collections.Generic.List<AuditLogEntry> entries = [];

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@limit", limit);
            foreach ((string name, object value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            )
            {
                entries.Add(
                    new AuditLogEntry
                    {
                        Id = reader.GetInt64(0),
                        Timestamp = DateTimeOffset.FromUnixTimeSeconds(
                            reader.GetInt64(1)
                        ),
                        EventType = reader.GetString(2),
                        PeerId = reader.IsDBNull(3)
                            ? null
                            : Guid.Parse(reader.GetString(3)),
                        PeerName = reader.IsDBNull(4)
                            ? null
                            : reader.GetString(4),
                        Details = reader.IsDBNull(5)
                            ? null
                            : reader.GetString(5),
                        IPAddress = reader.IsDBNull(6)
                            ? null
                            : reader.GetString(6),
                        Success = reader.IsDBNull(7)
                            ? null
                            : reader.GetInt32(7) == 1,
                    }
                );
            }

            return entries;
        }
        finally
        {
            this.m_dbLock.Release();
        }
    }

    #endregion Query Methods

    #region Helper Methods

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this.m_disposed, this);
    }

    private void ThrowIfNotInitialized()
    {
        if (this.m_connection is null)
        {
            throw new InvalidOperationException(
                "Audit log not initialized. Call InitializeAsync first."
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

/// <summary>
/// Represents an entry in the security audit log.
/// </summary>
public sealed record AuditLogEntry
{
    /// <summary>
    /// Gets or sets the unique identifier for the log entry.
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Gets or sets the timestamp of the event.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Gets or sets the type of event.
    /// </summary>
    public required string EventType { get; init; }

    /// <summary>
    /// Gets or sets the peer ID associated with the event.
    /// </summary>
    public Guid? PeerId { get; init; }

    /// <summary>
    /// Gets or sets the peer name associated with the event.
    /// </summary>
    public string? PeerName { get; init; }

    /// <summary>
    /// Gets or sets additional details about the event.
    /// </summary>
    public string? Details { get; init; }

    /// <summary>
    /// Gets or sets the IP address associated with the event.
    /// </summary>
    public string? IPAddress { get; init; }

    /// <summary>
    /// Gets or sets whether the operation was successful.
    /// </summary>
    public bool? Success { get; init; }
}
