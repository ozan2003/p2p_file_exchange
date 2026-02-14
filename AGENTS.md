# AGENTS.md

Guidelines for AI coding agents working in this repository.

## Commands

```bash
# Restore dependencies
dotnet restore

# Build (debug)
dotnet build

# Build (release)
dotnet build -c Release

# Run the desktop app
dotnet run --project src/P2PFileExchange.Desktop

# Run tests (when available)
dotnet test

# Format code per .editorconfig
dotnet format
```

## Tech Stack

- **.NET 10.0**, C#
- **Avalonia UI 11.3** (cross-platform desktop UI)
- **SQLite** via `Microsoft.Data.Sqlite` (trust database, audit log)
- Nullable reference types enabled (`<Nullable>enable</Nullable>`)
- Implicit usings disabled (`<ImplicitUsings>disable</ImplicitUsings>`)
- File-scoped namespaces

### P2PFileExchange.Core (class library)

| Package | Purpose |
|---------|---------|
| Sodium.Core | Ed25519/X25519/ChaCha20-Poly1305 cryptography |
| Konscious.Security.Cryptography.Argon2 | Key derivation |
| System.Security.Cryptography.ProtectedData | OS-level data protection |
| Microsoft.Data.Sqlite | Trust database and audit log (SQLite) |

### P2PFileExchange.Desktop (Avalonia UI)

| Package | Purpose |
|---------|---------|
| Avalonia UI 11.3 | Cross-platform UI framework |
| ReactiveUI.Avalonia | Reactive MVVM bindings |
| Microsoft.Extensions.DependencyInjection | Service container |

The desktop project follows the **MVVM** pattern: ViewModels expose reactive
properties, Views are AXAML, and services are injected via DI.

## Database

Two SQLite databases, both under `{LocalApplicationData}/P2PFileExchange/`:

| Database | File | Owning Service |
|----------|------|----------------|
| Trust DB | `trust.db` | `PeerTrustManager` (`Services/Security/`) |
| Audit Log | `security_audit.db` | `SecurityAuditLog` (`Services/Security/`) |

Typical runtime paths:

- Linux: `~/.local/share/P2PFileExchange/`
- Windows: `%LOCALAPPDATA%\P2PFileExchange\`

### Access Patterns

- Raw SQL with `Microsoft.Data.Sqlite` (no ORM)
- Single `SqliteConnection` per service, WAL journal mode
- Concurrency guarded by `SemaphoreSlim`
- Both services implement `IAsyncDisposable`

### Schema

- **TrustedPeers**: peer identity, trust level, transfer stats
- **SchemaVersion**: single-row version tracking (current: v1)
- **AuditLog**: timestamped security events with severity, peer info

### Versioning

- Trust DB uses a `SchemaVersion` table (v1) with `CREATE TABLE IF NOT EXISTS`
- Audit Log uses try/catch `ALTER TABLE` for column migrations

The orchestrator in the Desktop layer is `PeerTrustService`
(`Desktop/Services/`), which coordinates both managers via DI.

## Code Style

All style rules are enforced by `.editorconfig` and `EnforceCodeStyleInBuild`
is enabled in both `.csproj` files. Key rules:

### Formatting

- 4-space indentation, UTF-8 encoding, LF line endings for C# files
- Maximum line length: 80 characters
- Allman brace style (opening `{` on its own line)

### Naming

| Symbol | Convention | Example |
|--------|-----------|---------|
| Private static fields | `s_` prefix + camelCase | `s_instance` |
| Private/internal fields | `m_` prefix + camelCase | `m_buffer` |
| Constants | PascalCase | `MaxRetries` |
| Interfaces | `I` prefix + PascalCase | `ITransferService` |
| Types, properties, methods | PascalCase | `PeerDiscoveryService` |

### Language Preferences

- Qualify members with `this.` (fields, properties, methods, events)
- Prefer explicit types over `var`
- Use file-scoped namespaces
- Do not use primary constructors
- Do not use top-level statements
- Sort `System` usings first, separate import directive groups
- Place `using` directives outside the namespace

## Testing

> **Status**: No test projects exist yet.

### Planned conventions

- **Framework**: xUnit
- **Project name**: `P2PFileExchange.Core.Tests` under `src/`
- **Test naming**: `MethodName_StateUnderTest_ExpectedBehavior`
- **Scope**: Unit tests for services and utilities, integration tests for
  network and cryptographic flows

## Project Structure

```text
src/
├── P2PFileExchange.Core/           # Core library
│   ├── Models/                         # Data models
│   ├── Services/
│   │   ├── Discovery/                  # UDP peer discovery
│   │   ├── Security/                   # Crypto, trust, audit
│   │   └── Transfer/                   # TCP file transfer
│   ├── Serialization/                  # JSON serialization
│   └── Utilities/                      # Helpers
│
└── P2PFileExchange.Desktop/        # Avalonia UI client
    ├── ViewModels/                     # MVVM view models
    ├── Views/                          # AXAML views
    ├── Services/                       # UI services
    └── Settings/                       # App settings
```

## Git Workflow

- **Branch**: `master` (single branch, remote `origin/master`)
- **Commit style**: Conventional-style prefixes with scope:
  - `feat(scope): description` -- new feature
  - `fix(scope): description` -- bug fix
  - `refactor(scope): description` -- code restructuring
  - `docs(scope): description` -- documentation changes
  - `chore(scope): description` -- maintenance tasks
- Commits go directly to `master` (current practice)

## Documentation

Detailed protocol and design docs live under `docs/`:

| Document | Description |
|----------|-------------|
| [docs/security.md](docs/security.md) | Cryptographic design, threat model, trust database |
| [docs/peer-discovery.md](docs/peer-discovery.md) | UDP broadcast protocol, signed announcements |
| [docs/file-transfer.md](docs/file-transfer.md) | TCP transport, chunking, integrity checks |

## Boundaries

The following actions require **explicit user approval** before proceeding:

- **Security**: Do not modify cryptographic primitives, key derivation, trust
  model, or `SecureP2PStream` handshake logic.
- **Dependencies**: Do not add, remove, or upgrade NuGet packages.
- **Protocol docs**: Do not modify files under `docs/`.
- **Architecture**: Do not add, remove, or rename projects in the solution.
- **Generated files**: Do not modify `.sln`, `.slnx`, or `app.manifest`.
- **File deletion**: Do not delete any file.

The following are **unconditional prohibitions**:

- Do not auto-commit, force push, or modify git configuration.
- Never hardcode keys, passwords, or secrets.
- Never commit sensitive data.
