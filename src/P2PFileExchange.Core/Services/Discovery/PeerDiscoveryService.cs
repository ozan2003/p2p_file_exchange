using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using P2PFileExchange.Core.Models;
using P2PFileExchange.Core.Serialization;
using P2PFileExchange.Core.Services.Security;
using P2PFileExchange.Core.Utilities;

namespace P2PFileExchange.Core.Services.Discovery;

/// <summary>
/// Handles UDP broadcast discovery of peers on the local network.
///
/// <list type="bullet">
/// <item>Broadcasts signed peer announcements over UDP at a configured interval.</item>
/// <item>Listens for announcements, validates fingerprints, and verifies ECDSA signatures.</item>
/// <item>Ignores self-announcements, deduplicates within a time window, and updates the peer registry.</item>
/// <item>Raises events when peers are added/updated/removed and when status changes.</item>
/// <item>Periodically cleans up stale peers based on the configured timeout.</item>
/// </list>
/// </summary>
public sealed class PeerDiscoveryService : IPeerDiscoveryService
{
    #region Constants
    /// <summary>
    /// The deduplication window. If a same peer sends an announcement within this window, it will be ignored.
    /// </summary>
    private static readonly TimeSpan s_deduplicationWindow =
        TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum allowed clock skew for timestamp validation (30 seconds).
    /// </summary>
    private static readonly TimeSpan s_maxClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to retain nonces for replay protection (2 minutes).
    /// </summary>
    private static readonly TimeSpan s_nonceRetentionPeriod =
        TimeSpan.FromMinutes(2);

    /// <summary>
    /// Rate limit: maximum announcements per peer per minute.
    /// </summary>
    private const int MaxAnnouncementsPerMinute = 30;

    /// <summary>
    /// JSON serialization options for canonical signing.
    /// </summary>
    private static readonly JsonSerializerOptions s_canonicalJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
    #endregion Constants

    #region Configuration
    /// <summary>
    /// Settings for the peer discovery service.
    /// </summary>
    private readonly PeerDiscoveryOptions m_options;

    /// <summary>
    /// JSON serialization settings.
    /// </summary>
    private readonly JsonSerializerOptions m_jsonOptions;
    #endregion Configuration

    #region Peer State
    /// <summary>
    /// Collection of discovered peers by their ID.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, PeerInfo> m_peers = new();

    /// <summary>
    /// Seen nonces for replay protection: nonce hash -> expiration time.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> m_seenNonces =
        new();

    /// <summary>
    /// Rate limiting: peer ID -> (window start, count).
    /// </summary>
    private readonly ConcurrentDictionary<
        Guid,
        (DateTimeOffset WindowStart, int Count)
    > m_rateLimits = new();
    #endregion Peer State

    #region Synchronization
    /// <summary>
    /// Semaphore to protect start/stop operations.
    /// </summary>
    private readonly SemaphoreSlim m_stateLock = new(1, 1);

    /// <summary>
    /// Cancellation token source for discovery operations.
    /// </summary>
    private CancellationTokenSource? m_discoveryCts;
    #endregion Synchronization

    #region Networking
    /// <summary>
    /// UDP client for broadcasting announcements.
    /// </summary>
    private UdpClient? m_broadcastClient;

    /// <summary>
    /// UDP client for listening to announcements.
    /// </summary>
    private UdpClient? m_listenClient;
    #endregion Networking

    #region Background Tasks
    /// <summary>
    /// Task for broadcasting announcements.
    /// </summary>
    private Task? m_broadcastTask;

    /// <summary>
    /// Task for listening to announcements.
    /// </summary>
    private Task? m_listenTask;

    /// <summary>
    /// Task for cleaning up stale peers.
    /// </summary>
    private Task? m_cleanupTask;
    #endregion Background Tasks

    #region Local Identity
    /// <summary>
    /// The local peer's display name.
    /// </summary>
    private string m_displayName = string.Empty;

    /// <summary>
    /// The local peer's certificate fingerprint.
    /// </summary>
    private string m_certificateFingerprint = string.Empty;

    /// <summary>
    /// The local peer's TCP port for incoming connections.
    /// </summary>
    private ushort m_tcpPort;

    /// <summary>
    /// The local peer's Ed25519 identity key manager.
    /// </summary>
    private IdentityKeyManager? m_identityKeyManager;
    #endregion Local Identity

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerDiscoveryService"/> class.
    /// </summary>
    public PeerDiscoveryService()
        : this(new PeerDiscoveryOptions()) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerDiscoveryService"/> class with options.
    /// </summary>
    /// <param name="options">The discovery options.</param>
    public PeerDiscoveryService(PeerDiscoveryOptions options)
    {
        this.m_options =
            options ?? throw new ArgumentNullException(nameof(options));
        this.m_jsonOptions = new JsonSerializerOptions(
            JsonSerializerDefaults.Web
        );
        // Add our own custom IP address serializer.
        this.m_jsonOptions.Converters.Add(new IPAddressConverter());
    }

    /// <inheritdoc />
    public event EventHandler<PeerInfo>? PeerUpdated;

    /// <inheritdoc />
    public event EventHandler<Guid>? PeerRemoved;

    /// <inheritdoc />
    public event EventHandler<string>? StatusChanged;

    /// <inheritdoc />
    public Guid LocalPeerId => this.m_identityKeyManager?.PeerId ?? Guid.Empty;

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public ushort BroadcastPort => this.m_options.BroadcastPort;

    /// <inheritdoc />
    public async Task StartAsync(
        ushort tcpPort,
        ReadOnlyMemory<char> displayName,
        ReadOnlyMemory<char> certificateFingerprint,
        IdentityKeyManager identityKeyManager,
        CancellationToken cancellationToken
    )
    {
        if (tcpPort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tcpPort));
        }

        ArgumentNullException.ThrowIfNull(
            identityKeyManager,
            nameof(identityKeyManager)
        );
        if (!identityKeyManager.IsLoaded)
        {
            throw new InvalidOperationException(
                "Identity key must be loaded before starting discovery."
            );
        }

        await this
            .m_stateLock.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (this.IsRunning)
            {
                return;
            }

            this.m_tcpPort = tcpPort;
            this.m_displayName = displayName.Trim().ToString();
            this.m_certificateFingerprint = certificateFingerprint
                .Trim()
                .ToString();
            this.m_identityKeyManager = identityKeyManager;

            this.m_discoveryCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
            CancellationToken token = this.m_discoveryCts.Token;

            this.m_listenClient = this.CreateListenerClient();
            this.m_broadcastClient = CreateBroadcastClient();

            this.m_listenTask = this.ListenLoopAsync(
                this.m_listenClient,
                token
            );
            this.m_broadcastTask = this.BroadcastLoopAsync(
                this.m_broadcastClient,
                token
            );
            this.m_cleanupTask = this.CleanupLoopAsync(token);

            this.IsRunning = true;
            StatusChanged?.Invoke(this, "Discovery started.");
        }
        finally
        {
            this.m_stateLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        await this.m_stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!this.IsRunning)
            {
                return;
            }

            this.m_discoveryCts?.Cancel();
            this.m_listenClient?.Close();
            this.m_broadcastClient?.Close();

            List<Task> tasks = [];
            if (this.m_listenTask != null)
            {
                tasks.Add(this.m_listenTask);
            }

            if (this.m_broadcastTask != null)
            {
                tasks.Add(this.m_broadcastTask);
            }

            if (this.m_cleanupTask != null)
            {
                tasks.Add(this.m_cleanupTask);
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(
                        tasks.Select(task => task.ContinueWith(_ => { }))
                    )
                    .ConfigureAwait(false);
            }

            this.m_listenClient?.Dispose();
            this.m_broadcastClient?.Dispose();
            this.m_discoveryCts?.Dispose();

            this.m_listenClient = null;
            this.m_broadcastClient = null;
            this.m_discoveryCts = null;
            this.m_listenTask = null;
            this.m_broadcastTask = null;
            this.m_cleanupTask = null;

            this.IsRunning = false;
            StatusChanged?.Invoke(this, "Discovery stopped.");
        }
        finally
        {
            this.m_stateLock.Release();
        }
    }

    /// <inheritdoc />
    public void UpdateDisplayName(ReadOnlySpan<char> displayName)
    {
        this.m_displayName = displayName.Trim().ToString();
    }

    /// <inheritdoc />
    public IReadOnlyCollection<PeerInfo> GetPeers()
    {
        return [.. this.m_peers.Values];
    }

    /// <inheritdoc />
    public string? GetPeerFingerprintByIPAddress(IPAddress ipAddress)
    {
        if (ipAddress == null)
        {
            return null;
        }

        return this
            .m_peers.Values.FirstOrDefault(peer =>
                peer.IPAddress == ipAddress
                && !string.IsNullOrWhiteSpace(peer.CertificateFingerprint)
            )
            ?.CertificateFingerprint;
    }

    /// <inheritdoc />
    public string? GetPeerDisplayNameByIPAddress(IPAddress ipAddress)
    {
        if (ipAddress == null)
        {
            return null;
        }

        return this
            .m_peers.Values.FirstOrDefault(peer =>
                peer.IPAddress == ipAddress
                && !string.IsNullOrWhiteSpace(peer.DisplayName)
            )
            ?.DisplayName;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
        this.m_stateLock.Dispose();
    }

    /// <summary>
    /// Normalizes a display name by trimming whitespace and providing a default value.
    /// </summary>
    /// <param name="name">The raw display name.</param>
    /// <returns>The normalized display name, or "Unknown" if empty.</returns>
    private static string NormalizeDisplayName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || MemoryExtensions.IsWhiteSpace(name))
        {
            return "Unknown";
        }
        return name.Trim().ToString();
    }

    /// <summary>
    /// Validates that a certificate fingerprint is a valid SHA-256 hex string (64 characters).
    /// </summary>
    /// <param name="fingerprint">The fingerprint to validate.</param>
    /// <returns>True if the fingerprint is valid; otherwise, false.</returns>
    private static bool IsValidCertificateFingerprint(
        ReadOnlySpan<char> fingerprint
    )
    {
        if (fingerprint.IsEmpty || MemoryExtensions.IsWhiteSpace(fingerprint))
        {
            return false;
        }

        // SHA-256 produces 32 bytes = 64 hex characters.
        if (fingerprint.Length != 64)
        {
            return false;
        }

        // Verify all characters are valid hex digits.
        foreach (char ch in fingerprint)
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Creates and configures a UDP client for listening to peer announcements.
    /// Binds to the configured broadcast port with address reuse enabled.
    /// </summary>
    /// <returns>A configured UDP client ready to receive broadcasts.</returns>
    private UdpClient CreateListenerClient()
    {
        UdpClient client = new(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true,
        };

        client.Client.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.ReuseAddress,
            true
        );
        client.Client.ExclusiveAddressUse = false;
        client.Client.Bind(
            new IPEndPoint(IPAddress.Any, this.m_options.BroadcastPort)
        );
        return client;
    }

    /// <summary>
    /// Creates and configures a UDP client for sending broadcast announcements.
    /// </summary>
    /// <returns>A configured UDP client ready to send broadcasts.</returns>
    private static UdpClient CreateBroadcastClient()
    {
        UdpClient client = new(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true,
        };

        client.Client.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.Broadcast,
            true
        );
        return client;
    }

    /// <summary>
    /// Periodically broadcasts the local peer's announcement to the network.
    /// Runs at the configured interval until cancellation.
    /// </summary>
    /// <param name="client">The UDP client used for broadcasting.</param>
    /// <param name="cancellationToken">A token to signal loop termination.</param>
    private async Task BroadcastLoopAsync(
        UdpClient client,
        CancellationToken cancellationToken
    )
    {
        IPEndPoint endpoint = new(
            this.m_options.BroadcastAddress,
            this.m_options.BroadcastPort
        );
        using PeriodicTimer timer = new(this.m_options.BroadcastInterval);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                string displayName = NormalizeDisplayName(this.m_displayName);

                // Generate a fresh nonce for replay protection
                byte[] nonce = new byte[16];
                RandomNumberGenerator.Fill(nonce);
                string nonceBase64 = Convert.ToBase64String(nonce);

                // Current timestamp for freshness
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Create canonical JSON for signing (sorted keys, no signature field)
                string canonicalJson = CreateCanonicalSigningJson(
                    this.LocalPeerId,
                    displayName,
                    NetworkUtilities.GetPrimaryIPv4Address(),
                    this.m_tcpPort,
                    this.m_certificateFingerprint,
                    this.m_identityKeyManager!.PublicKeyBase64,
                    timestamp,
                    nonceBase64
                );

                // Sign using Ed25519
                byte[] signingData = Encoding.UTF8.GetBytes(canonicalJson);
                byte[] signatureBytes = this.m_identityKeyManager.Sign(
                    signingData
                );
                string signature = Convert.ToBase64String(signatureBytes);

                PeerAnnouncement announcement = new()
                {
                    PeerId = this.LocalPeerId,
                    DisplayName = displayName,
                    IPAddress = NetworkUtilities.GetPrimaryIPv4Address(),
                    TcpPort = this.m_tcpPort,
                    CertificateFingerprint = this.m_certificateFingerprint,
                    PublicKey = this.m_identityKeyManager.PublicKeyBase64,
                    Timestamp = timestamp,
                    Nonce = nonceBase64,
                    Signature = signature,
                };

                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                    announcement,
                    this.m_jsonOptions
                );
                await client
                    .SendAsync(payload, payload.Length, endpoint)
                    .ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                StatusChanged?.Invoke(this, $"Broadcast failed: {ex.Message}");
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            await timer
                .WaitForNextTickAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Continuously listens for peer announcements and updates the peer registry.
    /// Ignores announcements from the local peer, malformed payloads, and invalid signatures.
    /// </summary>
    /// <param name="client">The UDP client used for receiving announcements.</param>
    /// <param name="cancellationToken">A token to signal loop termination.</param>
    private async Task ListenLoopAsync(
        UdpClient client,
        CancellationToken cancellationToken
    )
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Receive the announcement from the network.
                UdpReceiveResult received = await client
                    .ReceiveAsync(cancellationToken)
                    .ConfigureAwait(false);

                // Assumed the format is valid JSON. Unpack it.
                PeerAnnouncement? announcement =
                    JsonSerializer.Deserialize<PeerAnnouncement>(
                        received.Buffer,
                        this.m_jsonOptions
                    );

                if (
                    announcement == null
                    || announcement.PeerId == this.LocalPeerId
                )
                {
                    continue;
                }

                if (announcement.TcpPort == 0)
                {
                    continue;
                }

                // Ignore announcements without valid certificate fingerprint (SHA-256 = 64 hex chars).
                if (
                    !IsValidCertificateFingerprint(
                        announcement.CertificateFingerprint
                    )
                )
                {
                    continue;
                }

                // Rate limiting check
                if (!this.CheckRateLimit(announcement.PeerId))
                {
                    StatusChanged?.Invoke(
                        this,
                        $"Rate limit exceeded for peer {announcement.PeerId}: discarding announcement."
                    );
                    continue;
                }

                // Full verification: signature, timestamp, nonce, PeerId derivation, TOFU
                (bool isValid, string? error) = this.VerifyAnnouncement(
                    announcement
                );
                if (!isValid)
                {
                    StatusChanged?.Invoke(
                        this,
                        $"Invalid announcement from peer {announcement.PeerId}: {error}"
                    );
                    continue;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;

                // Cheap dedup check to avoid processing the same peer's announcement multiple times.
                // Still update LastSeen to prevent premature cleanup.
                if (
                    this.m_peers.TryGetValue(
                        announcement.PeerId,
                        out PeerInfo? existingPeer
                    )
                )
                {
                    bool isDuplicate =
                        now - existingPeer.LastSeen < s_deduplicationWindow;
                    existingPeer.LastSeen = now;
                    if (isDuplicate)
                    {
                        continue;
                    }
                }

                IPAddress ipAddress = announcement.IPAddress;
                if (ipAddress == null || ipAddress.Equals(IPAddress.None))
                {
                    ipAddress = received.RemoteEndPoint.Address;
                }

                // Decode the public key for storage
                byte[] publicKeyBytes = Convert.FromBase64String(
                    announcement.PublicKey
                );
                string identityFingerprint =
                    IdentityKeyManager.ComputeFingerprint(publicKeyBytes);

                // Peers info is updated here with TOFU support.
                PeerInfo peerInfo = this.m_peers.AddOrUpdate(
                    announcement.PeerId,
                    // Add a new peer if it doesn't exist (Trust-On-First-Use).
                    _ => new PeerInfo
                    {
                        PeerId = announcement.PeerId,
                        DisplayName = NormalizeDisplayName(
                            announcement.DisplayName
                        ),
                        IPAddress = ipAddress,
                        TcpPort = announcement.TcpPort,
                        LastSeen = now,
                        CertificateFingerprint =
                            announcement.CertificateFingerprint ?? string.Empty,
#pragma warning disable CS0618 // Keep for backward compatibility
                        PublicKey = announcement.PublicKey ?? string.Empty,
#pragma warning restore CS0618
                        IdentityPublicKey =
                            announcement.PublicKey ?? string.Empty,
                        IdentityFingerprint = identityFingerprint,
                        FirstTrusted = now,
                        IsVerified = false, // TOFU: not yet verified by user
                    },
                    // Update existing peer info otherwise.
                    (_, existing) =>
                    {
                        existing.DisplayName = NormalizeDisplayName(
                            announcement.DisplayName
                        );
                        existing.IPAddress = ipAddress;
                        existing.TcpPort = announcement.TcpPort;
                        // LastSeen is updated above in the dedup check.
                        existing.CertificateFingerprint =
                            announcement.CertificateFingerprint ?? string.Empty;
#pragma warning disable CS0618
                        existing.PublicKey =
                            announcement.PublicKey ?? string.Empty;
#pragma warning restore CS0618
                        // Identity fields are immutable after first trust
                        return existing;
                    }
                );

                // Notify the UI that the peer info has been updated.
                PeerUpdated?.Invoke(this, peerInfo);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exc)
            {
                StatusChanged?.Invoke(
                    this,
                    $"Discovery listen failed: {exc.Message}"
                );
            }
            catch (JsonException)
            {
                // Ignore malformed payloads.
            }
        }
    }

    /// <summary>
    /// Verifies an announcement: signature, timestamp, nonce, PeerId derivation, and TOFU.
    /// </summary>
    /// <param name="announcement">The announcement to verify.</param>
    /// <returns>A tuple containing success status and optional error message.</returns>
    private (bool IsValid, string? Error) VerifyAnnouncement(
        PeerAnnouncement announcement
    )
    {
        // Basic validation
        if (string.IsNullOrWhiteSpace(announcement.PublicKey))
        {
            return (false, "Missing public key");
        }

        if (string.IsNullOrWhiteSpace(announcement.Signature))
        {
            return (false, "Missing signature");
        }

        if (string.IsNullOrWhiteSpace(announcement.Nonce))
        {
            return (false, "Missing nonce");
        }

        try
        {
            // Decode public key
            byte[] publicKeyBytes = Convert.FromBase64String(
                announcement.PublicKey
            );
            if (publicKeyBytes.Length != IdentityKeyManager.PublicKeyLength)
            {
                return (false, "Invalid public key length");
            }

            // 1. Verify PeerId is derived from public key (cryptographic binding)
            Guid derivedPeerId = IdentityKeyManager.ComputePeerId(
                publicKeyBytes
            );
            if (derivedPeerId != announcement.PeerId)
            {
                return (false, "PeerId does not match public key");
            }

            // 2. Verify timestamp is within acceptable window
            DateTimeOffset announcementTime =
                DateTimeOffset.FromUnixTimeSeconds(announcement.Timestamp);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan drift = now - announcementTime;
            if (drift.Duration() > s_maxClockSkew)
            {
                return (
                    false,
                    $"Timestamp outside acceptable window (drift: {drift.TotalSeconds:F1}s)"
                );
            }

            // 3. Check for replay (nonce reuse)
            string nonceHash = ComputeNonceHash(
                announcement.PeerId,
                announcement.Nonce
            );
            DateTimeOffset nonceExpiration = now + s_nonceRetentionPeriod;
            if (!this.m_seenNonces.TryAdd(nonceHash, nonceExpiration))
            {
                return (false, "Replay detected: nonce already used");
            }

            // 4. TOFU check: if we know this peer, ensure identity hasn't changed
            if (
                this.m_peers.TryGetValue(
                    announcement.PeerId,
                    out PeerInfo? existingPeer
                )
            )
            {
                if (
                    !string.IsNullOrEmpty(existingPeer.IdentityPublicKey)
                    && !string.Equals(
                        existingPeer.IdentityPublicKey,
                        announcement.PublicKey,
                        StringComparison.Ordinal
                    )
                )
                {
                    return (
                        false,
                        "Identity key mismatch - possible impersonation attempt"
                    );
                }
            }

            // 5. Verify Ed25519 signature
            string canonicalJson = CreateCanonicalSigningJson(
                announcement.PeerId,
                announcement.DisplayName,
                announcement.IPAddress,
                announcement.TcpPort,
                announcement.CertificateFingerprint,
                announcement.PublicKey,
                announcement.Timestamp,
                announcement.Nonce
            );

            byte[] signingData = Encoding.UTF8.GetBytes(canonicalJson);
            byte[] signature = Convert.FromBase64String(announcement.Signature);

            if (
                !IdentityKeyManager.Verify(
                    signingData,
                    signature,
                    publicKeyBytes
                )
            )
            {
                return (false, "Signature verification failed");
            }

            return (true, null);
        }
        catch (FormatException)
        {
            return (false, "Invalid Base64 encoding");
        }
        catch (Exception ex)
        {
            return (false, $"Verification error: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a canonical JSON string for signing, with sorted keys and no signature field.
    /// </summary>
    private static string CreateCanonicalSigningJson(
        Guid peerId,
        string displayName,
        IPAddress ipAddress,
        ushort tcpPort,
        string certificateFingerprint,
        string publicKey,
        long timestamp,
        string nonce
    )
    {
        SortedDictionary<string, object?> payload = new()
        {
            ["certificateFingerprint"] = certificateFingerprint ?? string.Empty,
            ["displayName"] = displayName ?? string.Empty,
            ["ipAddress"] = ipAddress?.ToString() ?? string.Empty,
            ["nonce"] = nonce ?? string.Empty,
            ["peerId"] = peerId.ToString(),
            ["publicKey"] = publicKey ?? string.Empty,
            ["tcpPort"] = tcpPort,
            ["timestamp"] = timestamp,
        };

        return JsonSerializer.Serialize(payload, s_canonicalJsonOptions);
    }

    /// <summary>
    /// Escapes special characters in a JSON string value.
    /// </summary>
    private static string EscapeJsonString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    _ = sb.Append("\\\\");
                    break;
                case '"':
                    _ = sb.Append("\\\"");
                    break;
                case '\n':
                    _ = sb.Append("\\n");
                    break;
                case '\r':
                    _ = sb.Append("\\r");
                    break;
                case '\t':
                    _ = sb.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        _ = sb.Append($"\\u{(int)c:X4}");
                    }
                    else
                    {
                        _ = sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Computes a unique hash for a nonce to prevent replay attacks.
    /// </summary>
    private static string ComputeNonceHash(Guid peerId, string nonce)
    {
        byte[] data = Encoding.UTF8.GetBytes($"{peerId}:{nonce}");
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Checks if a peer is within rate limits.
    /// </summary>
    /// <param name="peerId">The peer ID to check.</param>
    /// <returns>True if within limits, false if rate limited.</returns>
    private bool CheckRateLimit(Guid peerId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset windowStart = now.AddMinutes(-1);

        (DateTimeOffset WindowStart, int Count) current =
            this.m_rateLimits.GetOrAdd(peerId, _ => (now, 0));

        if (current.WindowStart < windowStart)
        {
            // Reset window
            _ = this.m_rateLimits.TryUpdate(peerId, (now, 1), current);
            return true;
        }

        if (current.Count >= MaxAnnouncementsPerMinute)
        {
            return false;
        }

        // Increment counter
        _ = this.m_rateLimits.TryUpdate(
            peerId,
            (current.WindowStart, current.Count + 1),
            current
        );
        return true;
    }

    /// <summary>
    /// Periodically removes stale peers that have not been seen within the timeout period.
    /// Also cleans up expired nonces and rate limit entries.
    /// Runs at the configured cleanup interval until cancellation.
    /// </summary>
    /// <param name="cancellationToken">A token to signal loop termination.</param>
    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(this.m_options.CleanupInterval);
        while (
            await timer
                .WaitForNextTickAsync(cancellationToken)
                .ConfigureAwait(false)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset expiration = now - this.m_options.PeerTimeout;

            // Clean up stale peers
            foreach ((Guid guid, PeerInfo peer) in this.m_peers)
            {
                if (peer.LastSeen >= expiration)
                {
                    continue;
                }

                if (this.m_peers.TryRemove(guid, out _))
                {
                    PeerRemoved?.Invoke(this, guid);
                }
            }

            // Clean up expired nonces
            foreach (
                (
                    string nonceHash,
                    DateTimeOffset nonceExpiration
                ) in this.m_seenNonces
            )
            {
                if (nonceExpiration < now)
                {
                    _ = this.m_seenNonces.TryRemove(nonceHash, out _);
                }
            }

            // Clean up old rate limit entries
            DateTimeOffset rateLimitExpiration = now.AddMinutes(-2);
            foreach (
                (
                    Guid peerId,
                    (DateTimeOffset WindowStart, int _) entry
                ) in this.m_rateLimits
            )
            {
                if (entry.WindowStart < rateLimitExpiration)
                {
                    _ = this.m_rateLimits.TryRemove(peerId, out _);
                }
            }
        }
    }

    /// <summary>
    /// Internal DTO representing a peer announcement payload broadcast over UDP.
    ///
    /// Each peer sends its own info to the network.
    ///
    /// Serialized to/from JSON for network transmission.
    /// </summary>
    private sealed class PeerAnnouncement
    {
        [JsonPropertyName("peerId")]
        public Guid PeerId { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("ipAddress")]
        public IPAddress IPAddress { get; set; } = IPAddress.None;

        [JsonPropertyName("tcpPort")]
        public ushort TcpPort { get; set; }

        [JsonPropertyName("certificateFingerprint")]
        public string CertificateFingerprint { get; set; } = string.Empty;

        /// <summary>
        /// Base64-encoded Ed25519 public key (32 bytes) for signature verification.
        /// </summary>
        [JsonPropertyName("publicKey")]
        public string PublicKey { get; set; } = string.Empty;

        /// <summary>
        /// Unix timestamp (seconds since epoch) for replay protection.
        /// </summary>
        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        /// <summary>
        /// Base64-encoded random nonce (16 bytes) for replay protection.
        /// </summary>
        [JsonPropertyName("nonce")]
        public string Nonce { get; set; } = string.Empty;

        /// <summary>
        /// Base64-encoded Ed25519 signature (64 bytes) over canonical JSON of all other fields.
        /// </summary>
        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;
    }
}
