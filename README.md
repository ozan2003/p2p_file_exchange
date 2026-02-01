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

## Security

### Architecture Overview

```text
┌─────────────────────────────────────────────────────────────┐
│                    Security Layers                          │
├─────────────────────────────────────────────────────────────┤
│  Identity      │  Ed25519 persistent keypair                │
│  Key Exchange  │  X25519 ephemeral keys (forward secrecy)   │
│  Encryption    │  ChaCha20-Poly1305 AEAD                    │
│  Trust Model   │  TOFU with SQLite-backed database          │
│  Integrity     │  SHA-256 per-chunk verification            │
└─────────────────────────────────────────────────────────────┘
```

### Identity Keys

On first run, an Ed25519 identity keypair is generated and encrypted at rest using Argon2id key derivation:

```text
~/.local/share/P2PFileExchange/identity.key    (Linux)
%LOCALAPPDATA%\P2PFileExchange\identity.key    (Windows)
```

The identity provides:

- **Peer ID**: Derived from SHA-256 of the public key
- **Fingerprint**: Human-readable hash for verification
- **Signatures**: For discovery authentication

### Encrypted Transport

All file transfers use a custom encrypted transport (`SecureP2PStream`) that replaces TLS:

1. **X25519 key exchange** - Ephemeral keys provide forward secrecy
2. **HKDF key derivation** - Separate TX/RX session keys
3. **Ed25519 mutual authentication** - Both peers prove identity
4. **ChaCha20-Poly1305 frames** - Authenticated encryption with replay protection

### Trust Model (TOFU)

Peer identities follow Trust-On-First-Use:

- First contact: Identity is stored in the trust database
- Subsequent contacts: Identity must match stored key
- Mismatch: Connection is rejected (potential MITM attack)

Trust database location:

```text
~/.local/share/P2PFileExchange/trust.db    (Linux)
%LOCALAPPDATA%\P2PFileExchange\trust.db    (Windows)
```

### Discovery Authentication

Each discovery broadcast includes:

- Peer ID, display name, IP address, TCP port
- Ed25519 public key
- Timestamp and random nonce (anti-replay)
- Ed25519 signature over all fields

Invalid signatures are silently discarded.

## Documentation

For detailed protocol specifications, see:

| Document | Description |
|----------|-------------|
| [Security Architecture](docs/security.md) | Cryptographic design, threat model, trust database |
| [Peer Discovery Protocol](docs/peer-discovery.md) | UDP broadcast, signed announcements, verification |
| [File Transfer Protocol](docs/file-transfer.md) | TCP transport, chunking, integrity checks |

## Project Layout

```text
src/
├── P2PFileExchange.Core/           # Core library
│   ├── Models/                     # Data models
│   ├── Services/
│   │   ├── Discovery/              # UDP peer discovery
│   │   ├── Security/               # Crypto, trust, audit
│   │   └── Transfer/               # TCP file transfer
│   └── Utilities/                  # Helpers
│
└── P2PFileExchange.Desktop/        # Avalonia UI client
    ├── ViewModels/                 # MVVM view models
    ├── Views/                      # AXAML views
    └── Services/                   # UI services
```

## Building

```bash
# Restore dependencies
dotnet restore

# Build release
dotnet build -c Release

# Run tests (if any)
dotnet test

# Publish for Linux
dotnet publish src/P2PFileExchange.Desktop -c Release -r linux-x64

# Publish for Windows
dotnet publish src/P2PFileExchange.Desktop -c Release -r win-x64
```

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
