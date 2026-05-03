# Changelog

All notable changes to CoverageMcpServer are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `SECURITY.md` documenting threat model, private vulnerability reporting, and
  hardening recommendations.
- `CONTRIBUTING.md` with development setup and pull request guidelines.
- Issue and pull request templates under `.github/`.
- Dependabot configuration for NuGet and GitHub Actions ecosystems.
- CodeQL workflow running `security-and-quality` queries on every push, PR, and
  weekly cron.
- Least-privilege `permissions: contents: read` block on the `build` workflow.

### Changed
- `build` workflow now runs on a `ubuntu-latest`, `windows-latest`, `macos-latest`
  matrix to verify cross-platform support claimed in the README.
- `AppendTestCode` now catches generic exceptions and returns a structured
  `insertFailed` error, matching the error-handling shape of peer tools.
- README documents the `CleanupSession` tool and the list-of-paths input to
  `GetSourceFiles`.

## Earlier history

Prior to the introduction of this changelog, changes were tracked only in commit
history and merged pull requests. See `git log` and the
[Pulls page](https://github.com/Hyeonu-Cha/TestCoverageMcpServer/pulls?q=is%3Apr+is%3Amerged)
for context.

Highlights:
- Concurrent multi-agent support via `sessionId`-scoped output directories,
  state files, and baselines.
- Path validation against `COVERAGE_MCP_ALLOWED_ROOT` for every tool.
- Cancellation tokens and a 10-minute default timeout on `dotnet test` and
  `reportgenerator` invocations
  (`COVERAGE_MCP_DOTNET_TEST_TIMEOUT_MS` overrides).
- Atomic writes for state files and inserted test code.
