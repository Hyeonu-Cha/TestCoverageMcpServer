# Integration Test Rules — Clean DI with In-Memory Infrastructure

## Principle
Integration tests verify that real services work together through the DI container. Use in-memory replacements for infrastructure (database, message bus) but real implementations for application services.

## DI Setup
- Build a real `ServiceCollection` matching the application's DI composition root
- Register real application services (the same `AddXxx()` extensions used in `Program.cs` or `Startup.cs`)
- Replace infrastructure with in-memory implementations:
  - **EF Core DbContext** → `UseInMemoryDatabase("TestDb_" + Guid.NewGuid())`
  - **HTTP clients** → mock `HttpMessageHandler` or use `WebApplicationFactory`
  - **External APIs** → mock the interface
- Build `ServiceProvider` and resolve the class under test

## In-Memory Database Pattern
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

        // Register real application services
        services.AddScoped<IExampleService, ExampleService>();
        services.AddScoped<IRepository, Repository>();

        // Replace DbContext with in-memory
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase("TestDb_" + Guid.NewGuid()));

        // Mock only external dependencies
        var httpClientMock = new Mock<IExternalApiClient>();
        services.AddSingleton(httpClientMock.Object);

        _serviceProvider = services.BuildServiceProvider();
        _sut = _serviceProvider.GetRequiredService<ExampleService>();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
    }
}
```

## WebApplicationFactory Pattern (for API/controller-level tests)
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
                    // Remove real DbContext and add in-memory
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

## Rules
- Use unique database names per test (`Guid.NewGuid()`) to prevent test pollution
- Seed test data in `[SetUp]` or at the start of each test — never rely on shared state
- Only mock what is truly external (third-party APIs, email, SMS)
- Let real services, repositories, and validators run through the DI pipeline
- Dispose `ServiceProvider` in `[TearDown]` to clean up
- Test names: `MethodName_Scenario_ExpectedOutcome`
- Assert on observable outcomes (return values, database state) not internal behavior
- If no EF Core / database dependency, just use `ServiceCollection` with real registrations
