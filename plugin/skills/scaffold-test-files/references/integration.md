# Integration Test Rules — Clean DI with In-Memory Infrastructure

## Principle
Integration tests verify that real services work together through the DI container. Use in-memory replacements for infrastructure (database, message bus) but real implementations for application services.

**IMPORTANT:** Adapt lifecycle patterns to the detected test framework. Do NOT mix frameworks.

## DI Setup
- Build a real `ServiceCollection` matching the application's DI composition root
- Register real application services (the same `AddXxx()` extensions used in `Program.cs` or `Startup.cs`)
- Replace infrastructure with in-memory implementations:
  - **EF Core DbContext** → `UseInMemoryDatabase("TestDb_" + Guid.NewGuid())`
  - **HTTP clients** → mock `HttpMessageHandler` or use `WebApplicationFactory`
  - **External APIs** → mock the interface
- Build `ServiceProvider` and resolve the class under test

## In-Memory Database Pattern

### NUnit
```csharp
[TestFixture]
public class ExampleServiceIntegrationTests
{
    private ServiceProvider _serviceProvider;
    private ExampleService _sut;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddScoped<IExampleService, ExampleService>();
        services.AddScoped<IRepository, Repository>();
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase("TestDb_" + Guid.NewGuid()));

        var httpClientMock = new Mock<IExternalApiClient>();
        services.AddSingleton(httpClientMock.Object);

        _serviceProvider = services.BuildServiceProvider();
        _sut = _serviceProvider.GetRequiredService<ExampleService>();
    }

    [TearDown]
    public void TearDown() => _serviceProvider?.Dispose();
}
```

### xUnit
```csharp
public class ExampleServiceIntegrationTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly ExampleService _sut;

    public ExampleServiceIntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<IExampleService, ExampleService>();
        services.AddScoped<IRepository, Repository>();
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase("TestDb_" + Guid.NewGuid()));

        var httpClientMock = new Mock<IExternalApiClient>();
        services.AddSingleton(httpClientMock.Object);

        _serviceProvider = services.BuildServiceProvider();
        _sut = _serviceProvider.GetRequiredService<ExampleService>();
    }

    public void Dispose() => _serviceProvider?.Dispose();
}
```

### MSTest
```csharp
[TestClass]
public class ExampleServiceIntegrationTests
{
    private ServiceProvider _serviceProvider;
    private ExampleService _sut;

    [TestInitialize]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddScoped<IExampleService, ExampleService>();
        services.AddScoped<IRepository, Repository>();
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase("TestDb_" + Guid.NewGuid()));

        var httpClientMock = new Mock<IExternalApiClient>();
        services.AddSingleton(httpClientMock.Object);

        _serviceProvider = services.BuildServiceProvider();
        _sut = _serviceProvider.GetRequiredService<ExampleService>();
    }

    [TestCleanup]
    public void TearDown() => _serviceProvider?.Dispose();
}
```

## WebApplicationFactory Pattern (for API/controller-level tests)

### NUnit
```csharp
[TestFixture]
public class ExampleControllerIntegrationTests
{
    private WebApplicationFactory<Program> _factory;
    private HttpClient _client;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddDbContext<AppDbContext>(options =>
                        options.UseInMemoryDatabase("TestDb_" + Guid.NewGuid()));
                });
            });
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }
}
```

### xUnit
```csharp
public class ExampleControllerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ExampleControllerIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase("TestDb_" + Guid.NewGuid()));
            });
        }).CreateClient();
    }
}
```

## Rules
- Use unique database names per test (`Guid.NewGuid()`) to prevent test pollution
- Seed test data in setup or at the start of each test — never rely on shared state
- Only mock what is truly external (third-party APIs, email, SMS)
- Let real services, repositories, and validators run through the DI pipeline
- Dispose `ServiceProvider` in teardown/cleanup to prevent leaks
- Test names: `MethodName_Scenario_ExpectedOutcome`
- Assert on observable outcomes (return values, database state) not internal behavior
- If no EF Core / database dependency, just use `ServiceCollection` with real registrations
- Use the framework's lifecycle attributes — do NOT mix `[SetUp]` (NUnit) with `[TestInitialize]` (MSTest)
