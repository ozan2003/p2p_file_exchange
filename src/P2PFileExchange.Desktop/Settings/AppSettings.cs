using System;
using System.Net;
using P2PFileExchange.Core.Services.Discovery;
using P2PFileExchange.Core.Services.Security;
using P2PFileExchange.Core.Services.Transfer;
using P2PFileExchange.Core.Utilities;

namespace P2PFileExchange.Desktop.Settings;

/// <summary>
/// Represents persisted application settings.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Discovery settings.
    /// </summary>
    public PeerDiscoveryOptions Discovery { get; set; } = new();

    /// <summary>
    /// Transfer settings.
    /// </summary>
    public FileTransferOptions Transfer { get; set; } = new();

    /// <summary>
    /// Download directory for inbound transfers.
    /// </summary>
    public string DownloadDirectory { get; set; } =
        FilePathUtilities.GetDefaultDownloadDirectory();

    /// <summary>
    /// Security settings.
    /// </summary>
    public SecuritySettings Security { get; set; } = new();

    /// <summary>
    /// Normalizes settings and applies defaults for missing or invalid values.
    /// </summary>
    public void Normalize()
    {
        this.Discovery ??= new PeerDiscoveryOptions();
        this.Transfer ??= new FileTransferOptions();
        this.Security ??= new SecuritySettings();

        SettingsSanitizer.NormalizeDiscoveryOptions(this.Discovery);
        SettingsSanitizer.NormalizeTransferOptions(this.Transfer);
        this.Security.Normalize();

        if (string.IsNullOrWhiteSpace(this.DownloadDirectory))
        {
            this.DownloadDirectory =
                FilePathUtilities.GetDefaultDownloadDirectory();
        }
    }
}

/// <summary>
/// Represents security-related settings.
/// </summary>
public sealed class SecuritySettings
{
    /// <summary>
    /// The Ed25519 identity key file path.
    /// </summary>
    public string IdentityKeyPath { get; set; } =
        IdentityKeyManager.DefaultIdentityKeyPath;

    /// <summary>
    /// Whether to require a password on application startup.
    /// When false, uses auto-unlock with OS-protected secret storage.
    /// Default: true (require password for maximum security).
    /// </summary>
    public bool RequirePasswordOnStartup { get; set; } = true;

    /// <summary>
    /// Normalizes settings and applies defaults for missing or invalid values.
    /// </summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(this.IdentityKeyPath))
        {
            this.IdentityKeyPath = IdentityKeyManager.DefaultIdentityKeyPath;
        }
    }
}

/// <summary>
/// Normalizes settings objects to enforce safe defaults.
/// </summary>
internal static class SettingsSanitizer
{
    /// <summary>
    /// Ensures discovery settings have valid values.
    /// </summary>
    public static void NormalizeDiscoveryOptions(PeerDiscoveryOptions options)
    {
        if (options.BroadcastPort == 0)
        {
            options.BroadcastPort = PeerDiscoveryOptions.DefaultBroadcastPort;
        }

        options.BroadcastAddress ??= IPAddress.Broadcast;

        if (options.BroadcastInterval <= TimeSpan.Zero)
        {
            options.BroadcastInterval =
                PeerDiscoveryOptions.DefaultBroadcastInterval;
        }

        if (options.PeerTimeout <= TimeSpan.Zero)
        {
            options.PeerTimeout = PeerDiscoveryOptions.DefaultPeerTimeout;
        }

        if (options.CleanupInterval <= TimeSpan.Zero)
        {
            options.CleanupInterval =
                PeerDiscoveryOptions.DefaultCleanupInterval;
        }
    }

    /// <summary>
    /// Ensures transfer settings have valid values.
    /// </summary>
    public static void NormalizeTransferOptions(FileTransferOptions options)
    {
        if (options.ChunkSize <= 0)
        {
            options.ChunkSize = FileTransferOptions.DefaultChunkSize;
        }

        if (options.BufferSize <= 0)
        {
            options.BufferSize = FileTransferOptions.DefaultBufferSize;
        }

        if (options.HandshakeTimeout <= TimeSpan.Zero)
        {
            options.HandshakeTimeout =
                FileTransferOptions.DefaultHandshakeTimeout;
        }

        if (options.TransferRequestTimeout <= TimeSpan.Zero)
        {
            options.TransferRequestTimeout =
                FileTransferOptions.DefaultTransferRequestTimeout;
        }
    }
}
