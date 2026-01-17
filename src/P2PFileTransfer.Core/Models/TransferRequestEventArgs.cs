using System;

namespace P2PFileTransfer.Core.Models;

/// <summary>
/// Provides data for the transfer request received event.
/// This is raised when an incoming transfer request is received,
/// allowing the user to accept or reject the transfer.
/// </summary>
public sealed class TransferRequestEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransferRequestEventArgs"/> class.
    /// </summary>
    /// <param name="requestId">The unique request identifier.</param>
    /// <param name="metadata">The file metadata from the sender.</param>
    /// <param name="remoteEndpoint">The remote endpoint of the sender.</param>
    /// <param name="senderDisplayName">The display name of the sender, if known.</param>
    public TransferRequestEventArgs(
        Guid requestId,
        FileMetadata metadata,
        string remoteEndpoint,
        string? senderDisplayName
    )
    {
        this.RequestId = requestId;
        this.Metadata = metadata;
        this.RemoteEndpoint = remoteEndpoint;
        this.SenderDisplayName = senderDisplayName;
    }

    /// <summary>
    /// The unique request identifier used to respond to this request.
    /// </summary>
    public Guid RequestId { get; }

    /// <summary>
    /// The file metadata describing the incoming file.
    /// </summary>
    public FileMetadata Metadata { get; }

    /// <summary>
    /// The remote endpoint of the sender (IP:Port).
    /// </summary>
    public string RemoteEndpoint { get; }

    /// <summary>
    /// The display name of the sender, if known from peer discovery.
    /// </summary>
    public string? SenderDisplayName { get; }
}
