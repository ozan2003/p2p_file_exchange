using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using P2PFileTransfer.Core.Models;
using ReactiveUI;

namespace P2PFileTransfer.Desktop.ViewModels;

/// <summary>
/// Represents a transfer in the UI.
/// </summary>
public sealed class TransferItemViewModel : ReactiveObject
{
    /// <summary>
    /// Converter for background color based on transfer state.
    /// </summary>
    public static readonly IMultiValueConverter BackgroundConverter =
        new TransferBackgroundConverter();

    private readonly Stopwatch m_stopwatch;
    private readonly long m_totalBytes;

    private DateTimeOffset? m_startedAt;
    private int m_progressPercent;
    private long m_bytesTransferred;
    private string m_statusText;
    private string m_speedText;
    private string m_etaText;
    private bool m_isFinished;
    private bool m_isSuccess;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransferItemViewModel"/> class.
    /// </summary>
    /// <param name="transferId">The transfer identifier.</param>
    /// <param name="mode">The transfer mode.</param>
    /// <param name="fileName">The file name.</param>
    /// <param name="totalBytes">The total file size in bytes.</param>
    /// <param name="remoteEndpoint">The remote endpoint.</param>
    public TransferItemViewModel(
        Guid transferId,
        TransferMode mode,
        string fileName,
        long totalBytes,
        string remoteEndpoint
    )
    {
        this.TransferId = transferId;
        this.Mode = mode;
        this.FileName = fileName;
        this.m_totalBytes = totalBytes;
        this.RemoteEndpoint = remoteEndpoint;
        this.m_statusText = "Waiting...";
        this.m_speedText = string.Empty;
        this.m_etaText = string.Empty;
        this.m_stopwatch = new Stopwatch();
    }

    /// <summary>
    /// The transfer identifier.
    /// </summary>
    public Guid TransferId { get; }

    /// <summary>
    /// The transfer mode.
    /// </summary>
    public TransferMode Mode { get; }

    /// <summary>
    /// The file name.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// The remote endpoint.
    /// </summary>
    public string RemoteEndpoint { get; }

    /// <summary>
    /// The formatted total file size.
    /// </summary>
    public string FileSizeText => FormatBytes(this.m_totalBytes);

    /// <summary>
    /// The transfer title.
    /// </summary>
    public string Title => $"{this.ModeLabel}: {this.FileName}";

    /// <summary>
    /// The mode label.
    /// </summary>
    public string ModeLabel =>
        this.Mode == TransferMode.Send ? "Sending" : "Receiving";

    /// <summary>
    /// The timestamp when the transfer was accepted.
    /// </summary>
    public string StartedAtText =>
        this.m_startedAt?.ToString(
            "yyyy-MM-ddTHH:mm:ssK",
            CultureInfo.InvariantCulture
        ) ?? string.Empty;

    /// <summary>
    /// The progress percent.
    /// </summary>
    public int ProgressPercent
    {
        get => this.m_progressPercent;
        private set =>
            this.RaiseAndSetIfChanged(ref this.m_progressPercent, value);
    }

    /// <summary>
    /// The bytes transferred so far.
    /// </summary>
    public long BytesTransferred
    {
        get => this.m_bytesTransferred;
        private set
        {
            this.RaiseAndSetIfChanged(ref this.m_bytesTransferred, value);
            this.RaisePropertyChanged(nameof(this.ProgressText));
        }
    }

    /// <summary>
    /// The progress text showing bytes transferred / total.
    /// </summary>
    public string ProgressText =>
        $"{FormatBytes(this.BytesTransferred)} / {this.FileSizeText}";

    /// <summary>
    /// The status text.
    /// </summary>
    public string StatusText
    {
        get => this.m_statusText;
        private set => this.RaiseAndSetIfChanged(ref this.m_statusText, value);
    }

    /// <summary>
    /// The transfer speed text.
    /// </summary>
    public string SpeedText
    {
        get => this.m_speedText;
        private set => this.RaiseAndSetIfChanged(ref this.m_speedText, value);
    }

    /// <summary>
    /// The estimated time remaining.
    /// </summary>
    public string EtaText
    {
        get => this.m_etaText;
        private set => this.RaiseAndSetIfChanged(ref this.m_etaText, value);
    }

    /// <summary>
    /// A value indicating whether the transfer has finished (completed or failed).
    /// </summary>
    public bool IsFinished
    {
        get => this.m_isFinished;
        private set => this.RaiseAndSetIfChanged(ref this.m_isFinished, value);
    }

    /// <summary>
    /// A value indicating whether the transfer completed successfully.
    /// </summary>
    public bool IsSuccess
    {
        get => this.m_isSuccess;
        private set => this.RaiseAndSetIfChanged(ref this.m_isSuccess, value);
    }

    /// <summary>
    /// Updates the transfer progress percentage.
    /// </summary>
    /// <param name="progress">The progress percent.</param>
    public void UpdateProgress(int progress)
    {
        // Capture timestamp and start stopwatch on first progress (transfer accepted).
        if (!this.m_startedAt.HasValue)
        {
            this.m_startedAt = DateTimeOffset.Now;
            this.m_stopwatch.Start();
            this.RaisePropertyChanged(nameof(this.StartedAtText));
        }

        this.ProgressPercent = progress;

        // Calculate bytes transferred from percentage
        long bytes = (long)(this.m_totalBytes * (progress / 100.0));
        this.BytesTransferred = bytes;

        // Calculate speed
        double elapsedSeconds = this.m_stopwatch.Elapsed.TotalSeconds;
        if (elapsedSeconds > 0.5 && bytes > 0)
        {
            double bytesPerSecond = bytes / elapsedSeconds;
            this.SpeedText = $"{FormatBytes((long)bytesPerSecond)}/s";

            // Calculate ETA
            if (bytesPerSecond > 0 && progress < 100)
            {
                long remainingBytes = this.m_totalBytes - bytes;
                double remainingSeconds = remainingBytes / bytesPerSecond;
                this.EtaText = FormatDuration(
                    TimeSpan.FromSeconds(remainingSeconds)
                );
            }
        }

        if (progress >= 100)
        {
            this.StatusText = "Finalizing...";
            this.EtaText = string.Empty;
        }
        else
        {
            this.StatusText = "Transferring";
        }
    }

    /// <summary>
    /// Marks the transfer as successfully completed.
    /// </summary>
    public void MarkCompleted()
    {
        this.m_stopwatch.Stop();
        this.ProgressPercent = 100;
        this.BytesTransferred = this.m_totalBytes;
        this.StatusText = "Completed";
        this.SpeedText = string.Empty;
        this.EtaText = $"in {FormatDuration(this.m_stopwatch.Elapsed)}";
        this.IsFinished = true;
        this.IsSuccess = true;
    }

    /// <summary>
    /// Marks the transfer as failed with an error message.
    /// </summary>
    /// <param name="errorMessage">The error message.</param>
    public void MarkFailed(string errorMessage)
    {
        this.m_stopwatch.Stop();
        this.StatusText = $"Failed: {errorMessage}";
        this.SpeedText = string.Empty;
        this.EtaText = string.Empty;
        this.IsFinished = true;
        this.IsSuccess = false;
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return order == 0
            ? $"{size:0} {sizes[order]}"
            : $"{size:0.##} {sizes[order]}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
        {
            return "<1s";
        }

        if (duration.TotalMinutes < 1)
        {
            return $"{(int)duration.TotalSeconds}s";
        }

        if (duration.TotalHours < 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{(int)duration.TotalHours}h {duration.Minutes}m";
    }
}

/// <summary>
/// Converts transfer state to background brush.
/// </summary>
internal sealed class TransferBackgroundConverter : IMultiValueConverter
{
    private static readonly SolidColorBrush s_defaultBrush = new(
        Color.FromArgb(40, 128, 128, 128)
    );

    private static readonly SolidColorBrush s_successBrush = new(
        Color.FromArgb(40, 76, 175, 80)
    );

    private static readonly SolidColorBrush s_failureBrush = new(
        Color.FromArgb(40, 244, 67, 54)
    );

    /// <inheritdoc />
    public object? Convert(
        IList<object?> values,
        Type targetType,
        object? parameter,
        CultureInfo culture
    )
    {
        if (values.Count < 2)
        {
            return s_defaultBrush;
        }

        bool isFinished = values[0] is true;
        bool isSuccess = values[1] is true;

        if (!isFinished)
        {
            return s_defaultBrush;
        }

        return isSuccess ? s_successBrush : s_failureBrush;
    }
}
