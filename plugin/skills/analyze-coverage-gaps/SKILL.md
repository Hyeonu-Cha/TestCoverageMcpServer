---
name: analyze-coverage-gaps
description: "Identify uncovered branches and coverage changes for a specific .NET method. USE FOR: finding exactly which branch conditions are untested in a method, comparing current coverage against the previous baseline to see what improved. Calls GetUncoveredBranches and GetCoverageDiff via TestCoverageMcpServer MCP tools. DO NOT USE FOR: running tests (use run-coverage), full iterative improvement loops (use improve-test-coverage)."
---

# Analyze Coverage Gaps

Identify which specific branch conditions are uncovered in a .NET method, and compare the current coverage report against the previous baseline to see what changed.

## When to Use
- User asks "what branches are uncovered in method X?"
- User wants to know what improved since the last test run
- Deciding which test to write next during a coverage improvement session
- Inspecting a specific low-coverage method before writing tests

## When Not to Use
- User wants to run tests and get an overall report — use `run-coverage`
- User wants the full iterative loop of writing tests automatically — use `improve-test-coverage`
- No coverage XML file exists yet (run tests first with `run-coverage`)

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| `coberturaXmlPath` | Yes | Path to `coverage.cobertura.xml`; auto-falls back to `.coverage-state` if not found |
| `methodName` | Yes | Method name to inspect (partial match supported) |
| `workingDir` | No | Directory for storing `.coverage-prev.xml` baseline; defaults to XML's parent directory |

## Workflow

### Step 1: Find uncovered branches
Call `GetUncoveredBranches` with `coberturaXmlPath` and `methodName`.

Returns JSON:
```json
{
  "method": "ProcessOrder",
  "uncoveredBranches": [
    { "line": 42, "missing": ["condition 0 (jump)", "condition 1 (jump)"] },
    { "line": 57, "missing": ["condition 0 (jump)"] }
  ]
}
```

### Step 2: Interpret the uncovered branches
For each uncovered branch, describe in plain language what condition is missing:
- `branch-N-true` → the `if` condition was never `true`
- `branch-N-false` → the `if` condition was never `false`
- Note the line number so tests can be targeted precisely

### Step 3: Compare against previous baseline (optional)
If a previous coverage run exists, call `GetCoverageDiff` with `coberturaXmlPath`.

Returns three sections:
- `cycleImprovement` — overall delta in line and branch rates
- `changedMethods` — methods with before/after coverage values
- `unchanged` — methods with no change

### Step 4: Report findings
Present:
1. Which branches are missing and what they represent (e.g., "line 42: the null-check path when input is null was never tested")
2. Coverage diff summary if available (e.g., "+5% line, +8% branch since last run")
3. Suggested test scenario for each uncovered branch

## Validation
- [ ] `GetUncoveredBranches` returned results for the target method
- [ ] Each uncovered branch is explained in plain language
- [ ] Diff summary is shown if a baseline exists
- [ ] Suggested test scenarios match the uncovered conditions

## Common Pitfalls

| Pitfall | Solution |
|---------|----------|
| Method not found | Use a shorter partial name; method names are case-sensitive |
| No baseline for diff | First run produces no diff — run once more after adding tests |
| XML path wrong | Tool auto-reads `.coverage-state` as fallback; ensure tests were run recently |
