# 🎯 Production Coverage Mission: ExampleFile.cs

## 📋 Step -1: Variable Resolution (Confirm Before Executing)

Resolve all variables before running any tool. Do not proceed until each value is confirmed.

| Variable | Value |
| :--- | :--- |
| `TARGET_FILE` | `src/YourProject/ExampleFile.cs` |
| `TEST_FILE` | `tests/YourProject.Tests/Unit/ExampleFileTests.cs` |

> ⚠️ If any path does not exist on disk, stop and ask the user to correct it.

## 🏗️ Step 0: Infrastructure & DI Setup
1. **Tooling Check:** Run `dotnet tool list -g`. If `reportgenerator` does not appear in the output, install it: `dotnet tool install -g dotnet-reportgenerator-globaltool`.
2. **Dependency Analysis:** Read the target file at the path specified in the `TARGET_FILE` row above. Start with its constructor.
3. **[SetUp] Initialization:** In the test file at the path specified in the `TEST_FILE` row above, ensure a `[SetUp]` method exists that mocks all constructor interfaces using **Moq**. Use your available file editing tool to create or modify `[SetUp]` — `AppendTestCode` is for adding new test methods only, not modifying existing structure.
4. **Cold Start:** If no coverage exists, identify the 3 most complex public methods (highest branching) and write baseline tests first.

## 🛡️ Strict Safety & Edit Protocols
1. **PROD LOCK:** NEVER modify the production file listed in the `TARGET_FILE` row above.
2. **CLEAN APPEND:** Call `AppendTestCode` with `insertAfterAnchor` set to the closing `}` of the last existing `[Test]` or `[TestCase]` method to insert new tests in the correct location. If no tests exist yet, insert after the `[SetUp]` method's closing `}`.
3. **RECOVERY:** If a test fails, **delete the specific failing method/attribute** and re-run to confirm the pass count is restored before trying a different approach.
4. **ITERATION CAP:** Keep iterating until both Line and Branch coverage reach 80%, or until coverage shows no improvement for **3 consecutive cycles** (whichever comes first). STOP for human review at that point.

## 🔄 The Execution Loop

### 1. Run Tests & Collect Coverage
DO NOT use raw `dotnet test` or `reportgenerator` terminal commands. Call the `RunTestsWithCoverage` MCP tool:
- `testProjectPath` → the `.csproj` path derived from the `TEST_FILE` row in Step -1 (the parent project folder containing the `.csproj`)
- `filter` → the filename of `TARGET_FILE` without the `.cs` extension, suffixed with `UnitTests` (e.g. `ExampleFile` → `ExampleFileUnitTests`). If the test class uses a different naming convention, open the `TEST_FILE` and find the `class` declaration to get the exact class name, then use that as the filter instead.
  - **Short names** (no dots, e.g. `ExampleFileUnitTests`) use partial match (`~`) — matches any test whose fully qualified name contains the string.
  - **Fully qualified names** (contains dots, e.g. `MyProject.Tests.ExampleFileUnitTests`) use exact match (`=`) — runs only that specific class.

This tool skips NuGet restore (`--no-restore`) for speed and enforces a 30-second hang timeout to prevent stuck tests. It returns both the `Summary.json` path and the `coverage.cobertura.xml` path.

### 2. Read Coverage Summary
**First run only:** Call `GetCoverageSummary` with the `Summary.json` path returned in step 1. Returns compact JSON — an array of classes, each with `lineCoverage`, `branchCoverage`, and a `methods` list sorted by branch coverage ascending (lowest first). Use this to populate the **Before** columns of the progress table.
 
**Subsequent runs:** Call `GetCoverageDiff` with the `coverage.cobertura.xml` path instead. Pass `workingDir` if the `.coverage-prev.xml` baseline should be stored somewhere other than the XML's parent directory. Returns only methods where coverage changed plus aggregate `cycleImprovement` deltas. Use `GetCoverageSummary` only if you need the full picture again.

### 3. Find Uncovered Branches
Call `GetUncoveredBranches` with:
- `coberturaXmlPath` → use the `coverage.cobertura.xml` path returned by Step 1. If the file is not found at the given path, the tool automatically falls back to the path stored in `.coverage-state` (written by `RunTestsWithCoverage`).
- `methodName` → name of the method to inspect

Returns compact JSON with the method name and a list of uncovered branch conditions per line. Use this to identify exactly which conditions are uncovered before writing tests.

### 4. Prioritize
Focus on the 3 methods with the **absolute lowest coverage** (ignore methods already >= 80%).

### 5. Write Tests
- Use `[TestCase]` for simple value variations on the same logic path.
- Use unique `[Test]` methods when different `Mock.Setup` configurations are required.
- Call `AppendTestCode` to add new test methods. Use `insertAfterAnchor` to place them after the last existing test rather than at the end of the file.

### 6. Self-Healing
Fix a failure **once**. If it fails again, mark as `🛠️ Blocked` with a root-cause note and move to the next method.

## 💎 Test Quality Rules (Anti-Padding)
1. **NO BULK POCO TESTS:** Strictly avoid tests that simply set/get 20+ properties in a row.
2. **BEHAVIORAL FOCUS:** Every test must target a specific logic branch, calculation, or external interaction (e.g., `Should_Calculate_TotalValue_When_Components_Are_Provided`).
3. **ATOMICITY:** Each test should ideally have 1-3 assertions max.
4. **DATA VARIATION:** Use `[TestCase]` to test different data inputs for the same method instead of writing multiple identical `[Test]` methods.

## ⚠️ Exemption & Blocked Criteria
- **Exempt:** Static factories, legacy code with no interfaces, or `private` methods.
- **Blocked:** AI attempted 3 times and hit a logic wall (e.g., async deadlocks, sealed classes, static dependencies).

## 📊 Comparative Progress Table
> Populate the **Before** column after the first `RunTestsWithCoverage` run. Populate **After** after each subsequent run.

| Method | Line % (Before) | Line % (After) | Branch % (Before) | Branch % (After) | New Tests | Status | Notes |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| *(fill after first run)* | — | — | — | — | 0 | 🔄 Pending | — |

## 🏁 Termination
Target: Both Branch Coverage >= 80% && Line Coverage >= 80% (excluding Blocked/Exempt). If coverage shows no improvement for 3 consecutive cycles, STOP and report — further iteration is unlikely to help.
