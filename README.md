# P2P File Exchange

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
[![Stars](https://img.shields.io/github/stars/ozan2003/p2p_file_exchange)](https://github.com/ozan2003/p2p_file_exchange/stargazers)
[![Last Commit](https://img.shields.io/github/last-commit/ozan2003/p2p_file_exchange)](https://github.com/ozan2003/p2p_file_exchange/commits/master)
[![Code Size](https://img.shields.io/github/languages/code-size/ozan2003/p2p_file_exchange)](https://github.com/ozan2003/p2p_file_exchange)

Simple peer-to-peer file sharing for your local network. Run it on two devices on the same Wi‑Fi/LAN, it finds other peers automatically, and can send files through a cross‑platform desktop app.

> [!WARNING]
> This app is not audited for security. Do not use it to transfer sensitive files
> over untrusted networks.

## Features

- **Automatic peer discovery** on the local network via UDP broadcast
- **Ed25519-signed discovery** to prevent peer impersonation
- **End-to-end encrypted transfers** with X25519 + ChaCha20-Poly1305
- **TOFU (Trust-On-First-Use)** identity verification with persistent trust database
- **Per-chunk SHA-256** integrity validation
- **Drag-and-drop** or file picker sending
- **Transfer progress**, speed, and ETA tracking
- **Automatic download folder** and collision-safe file naming

## Screenshots

[![p2p_mainscreen.png](https://i.postimg.cc/SxdCCqDX/p2p_mainscreen.png)](https://postimg.cc/6878Nssw)
[![p2p_receiving.png](https://i.postimg.cc/P5yYYh2p/p2p_receiving.png)](https://postimg.cc/NKyKwhhB)

> [!NOTE]
> Peer discovery uses UDP broadcast, so devices must be on the same LAN.

## Requirements

- .NET SDK 10.0

## Quick Start

```bash
dotnet restore
dotnet run --project src/P2PFileExchange.Desktop
```

Run the app on two machines on the same network, start discovery, and send files
between peers.

## Default Download Location

Inbound files are saved to:

```text
~/Downloads/P2PFileExchange    (Linux)
%USERPROFILE%\Downloads\P2PFileExchange    (Windows)
```

## Network Details

| Protocol | Port | Purpose |
|----------|------|---------|
| UDP | 37020 | Peer discovery broadcasts |
| TCP | Dynamic | File transfers (port announced via discovery) |

> [!NOTE]
> If discovery or transfers do not work, ensure UDP broadcast and TCP traffic
> are allowed by your firewall.

## Documentation

For detailed specifications, see:

| Document | Description |
|----------|-------------|
| [Security Architecture](docs/security.md) | Cryptographic design, threat model, trust database |
| [Peer Discovery Protocol](docs/peer-discovery.md) | UDP broadcast, signed announcements, verification |
| [File Transfer Protocol](docs/file-transfer.md) | TCP transport, chunking, integrity checks |

## Project Layout

```text
src/
├── P2PFileExchange.Core/       # Core library
│   ├── Models/                     # Data models
│   ├── Services/
│   │   ├── Discovery/              # UDP peer discovery
│   │   ├── Security/               # Crypto, trust, audit
│   │   └── Transfer/               # TCP file transfer
│   └── Utilities/                  # Helpers
│
└── P2PFileExchange.Desktop/    # Avalonia UI client
    ├── ViewModels/                 # MVVM view models
    ├── Views/                      # AXAML views
    ├── Services/                   # UI services
    └── Settings/                   # App settings
```

## Building

```bash
# Restore dependencies
dotnet restore

# Build release
dotnet build -c Release

# Run tests (if any)
dotnet test
```

## License

This project is licensed under the MIT License - refer to [LICENSE](LICENSE) file for details.
