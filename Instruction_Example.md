# Production Coverage Mission: ExampleFile.cs

## Step -1: Variable Resolution (Confirm Before Executing)

Resolve all variables before running any tool. Do not proceed until each value is confirmed.

| Variable | Value |
| :--- | :--- |
| `SOURCE_PATH` | `src/YourProject/ExampleFile.cs` |
| `TEST_FILE` | `tests/YourProject.Tests/Unit/ExampleFileTests.cs` |
| `TEST_PROJECT` | `tests/YourProject.Tests/YourProject.Tests.csproj` |

> If any path does not exist on disk, stop and ask the user to correct it.

## Step 0: Discover Scope & Build Batches

1. **Tooling Check:** Run `dotnet tool list -g`. If `reportgenerator` does not appear, install it: `dotnet tool install -g dotnet-reportgenerator-globaltool`.
2. **Scope Discovery:** Call `GetSourceFiles` with:
   - `path` → `SOURCE_PATH` (for a single file), a folder path (for a directory), or a `.csproj` path (for a project)
   - `lineBudget` → `300` (default; adjust if context is limited)

   This returns file metadata (lines, method count) and smart batches. Small files are grouped together; large files get their own batch.
3. **Pick a batch:** Start with batch 0. You will work through each batch sequentially.

## Step 1: Infrastructure & DI Setup (per batch)

1. **Dependency Analysis:** Read each source file in the current batch. Identify constructors, injected interfaces, and public methods.
2. **Test File Check:** If test files don't exist yet for files in this batch, use the `scaffold-test-files` skill or manually create them:
   - Unit tests: mock all constructor interfaces, instantiate with `new ClassName(mock1.Object, ...)`
   - Integration tests: build `ServiceCollection` with in-memory infrastructure
3. **SetUp Initialization:** Ensure each test file has proper setup (framework-specific: `[SetUp]` for NUnit, constructor for xUnit, `[TestInitialize]` for MSTest) that mocks/initializes all dependencies.
4. **Cold Start:** If no coverage exists for a file, identify the 3 most complex public methods (highest branching) and write baseline tests first.
5. **NuGet Restore:** If you created new test projects or added packages, the first `RunTestsWithCoverage` call must use `forceRestore=true`.

## Strict Safety & Edit Protocols

1. **PROD LOCK:** NEVER modify production source files.
2. **CLEAN APPEND:** Call `AppendTestCode` with `insertAfterAnchor` set to the closing `}` of the last test method to insert in the correct location. If no tests exist yet, insert after the setup method's closing `}`.
3. **RECOVERY:** If a test fails, use your editor to fix the specific failing method. If the same test fails 3 times, delete it entirely and move on.
4. **ITERATION CAP:** Keep iterating until both Line and Branch coverage reach 80%, or until 3 consecutive cycles show no improvement. STOP for human review at that point.

## The Execution Loop

### 1. Run Tests & Collect Coverage

Call `RunTestsWithCoverage`:
- `testProjectPath` → the `.csproj` from `TEST_PROJECT`
- `filter` → use a broad filter (e.g., `*` or the test namespace) to cover all files in the batch with a single test run
- `forceRestore` → `true` on the first run after scaffolding; omit otherwise

This returns both the `Summary.json` path and the `coverage.cobertura.xml` path. One test run covers the entire batch.

### 2. Check Per-File Coverage

For each file in the current batch, call `GetFileCoverage`:
- `coberturaXmlPath` → the XML path from step 1
- `sourceFileName` → the `.cs` filename (e.g., `ExampleService.cs`)

This is instant (XML parsing only, no test re-run). Check `allMeetTarget` — when `true`, that file has reached 80% line and branch coverage and can be marked done.

### 3. Read Full Summary (first run only)

Call `GetCoverageSummary` with the `Summary.json` path. Returns compact JSON — an array of classes with `lineCoverage`, `branchCoverage`, and `methods` sorted by branch coverage ascending. Use this to populate the **Before** columns of the progress table.

On subsequent runs, use `GetCoverageDiff` instead — it returns only what changed plus `cycleImprovement` deltas.

### 4. Find Uncovered Branches

Pick the 3 methods with the **lowest branch coverage** across all files in the batch (ignore methods already >= 80%).

For each, call `GetUncoveredBranches`:
- `coberturaXmlPath` → the XML path
- `methodName` → name of the method (partial match supported; returns all matches)

Returns uncovered branch conditions per line with `matchCount` showing how many methods matched.

### 5. Write Tests

- Use data-driven attributes (`[TestCase]` NUnit, `[InlineData]` xUnit, `[DataRow]` MSTest) for simple value variations
- Use separate test methods when different mock setups are required
- Call `AppendTestCode` to add new test methods with `insertAfterAnchor` for placement
- Max 1-3 assertions per test; name tests `MethodName_Condition_ExpectedResult`

### 6. Handle Build Errors

If `RunTestsWithCoverage` returns a build error after writing tests:
- Use your editor (Edit tool) to fix the syntax error — do NOT use `AppendTestCode` to fix existing code
- If the same test fails 3 times, delete it and move on

### 7. Re-run & Diff

Run tests once (step 1 again), then call `GetCoverageDiff` to verify improvement. Call `GetFileCoverage` for each file to update per-file status.

### 8. Advance or Repeat

- If all files in the batch have `allMeetTarget: true` → move to the next batch
- If 3 cycles pass with no improvement → mark stuck methods as blocked, move to next batch
- Otherwise → repeat from step 4

## Test Quality Rules

1. **NO BULK POCO TESTS:** Avoid tests that just set/get properties with no logic.
2. **BEHAVIORAL FOCUS:** Every test must target a specific logic branch, calculation, or external interaction.
3. **ATOMICITY:** Each test should have 1-3 assertions max.
4. **DATA VARIATION:** Use data-driven attributes for input variations instead of duplicating test methods.
5. **NAMING:** Use `MethodName_Condition_ExpectedResult` pattern.

## Exemption & Blocked Criteria

- **Exempt:** Static factories, legacy code with no interfaces, `private` methods.
- **Blocked:** 3 failed attempts (async deadlocks, sealed classes, static dependencies). Note root cause and move on.

## Comparative Progress Table

> Populate **Before** after the first `GetFileCoverage` run. Populate **After** after each subsequent run.

| File | Method | Line % (Before) | Line % (After) | Branch % (Before) | Branch % (After) | New Tests | Status | Notes |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| *(fill after first run)* | — | — | — | — | — | 0 | Pending | — |

## Termination

Target: Both Branch Coverage >= 80% and Line Coverage >= 80% for all files (excluding Blocked/Exempt). If coverage shows no improvement for 3 consecutive cycles, STOP and report.
