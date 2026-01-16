using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using P2PFileTransfer.Core.Models;
using P2PFileTransfer.Core.Utilities;

namespace P2PFileTransfer.Core.Services;

/// <summary>
/// Handles UDP broadcast discovery of peers on the local network.
/// </summary>
public sealed class PeerDiscoveryService : IPeerDiscoveryService
{
    private readonly ConcurrentDictionary<Guid, PeerInfo> m_peers = new();
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
    private int m_tcpPort;

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
    public async Task StartAsync(
        int tcpPort,
        string displayName,
        CancellationToken cancellationToken
    )
    {
        if (tcpPort <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tcpPort));
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
            this.m_displayName = displayName?.Trim() ?? string.Empty;

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
    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
        this.m_stateLock.Dispose();
    }

    private static string NormalizeDisplayName(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? "Unknown" : name.Trim();
    }

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
                PeerAnnouncement announcement = new()
                {
                    PeerId = this.LocalPeerId,
                    DisplayName = NormalizeDisplayName(this.m_displayName),
                    IPAddress = NetworkUtilities
                        .GetPrimaryIPv4Address()
                        .ToString(),
                    TcpPort = this.m_tcpPort,
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

    private async Task ListenLoopAsync(
        UdpClient client,
        CancellationToken cancellationToken
    )
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await client
                    .ReceiveAsync(cancellationToken)
                    .ConfigureAwait(false);
                PeerAnnouncement? announcement =
                    JsonSerializer.Deserialize<PeerAnnouncement>(
                        result.Buffer,
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

                DateTimeOffset now = DateTimeOffset.UtcNow;
                string address = string.IsNullOrWhiteSpace(
                    announcement.IPAddress
                )
                    ? result.RemoteEndPoint.Address.ToString()
                    : announcement.IPAddress;

                PeerInfo peerInfo = this.m_peers.AddOrUpdate(
                    announcement.PeerId,
                    _ => new PeerInfo
                    {
                        PeerId = announcement.PeerId,
                        DisplayName = NormalizeDisplayName(
                            announcement.DisplayName
                        ),
                        IPAddress = address,
                        TcpPort = announcement.TcpPort,
                        LastSeen = now,
                    },
                    (_, existing) =>
                    {
                        existing.DisplayName = NormalizeDisplayName(
                            announcement.DisplayName
                        );
                        existing.IPAddress = address;
                        existing.TcpPort = announcement.TcpPort;
                        existing.LastSeen = now;
                        return existing;
                    }
                );

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
            catch (SocketException ex)
            {
                StatusChanged?.Invoke(
                    this,
                    $"Discovery listen failed: {ex.Message}"
                );
            }
            catch (JsonException)
            {
                // Ignore malformed payloads.
            }
        }
    }

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
            foreach ((Guid key, PeerInfo value) in this.m_peers)
            {
                if (value.LastSeen >= expiration)
                {
                    continue;
                }

                if (this.m_peers.TryRemove(key, out _))
                {
                    PeerRemoved?.Invoke(this, key);
                }
            }
        }
    }

    private sealed class PeerAnnouncement
    {
        public Guid PeerId { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        public string IPAddress { get; set; } = string.Empty;

        public int TcpPort { get; set; }
    }
}
