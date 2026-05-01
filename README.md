# CoverageMcpServer

[![build](https://github.com/Hyeonu-Cha/TestCoverageMcpServer/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Hyeonu-Cha/TestCoverageMcpServer/actions/workflows/dotnet.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

An MCP (Model Context Protocol) server that exposes .NET code coverage tooling as callable tools for AI assistants such as Claude Code or Gemini CLI.

## Purpose

This server lets an AI assistant run unit tests, collect coverage data, and analyse results — all without leaving the chat. Instead of manually running `dotnet test` and parsing reports, the AI can call the server's tools directly to:

- Discover source files and build smart batches by line budget
- Run a filtered set of tests and collect coverage
- Read compact, AI-optimised coverage summaries (method-level line/branch rates)
- Check per-file coverage against an 80% target
- Identify uncovered branches as structured JSON
- Diff coverage between runs to see only what changed
- Append new test code to an existing test file with atomic writes

## How It Works

The server starts as a console process and communicates over **stdio** using the MCP protocol. An MCP-compatible client (Claude Code, Gemini CLI, etc.) launches the process and calls its tools as if they were functions.

```
AI Client  <--stdio/MCP-->  CoverageMcpServer  <--shell-->  dotnet test + reportgenerator
```

## Available Tools

| Tool | Description |
|------|-------------|
| `GetSourceFiles` | Discover `.cs` files from a file, folder, or `.csproj` project. Returns file metadata (lines, method count) and smart batches grouped by `lineBudget`. |
| `RunTestsWithCoverage` | Run `dotnet test` with XPlat Code Coverage, generate a JSON summary via `reportgenerator`. Returns paths to `Summary.json` and `coverage.cobertura.xml`. Supports `forceRestore` and `sessionId` for concurrent isolation. |
| `GetCoverageSummary` | Parse `Summary.json` into structured class/method coverage data sorted by branch coverage ascending (lowest first). |
| `GetFileCoverage` | Get coverage for a single source file from Cobertura XML. Returns `allMeetTarget` (true when all classes have line >= 80% and branch >= 80%). Supports `sessionId`. |
| `GetUncoveredBranches` | Find uncovered branch conditions for methods matching a given name. Returns all matching methods with partial name support. Supports `sessionId`. |
| `GetCoverageDiff` | Compare current Cobertura XML against baseline. Shows method-level changes including new and removed methods. Supports `sessionId` for concurrent isolation. |
| `AppendTestCode` | Insert or append C# test code into a test file. Supports anchor-based insertion with whitespace-tolerant fallback matching. Uses atomic writes to prevent file corruption. |

## Batch Workflow

For projects with many source files, the recommended workflow is:

1. **Discover** — Call `GetSourceFiles` on a folder or `.csproj` to get all files and smart batches
2. **Run once** — Call `RunTestsWithCoverage` with a broad filter (e.g., `*`) to collect coverage across all files
3. **Check per-file** — Call `GetFileCoverage` for each file in the current batch (instant XML parsing, no test re-run)
4. **Focus** — Pick the 3 lowest branch-coverage methods and call `GetUncoveredBranches` for each
5. **Write tests** — Use `AppendTestCode` to add test methods
6. **Re-run and diff** — Run tests once, call `GetCoverageDiff` to verify improvement
7. **Repeat** — Continue until batch files reach 80% or 3 cycles with no improvement, then move to next batch

This minimises `dotnet test` invocations (the main bottleneck) while still tracking per-file progress.

## Concurrency

Multiple AI agents can safely run in parallel against the same repository by passing a `sessionId` to each tool call:

- **Isolated output directories** — `RunTestsWithCoverage` creates `TestResults-{hash}/` and `coveragereport-{hash}/` per session, preventing one agent from deleting another's XML mid-parse
- **Scoped state files** — Coverage state is written to `.mcp-coverage/.coverage-state-{hash}`, so `ResolveCoberturaPath` resolves to the correct XML for each session
- **Scoped baselines** — `GetCoverageDiff` stores baselines as `.coverage-prev-{hash}.xml` per session
- **Atomic writes** — All file writes (state files and test code) use write-to-temp-then-rename to prevent corruption from race conditions or process crashes

Without `sessionId`, tools use shared defaults — safe for single-agent use.

## Requirements

- **.NET 9.0 SDK (or later)** — [https://dotnet.microsoft.com/download](https://dotnet.microsoft.com/download)
- **reportgenerator** global tool — install once:
  ```bash
  dotnet tool install --global dotnet-reportgenerator-globaltool
  ```
- An MCP-compatible client (Claude Code, Gemini CLI, etc.)
- **`COVERAGE_MCP_ALLOWED_ROOT`** — recommended. Set to your repository root to restrict every tool's filesystem access to that subtree. Any path passed by the client outside this root is rejected with `pathNotAllowed`. When unset, the server logs a warning once and accepts any path (backward-compatible, but not recommended for shared environments).

  ```bash
  export COVERAGE_MCP_ALLOWED_ROOT=/path/to/your/repo
  ```

## Build & Run

```bash
cd <path-to-CoverageMcpServer>

# Restore dependencies
dotnet restore

# Build
dotnet build

# Run
dotnet run
```

The server will start and wait for MCP messages over stdin/stdout.

## MCP Client Configuration

To register this server in Gemini/Claude Code, add it to your MCP settings:

```json
{
  "mcpServers": {
    "coverage": {
      "command": "dotnet",
      "args": ["run", "--project", "<path-to-CoverageMcpServer>"],
      "transport": "stdio"
    }
  }
}
```

Or point directly at the compiled executable:

```json
{
  "mcpServers": {
    "coverage": {
      "command": "<path-to-CoverageMcpServer>\\bin\\Debug\\net9.0\\CoverageMcpServer.exe",
      "transport": "stdio"
    }
  }
}
```

## Tool Parameters

### `GetSourceFiles`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `path` | string | Yes | Path to a `.cs` file, folder, or `.csproj` project |
| `lineBudget` | int | No | Max total lines per batch (default: 300). Small files are grouped together; large files get their own batch. |

### `RunTestsWithCoverage`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `testProjectPath` | string | Yes | Full path to the `.csproj` test project |
| `filter` | string | Yes | Test filter string (matched against `FullyQualifiedName`). Broad filters containing `*` or `,` skip `/p:Include` scoping. |
| `workingDir` | string | No | Working directory; defaults to the project directory |
| `forceRestore` | bool | No | When `true`, skips the `--no-restore` flag. Use after scaffolding a new test project or adding NuGet packages. |
| `sessionId` | string | No | Isolates output directories (`TestResults-{hash}/`, `coveragereport-{hash}/`) and state files for concurrent multi-agent use. |

### `GetCoverageSummary`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `summaryJsonPath` | string | Yes | Full path to the generated `Summary.json` file |

### `GetFileCoverage`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `coberturaXmlPath` | string | Yes | Path to `coverage.cobertura.xml` (falls back to `.mcp-coverage/.coverage-state` if not found) |
| `sourceFileName` | string | Yes | Source file name to look up (e.g., `ExampleService.cs`) |
| `sessionId` | string | No | Resolves session-scoped state file for concurrent isolation. |

### `GetUncoveredBranches`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `coberturaXmlPath` | string | Yes | Path to `coverage.cobertura.xml` (falls back to `.mcp-coverage/.coverage-state` if not found) |
| `methodName` | string | Yes | Method name to inspect (partial match supported; returns all matching methods) |
| `sessionId` | string | No | Resolves session-scoped state file for concurrent isolation. |

### `GetCoverageDiff`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `coberturaXmlPath` | string | Yes | Path to the current `coverage.cobertura.xml` |
| `workingDir` | string | No | Directory for storing baseline; defaults to the XML's parent directory |
| `sessionId` | string | No | Isolates baseline as `.coverage-prev-{hash}.xml` and resolves session-scoped state file. |

### `AppendTestCode`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `testFilePath` | string | Yes | Full path to the target `.cs` test file |
| `codeToAppend` | string | Yes | C# code to insert |
| `insertAfterAnchor` | string | No | If provided, inserts code after the last occurrence of this string (with whitespace-tolerant fallback). If omitted, appends before the last `}`. |

## State Files

All state files are written to a `.mcp-coverage/` subdirectory inside the working directory, keeping the project root clean. Add `.mcp-coverage/` to the target repository's `.gitignore`.

| File | Purpose |
|------|---------|
| `.coverage-state` | Default Cobertura XML path for single-agent use |
| `.coverage-state-{hash}` | Session-scoped Cobertura XML path |
| `.coverage-prev.xml` | Default coverage baseline for diff |
| `.coverage-prev-{hash}.xml` | Session-scoped coverage baseline |

## Plugin (Skills & Agent)

This repo includes a `plugin/` directory with Claude Code skills and an agent definition for guided test coverage workflows:

```
plugin/
├── plugin.json
├── agents/
│   └── test-coverage.agent.md
└── skills/
    ├── scaffold-test-files/     — Create test directories and files mirroring source structure
    ├── run-coverage/            — Run tests and view coverage reports
    ├── analyze-coverage-gaps/   — Find uncovered branches and compare diffs
    └── improve-test-coverage/   — Iterative loop to reach 80% coverage
```

The skills support NUnit, xUnit, and MSTest with framework-agnostic reference docs in `references/unit.md` and `references/integration.md`.

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.Extensions.Hosting` | 9.0.0 | DI and hosting |
| `ModelContextProtocol` | 1.1.0 | MCP server framework |
| `Microsoft.CodeAnalysis.CSharp` | 5.3.0 | Roslyn AST for safe code insertion and accurate method counting (~15MB) |
