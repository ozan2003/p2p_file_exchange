using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reactive;
using P2PFileTransfer.Core.Services.Transfer;
using P2PFileTransfer.Desktop.Services;
using P2PFileTransfer.Desktop.Settings;
using ReactiveUI;

namespace P2PFileTransfer.Desktop.ViewModels;

/// <summary>
/// View model for editing application settings.
/// </summary>
public sealed class SettingsViewModel : ReactiveObject
{
    private const int BytesPerKiB = 1024;
    private readonly AppSettings m_settings;
    private readonly SettingsStore m_settingsStore;
    private readonly IFileTransferService m_fileTransferService;
    private string m_broadcastPortText = string.Empty;
    private string m_broadcastAddressText = string.Empty;
    private string m_broadcastIntervalSecondsText = string.Empty;
    private string m_peerTimeoutSecondsText = string.Empty;
    private string m_cleanupIntervalSecondsText = string.Empty;
    private string m_chunkSizeKiBText = string.Empty;
    private string m_bufferSizeKiBText = string.Empty;
    private string m_tlsHandshakeTimeoutSecondsText = string.Empty;
    private string m_transferRequestTimeoutSecondsText = string.Empty;
    private string m_downloadDirectory = string.Empty;
    private string m_certificatePath = string.Empty;
    private string m_certificateValidityYearsText = string.Empty;
    private string m_signingKeyPath = string.Empty;
    private string m_statusMessage = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsViewModel"/> class.
    /// </summary>
    /// <param name="settings">The current application settings.</param>
    /// <param name="settingsStore">The settings store.</param>
    /// <param name="fileTransferService">The file transfer service.</param>
    public SettingsViewModel(
        AppSettings settings,
        SettingsStore settingsStore,
        IFileTransferService fileTransferService
    )
    {
        this.m_settings = settings;
        this.m_settingsStore = settingsStore;
        this.m_fileTransferService = fileTransferService;

        this.LoadFromSettings();

        this.SaveCommand = ReactiveCommand.Create(this.Save);
        this.CancelCommand = ReactiveCommand.Create(() =>
            this.RequestClose?.Invoke(this, EventArgs.Empty)
        );
    }

    /// <summary>
    /// Raised when the window should close.
    /// </summary>
    public event EventHandler? RequestClose;

    /// <summary>
    /// The broadcast port.
    /// </summary>
    public string BroadcastPortText
    {
        get => this.m_broadcastPortText;
        set => this.RaiseAndSetIfChanged(ref this.m_broadcastPortText, value);
    }

    /// <summary>
    /// The broadcast address.
    /// </summary>
    public string BroadcastAddressText
    {
        get => this.m_broadcastAddressText;
        set =>
            this.RaiseAndSetIfChanged(ref this.m_broadcastAddressText, value);
    }

    /// <summary>
    /// The broadcast interval in seconds.
    /// </summary>
    public string BroadcastIntervalSecondsText
    {
        get => this.m_broadcastIntervalSecondsText;
        set =>
            this.RaiseAndSetIfChanged(
                ref this.m_broadcastIntervalSecondsText,
                value
            );
    }

    /// <summary>
    /// The peer timeout in seconds.
    /// </summary>
    public string PeerTimeoutSecondsText
    {
        get => this.m_peerTimeoutSecondsText;
        set =>
            this.RaiseAndSetIfChanged(ref this.m_peerTimeoutSecondsText, value);
    }

    /// <summary>
    /// The cleanup interval in seconds.
    /// </summary>
    public string CleanupIntervalSecondsText
    {
        get => this.m_cleanupIntervalSecondsText;
        set =>
            this.RaiseAndSetIfChanged(
                ref this.m_cleanupIntervalSecondsText,
                value
            );
    }

    /// <summary>
    /// The chunk size in KiB.
    /// </summary>
    public string ChunkSizeKiBText
    {
        get => this.m_chunkSizeKiBText;
        set => this.RaiseAndSetIfChanged(ref this.m_chunkSizeKiBText, value);
    }

    /// <summary>
    /// The buffer size in KiB.
    /// </summary>
    public string BufferSizeKiBText
    {
        get => this.m_bufferSizeKiBText;
        set => this.RaiseAndSetIfChanged(ref this.m_bufferSizeKiBText, value);
    }

    /// <summary>
    /// The TLS handshake timeout in seconds.
    /// </summary>
    public string TlsHandshakeTimeoutSecondsText
    {
        get => this.m_tlsHandshakeTimeoutSecondsText;
        set =>
            this.RaiseAndSetIfChanged(
                ref this.m_tlsHandshakeTimeoutSecondsText,
                value
            );
    }

    /// <summary>
    /// The transfer request timeout in seconds.
    /// </summary>
    public string TransferRequestTimeoutSecondsText
    {
        get => this.m_transferRequestTimeoutSecondsText;
        set =>
            this.RaiseAndSetIfChanged(
                ref this.m_transferRequestTimeoutSecondsText,
                value
            );
    }

    /// <summary>
    /// The download directory.
    /// </summary>
    public string DownloadDirectory
    {
        get => this.m_downloadDirectory;
        set => this.RaiseAndSetIfChanged(ref this.m_downloadDirectory, value);
    }

    /// <summary>
    /// The certificate path.
    /// </summary>
    public string CertificatePath
    {
        get => this.m_certificatePath;
        set => this.RaiseAndSetIfChanged(ref this.m_certificatePath, value);
    }

    /// <summary>
    /// The certificate validity in years.
    /// </summary>
    public string CertificateValidityYearsText
    {
        get => this.m_certificateValidityYearsText;
        set =>
            this.RaiseAndSetIfChanged(
                ref this.m_certificateValidityYearsText,
                value
            );
    }

    /// <summary>
    /// The signing key path.
    /// </summary>
    public string SigningKeyPath
    {
        get => this.m_signingKeyPath;
        set => this.RaiseAndSetIfChanged(ref this.m_signingKeyPath, value);
    }

    /// <summary>
    /// The status message for validation and save feedback.
    /// </summary>
    public string StatusMessage
    {
        get => this.m_statusMessage;
        set => this.RaiseAndSetIfChanged(ref this.m_statusMessage, value);
    }

    /// <summary>
    /// Command to save settings.
    /// </summary>
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    /// <summary>
    /// Command to cancel and close the window.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>
    /// Loads persisted settings into editable fields.
    /// </summary>
    private void LoadFromSettings()
    {
        this.m_settings.Normalize();

        this.BroadcastPortText =
            this.m_settings.Discovery.BroadcastPort.ToString(
                CultureInfo.InvariantCulture
            );
        this.BroadcastAddressText =
            this.m_settings.Discovery.BroadcastAddress.ToString();
        this.BroadcastIntervalSecondsText = Math.Max(
                1,
                (int)this.m_settings.Discovery.BroadcastInterval.TotalSeconds
            )
            .ToString(CultureInfo.InvariantCulture);
        this.PeerTimeoutSecondsText = Math.Max(
                1,
                (int)this.m_settings.Discovery.PeerTimeout.TotalSeconds
            )
            .ToString(CultureInfo.InvariantCulture);
        this.CleanupIntervalSecondsText = Math.Max(
                1,
                (int)this.m_settings.Discovery.CleanupInterval.TotalSeconds
            )
            .ToString(CultureInfo.InvariantCulture);

        this.ChunkSizeKiBText = Math.Max(
                1,
                (int)
                    Math.Ceiling(
                        this.m_settings.Transfer.ChunkSize / (double)BytesPerKiB
                    )
            )
            .ToString(CultureInfo.InvariantCulture);
        this.BufferSizeKiBText = Math.Max(
                1,
                (int)
                    Math.Ceiling(
                        this.m_settings.Transfer.BufferSize
                            / (double)BytesPerKiB
                    )
            )
            .ToString(CultureInfo.InvariantCulture);
        this.TlsHandshakeTimeoutSecondsText = Math.Max(
                1,
                (int)this.m_settings.Transfer.TlsHandshakeTimeout.TotalSeconds
            )
            .ToString(CultureInfo.InvariantCulture);
        this.TransferRequestTimeoutSecondsText = Math.Max(
                1,
                (int)
                    this.m_settings.Transfer.TransferRequestTimeout.TotalSeconds
            )
            .ToString(CultureInfo.InvariantCulture);

        this.DownloadDirectory = this.m_settings.DownloadDirectory;
        this.CertificatePath = this.m_settings.Security.CertificatePath;
        this.CertificateValidityYearsText =
            this.m_settings.Security.CertificateValidityYears.ToString(
                CultureInfo.InvariantCulture
            );
        this.SigningKeyPath = this.m_settings.Security.SigningKeyPath;
    }

    /// <summary>
    /// Validates and saves settings changes.
    /// </summary>
    private void Save()
    {
        this.StatusMessage = string.Empty;

        if (
            !TryParsePort(
                this.BroadcastPortText,
                "Broadcast port",
                out ushort broadcastPort
            )
        )
        {
            return;
        }

        if (
            string.IsNullOrWhiteSpace(this.BroadcastAddressText)
            || !IPAddress.TryParse(
                this.BroadcastAddressText.Trim(),
                out IPAddress? broadcastAddress
            )
        )
        {
            this.StatusMessage = "Broadcast address must be a valid IP.";
            return;
        }

        if (
            !TryParsePositiveInt(
                this.BroadcastIntervalSecondsText,
                "Broadcast interval (seconds)",
                out int broadcastIntervalSeconds
            )
            || !TryParsePositiveInt(
                this.PeerTimeoutSecondsText,
                "Peer timeout (seconds)",
                out int peerTimeoutSeconds
            )
            || !TryParsePositiveInt(
                this.CleanupIntervalSecondsText,
                "Cleanup interval (seconds)",
                out int cleanupIntervalSeconds
            )
            || !TryParsePositiveInt(
                this.ChunkSizeKiBText,
                "Chunk size (KiB)",
                out int chunkSizeKiB
            )
            || !TryParsePositiveInt(
                this.BufferSizeKiBText,
                "Buffer size (KiB)",
                out int bufferSizeKiB
            )
            || !TryParsePositiveInt(
                this.TlsHandshakeTimeoutSecondsText,
                "TLS handshake timeout (seconds)",
                out int tlsHandshakeSeconds
            )
            || !TryParsePositiveInt(
                this.TransferRequestTimeoutSecondsText,
                "Transfer request timeout (seconds)",
                out int transferRequestSeconds
            )
            || !TryParsePositiveInt(
                this.CertificateValidityYearsText,
                "Certificate validity (years)",
                out int validityYears
            )
        )
        {
            return;
        }

        if (
            !TryNormalizePath(
                this.DownloadDirectory,
                "Download directory",
                out string downloadDirectory
            )
            || !TryNormalizePath(
                this.CertificatePath,
                "Certificate path",
                out string certificatePath
            )
            || !TryNormalizePath(
                this.SigningKeyPath,
                "Signing key path",
                out string signingKeyPath
            )
        )
        {
            return;
        }

        if (chunkSizeKiB > int.MaxValue / BytesPerKiB)
        {
            this.StatusMessage = "Chunk size is too large.";
            return;
        }

        if (bufferSizeKiB > int.MaxValue / BytesPerKiB)
        {
            this.StatusMessage = "Buffer size is too large.";
            return;
        }

        this.m_settings.Discovery.BroadcastPort = broadcastPort;
        this.m_settings.Discovery.BroadcastAddress = broadcastAddress;
        this.m_settings.Discovery.BroadcastInterval = TimeSpan.FromSeconds(
            broadcastIntervalSeconds
        );
        this.m_settings.Discovery.PeerTimeout = TimeSpan.FromSeconds(
            peerTimeoutSeconds
        );
        this.m_settings.Discovery.CleanupInterval = TimeSpan.FromSeconds(
            cleanupIntervalSeconds
        );

        this.m_settings.Transfer.ChunkSize = chunkSizeKiB * BytesPerKiB;
        this.m_settings.Transfer.BufferSize = bufferSizeKiB * BytesPerKiB;
        this.m_settings.Transfer.TlsHandshakeTimeout = TimeSpan.FromSeconds(
            tlsHandshakeSeconds
        );
        this.m_settings.Transfer.TransferRequestTimeout = TimeSpan.FromSeconds(
            transferRequestSeconds
        );

        this.m_settings.DownloadDirectory = downloadDirectory;
        this.m_settings.Security.CertificatePath = certificatePath;
        this.m_settings.Security.CertificateValidityYears = validityYears;
        this.m_settings.Security.SigningKeyPath = signingKeyPath;

        try
        {
            this.m_settingsStore.Save(this.m_settings);
        }
        catch (IOException)
        {
            this.StatusMessage = "Failed to save settings to disk.";
            return;
        }
        catch (UnauthorizedAccessException)
        {
            this.StatusMessage = "Settings file is not accessible.";
            return;
        }

        if (this.m_fileTransferService is FileTransferService transferService)
        {
            transferService.UpdateDownloadDirectory(downloadDirectory);
        }

        this.RequestClose?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Parses a positive integer from user input.
    /// </summary>
    private bool TryParsePositiveInt(
        string? value,
        string fieldName,
        out int result
    )
    {
        if (
            !int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out result
            )
            || result <= 0
        )
        {
            this.StatusMessage = $"{fieldName} must be a positive number.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses a TCP/UDP port from user input.
    /// </summary>
    private bool TryParsePort(
        string? value,
        string fieldName,
        out ushort result
    )
    {
        if (
            !ushort.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out result
            )
            || result == 0
        )
        {
            this.StatusMessage = $"{fieldName} must be between 1 and 65535.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Normalizes a file system path from user input.
    /// </summary>
    private bool TryNormalizePath(
        string? value,
        string fieldName,
        out string normalized
    )
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            this.StatusMessage = $"{fieldName} is required.";
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(value.Trim());
            return true;
        }
        catch (Exception)
        {
            this.StatusMessage = $"{fieldName} is invalid.";
            return false;
        }
    }
}
