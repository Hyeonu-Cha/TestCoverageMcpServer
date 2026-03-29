# Unit Test Rules — Clean DI

## Principle
Unit tests isolate the class under test by mocking **all** injected dependencies. No real I/O, no database, no HTTP calls.

## DI Setup
- Create mocks for every interface in the constructor
- Instantiate the class under test manually with mocked dependencies
- Do NOT use `ServiceCollection` or DI container — direct `new ClassName(mock1, mock2, ...)`

## Mock Behavior
- Set up mock returns **per test**, not globally, to keep each test self-contained
- Verify mock interactions only when the interaction IS the behavior being tested
- Avoid over-verification — don't assert that every mock was called

## Test Structure (NUnit)
```csharp
[TestFixture]
public class ExampleServiceTests
{
    private Mock<IRepository> _repositoryMock;
    private Mock<ILogger<ExampleService>> _loggerMock;
    private ExampleService _sut;

    [SetUp]
    public void SetUp()
    {
        _repositoryMock = new Mock<IRepository>();
        _loggerMock = new Mock<ILogger<ExampleService>>();
        _sut = new ExampleService(_repositoryMock.Object, _loggerMock.Object);
    }
}
```

## Rules
- One class per test file
- Test name: `MethodName_Condition_ExpectedResult`
- Max 1–3 assertions per test
- Use `[TestCase]` for data-driven variations
- Separate `[Test]` methods when mock setups differ
- No real file system, network, or database access
- No `Thread.Sleep` or time-dependent tests — inject `ITimeProvider` if needed
