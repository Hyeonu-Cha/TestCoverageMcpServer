# Contributing to CoverageMcpServer

Thanks for your interest in contributing. This document covers the development
workflow, expectations for pull requests, and how to report issues.

## Reporting Issues

- **Bugs**: open an issue using the bug report template. Include the .NET SDK
  version (`dotnet --version`), OS, and a minimal reproduction.
- **Feature requests**: open an issue using the feature request template. Explain
  the use case before proposing an implementation.
- **Security vulnerabilities**: do **not** open a public issue. Follow the private
  reporting process in [SECURITY.md](SECURITY.md).

## Development Setup

### Prerequisites
- .NET 9.0 SDK or later
- `dotnet-reportgenerator-globaltool`:
  ```bash
  dotnet tool install --global dotnet-reportgenerator-globaltool
  ```

### Build and test
```bash
dotnet restore
dotnet build
dotnet test
```

All tests must pass before opening a pull request. CI runs the same commands on
`ubuntu-latest` against .NET 9.0.x — see `.github/workflows/dotnet.yml`.

### Running the server locally
```bash
dotnet run
```
The server speaks MCP over stdio; pair it with an MCP client (Claude Code, Gemini
CLI) per the README's MCP Client Configuration section.

## Pull Request Guidelines

1. **Branch from `main`**. Use a descriptive branch name (e.g.
   `fix/cobertura-parse-edge-case`, `improvement/session-cleanup`).
2. **Keep PRs focused**. One concern per PR. Refactors land separately from
   feature work.
3. **Match existing style**. Service classes go under `Services/`, MCP tool
   methods stay in `CoverageTools.cs`, integration tests under
   `CoverageMcpServer.Tests/Integration/`.
4. **Write or update tests**. Bug fixes need a regression test; new tools need
   integration coverage.
5. **Update docs**. If a tool's parameters change, update `README.md` and the
   relevant `plugin/skills/*/SKILL.md`.
6. **Commit messages**: imperative mood, present tense. Explain *why* in the
   body when the *what* isn't self-evident.

## Code Conventions

- DI for all services; null-guard constructor arguments with
  `ArgumentNullException.ThrowIfNull`.
- Path-bearing tool inputs must be validated through `IPathGuard`.
- Long-running external processes must propagate `CancellationToken` and respect
  `COVERAGE_MCP_DOTNET_TEST_TIMEOUT_MS`.
- File writes must go through `IFileService.AtomicWriteFile` to avoid partial
  writes under concurrent access.

## Releasing

Releases are tagged from `main` once the changeset stabilises. See
[CHANGELOG.md](CHANGELOG.md) for the version history and release notes format.
