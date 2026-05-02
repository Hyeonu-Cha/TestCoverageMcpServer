# Security Policy

## Supported Versions

CoverageMcpServer is in early development. Security fixes target the latest released
version on the `main` branch only.

| Version | Supported |
| ------- | --------- |
| latest `main` | ✅ |
| older commits | ❌ |

## Reporting a Vulnerability

If you find a security issue, please **do not open a public GitHub issue**. Instead:

1. Open a private GitHub Security Advisory at
   <https://github.com/Hyeonu-Cha/TestCoverageMcpServer/security/advisories/new>, or
2. Email the maintainer at the address listed on the [GitHub profile](https://github.com/Hyeonu-Cha).

Please include:

- A description of the issue and how to reproduce it
- The affected version (commit SHA or tag)
- Any proof-of-concept input or sample tool call

You should expect an initial acknowledgement within 7 days. We aim to release a fix
or mitigation within 30 days for high-severity issues.

## Threat Model

CoverageMcpServer runs as a local stdio process launched by an MCP client (Claude Code,
Gemini CLI, etc.) and is not exposed to the network. The threat model assumes:

- The MCP client is trusted to launch the server with the user's permissions.
- Tool calls originate from a model that may be influenced by untrusted input
  (source files, test output, coverage XML).

The server defends against the following classes of issue:

| Risk | Mitigation |
| ---- | ---------- |
| Path traversal via tool arguments | Every tool validates `path`/`workingDir`/`testFilePath` against `COVERAGE_MCP_ALLOWED_ROOT`. Calls outside the configured root return `pathNotAllowed`. |
| File corruption from concurrent writes | `AtomicWriteFile` writes to `.tmp-{guid}` then renames; per-file `SemaphoreSlim` serializes writes within a process. |
| Test file mutation outside the target file | `AppendTestCode` only inserts into the file passed by the client; Roslyn AST insertion preserves existing structure, with a string fallback that targets the last class brace. |
| Long-running test or report processes | `dotnet test` and `reportgenerator` invocations are wrapped with cancellation tokens and a default 10-minute timeout (override via `COVERAGE_MCP_DOTNET_TEST_TIMEOUT_MS`). |

## Hardening Recommendations

Operators running CoverageMcpServer in shared environments should:

- Set `COVERAGE_MCP_ALLOWED_ROOT` to the repository root. When unset, the server
  logs a warning once and accepts any path.
- Run the server as the same user as the model client; do not elevate privileges.
- Treat the `.mcp-coverage/` directory as untrusted state and add it to
  `.gitignore`.

## Out of Scope

The following are **not** considered vulnerabilities:

- The server reading/writing files inside the configured allowed root.
- The server invoking `dotnet test` and `reportgenerator` with the user's
  environment.
- Tool calls that exhaust local disk space or CPU when invoked with very large
  inputs — operators should impose resource limits at the OS level.
