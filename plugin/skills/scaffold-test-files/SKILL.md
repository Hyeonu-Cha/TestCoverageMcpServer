---
name: scaffold-test-files
description: "Automatically create matching test directory structure and test files for a given .NET source file. USE FOR: when a user selects a source file and wants unit and/or integration test files created, mirroring the source project's folder structure under the test project. Detects existing test project layout (separate or unified), creates directories and boilerplate test files following clean DI patterns read from the actual project. DO NOT USE FOR: writing test logic (use improve-test-coverage), running tests (use run-coverage)."
---

# Scaffold Test Files

Given a .NET source file, automatically create matching unit test and integration test directories and files under the test project, following the project's existing conventions and clean DI patterns.

## When to Use
- User selects a source file and wants test files created for it
- User asks to "set up tests for this file" or "create test structure for X"
- Starting a new coverage session where no test file exists yet
- Adding integration test support alongside existing unit tests

## When Not to Use
- Test files already exist for the source file — use `improve-test-coverage` instead
- User only wants to run existing tests — use `run-coverage`
- User wants to inspect coverage gaps — use `analyze-coverage-gaps`
- Non-.NET project

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| `sourceFilePath` | Yes | Full path to the `.cs` source file to create tests for |
| `testTypes` | No | Comma-separated list: `unit`, `integration`, or both (default: both) |

## Workflow

### Step 1: Analyze source file
Read the source file to extract:
- **Namespace** (e.g., `MainProject.Subproject.Services`)
- **Class name** (e.g., `ExampleService`)
- **Constructor parameters** — all injected interfaces (e.g., `IRepository`, `ILogger<T>`)
- **Public methods** — names and signatures to create test stubs for

### Step 2: Detect test project structure
Scan the solution for test projects:
```
glob **/*Test*.csproj
glob **/*Tests*.csproj
```

Determine the layout:
- **Separate projects**: `UnitTests.csproj` and `IntegrationTests.csproj` exist separately → use each
- **Single project**: One test `.csproj` with folders like `Unit/` and `Integration/` inside → use subfolders
- **No test project**: Inform the user they need to create one first

### Step 3: Mirror directory structure
Map the source path to test paths by mirroring the subfolder structure:

**Source:** `MainProject/Subproject/Services/ExampleService.cs`

**Separate projects:**
- `TestProject.UnitTests/Subproject/Services/ExampleServiceTests.cs`
- `TestProject.IntegrationTests/Subproject/Services/ExampleServiceIntegrationTests.cs`

**Single project:**
- `TestProject/UnitTests/Subproject/Services/ExampleServiceTests.cs`
- `TestProject/IntegrationTests/Subproject/Services/ExampleServiceIntegrationTests.cs`

Create any missing directories.

### Step 4: Read project conventions
Before generating boilerplate, read 2–3 existing test files in the test project to learn:
- **Using statements** — which namespaces are imported
- **Mock framework** — Moq (`Mock<T>`), NSubstitute (`Substitute.For<T>`), or FakeItEasy
- **Base class** — whether tests inherit from a base test class
- **Attribute style** — `[TestFixture]`, `[SetUp]`, `[Test]` (NUnit)
- **Naming convention** — `MethodName_Condition_Result` or other patterns
- **DI setup patterns** — how `ServiceCollection` or `WebApplicationFactory` is configured

If no existing test files, use NUnit defaults with clean DI patterns.

### Step 5: Read context-specific rules
Based on test type being generated:
- **Unit tests** → read `references/unit.md` for rules
- **Integration tests** → read `references/integration.md` for rules

Apply these rules when generating the boilerplate.

### Step 6: Generate unit test file
Create the unit test file with boilerplate matching the project's conventions:
- Namespace mirroring source namespace
- `[TestFixture]` class
- Constructor or `[SetUp]` creating mocks for all injected interfaces
- Instantiating the class under test with mocked dependencies
- Empty test method stubs for each public method (no assertions — those come from `improve-test-coverage`)

### Step 7: Generate integration test file
Create the integration test file with clean DI + in-memory infrastructure:
- Namespace mirroring source namespace
- `[TestFixture]` class
- `[SetUp]` building a real `ServiceCollection` with:
  - In-memory database (`UseInMemoryDatabase`) if EF Core `DbContext` is used
  - Real service registrations from the project's DI composition root
  - Only external services mocked (HTTP clients, third-party APIs)
- `ServiceProvider` built and class under test resolved from DI
- Empty test method stubs for each public method

### Step 8: Restore NuGet packages
**CRITICAL:** If you created a new test project or added NuGet packages (Moq, NUnit, Microsoft.NET.Test.Sdk, etc.), you MUST run `dotnet restore` before any test execution. When calling `RunTestsWithCoverage` after scaffolding, set `forceRestore=true` on the first run.

### Step 9: Update target .gitignore
If the target repository's `.gitignore` does not already contain `.mcp-coverage/`, add it. This directory holds session-scoped coverage state files generated by the MCP server.

### Step 10: Report created files
List all created directories and files with their paths.

## Validation
- [ ] Source file was read and class/constructor/methods extracted
- [ ] Test project structure detected correctly (separate vs unified)
- [ ] Directory structure mirrors the source path
- [ ] Unit test file follows project's existing mock framework and conventions
- [ ] Integration test file uses in-memory infrastructure with real DI
- [ ] No hardcoded templates — boilerplate matches what's already in the project
- [ ] Created files compile (no missing usings or references)

## Common Pitfalls

| Pitfall | Solution |
|---------|----------|
| No test project found | Inform user to create a test `.csproj` first |
| Mixed conventions in existing tests | Follow the most common pattern (majority wins) |
| Source class has no constructor injection | Still create the test file; note it may use static methods or `new` directly |
| EF Core not used but integration test requested | Use `ServiceCollection` without in-memory DB; register real services with mocked external dependencies |
| Namespace mismatch | Always derive test namespace from the test project root namespace + mirrored subfolder path |
