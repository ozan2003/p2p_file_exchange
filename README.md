# P2P File Exchange

Peer-to-peer file transfer app for local networks. It discovers peers via UDP
broadcast and sends files over TCP with per-chunk integrity checks, all from a
cross-platform Avalonia desktop UI.

## Features

- Automatic peer discovery on the local network
- TLS-encrypted file transfers with certificate pinning
- Per-chunk SHA-256 integrity validation
- Drag-and-drop or file picker sending
- Transfer progress, speed, and ETA tracking
- Automatic download folder and collision-safe file naming

> [!NOTE]
> Peer discovery uses UDP broadcast, so devices must be on the same LAN.

## Requirements

- .NET SDK 10.0

## Quick Start

```bash
dotnet restore
dotnet run --project src/P2PFileTransfer.Desktop
```

Run the app on two machines on the same network, start discovery, and send files
between peers.

> [!TIP]
> You can drag files onto the Transfers panel to send them to the selected peer.

## Default Download Location

Inbound files are saved to:

```text
~/Downloads/P2PFileTransfer
```

## Network Details

- UDP broadcast port: `37020`
- TCP listener port: dynamic (assigned at runtime)

> [!NOTE]
> If discovery or transfers do not work, ensure UDP broadcast and TCP traffic
> are allowed by your firewall.

## Security

All file transfers use TLS encryption with self-signed certificates. On first
run, a certificate is generated and saved to:

```text
~/.config/P2PFileTransfer/peer.pfx    (Linux)
%APPDATA%\P2PFileTransfer\peer.pfx    (Windows)
```

Certificate fingerprints are exchanged during peer discovery and verified during
TLS handshake to prevent man-in-the-middle attacks.

## Project Layout

- `src/P2PFileTransfer.Core`: discovery, protocol, and transfer logic
- `src/P2PFileTransfer.Desktop`: Avalonia UI client
