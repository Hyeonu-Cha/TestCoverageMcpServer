---
name: run-coverage
description: "Run .NET tests with code coverage and display a summary. USE FOR: generating a coverage report for a test project, getting baseline line/branch coverage per class and method, checking current coverage percentages. Calls RunTestsWithCoverage then GetCoverageSummary via TestCoverageMcpServer MCP tools. Requires testProjectPath and a test filter string. DO NOT USE FOR: iterative improvement loops (use improve-test-coverage), finding specific uncovered branches (use analyze-coverage-gaps)."
---

# Run Tests with Coverage

Run a .NET test project with code coverage enabled and display a structured summary of line and branch coverage per class and method.

## When to Use
- User wants to generate or refresh a coverage report
- User asks "what is the current coverage?" or "run the tests with coverage"
- Starting a new coverage improvement session to get a baseline
- Verifying coverage after manually writing tests

## When Not to Use
- User wants to iteratively write tests to improve coverage — use `improve-test-coverage`
- User wants to find specific uncovered branches in a method — use `analyze-coverage-gaps`
- Project is not a .NET test project (no `.csproj`, no NUnit/xUnit/MSTest references)

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| `testProjectPath` | Yes | Full path to the `.csproj` test project file |
| `filter` | Yes | Test filter string matched against `FullyQualifiedName` (partial match if no dots; exact match if contains dots) |
| `workingDir` | No | Working directory; defaults to the project directory |

## Workflow

### Step 1: Verify prerequisites
Check that the `reportgenerator` global tool is installed:
```bash
dotnet tool list -g | grep reportgenerator
```
If missing, install it:
```bash
dotnet tool install -g dotnet-reportgenerator-globaltool
```

### Step 2: Run tests with coverage
Call `RunTestsWithCoverage` with the provided `testProjectPath` and `filter`.

The tool returns paths to:
- `Summary.json` — AI-optimized coverage summary
- `coverage.cobertura.xml` — detailed Cobertura XML report

### Step 3: Get coverage summary
Call `GetCoverageSummary` with the `summaryJsonPath` returned from Step 2.

Returns a JSON array sorted by branch coverage (lowest first) with:
- Class name
- `lineCoverage` and `branchCoverage` (0.0–1.0)
- `methods` array sorted by branch coverage ascending

### Step 4: Check per-file coverage (optional)
If a broad filter was used, call `GetFileCoverage` for specific source files to check if they meet the 80% target.

Returns `allMeetTarget: true/false` per file — useful for deciding which files need work.

### Step 5: Present results
Display coverage as a table. Highlight any class or method below 80% line or branch coverage.

Example format:
```
Class: MyService
  Line: 72%  Branch: 61%
  Methods (lowest branch first):
    ProcessOrder   Line: 50%  Branch: 40%
    ValidateInput  Line: 85%  Branch: 75%
```

## Validation
- [ ] `RunTestsWithCoverage` completed without errors
- [ ] `Summary.json` path was returned and passed to `GetCoverageSummary`
- [ ] Coverage summary lists classes and methods with percentages
- [ ] Methods below 80% are visually highlighted

## Common Pitfalls

| Pitfall | Solution |
|---------|----------|
| `reportgenerator` not found | Install via `dotnet tool install -g dotnet-reportgenerator-globaltool` |
| Filter matches no tests | Broaden the filter or use a partial class name |
| `Summary.json` not found | Check the path returned by `RunTestsWithCoverage`; ensure tests ran successfully |
