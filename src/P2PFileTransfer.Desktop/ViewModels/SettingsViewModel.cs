using System;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using P2PFileTransfer.Core.Services.Discovery;
using P2PFileTransfer.Core.Services.Security;
using P2PFileTransfer.Core.Services.Transfer;
using P2PFileTransfer.Core.Utilities;
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
    private readonly IWindowProvider m_windowProvider;

    private decimal m_broadcastIntervalSeconds;
    private decimal m_peerTimeoutSeconds;
    private decimal m_cleanupIntervalSeconds;
    private decimal m_chunkSizeKiB;
    private decimal m_bufferSizeKiB;
    private decimal m_tlsHandshakeTimeoutSeconds;
    private decimal m_transferRequestTimeoutSeconds;
    private string m_downloadDirectory = string.Empty;
    private string m_certificatePath = string.Empty;
    private decimal m_certificateValidityYears;
    private string m_signingKeyPath = string.Empty;
    private string m_statusMessage = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsViewModel"/> class.
    /// </summary>
    /// <param name="settings">The current application settings.</param>
    /// <param name="settingsStore">The settings store.</param>
    /// <param name="fileTransferService">The file transfer service.</param>
    /// <param name="windowProvider">The window provider.</param>
    public SettingsViewModel(
        AppSettings settings,
        SettingsStore settingsStore,
        IFileTransferService fileTransferService,
        IWindowProvider windowProvider
    )
    {
        this.m_settings = settings;
        this.m_settingsStore = settingsStore;
        this.m_fileTransferService = fileTransferService;
        this.m_windowProvider = windowProvider;

        this.LoadFromSettings();

        this.SaveCommand = ReactiveCommand.Create(this.Save);
        this.CancelCommand = ReactiveCommand.Create(() =>
            this.RequestClose?.Invoke(this, EventArgs.Empty)
        );
        this.BrowseDownloadDirectoryCommand = ReactiveCommand.CreateFromTask(
            this.BrowseDownloadDirectoryAsync
        );
        this.BrowseCertificatePathCommand = ReactiveCommand.CreateFromTask(
            this.BrowseCertificatePathAsync
        );
        this.BrowseSigningKeyPathCommand = ReactiveCommand.CreateFromTask(
            this.BrowseSigningKeyPathAsync
        );
        this.ResetToDefaultsCommand = ReactiveCommand.Create(
            this.ResetToDefaults
        );
    }

    /// <summary>
    /// Raised when the window should close.
    /// </summary>
    public event EventHandler? RequestClose;

    /// <summary>
    /// The broadcast interval in seconds.
    /// </summary>
    public decimal BroadcastIntervalSeconds
    {
        get => this.m_broadcastIntervalSeconds;
        set
        {
            this.RaiseAndSetIfChanged(
                ref this.m_broadcastIntervalSeconds,
                value
            );
            this.RaisePropertyChanged(nameof(this.HasPeerTimeoutWarning));
        }
    }

    /// <summary>
    /// The peer timeout in seconds.
    /// </summary>
    public decimal PeerTimeoutSeconds
    {
        get => this.m_peerTimeoutSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref this.m_peerTimeoutSeconds, value);
            this.RaisePropertyChanged(nameof(this.HasPeerTimeoutWarning));
        }
    }

    /// <summary>
    /// The cleanup interval in seconds.
    /// </summary>
    public decimal CleanupIntervalSeconds
    {
        get => this.m_cleanupIntervalSeconds;
        set =>
            this.RaiseAndSetIfChanged(ref this.m_cleanupIntervalSeconds, value);
    }

    /// <summary>
    /// The chunk size in KiB.
    /// </summary>
    public decimal ChunkSizeKiB
    {
        get => this.m_chunkSizeKiB;
        set => this.RaiseAndSetIfChanged(ref this.m_chunkSizeKiB, value);
    }

    /// <summary>
    /// The buffer size in KiB.
    /// </summary>
    public decimal BufferSizeKiB
    {
        get => this.m_bufferSizeKiB;
        set => this.RaiseAndSetIfChanged(ref this.m_bufferSizeKiB, value);
    }

    /// <summary>
    /// The TLS handshake timeout in seconds.
    /// </summary>
    public decimal TlsHandshakeTimeoutSeconds
    {
        get => this.m_tlsHandshakeTimeoutSeconds;
        set =>
            this.RaiseAndSetIfChanged(
                ref this.m_tlsHandshakeTimeoutSeconds,
                value
            );
    }

    /// <summary>
    /// The transfer request timeout in seconds.
    /// </summary>
    public decimal TransferRequestTimeoutSeconds
    {
        get => this.m_transferRequestTimeoutSeconds;
        set =>
            this.RaiseAndSetIfChanged(
                ref this.m_transferRequestTimeoutSeconds,
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
    public decimal CertificateValidityYears
    {
        get => this.m_certificateValidityYears;
        set =>
            this.RaiseAndSetIfChanged(
                ref this.m_certificateValidityYears,
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
    /// Gets a value indicating whether the peer timeout is less than or equal to the broadcast interval,
    /// which could cause peers to disappear frequently.
    /// </summary>
    public bool HasPeerTimeoutWarning =>
        this.PeerTimeoutSeconds < this.BroadcastIntervalSeconds;

    /// <summary>
    /// Command to save settings.
    /// </summary>
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    /// <summary>
    /// Command to cancel and close the window.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>
    /// Command to browse for the download directory.
    /// </summary>
    public ReactiveCommand<Unit, Unit> BrowseDownloadDirectoryCommand { get; }

    /// <summary>
    /// Command to browse for the certificate file.
    /// </summary>
    public ReactiveCommand<Unit, Unit> BrowseCertificatePathCommand { get; }

    /// <summary>
    /// Command to browse for the signing key file.
    /// </summary>
    public ReactiveCommand<Unit, Unit> BrowseSigningKeyPathCommand { get; }

    /// <summary>
    /// Command to reset all settings to their default values.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ResetToDefaultsCommand { get; }

    /// <summary>
    /// Loads persisted settings into editable fields.
    /// </summary>
    private void LoadFromSettings()
    {
        this.m_settings.Normalize();

        this.BroadcastIntervalSeconds = Math.Max(
            1,
            (int)this.m_settings.Discovery.BroadcastInterval.TotalSeconds
        );
        this.PeerTimeoutSeconds = Math.Max(
            1,
            (int)this.m_settings.Discovery.PeerTimeout.TotalSeconds
        );
        this.CleanupIntervalSeconds = Math.Max(
            1,
            (int)this.m_settings.Discovery.CleanupInterval.TotalSeconds
        );

        this.ChunkSizeKiB = Math.Max(
            1,
            (int)
                Math.Ceiling(
                    this.m_settings.Transfer.ChunkSize / (double)BytesPerKiB
                )
        );
        this.BufferSizeKiB = Math.Max(
            1,
            (int)
                Math.Ceiling(
                    this.m_settings.Transfer.BufferSize / (double)BytesPerKiB
                )
        );
        this.TlsHandshakeTimeoutSeconds = Math.Max(
            1,
            (int)this.m_settings.Transfer.TlsHandshakeTimeout.TotalSeconds
        );
        this.TransferRequestTimeoutSeconds = Math.Max(
            1,
            (int)this.m_settings.Transfer.TransferRequestTimeout.TotalSeconds
        );

        this.DownloadDirectory = this.m_settings.DownloadDirectory;
        this.CertificatePath = this.m_settings.Security.CertificatePath;
        this.CertificateValidityYears = this.m_settings
            .Security
            .CertificateValidityYears;
        this.SigningKeyPath = this.m_settings.Security.SigningKeyPath;
    }

    /// <summary>
    /// Validates and saves settings changes.
    /// </summary>
    private void Save()
    {
        this.StatusMessage = string.Empty;

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

        int chunkSizeKiB = (int)this.ChunkSizeKiB;
        int bufferSizeKiB = (int)this.BufferSizeKiB;

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

        this.m_settings.Discovery.BroadcastInterval = TimeSpan.FromSeconds(
            (int)this.BroadcastIntervalSeconds
        );
        this.m_settings.Discovery.PeerTimeout = TimeSpan.FromSeconds(
            (int)this.PeerTimeoutSeconds
        );
        this.m_settings.Discovery.CleanupInterval = TimeSpan.FromSeconds(
            (int)this.CleanupIntervalSeconds
        );

        this.m_settings.Transfer.ChunkSize = chunkSizeKiB * BytesPerKiB;
        this.m_settings.Transfer.BufferSize = bufferSizeKiB * BytesPerKiB;
        this.m_settings.Transfer.TlsHandshakeTimeout = TimeSpan.FromSeconds(
            (int)this.TlsHandshakeTimeoutSeconds
        );
        this.m_settings.Transfer.TransferRequestTimeout = TimeSpan.FromSeconds(
            (int)this.TransferRequestTimeoutSeconds
        );

        this.m_settings.DownloadDirectory = downloadDirectory;
        this.m_settings.Security.CertificatePath = certificatePath;
        this.m_settings.Security.CertificateValidityYears = (int)
            this.CertificateValidityYears;
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
    /// Resets all settings to their default values, persists them, and updates the UI.
    /// </summary>
    private void ResetToDefaults()
    {
        this.StatusMessage = string.Empty;

        // Apply defaults to discovery settings.
        this.m_settings.Discovery.BroadcastInterval =
            PeerDiscoveryOptions.DefaultBroadcastInterval;
        this.m_settings.Discovery.PeerTimeout =
            PeerDiscoveryOptions.DefaultPeerTimeout;
        this.m_settings.Discovery.CleanupInterval =
            PeerDiscoveryOptions.DefaultCleanupInterval;

        // Apply defaults to transfer settings.
        this.m_settings.Transfer.ChunkSize =
            FileTransferOptions.DefaultChunkSize;
        this.m_settings.Transfer.BufferSize =
            FileTransferOptions.DefaultBufferSize;
        this.m_settings.Transfer.TlsHandshakeTimeout =
            FileTransferOptions.DefaultTlsHandshakeTimeout;
        this.m_settings.Transfer.TransferRequestTimeout =
            FileTransferOptions.DefaultTransferRequestTimeout;

        // Apply defaults to download and security settings.
        this.m_settings.DownloadDirectory =
            FilePathUtilities.GetDefaultDownloadDirectory();
        this.m_settings.Security.CertificatePath =
            CertificateManager.DefaultCertificatePath;
        this.m_settings.Security.CertificateValidityYears =
            CertificateManager.DefaultValidityYears;
        this.m_settings.Security.SigningKeyPath =
            SigningKeyManager.DefaultSigningKeyPath;

        try
        {
            this.m_settingsStore.Save(this.m_settings);
        }
        catch (IOException)
        {
            this.StatusMessage = "Failed to save default settings to disk.";
            return;
        }
        catch (UnauthorizedAccessException)
        {
            this.StatusMessage = "Settings file is not accessible.";
            return;
        }

        // Update the download directory in the transfer service.
        if (this.m_fileTransferService is FileTransferService transferService)
        {
            transferService.UpdateDownloadDirectory(
                this.m_settings.DownloadDirectory
            );
        }

        // Reload the UI fields from the updated settings.
        this.LoadFromSettings();

        this.StatusMessage = "Settings reverted to default.";
    }

    /// <summary>
    /// Opens a folder picker for the download directory.
    /// </summary>
    private async Task BrowseDownloadDirectoryAsync()
    {
        string? path = await this.PickFolderAsync("Select Download Directory");
        if (!string.IsNullOrWhiteSpace(path))
        {
            this.DownloadDirectory = path;
        }
    }

    /// <summary>
    /// Opens a file picker for the certificate path.
    /// </summary>
    private async Task BrowseCertificatePathAsync()
    {
        string? path = await this.PickFileAsync(
            "Select Certificate File",
            [new FilePickerFileType("PFX Certificate") { Patterns = ["*.pfx"] }]
        );
        if (!string.IsNullOrWhiteSpace(path))
        {
            this.CertificatePath = path;
        }
    }

    /// <summary>
    /// Opens a file picker for the signing key path.
    /// </summary>
    private async Task BrowseSigningKeyPathAsync()
    {
        string? path = await this.PickFileAsync(
            "Select Signing Key File",
            [
                new FilePickerFileType("Key File")
                {
                    Patterns = ["*.key", "*.pem"],
                },
            ]
        );
        if (!string.IsNullOrWhiteSpace(path))
        {
            this.SigningKeyPath = path;
        }
    }

    /// <summary>
    /// Opens a folder picker dialog.
    /// </summary>
    private async Task<string?> PickFolderAsync(string title)
    {
        Window? window = this.m_windowProvider.MainWindow;
        if (window == null)
        {
            return null;
        }

        FolderPickerOpenOptions options = new() { Title = title };
        System.Collections.Generic.IReadOnlyList<IStorageFolder> folders =
            await window.StorageProvider.OpenFolderPickerAsync(options);

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    /// <summary>
    /// Opens a file picker dialog.
    /// </summary>
    private async Task<string?> PickFileAsync(
        string title,
        FilePickerFileType[] fileTypes
    )
    {
        Window? window = this.m_windowProvider.MainWindow;
        if (window == null)
        {
            return null;
        }

        FilePickerOpenOptions options = new()
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = fileTypes,
        };
        System.Collections.Generic.IReadOnlyList<IStorageFile> files =
            await window.StorageProvider.OpenFilePickerAsync(options);

        return files.Count > 0 ? files[0].Path.LocalPath : null;
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
