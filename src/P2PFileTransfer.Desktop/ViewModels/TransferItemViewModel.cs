using System;
using P2PFileTransfer.Core.Models;
using ReactiveUI;

namespace P2PFileTransfer.Desktop.ViewModels;

/// <summary>
/// Represents a transfer in the UI.
/// </summary>
public sealed class TransferItemViewModel : ReactiveObject
{
    private int m_progressPercent;
    private string m_statusText;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransferItemViewModel"/> class.
    /// </summary>
    /// <param name="transferId">The transfer identifier.</param>
    /// <param name="mode">The transfer mode.</param>
    /// <param name="fileName">The file name.</param>
    /// <param name="remoteEndpoint">The remote endpoint.</param>
    public TransferItemViewModel(
        Guid transferId,
        TransferMode mode,
        string fileName,
        string remoteEndpoint
    )
    {
        this.TransferId = transferId;
        this.Mode = mode;
        this.FileName = fileName;
        this.RemoteEndpoint = remoteEndpoint;
        this.m_statusText = "In progress";
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
    /// The transfer title.
    /// </summary>
    public string Title => $"{this.ModeLabel}: {this.FileName}";

    /// <summary>
    /// The mode label.
    /// </summary>
    public string ModeLabel =>
        this.Mode == TransferMode.Send ? "Sending" : "Receiving";

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
    /// The status text.
    /// </summary>
    public string StatusText
    {
        get => this.m_statusText;
        private set => this.RaiseAndSetIfChanged(ref this.m_statusText, value);
    }

    /// <summary>
    /// Updates the transfer progress percentage.
    /// </summary>
    /// <param name="progress">The progress percent.</param>
    public void UpdateProgress(int progress)
    {
        this.ProgressPercent = progress;
        if (progress >= 100)
        {
            this.StatusText = "Finalizing";
        }
    }

    /// <summary>
    /// Marks the transfer as successfully completed.
    /// </summary>
    public void MarkCompleted()
    {
        this.ProgressPercent = 100;
        this.StatusText = "Completed";
    }

    /// <summary>
    /// Marks the transfer as failed with an error message.
    /// </summary>
    /// <param name="errorMessage">The error message.</param>
    public void MarkFailed(string errorMessage)
    {
        this.StatusText = errorMessage;
    }
}
