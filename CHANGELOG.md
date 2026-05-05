# Changelog

All notable changes to dotnet-coverage-mcp are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-05-05

First public release. Published to NuGet as `dotnet-coverage-mcp` 0.1.0
(installable via `dotnet tool install -g dotnet-coverage-mcp` or
`dnx dotnet-coverage-mcp`). Discoverable in the MCP registry under
`io.github.hyeonu-cha/dotnet-coverage-mcp`.

### Added
- `SECURITY.md` documenting threat model, private vulnerability reporting, and
  hardening recommendations.
- `CONTRIBUTING.md` with development setup and pull request guidelines.
- Issue and pull request templates under `.github/`.
- Dependabot configuration for NuGet and GitHub Actions ecosystems.
- CodeQL workflow running `security-and-quality` queries on every push, PR, and
  weekly cron.
- Least-privilege `permissions: contents: read` block on the `build` workflow.
- NuGet packaging metadata gated on `-p:IsPacking=true` so the default
  `dotnet pack` is a no-op (prevents accidental publishes of a malformed
  library package). The `McpServer` package type and embedded
  `.mcp/server.json` make the package discoverable in NuGet.org's MCP UI.

### Changed
- **Project renamed** from `CoverageMcpServer` to `dotnet-coverage-mcp`. NuGet
  package id is `dotnet-coverage-mcp`, tool command is `dotnet-coverage-mcp`,
  and the C# namespace is `DotNetCoverageMcp`. The new name reflects the
  .NET-only scope and aligns with MCP registry naming conventions.
- `build` workflow now runs on a `ubuntu-latest`, `windows-latest`, `macos-latest`
  matrix to verify cross-platform support claimed in the README.
- `AppendTestCode` now catches generic exceptions and returns a structured
  `insertFailed` error, matching the error-handling shape of peer tools.
- README documents the `CleanupSession` tool, the list-of-paths input to
  `GetSourceFiles`, the `includeClass` parameter on `RunTestsWithCoverage`,
  and the configurable `targetRate` on `GetFileCoverage`.

## Earlier history

Prior to the introduction of this changelog, changes were tracked only in commit
history and merged pull requests. See `git log` and the
[Pulls page](https://github.com/Hyeonu-Cha/dotnet-coverage-mcp/pulls?q=is%3Apr+is%3Amerged)
for context.

Highlights:
- Concurrent multi-agent support via `sessionId`-scoped output directories,
  state files, and baselines.
- Path validation against `COVERAGE_MCP_ALLOWED_ROOT` for every tool.
- Cancellation tokens and a 10-minute default timeout on `dotnet test` and
  `reportgenerator` invocations
  (`COVERAGE_MCP_DOTNET_TEST_TIMEOUT_MS` overrides).
- Atomic writes for state files and inserted test code.
