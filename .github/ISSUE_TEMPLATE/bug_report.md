---
name: Bug report
about: Report unexpected behavior in a tool, parsing logic, or the server itself
title: "[Bug] "
labels: bug
---

## Description
A clear and concise description of what the bug is.

## Reproduction
Steps or a minimal MCP tool call sequence that triggers the bug:

1. Configure server with `...`
2. Call `<ToolName>` with arguments `...`
3. Observe `...`

## Expected behavior
What you expected to happen.

## Actual behavior
What actually happened. Paste any error JSON returned by the tool.

## Environment
- CoverageMcpServer commit / version:
- .NET SDK version (`dotnet --version`):
- OS and version:
- MCP client (Claude Code / Gemini CLI / other) and version:
- `COVERAGE_MCP_ALLOWED_ROOT` set? (yes/no)

## Additional context
Logs from stderr, relevant `Summary.json` / Cobertura snippets, or anything else
that helps narrow down the cause.
