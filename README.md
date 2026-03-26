# CoverageMcpServer

An MCP (Model Context Protocol) server that exposes .NET code coverage tooling as callable tools for AI assistants such as Claude Code or Gemini CLI.

## Purpose

This server lets an AI assistant run unit tests, collect coverage data, and analyse results — all without leaving the chat. Instead of manually running `dotnet test` and parsing reports, the AI can call the server's tools directly to:

- Run a filtered set of tests and collect coverage
- Read compact, AI-optimised coverage summaries (method-level line/branch rates)
- Identify uncovered branches as structured JSON
- Diff coverage between runs to see only what changed
- Append new test code to an existing test file

## How It Works

The server starts as a console process and communicates over **stdio** using the MCP protocol. An MCP-compatible client (Claude Code, Gemini CLI, etc.) launches the process and calls its tools as if they were functions.

```
AI Client  <--stdio/MCP-->  CoverageMcpServer  <--shell-->  dotnet test + reportgenerator
```

## Available Tools

| Tool | Description |
|------|-------------|
| `RunTestsWithCoverage` | Runs `dotnet test` with XPlat Code Coverage, then generates a JSON summary via `reportgenerator`. Returns paths to both `Summary.json` and `coverage.cobertura.xml`. Writes the XML path to `.coverage-state` for downstream tools. |
| `GetCoverageSummary` | Parses `Summary.json` and returns compact JSON — an array of classes with `lineCoverage`, `branchCoverage`, and `methods` sorted by branch coverage ascending (lowest first). |
| `GetUncoveredBranches` | Parses Cobertura XML and returns compact JSON with uncovered branch conditions per line for a named method. Falls back to `.coverage-state` if the given path doesn't exist. |
| `GetCoverageDiff` | Compares current Cobertura XML against `.coverage-prev.xml` and returns compact JSON with `cycleImprovement` deltas, `changedMethods`, and `unchanged` lists. Saves current as baseline for next diff. |
| `AppendTestCode` | Appends or inserts a block of C# code into an existing test file. Supports an optional anchor string to insert after a specific location rather than at the end. |

## Requirements

- **.NET 9.0 SDK (or .NET 10.0)** — [https://dotnet.microsoft.com/download](https://dotnet.microsoft.com/download)
- **reportgenerator** global tool — install once:
  ```bash
  dotnet tool install --global dotnet-reportgenerator-globaltool
  ```
- An MCP-compatible client (Claude Code, Gemini CLI, etc.)

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

### `RunTestsWithCoverage`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `testProjectPath` | string | Yes | Full path to the `.csproj` test project |
| `filter` | string | Yes | Test filter string (matched against `FullyQualifiedName`) |
| `workingDir` | string | No | Working directory; defaults to the project directory |

### `GetCoverageSummary`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `summaryJsonPath` | string | Yes | Full path to the generated `Summary.json` file |

### `GetUncoveredBranches`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `coberturaXmlPath` | string | Yes | Path to `coverage.cobertura.xml` (or its parent directory) |
| `methodName` | string | Yes | Method name to inspect (partial match supported) |

### `GetCoverageDiff`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `coberturaXmlPath` | string | Yes | Path to the current `coverage.cobertura.xml` |
| `workingDir` | string | No | Directory for storing `.coverage-prev.xml`; defaults to the XML's parent directory |

### `AppendTestCode`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `testFilePath` | string | Yes | Full path to the target `.cs` test file |
| `codeToAppend` | string | Yes | C# code to insert |
| `insertAfterAnchor` | string | No | If provided, inserts code after the last occurrence of this string in the file. If omitted, appends to end. |

## Dependencies

| Package | Version |
|---------|---------|
| `Microsoft.Extensions.Hosting` | 9.0.0 |
| `ModelContextProtocol` | 1.1.0 |
