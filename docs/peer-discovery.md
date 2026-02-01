# Peer Discovery Protocol

This document describes how P2P File Exchange discovers peers on the local network using authenticated UDP broadcasts.

## Overview

Peer discovery uses **UDP broadcast** on port `37020` to announce presence and discover other peers on the same LAN. All announcements are **Ed25519 signed** to prevent peer impersonation.

```mermaid
flowchart LR
    subgraph LAN[Local Network]
        A[Peer A] -->|UDP Broadcast| BC((Broadcast))
        B[Peer B] -->|UDP Broadcast| BC
        C[Peer C] -->|UDP Broadcast| BC
        BC -->|Receive| A
        BC -->|Receive| B
        BC -->|Receive| C
    end
    
    style BC fill:#4dabf7,stroke:#1971c2
```

## Protocol Flow

```mermaid
sequenceDiagram
    participant A as Peer A
    participant BC as UDP Broadcast (255.255.255.255:37020)
    participant B as Peer B
    
    Note over A,B: Peer A starts discovery
    
    loop Every 5 seconds
        A->>A: Generate nonce (16 bytes)
        A->>A: Create announcement JSON
        A->>A: Sign with Ed25519 identity key
        A->>BC: Broadcast signed announcement
    end
    
    BC->>B: Receive announcement
    B->>B: Rate limit check
    B->>B: Verify timestamp (±30s)
    B->>B: Check nonce (replay protection)
    B->>B: Verify Ed25519 signature
    B->>B: Derive PeerId from PublicKey
    B->>B: TOFU: Check known identity
    
    alt Valid Announcement
        B->>B: Update peer registry
        B->>B: Notify UI (PeerUpdated event)
    else Invalid
        B->>B: Log and discard
    end
```

## Announcement Message Format

### JSON Structure

```json
{
  "peerId": "f3a7b82c-91d4-4e6f-a2c8-a4e917b3d6f2",
  "displayName": "Alice's PC",
  "ipAddress": "192.168.1.100",
  "tcpPort": 45678,
  "publicKey": "Base64(Ed25519PublicKey)",
  "timestamp": 1706745600,
  "nonce": "Base64(16RandomBytes)",
  "signature": "Base64(Ed25519Signature)"
}
```

### Field Descriptions

| Field | Type | Description |
|-------|------|-------------|
| `peerId` | GUID | Derived from SHA-256(publicKey)\[0:16] |
| `displayName` | string | Human-readable peer name |
| `ipAddress` | string | IPv4 address of the sender |
| `tcpPort` | uint16 | TCP port for file transfers |
| `publicKey` | Base64 | Ed25519 public key (32 bytes) |
| `timestamp` | int64 | Unix timestamp (seconds) |
| `nonce` | Base64 | Random 16-byte nonce |
| `signature` | Base64 | Ed25519 signature (64 bytes) |

### Signature Computation

The signature covers a **canonical JSON** representation (sorted keys, no whitespace):

```csharp
canonical_json = {
    "displayName": "Alice's PC",
    "ipAddress": "192.168.1.100", 
    "nonce": "Base64Nonce",
    "peerId": "f3a7b82c-91d4-4e6f-a2c8-a4e917b3d6f2",
    "publicKey": "Base64PublicKey",
    "tcpPort": 45678,
    "timestamp": 1706745600
}

signature = Ed25519.Sign(identity_private_key, UTF8(canonical_json))
```

## Verification Process

```mermaid
flowchart TD
    A[Receive UDP Packet] --> B{Parse JSON}
    B -->|Failed| X1[Discard]
    B -->|Success| C{Self-announcement?}
    C -->|Yes| X2[Ignore]
    C -->|No| D{Rate Limit OK?}
    D -->|No| X3[Discard + Log]
    D -->|Yes| E{Timestamp Valid?}
    E -->|No| X4[Discard: Clock Skew]
    E -->|Yes| F{Nonce Unseen?}
    F -->|No| X5[Discard: Replay]
    F -->|Yes| G[Build Canonical JSON]
    G --> H{Verify Signature}
    H -->|Invalid| X6[Discard + Log]
    H -->|Valid| I{PeerId matches SHA256 PubKey?}
    I -->|No| X7[Discard: ID Mismatch]
    I -->|Yes| J{Known Peer?}
    J -->|No| K[TOFU: Accept & Store]
    J -->|Yes| L{Identity Matches?}
    L -->|No| X8[Discard: Identity Changed]
    L -->|Yes| M[Update Peer Info]
    
    K --> N[Notify UI]
    M --> N
    
    style X1 fill:#ff6b6b,color:#fff
    style X2 fill:#868e96
    style X3 fill:#ff6b6b,color:#fff
    style X4 fill:#ff6b6b,color:#fff
    style X5 fill:#ff6b6b,color:#fff
    style X6 fill:#ff6b6b,color:#fff
    style X7 fill:#ff6b6b,color:#fff
    style X8 fill:#ff6b6b,color:#fff
    style N fill:#51cf66,color:#fff
```

### Verification Steps

1. **Rate Limiting**: Max 30 announcements per peer per minute
2. **Timestamp Check**: Must be within ±30 seconds of local time
3. **Nonce Check**: Nonces are cached for 2 minutes to prevent replay
4. **Signature Verification**: Ed25519 signature over canonical JSON
5. **PeerId Derivation**: Verify `peerId == SHA256(publicKey)[0:16]`
6. **TOFU Check**: If peer known, identity must match stored key

## Peer Registry

Discovered peers are stored in a thread-safe dictionary:

```csharp
ConcurrentDictionary<Guid, PeerInfo> m_peers
```

### PeerInfo Structure

```csharp
public sealed class PeerInfo
{
    public Guid PeerId { get; set; }
    public string DisplayName { get; set; }
    public IPAddress IPAddress { get; set; }
    public ushort TcpPort { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public string IdentityPublicKey { get; set; }      // Base64
    public string IdentityFingerprint { get; set; }    // Human-readable
    public TrustedPeerInfo? TrustInfo { get; set; }    // From trust DB
}
```

## Peer Lifecycle

```mermaid
stateDiagram-v2
    [*] --> Discovered: First announcement
    Discovered --> Active: Announcements received
    Active --> Active: Update LastSeen
    Active --> Stale: No announcement for 30s
    Stale --> Active: Announcement received
    Stale --> Removed: Timeout (60s)
    Removed --> [*]: PeerRemoved event
```

### Timeouts

| Parameter | Default Value | Description |
|-----------|---------------|-------------|
| Broadcast Interval | 5 seconds | How often to send announcements |
| Peer Timeout | 30 seconds | Mark peer as stale |
| Cleanup Interval | 10 seconds | How often to check for stale peers |
| Deduplication Window | 10 seconds | Ignore duplicate announcements |

## Security Measures

### Anti-Replay Protection

```mermaid
flowchart LR
    A[Nonce Received] --> B{In Cache?}
    B -->|Yes| C[Reject: Replay]
    B -->|No| D[Add to Cache]
    D --> E[Set Expiry: 2 min]
    E --> F[Accept]
    
    G[Cleanup Timer] --> H[Remove Expired Nonces]
    
    style C fill:#ff6b6b,color:#fff
    style F fill:#51cf66,color:#fff
```

### Rate Limiting

```text
Per-Peer Rate Limit:
  - Window: 1 minute (sliding)
  - Max announcements: 30
  - Action on exceed: Discard + log
```

### Timestamp Validation

```math
Valid if: |local_time - announcement_time| \leq 30 \text{ seconds}
```

This prevents attackers from replaying old captured announcements.

## Events

The discovery service emits events for UI updates:

| Event | Payload | Trigger |
|-------|---------|---------|
| `PeerUpdated` | `PeerInfo` | New peer discovered or existing peer updated |
| `PeerRemoved` | `Guid` (PeerId) | Peer timed out and removed |
| `StatusChanged` | `string` | Discovery started/stopped, errors |

## Configuration

### PeerDiscoveryOptions

```csharp
public sealed class PeerDiscoveryOptions
{
    public ushort BroadcastPort { get; set; } = 37020;
    public IPAddress BroadcastAddress { get; set; } = IPAddress.Broadcast;
    public TimeSpan BroadcastInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan PeerTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
```

## Network Requirements

### Firewall Rules

```text
UDP Inbound:  Port 37020 (discovery)
UDP Outbound: Port 37020 (discovery)
```

### Network Topology

Discovery only works on the **same broadcast domain**:

- Same LAN segment
- Same WiFi network
- Bridged network interfaces

**Does NOT work across:**

- Different subnets (unless broadcast relay configured)
- VPNs (unless broadcast enabled)
- Internet

## Sequence Diagram: Full Discovery Flow

```mermaid
sequenceDiagram
    participant UI as Desktop UI
    participant DS as DiscoveryService
    participant IK as IdentityKeyManager
    participant UDP as UDP Socket
    participant Net as Network
    
    Note over UI,Net: Startup
    UI->>IK: LoadOrCreateAsync(password)
    IK-->>UI: Identity loaded
    UI->>DS: StartAsync(tcpPort, displayName, identityKeyManager)
    DS->>UDP: Bind to port 37020
    DS->>DS: Start broadcast loop
    DS->>DS: Start listen loop
    DS->>DS: Start cleanup loop
    DS-->>UI: StatusChanged("Discovery started")
    
    Note over UI,Net: Broadcasting
    loop Every 5 seconds
        DS->>IK: Sign(canonicalJson)
        IK-->>DS: signature
        DS->>UDP: Send announcement
        UDP->>Net: Broadcast to 255.255.255.255:37020
    end
    
    Note over UI,Net: Receiving
    Net->>UDP: Incoming announcement
    UDP->>DS: Receive packet
    DS->>DS: Verify (rate, time, nonce, signature, TOFU)
    DS->>DS: Update peer registry
    DS-->>UI: PeerUpdated(peerInfo)
    
    Note over UI,Net: Cleanup
    loop Every 10 seconds
        DS->>DS: Check peer timeouts
        DS->>DS: Remove stale peers
        DS-->>UI: PeerRemoved(peerId)
    end
```
