using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using P2PFileExchange.Core.Utilities;

namespace P2PFileExchange.Core.Security;

/// <summary>
/// Severity levels for audit log events.
/// </summary>
public enum AuditSeverity
{
    /// <summary>Normal operations (handshake complete, peer trusted).</summary>
    Info,

    /// <summary>Suspicious but not blocking (timestamp slightly off).</summary>
    Warning,

    /// <summary>Security violation (invalid signature, replay).</summary>
    Error,

    /// <summary>Active attack suspected (key mismatch, tampering).</summary>
    Critical,
}

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
        #region Identity Events

        /// <summary>New Ed25519 keypair created.</summary>
        public const string IdentityGenerated = "IDENTITY_GENERATED";

        /// <summary>Existing identity decrypted and loaded.</summary>
        public const string IdentityLoaded = "IDENTITY_LOADED";

        /// <summary>Identity key exported to file.</summary>
        public const string IdentityExported = "IDENTITY_EXPORTED";

        /// <summary>Old identity deleted, new one generated.</summary>
        public const string IdentityRegenerated = "IDENTITY_REGENERATED";

        /// <summary>Master password updated.</summary>
        public const string PasswordChanged = "PASSWORD_CHANGED";

        /// <summary>Password stored in OS keyring for auto-unlock.</summary>
        public const string AutoUnlockEnabled = "AUTO_UNLOCK_ENABLED";

        /// <summary>Password removed from OS keyring.</summary>
        public const string AutoUnlockDisabled = "AUTO_UNLOCK_DISABLED";

        #endregion Identity Events

        #region Discovery Events

        /// <summary>First contact with peer, signature verified.</summary>
        public const string NewPeerDiscovered = "NEW_PEER_DISCOVERED";

        /// <summary>A new peer was explicitly trusted by user.</summary>
        public const string NewPeerTrusted = "NEW_PEER_TRUSTED";

        /// <summary>Peer presented different Ed25519 key than expected.</summary>
        public const string KeyMismatchDetected = "KEY_MISMATCH_DETECTED";

        /// <summary>User approved a key change for existing peer.</summary>
        public const string KeyChangeApproved = "KEY_CHANGE_APPROVED";

        /// <summary>User blocked a peer.</summary>
        public const string PeerBlocked = "PEER_BLOCKED";

        /// <summary>User unblocked a peer.</summary>
        public const string PeerUnblocked = "PEER_UNBLOCKED";

        /// <summary>Peer deleted from trust database.</summary>
        public const string PeerRemoved = "PEER_REMOVED";

        /// <summary>Ed25519 signature verification failed.</summary>
        public const string SignatureInvalid = "SIGNATURE_INVALID";

        /// <summary>PeerId didn't match derived from public key.</summary>
        public const string PeerIdSpoofing = "PEERID_SPOOFING";

        /// <summary>Duplicate nonce detected (replay attack).</summary>
        public const string ReplayDetected = "REPLAY_DETECTED";

        /// <summary>Timestamp outside acceptable range.</summary>
        public const string TimestampInvalid = "TIMESTAMP_INVALID";

        #endregion Discovery Events

        #region Connection Events

        /// <summary>SecureP2PStream handshake started.</summary>
        public const string HandshakeInitiated = "HANDSHAKE_INITIATED";

        /// <summary>Handshake successful, session keys derived.</summary>
        public const string HandshakeComplete = "HANDSHAKE_COMPLETE";

        /// <summary>Handshake timeout or error.</summary>
        public const string HandshakeFailed = "HANDSHAKE_FAILED";

        /// <summary>Peer key didn't match TOFU expectation during handshake.</summary>
        public const string KeyMismatchHandshake = "KEY_MISMATCH_HANDSHAKE";

        /// <summary>Frame tag verification failed (tampering detected).</summary>
        public const string TamperingDetected = "TAMPERING_DETECTED";

        /// <summary>Frame received out of order (replay attempt).</summary>
        public const string FrameOutOfOrder = "FRAME_OUT_OF_ORDER";

        /// <summary>Stream disposed, session keys cleared.</summary>
        public const string ConnectionClosed = "CONNECTION_CLOSED";

        #endregion Connection Events

        #region Transfer Events

        /// <summary>Transfer request received from peer.</summary>
        public const string TransferRequested = "TRANSFER_REQUESTED";

        /// <summary>User accepted incoming transfer.</summary>
        public const string TransferAccepted = "TRANSFER_ACCEPTED";

        /// <summary>A transfer was completed successfully.</summary>
        public const string TransferCompleted = "TRANSFER_COMPLETED";

        /// <summary>A transfer failed.</summary>
        public const string TransferFailed = "TRANSFER_FAILED";

        /// <summary>A transfer was rejected by the user.</summary>
        public const string TransferRejected = "TRANSFER_REJECTED";

        #endregion Transfer Events
    }

    #endregion Audit Event Types

    #region Fields

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
        this.DatabasePath = databasePath ?? DefaultAuditLogPath;
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
    public string DatabasePath { get; }

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
                Severity INTEGER NOT NULL DEFAULT 0,
                PeerId TEXT,
                PeerName TEXT,
                Details TEXT,
                IPAddress TEXT,
                Success INTEGER
            );

            CREATE INDEX IF NOT EXISTS idx_timestamp ON AuditLog(Timestamp);
            CREATE INDEX IF NOT EXISTS idx_event_type ON AuditLog(EventType);
            CREATE INDEX IF NOT EXISTS idx_severity ON AuditLog(Severity);
            CREATE INDEX IF NOT EXISTS idx_peer_id ON AuditLog(PeerId);
            """;

        // Migration: Add Severity column if it doesn't exist (for existing databases)
        const string migrationSql = """
            ALTER TABLE AuditLog ADD COLUMN Severity INTEGER NOT NULL DEFAULT 0;
            """;

        await using SqliteCommand command = this.m_connection!.CreateCommand();
        command.CommandText = createTableSql;
        _ = await command
            .ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        // Try to add Severity column for existing databases (ignore if already exists)
        try
        {
            await using SqliteCommand migrationCommand =
                this.m_connection.CreateCommand();
            migrationCommand.CommandText = migrationSql;
            _ = await migrationCommand
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // Column already exists, ignore
        }
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
    /// <param name="severity">The severity level of the event.</param>
    /// <param name="peerId">The peer ID associated with the event (optional).</param>
    /// <param name="peerName">The peer name associated with the event (optional).</param>
    /// <param name="details">Additional details about the event (optional).</param>
    /// <param name="ipAddress">The IP address associated with the event (optional).</param>
    /// <param name="success">Whether the operation was successful (optional).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task LogEventAsync(
        string eventType,
        AuditSeverity severity = AuditSeverity.Info,
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
            INSERT INTO AuditLog (Timestamp, EventType, Severity, PeerId, PeerName, Details, IPAddress, Success)
            VALUES (@timestamp, @eventType, @severity, @peerId, @peerName, @details, @ipAddress, @success)
            """;

        await this.m_dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command =
                this.m_connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@timestamp", timestamp);
            command.Parameters.AddWithValue("@eventType", eventType);
            command.Parameters.AddWithValue("@severity", (int)severity);
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

    #region Identity Events

    /// <summary>
    /// Logs that the identity key was generated.
    /// </summary>
    public Task LogIdentityGeneratedAsync(
        string fingerprint,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.IdentityGenerated,
            AuditSeverity.Info,
            details: $"Fingerprint: {fingerprint}",
            success: true,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs that the identity key was loaded/unlocked.
    /// </summary>
    public Task LogIdentityLoadedAsync(
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.IdentityLoaded,
            AuditSeverity.Info,
            success: true,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs that the identity key was exported.
    /// </summary>
    public Task LogIdentityExportedAsync(
        string destinationPath,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.IdentityExported,
            AuditSeverity.Warning,
            details: $"Exported to: {destinationPath}",
            success: true,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs that the identity was regenerated.
    /// </summary>
    public Task LogIdentityRegeneratedAsync(
        string newFingerprint,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.IdentityRegenerated,
            AuditSeverity.Warning,
            details: $"New fingerprint: {newFingerprint}",
            success: true,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs that auto-unlock was enabled.
    /// </summary>
    public Task LogAutoUnlockEnabledAsync(
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.AutoUnlockEnabled,
            AuditSeverity.Info,
            success: true,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs that auto-unlock was disabled.
    /// </summary>
    public Task LogAutoUnlockDisabledAsync(
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.AutoUnlockDisabled,
            AuditSeverity.Info,
            success: true,
            cancellationToken: cancellationToken
        );
    }

    #endregion Identity Events

    #region Discovery Events

    /// <summary>
    /// Logs that a new peer was discovered.
    /// </summary>
    public Task LogNewPeerDiscoveredAsync(
        Guid peerId,
        string peerName,
        string fingerprint,
        string? ipAddress = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.NewPeerDiscovered,
            AuditSeverity.Info,
            peerId,
            peerName,
            $"Fingerprint: {fingerprint}",
            ipAddress,
            true,
            cancellationToken
        );
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
            AuditSeverity.Info,
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
            AuditSeverity.Critical,
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
            AuditSeverity.Warning,
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
            AuditSeverity.Warning,
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
            AuditSeverity.Info,
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
            AuditSeverity.Info,
            peerId,
            peerName,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs an invalid signature during discovery.
    /// </summary>
    public Task LogSignatureInvalidAsync(
        Guid peerId,
        string? peerName = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.SignatureInvalid,
            AuditSeverity.Error,
            peerId,
            peerName,
            "Ed25519 signature verification failed",
            ipAddress,
            false,
            cancellationToken
        );
    }

    /// <summary>
    /// Logs a PeerId spoofing attempt.
    /// </summary>
    public Task LogPeerIdSpoofingAsync(
        Guid claimedPeerId,
        Guid derivedPeerId,
        string? ipAddress = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.PeerIdSpoofing,
            AuditSeverity.Critical,
            claimedPeerId,
            details: $"Claimed: {claimedPeerId}, Derived from key: {derivedPeerId}",
            ipAddress: ipAddress,
            success: false,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs a replay attack detection.
    /// </summary>
    public Task LogReplayDetectedAsync(
        Guid peerId,
        string? peerName = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.ReplayDetected,
            AuditSeverity.Error,
            peerId,
            peerName,
            "Duplicate nonce detected",
            ipAddress,
            false,
            cancellationToken
        );
    }

    /// <summary>
    /// Logs an invalid timestamp.
    /// </summary>
    public Task LogTimestampInvalidAsync(
        Guid peerId,
        long timestamp,
        string? ipAddress = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.TimestampInvalid,
            AuditSeverity.Warning,
            peerId,
            details: $"Timestamp: {timestamp} (outside acceptable range)",
            ipAddress: ipAddress,
            success: false,
            cancellationToken: cancellationToken
        );
    }

    #endregion Discovery Events

    #region Connection Events

    /// <summary>
    /// Logs that a handshake was initiated.
    /// </summary>
    public Task LogHandshakeInitiatedAsync(
        Guid? peerId = null,
        string? peerName = null,
        bool isInitiator = false,
        string? ipAddress = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.HandshakeInitiated,
            AuditSeverity.Info,
            peerId,
            peerName,
            $"Role: {(isInitiator ? "Initiator" : "Responder")}",
            ipAddress,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs that a handshake completed successfully.
    /// </summary>
    public Task LogHandshakeCompleteAsync(
        Guid peerId,
        string? peerName = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.HandshakeComplete,
            AuditSeverity.Info,
            peerId,
            peerName,
            "Session keys derived successfully",
            ipAddress,
            true,
            cancellationToken
        );
    }

    /// <summary>
    /// Logs that a handshake failed.
    /// </summary>
    public Task LogHandshakeFailedAsync(
        Guid? peerId = null,
        string? reason = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.HandshakeFailed,
            AuditSeverity.Error,
            peerId,
            details: reason ?? "Handshake timeout or error",
            ipAddress: ipAddress,
            success: false,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs a key mismatch during handshake.
    /// </summary>
    public Task LogKeyMismatchHandshakeAsync(
        Guid peerId,
        string expectedFingerprint,
        string actualFingerprint,
        string? ipAddress = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.KeyMismatchHandshake,
            AuditSeverity.Critical,
            peerId,
            details: $"Expected: {expectedFingerprint}, Actual: {actualFingerprint}",
            ipAddress: ipAddress,
            success: false,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs that tampering was detected (frame tag verification failed).
    /// </summary>
    public Task LogTamperingDetectedAsync(
        Guid? peerId = null,
        long? frameNumber = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.TamperingDetected,
            AuditSeverity.Critical,
            peerId,
            details: frameNumber.HasValue
                ? $"Frame #{frameNumber} tag verification failed"
                : "Frame tag verification failed",
            ipAddress: ipAddress,
            success: false,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs that a connection was closed.
    /// </summary>
    public Task LogConnectionClosedAsync(
        Guid? peerId = null,
        string? peerName = null,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.ConnectionClosed,
            AuditSeverity.Info,
            peerId,
            peerName,
            "Session keys cleared",
            cancellationToken: cancellationToken
        );
    }

    #endregion Connection Events

    #region Transfer Events

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
            AuditSeverity.Info,
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
            AuditSeverity.Error,
            peerId,
            peerName,
            $"File '{fileName}' failed: {errorMessage ?? "Unknown error"}",
            success: false,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs a transfer request received.
    /// </summary>
    public Task LogTransferRequestedAsync(
        Guid peerId,
        string peerName,
        string fileName,
        long fileSize,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.TransferRequested,
            AuditSeverity.Info,
            peerId,
            peerName,
            $"File '{fileName}' ({fileSize} bytes)",
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs a transfer was accepted.
    /// </summary>
    public Task LogTransferAcceptedAsync(
        Guid peerId,
        string peerName,
        string fileName,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.TransferAccepted,
            AuditSeverity.Info,
            peerId,
            peerName,
            $"File '{fileName}' accepted",
            success: true,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Logs a transfer was rejected.
    /// </summary>
    public Task LogTransferRejectedAsync(
        Guid peerId,
        string peerName,
        string fileName,
        CancellationToken cancellationToken = default
    )
    {
        return this.LogEventAsync(
            EventTypes.TransferRejected,
            AuditSeverity.Info,
            peerId,
            peerName,
            $"File '{fileName}' rejected by user",
            success: false,
            cancellationToken: cancellationToken
        );
    }

    #endregion Transfer Events

    #endregion Logging Methods

    #region Query Methods

    /// <summary>
    /// Gets audit log entries for a specific peer.
    /// </summary>
    /// <param name="peerId">The peer ID to query.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of audit log entries.</returns>
    public async Task<List<AuditLogEntry>> GetEntriesForPeerAsync(
        Guid peerId,
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql = """
            SELECT Id, Timestamp, EventType, Severity, PeerId, PeerName, Details, IPAddress, Success
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
    public async Task<List<AuditLogEntry>> GetRecentEntriesAsync(
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql = """
            SELECT Id, Timestamp, EventType, Severity, PeerId, PeerName, Details, IPAddress, Success
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
    public async Task<List<AuditLogEntry>> GetEntriesByTypeAsync(
        string eventType,
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        this.ThrowIfNotInitialized();

        const string sql = """
            SELECT Id, Timestamp, EventType, Severity, PeerId, PeerName, Details, IPAddress, Success
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
    private async Task<List<AuditLogEntry>> ExecuteQueryAsync(
        string sql,
        int limit,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters
    )
    {
        List<AuditLogEntry> entries = [];

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
                        Severity = (AuditSeverity)reader.GetInt32(3),
                        PeerId = reader.IsDBNull(4)
                            ? null
                            : Guid.Parse(reader.GetString(4)),
                        PeerName = reader.IsDBNull(5)
                            ? null
                            : reader.GetString(5),
                        Details = reader.IsDBNull(6)
                            ? null
                            : reader.GetString(6),
                        IPAddress = reader.IsDBNull(7)
                            ? null
                            : reader.GetString(7),
                        Success = reader.IsDBNull(8)
                            ? null
                            : reader.GetInt32(8) == 1,
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

        if (this.m_connection is not null)
        {
            await this.m_connection.CloseAsync().ConfigureAwait(false);
            await this.m_connection.DisposeAsync().ConfigureAwait(false);
            this.m_connection = null;
        }

        this.m_disposed = true;
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
    /// Gets or sets the severity level of the event.
    /// </summary>
    public AuditSeverity Severity { get; init; } = AuditSeverity.Info;

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
