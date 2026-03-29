---
name: improve-test-coverage
description: "Iteratively write .NET unit tests to improve code coverage toward 80% line and branch targets. USE FOR: autonomously running tests, finding gaps, writing targeted tests, and repeating until 80% coverage or plateau. Calls all 7 TestCoverageMcpServer MCP tools: GetSourceFiles, RunTestsWithCoverage, GetCoverageSummary, GetCoverageDiff, GetUncoveredBranches, GetFileCoverage, AppendTestCode. Uses smart batching by line budget — small files grouped, large files solo. DO NOT USE FOR: just running tests without writing new ones (use run-coverage), inspecting a single method (use analyze-coverage-gaps)."
---

# Improve Test Coverage Iteratively

Autonomously run tests, identify coverage gaps, write targeted unit tests, and repeat until both line and branch coverage reach 80% or no further improvement is possible.

## When to Use
- User asks to "improve coverage", "get to 80% coverage", or "write tests for X"
- Starting an automated coverage improvement session for a class or project
- Coverage is below 80% and the user wants tests written automatically

## When Not to Use
- User only wants a coverage report without writing tests — use `run-coverage`
- User wants to inspect a specific method's branches only — use `analyze-coverage-gaps`
- Project has no existing test file to append to (create one first)

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| `sourcePath` | Yes | Scope target: a `.cs` file, a folder, or a `.csproj` project |
| `testProjectPath` | Yes | Full path to the `.csproj` test project |
| `filter` | Yes | Test filter string — use `*` or broad filter for batch/folder/project scope |
| `lineBudget` | No | Max total lines per batch (default: 300). Small files get grouped, large files run solo |
| `workingDir` | No | Working directory; defaults to project directory |

## Workflow

### Step 0: Setup and discover scope
1. Verify `reportgenerator` is installed (`dotnet tool list -g`); install if missing
2. Call `GetSourceFiles` with `sourcePath` and `lineBudget` to get:
   - `files` — all `.cs` files with `lines` and `methodCount` metadata
   - `batches` — files grouped by line budget (small files together, large files solo)
   - `batchCount` — how many cycles to expect

Example response:
```json
{
  "scope": "folder",
  "totalFiles": 18,
  "totalLines": 4200,
  "lineBudget": 300,
  "batchCount": 8,
  "batches": [
    [{"path": "SmallA.cs", "lines": 80}, {"path": "SmallB.cs", "lines": 120}, {"path": "SmallC.cs", "lines": 90}],
    [{"path": "BigService.cs", "lines": 920}]
  ]
}
```

### Step 0.5: Per-batch setup (repeat for each batch)
For each file in the current batch:
1. Read the source file to understand its constructor, dependencies, and public methods
2. Check if a matching test file exists; if not, use `scaffold-test-files` to create one
3. Verify the test file has a `[SetUp]` method; add one if missing using `AppendTestCode`

### Step 1: Run coverage for the batch (ONE test run)
Call `RunTestsWithCoverage` with `testProjectPath` and a **broad filter** (use `*` or namespace-level filter).
This runs `dotnet test` **once** and collects coverage across all source files.

Then for **each file in the batch**, call `GetFileCoverage` with `coberturaXmlPath` and the file name.
- If `allMeetTarget: true` → mark file as done, skip it
- If `allMeetTarget: false` → include in this cycle's work

### Step 2: Identify lowest-coverage methods across the batch
From all non-done files in the batch, pick the **3 lowest branch-coverage methods**, excluding:
- Methods already at ≥ 80% branch coverage
- Methods marked as blocked (failed 3 times with no improvement)
- Static factory methods and auto-generated property accessors

### Step 3: Find uncovered branches
For each target method, call `GetUncoveredBranches` with `coberturaXmlPath` and the method name.

Note which branch conditions (`branch-N-true`, `branch-N-false`) are uncovered and what they represent.

### Step 4: Write targeted tests for the batch
For each uncovered branch, write a focused unit test using `AppendTestCode`:
- Target one specific branch condition per test
- Max 1–3 assertions per test
- Use `[TestCase]` for data variations, separate `[Test]` methods for different mock setups
- Use `insertAfterAnchor` to place tests after the relevant test class or last test method
- Tests may go to **different test files** across the batch — that's expected

**Test quality rules (strictly enforce):**
- No bulk POCO tests (avoid mass property getter/setter tests with no logic)
- Behavioral focus: test calculations, conditional logic, or external interactions
- Descriptive names: `MethodName_Condition_ExpectedResult` pattern
- Each test must exercise a specific branch, not just increase line count

### Step 5: Re-run and check batch-level progress
Call `RunTestsWithCoverage` **once** with the same broad filter.
Call `GetFileCoverage` for **each file in the batch** to check per-file progress.
Call `GetCoverageDiff` to see method-level deltas.

For each file in the batch:
- **`allMeetTarget: true`** → mark as done, remove from batch
- **`allMeetTarget: false`** → keep in batch for next cycle

### Step 6: Repeat or advance
- **If batch has remaining files below 80%** → return to Step 2 (same batch, fewer files)
- **If all files in batch are done OR 3 cycles with no improvement** → move to next batch
- **If all batches complete** → report final coverage summary

## Termination Criteria

| Condition | Action |
|-----------|--------|
| All files in batch at ≥ 80% line AND branch | Batch done — move to next batch |
| No improvement for 3 consecutive cycles in a batch | Batch plateau — move to next batch |
| Method fails 3 times with no gain | Mark as blocked, skip in future cycles |
| All batches processed | Session complete — report final state |

## Performance Notes

- **One `dotnet test` per cycle** — not per file. This is the key optimization.
- `GetFileCoverage` is instant (XML parse) — call it for every file in the batch freely
- Small files (< 100 lines) get batched together so AI handles 3–5 at once
- Large files (> lineBudget) get their own batch for focused attention
- Use broad filter (`*` or namespace) for batch/folder/project scope to avoid `/p:Include` class scoping

## Validation
- [ ] `GetSourceFiles` returned batches grouped by line budget
- [ ] Only one `RunTestsWithCoverage` call per cycle (not per file)
- [ ] `GetFileCoverage` checked for every file in the batch after each test run
- [ ] Each test targets a specific uncovered branch (not random line coverage)
- [ ] No bulk POCO/property tests added
- [ ] Files marked done and removed from batch when they hit 80%
- [ ] Final report shows per-file and overall coverage percentages

## Common Pitfalls

| Pitfall | Solution |
|---------|----------|
| Tests compile but coverage doesn't improve | The test may not be reaching the target branch — verify mock setup matches the condition |
| `AppendTestCode` inserts in wrong location | Use a precise `insertAfterAnchor` string matching the last test method signature |
| Plateau after 1–2 cycles | Check if remaining uncovered branches require complex setup (async, private methods, static calls) — mark as blocked |
| Over-testing simple properties | Skip auto-properties with no logic; focus on methods with conditionals |
| Broad filter runs too many tests | Narrow filter to namespace level instead of `*` for faster test runs |
| Batch too large for AI context | Reduce `lineBudget` (e.g., 200) to get smaller batches |
