using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using P2PFileTransfer.Core.Models;
using P2PFileTransfer.Core.Services.Security;
using P2PFileTransfer.Core.Utilities;

namespace P2PFileTransfer.Core.Services.Discovery;

/// <summary>
/// Handles UDP broadcast discovery of peers on the local network.
/// </summary>
public sealed class PeerDiscoveryService : IPeerDiscoveryService
{
    private readonly ConcurrentDictionary<Guid, PeerInfo> m_peers = new();
    private readonly ConcurrentDictionary<Guid, string> m_verifiedPublicKeys =
        new();
    private readonly PeerDiscoveryOptions m_options;
    private readonly JsonSerializerOptions m_jsonOptions;
    private readonly SemaphoreSlim m_stateLock = new(1, 1);

    private CancellationTokenSource? m_discoveryCts;
    private UdpClient? m_broadcastClient;
    private UdpClient? m_listenClient;
    private Task? m_broadcastTask;
    private Task? m_listenTask;
    private Task? m_cleanupTask;
    private string m_displayName = string.Empty;
    private string m_certificateFingerprint = string.Empty;
    private int m_tcpPort;
    private ECDsa? m_signingKey;
    private string m_localPublicKey = string.Empty;

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
    }

    /// <inheritdoc />
    public event EventHandler<PeerInfo>? PeerUpdated;

    /// <inheritdoc />
    public event EventHandler<Guid>? PeerRemoved;

    /// <inheritdoc />
    public event EventHandler<string>? StatusChanged;

    /// <inheritdoc />
    public Guid LocalPeerId { get; } = Guid.NewGuid();

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public int BroadcastPort => this.m_options.BroadcastPort;

    /// <inheritdoc />
    public async Task StartAsync(
        int tcpPort,
        string displayName,
        string certificateFingerprint,
        ECDsa signingKey,
        CancellationToken cancellationToken
    )
    {
        if (tcpPort <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tcpPort));
        }

        ArgumentNullException.ThrowIfNull(signingKey, nameof(signingKey));

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
            this.m_displayName = displayName?.Trim() ?? string.Empty;
            this.m_certificateFingerprint =
                certificateFingerprint ?? string.Empty;
            this.m_signingKey = signingKey;
            this.m_localPublicKey = SigningKeyManager.ExportPublicKey(
                signingKey
            );

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
    public void UpdateDisplayName(string displayName)
    {
        this.m_displayName = displayName?.Trim() ?? string.Empty;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<PeerInfo> GetPeers()
    {
        return [.. this.m_peers.Values];
    }

    /// <inheritdoc />
    public string? GetPeerFingerprintByIPAddress(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return null;
        }

        foreach (PeerInfo peer in this.m_peers.Values)
        {
            if (
                string.Equals(
                    peer.IPAddress,
                    ipAddress,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return string.IsNullOrWhiteSpace(peer.CertificateFingerprint)
                    ? null
                    : peer.CertificateFingerprint;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public string? GetPeerDisplayNameByIPAddress(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return null;
        }

        foreach (PeerInfo peer in this.m_peers.Values)
        {
            if (
                string.Equals(
                    peer.IPAddress,
                    ipAddress,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return string.IsNullOrWhiteSpace(peer.DisplayName)
                    ? null
                    : peer.DisplayName;
            }
        }

        return null;
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
    private static string NormalizeDisplayName(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? "Unknown" : name.Trim();
    }

    /// <summary>
    /// Validates that a certificate fingerprint is a valid SHA-256 hex string (64 characters).
    /// </summary>
    /// <param name="fingerprint">The fingerprint to validate.</param>
    /// <returns>True if the fingerprint is valid; otherwise, false.</returns>
    private static bool IsValidCertificateFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return false;
        }

        // SHA-256 produces 32 bytes = 64 hex characters.
        if (fingerprint.Length != 64)
        {
            return false;
        }

        // Verify all characters are valid hex digits.
        return fingerprint.All(char.IsAsciiHexDigit);
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

                // Create the signature over the announcement data.
                byte[] signingData =
                    SigningKeyManager.CreateAnnouncementSigningData(
                        this.LocalPeerId,
                        displayName,
                        this.m_tcpPort,
                        this.m_certificateFingerprint
                    );

                string signature = string.Empty;
                if (this.m_signingKey != null)
                {
                    signature = SigningKeyManager.SignDataToBase64(
                        this.m_signingKey,
                        signingData
                    );
                }

                PeerAnnouncement announcement = new()
                {
                    PeerId = this.LocalPeerId,
                    DisplayName = displayName,
                    IPAddress = NetworkUtilities
                        .GetPrimaryIPv4Address()
                        .ToString(),
                    TcpPort = this.m_tcpPort,
                    CertificateFingerprint = this.m_certificateFingerprint,
                    PublicKey = this.m_localPublicKey,
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

                if (announcement.TcpPort <= 0)
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

                // Announcement signature is verified here.
                if (!this.VerifyAnnouncementSignature(announcement))
                {
                    StatusChanged?.Invoke(
                        this,
                        $"Invalid signature from peer {announcement.PeerId}: discarding announcement."
                    );
                    continue;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;

                string ipAddress = string.Empty;
                if (string.IsNullOrWhiteSpace(announcement.IPAddress))
                {
                    ipAddress = received.RemoteEndPoint.Address.ToString();
                }
                else
                {
                    ipAddress = announcement.IPAddress;
                }

                // Store the verified public key for this peer.
                this.m_verifiedPublicKeys.AddOrUpdate(
                    announcement.PeerId,
                    announcement.PublicKey,
                    (_, _) => announcement.PublicKey
                );

                // Peers info is updated here.
                PeerInfo peerInfo = this.m_peers.AddOrUpdate(
                    announcement.PeerId,
                    // Add a new peer if it doesn't exist.
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
                        PublicKey = announcement.PublicKey ?? string.Empty,
                    },
                    // Update existing peer info otherwise.
                    (_, existing) =>
                    {
                        existing.DisplayName = NormalizeDisplayName(
                            announcement.DisplayName
                        );
                        existing.IPAddress = ipAddress;
                        existing.TcpPort = announcement.TcpPort;
                        existing.LastSeen = now;
                        existing.CertificateFingerprint =
                            announcement.CertificateFingerprint ?? string.Empty;
                        existing.PublicKey =
                            announcement.PublicKey ?? string.Empty;
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
    /// Verifies the ECDSA signature on a peer announcement.
    /// </summary>
    /// <param name="announcement">The announcement to verify.</param>
    /// <returns>True if the signature is valid; otherwise, false.</returns>
    private bool VerifyAnnouncementSignature(PeerAnnouncement announcement)
    {
        if (string.IsNullOrWhiteSpace(announcement.PublicKey))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(announcement.Signature))
        {
            return false;
        }

        try
        {
            // Re-create the signing data from the announcement fields.
            byte[] signingData =
                SigningKeyManager.CreateAnnouncementSigningData(
                    announcement.PeerId,
                    announcement.DisplayName,
                    announcement.TcpPort,
                    announcement.CertificateFingerprint
                );

            // Re-import the public key and verify the signature.
            using ECDsa publicKey = SigningKeyManager.ImportPublicKey(
                announcement.PublicKey
            );

            return SigningKeyManager.VerifySignatureFromBase64(
                publicKey,
                signingData,
                announcement.Signature
            );
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Periodically removes stale peers that have not been seen within the timeout period.
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
            DateTimeOffset expiration =
                DateTimeOffset.UtcNow - this.m_options.PeerTimeout;
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
        public Guid PeerId { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        public string IPAddress { get; set; } = string.Empty;

        public int TcpPort { get; set; }

        public string CertificateFingerprint { get; set; } = string.Empty;

        /// <summary>
        /// Base64-encoded ECDSA P-256 public key for signature verification.
        /// </summary>
        public string PublicKey { get; set; } = string.Empty;

        /// <summary>
        /// Base64-encoded ECDSA signature over SHA256(PeerId + DisplayName + TcpPort + CertificateFingerprint).
        /// </summary>
        public string Signature { get; set; } = string.Empty;
    }
}
