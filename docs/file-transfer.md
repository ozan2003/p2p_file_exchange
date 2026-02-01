# File Transfer Protocol

This document describes how P2P File Exchange transfers files between peers over encrypted TCP connections.

## Overview

File transfers use **TCP** with a custom encrypted transport layer (`SecureP2PStream`) that provides:

- X25519 key exchange for forward secrecy
- ChaCha20-Poly1305 authenticated encryption
- Ed25519 mutual authentication
- Per-chunk SHA-256 integrity verification

```mermaid
flowchart LR
    subgraph Sender
        A[File] --> B[Chunker]
        B --> C[SHA-256 Hash]
        C --> D[SecureP2PStream]
    end
    
    subgraph Network
        D --> E[TCP + Encryption]
    end
    
    subgraph Receiver
        E --> F[SecureP2PStream]
        F --> G[Verify Hash]
        G --> H[Write Chunk]
        H --> I[File]
    end
    
    style E fill:#4dabf7,stroke:#1971c2
```

## Transfer Flow

```mermaid
sequenceDiagram
    participant S as Sender
    participant TCP as TCP Connection
    participant R as Receiver
    
    Note over S,R: Step 1: Connection
    S->>TCP: Connect to receiver:port
    TCP->>R: Accept connection
    
    Note over S,R: Step 2: SecureP2PStream Handshake
    S->>R: X25519 ephemeral public key
    R->>S: X25519 ephemeral public key
    Note over S,R: Both derive session keys via HKDF
    S->>R: Ed25519 identity + signature
    R->>S: Ed25519 identity + signature
    Note over S,R: TOFU verification complete
    
    Note over S,R: Step 3: Metadata Exchange
    S->>R: FileMetadata (encrypted)
    R->>R: Prompt user for approval
    
    alt User Accepts
        R->>S: TransferResponse.Accepted
        Note over S,R: Step 4: Chunk Transfer
        loop For each chunk
            S->>S: Read chunk from file
            S->>S: Compute SHA-256 hash
            S->>R: FileChunk (encrypted)
            R->>R: Verify hash
            R->>R: Write to disk
        end
        Note over S,R: Transfer complete
    else User Rejects
        R->>S: TransferResponse.Rejected
        Note over S,R: Transfer aborted
    end
```

## Protocol Messages

### Message Types

All messages are serialized as JSON and encrypted via `SecureP2PStream`.

```mermaid
classDiagram
    class FileMetadata {
        +string FileName
        +long FileSize
        +int TotalChunksNumber
        +int ChunkSize
    }
    
    class TransferResponse {
        <<enumeration>>
        Accepted
        Rejected
    }
    
    class FileChunk {
        +int ChunkIndex
        +byte[] Data
        +byte[] Hash
    }
```

### FileMetadata

Sent by the sender immediately after handshake:

```json
{
  "fileName": "document.pdf",
  "fileSize": 1048576,
  "totalChunksNumber": 4,
  "chunkSize": 262144
}
```

| Field | Type | Description |
|-------|------|-------------|
| `fileName` | string | Original filename (sanitized on receive) |
| `fileSize` | int64 | Total file size in bytes |
| `totalChunksNumber` | int32 | Number of chunks |
| `chunkSize` | int32 | Size of each chunk (default: 256 KB) |

### TransferResponse

Sent by receiver after user prompt:

```json
{
  "response": 0
}
```

| Value | Meaning |
|-------|---------|
| 0 | Accepted |
| 1 | Rejected |

### FileChunk

Sent for each chunk of the file:

```json
{
  "chunkIndex": 0,
  "data": "Base64EncodedData",
  "hash": "Base64SHA256Hash"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `chunkIndex` | int32 | Zero-based chunk index |
| `data` | Base64 | Chunk bytes |
| `hash` | Base64 | SHA-256 hash of data (32 bytes) |

## Chunking Strategy

Files are split into fixed-size chunks for streaming transfer:

```mermaid
flowchart LR
    subgraph "File (1 MB)"
        A[Chunk 0<br>256 KB] 
        B[Chunk 1<br>256 KB]
        C[Chunk 2<br>256 KB]
        D[Chunk 3<br>256 KB]
    end
    
    A --> |Hash + Send| E[Network]
    B --> |Hash + Send| E
    C --> |Hash + Send| E
    D --> |Hash + Send| E
```

### Chunk Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| Chunk Size | 256 KB | Maximum bytes per chunk |
| Buffer Size | 64 KB | File I/O buffer size |

### Chunk Count Calculation

```math
totalChunks = \lceil \frac{fileSize}{chunkSize} \rceil
```

### Last Chunk Handling

The last chunk may be smaller than `chunkSize`. The actual size is determined by the `data` field length.

## Integrity Verification

```mermaid
flowchart TD
    A[Receive Chunk] --> B[Extract Data + Hash]
    B --> C[Compute SHA-256]
    C --> D{Hash Match?}
    D -->|Yes| E[Write to Disk]
    D -->|No| F[Abort Transfer]
    F --> G[Delete Partial File]
    
    style F fill:#ff6b6b,color:#fff
    style E fill:#51cf66,color:#fff
```

### Verification Steps

1. Receiver computes `SHA256(chunk.data)`
2. Compare with `chunk.hash` using constant-time comparison
3. On mismatch: abort transfer, delete partial file
4. On match: write chunk to disk

## Filename Handling

### Sanitization

Received filenames are sanitized to prevent path traversal:

```csharp
// Dangerous: "../../../etc/passwd"
// Safe: "passwd"

fileName = Path.GetFileName(fileName);  // Remove path components
```

### Collision Handling

If the target file exists, a counter is appended:

```text
document.pdf    -> document.pdf
document.pdf    -> document (1).pdf
document.pdf    -> document (2).pdf
```

### Download Directory

Default location:

```sh
~/Downloads/P2PFileExchange/  (Linux)
%USERPROFILE%\Downloads\P2PFileExchange\  (Windows)
```

## Error Handling

### Transfer Failures

```mermaid
flowchart TD
    A[Transfer Error] --> B{Error Type}
    B -->|Connection Lost| C[Cleanup Partial File]
    B -->|Hash Mismatch| D[Cleanup Partial File]
    B -->|User Canceled| E[Cleanup Partial File]
    B -->|Handshake Failed| F[No File Created]
    B -->|Identity Mismatch| G[No File Created]
    
    C --> H[TransferFailed Event]
    D --> H
    E --> H
    F --> H
    G --> H
```

### Partial File Cleanup

If a transfer fails after some chunks were written:

1. Close file handle
2. Delete partial file from disk
3. Fire `TransferFailed` event with error message

## Events

The transfer service emits events for UI updates:

| Event | Payload | Trigger |
|-------|---------|---------|
| `TransferRequestReceived` | `TransferRequestEventArgs` | Incoming transfer request (prompts user) |
| `TransferStarted` | `TransferStartedEventArgs` | Transfer begins (metadata exchanged) |
| `TransferProgressChanged` | `TransferProgressEventArgs` | Chunk transferred (progress update) |
| `TransferCompleted` | `TransferCompletedEventArgs` | Transfer finished successfully |
| `TransferFailed` | `TransferFailedEventArgs` | Transfer failed (with error message) |

### Progress Calculation

```math
progressPercentage = \frac{chunksReceived}{totalChunks} \times 100
```

## Sender-Side Flow

```mermaid
stateDiagram-v2
    [*] --> Connecting: SendFileAsync()
    Connecting --> Handshaking: TCP connected
    Handshaking --> SendingMetadata: Handshake complete
    SendingMetadata --> AwaitingResponse: Metadata sent
    AwaitingResponse --> SendingChunks: Response = Accepted
    AwaitingResponse --> Failed: Response = Rejected
    SendingChunks --> SendingChunks: Send next chunk
    SendingChunks --> Completed: All chunks sent
    
    Connecting --> Failed: Connection error
    Handshaking --> Failed: Auth failed
    SendingChunks --> Failed: Network error
    
    Completed --> [*]
    Failed --> [*]
```

## Receiver-Side Flow

```mermaid
stateDiagram-v2
    [*] --> Listening: StartListenerAsync()
    Listening --> Accepting: Connection received
    Accepting --> Handshaking: TCP accepted
    Handshaking --> ReceivingMetadata: Handshake complete
    ReceivingMetadata --> AwaitingApproval: Metadata received
    AwaitingApproval --> ReceivingChunks: User accepts
    AwaitingApproval --> Rejected: User rejects
    ReceivingChunks --> ReceivingChunks: Receive & verify chunk
    ReceivingChunks --> Completed: All chunks received
    
    Handshaking --> Failed: Auth failed / Identity mismatch
    ReceivingChunks --> Failed: Hash mismatch / Network error
    
    Completed --> Listening
    Rejected --> Listening
    Failed --> Listening
```

## Configuration

### FileTransferOptions

```csharp
public sealed class FileTransferOptions
{
    public int ChunkSize { get; set; } = 256 * 1024;      // 256 KB
    public int BufferSize { get; set; } = 64 * 1024;      // 64 KB
    public TimeSpan TlsHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
```

## Network Requirements

### Firewall Rules

```text
TCP Inbound:  Dynamic port (assigned at runtime)
TCP Outbound: Peer's TCP port (from discovery)
```

### Port Assignment

The receiver binds to port 0, letting the OS assign an available port. This port is broadcast via discovery announcements.

## Wire Format

### Encrypted Frame Structure

All protocol messages are wrapped in `SecureP2PStream` frames:

```text
┌─────────────────────────────────────────────────────────────┐
│                    SecureP2PStream Frame                    │
├────────────────┬────────────────┬───────────────────────────┤
│ Frame Number   │ Length         │ Ciphertext + Tag          │
│ (8 bytes)      │ (2 bytes)      │ (JSON + 16 byte tag)      │
└────────────────┴────────────────┴───────────────────────────┘
```

### Message Framing

Each protocol message (metadata, response, chunk) is:

1. Serialized to JSON
2. Encrypted with ChaCha20-Poly1305
3. Sent as one or more frames (max 16 KB payload per frame)

## Sequence Diagram: Complete Transfer

```mermaid
sequenceDiagram
    participant SUI as Sender UI
    participant STS as Sender TransferService
    participant SS as SecureP2PStream
    participant TCP as TCP
    participant RS as SecureP2PStream  
    participant RTS as Receiver TransferService
    participant RUI as Receiver UI
    
    Note over SUI,RUI: User drags file to send
    SUI->>STS: SendFileAsync(filePath, peer)
    STS->>TCP: Connect to peer
    TCP->>RTS: Accept connection
    
    Note over SUI,RUI: Handshake
    STS->>SS: Create SecureP2PStream
    RTS->>RS: Create SecureP2PStream
    SS->>RS: X25519 + Ed25519 exchange
    
    Note over SUI,RUI: Metadata
    STS->>SS: WriteMetadataAsync
    SS->>RS: Encrypted metadata
    RS->>RTS: FileMetadata
    RTS->>RUI: TransferRequestReceived
    RUI->>RUI: Show confirmation dialog
    
    Note over SUI,RUI: User decision
    RUI->>RTS: RespondToTransferRequest(Accept)
    RTS->>RS: WriteResponseAsync(Accepted)
    RS->>SS: Encrypted response
    SS->>STS: TransferResponse.Accepted
    STS->>SUI: TransferStarted
    
    Note over SUI,RUI: Chunk transfer
    loop For each chunk
        STS->>STS: Read chunk + SHA-256
        STS->>SS: WriteChunkAsync
        SS->>RS: Encrypted chunk
        RS->>RTS: FileChunk
        RTS->>RTS: Verify SHA-256
        RTS->>RTS: Write to disk
        RTS->>RUI: TransferProgressChanged
        STS->>SUI: TransferProgressChanged
    end
    
    Note over SUI,RUI: Complete
    STS->>SUI: TransferCompleted
    RTS->>RUI: TransferCompleted
```
