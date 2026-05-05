using DotNetCoverageMcp.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace DotNetCoverageMcp.Tests.Unit;

public class FileServiceTests : IDisposable
{
    private readonly FileService _sut;
    private readonly string _tempDir;

    public FileServiceTests()
    {
        var logger = new Mock<ILogger<FileService>>();
        _sut = new FileService(logger.Object);
        _tempDir = Path.Combine(Path.GetTempPath(), $"fst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // --- AtomicWriteFile ---

    [Fact]
    public void AtomicWriteFile_WritesContentCorrectly()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        _sut.AtomicWriteFile(path, "hello world");

        File.ReadAllText(path).Should().Be("hello world");
    }

    [Fact]
    public void AtomicWriteFile_OverwritesExistingFile()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(path, "old content");

        _sut.AtomicWriteFile(path, "new content");

        File.ReadAllText(path).Should().Be("new content");
    }

    [Fact]
    public void AtomicWriteFile_NoTempFileLeftBehind()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        _sut.AtomicWriteFile(path, "content");

        Directory.GetFiles(_tempDir, ".tmp-*").Should().BeEmpty();
    }

    // --- SafeDelete ---

    [Fact]
    public void SafeDelete_RemovesExistingDirectory()
    {
        var dir = Path.Combine(_tempDir, "to-delete");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "file.txt"), "data");

        _sut.SafeDelete(dir);

        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public void SafeDelete_NoOpForMissingDirectory()
    {
        var dir = Path.Combine(_tempDir, "nonexistent");

        var act = () => _sut.SafeDelete(dir);

        act.Should().NotThrow();
    }

    // --- WithFileLockAsync ---

    [Fact]
    public async Task WithFileLockAsync_SerializesConcurrentAccess()
    {
        var path = Path.Combine(_tempDir, "locked.txt");
        var counter = 0;
        var maxConcurrent = 0;
        var current = 0;

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            _sut.WithFileLockAsync(path, async () =>
            {
                var c = Interlocked.Increment(ref current);
                if (c > 1) Interlocked.Exchange(ref maxConcurrent, c);
                await Task.Delay(10);
                Interlocked.Increment(ref counter);
                Interlocked.Decrement(ref current);
            })).ToArray();

        await Task.WhenAll(tasks);

        counter.Should().Be(10);
        maxConcurrent.Should().Be(0, "no concurrent execution should occur on the same path");
    }

    [Fact]
    public async Task WithFileLockAsync_AllowsParallelOnDifferentPaths()
    {
        var path1 = Path.Combine(_tempDir, "a.txt");
        var path2 = Path.Combine(_tempDir, "b.txt");
        var t1Entered = new TaskCompletionSource();
        var t2Entered = new TaskCompletionSource();

        var t1 = _sut.WithFileLockAsync(path1, async () =>
        {
            t1Entered.SetResult();
            await t2Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });

        await t1Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var t2 = _sut.WithFileLockAsync(path2, async () =>
        {
            t2Entered.SetResult();
            await Task.CompletedTask;
        });

        await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(10));
    }

    // --- GetFileMetadata ---

    [Fact]
    public void GetFileMetadata_ReturnsLineAndMethodCount()
    {
        var path = Path.Combine(_tempDir, "Sample.cs");
        File.WriteAllText(path, @"
public class Foo
{
    public void Method1() { }
    public void Method2() { }
    private void Secret() { }
}
");
        var (lines, methods) = _sut.GetFileMetadata(path);

        lines.Should().BeGreaterThan(1);
        methods.Should().Be(2);
    }

    [Fact]
    public void GetFileMetadata_CountsOnlyPublicMethods()
    {
        var path = Path.Combine(_tempDir, "Internal.cs");
        File.WriteAllText(path, @"
public class Bar
{
    internal void A() { }
    protected void B() { }
    private void C() { }
    public void D() { }
}
");
        var (_, methods) = _sut.GetFileMetadata(path);
        methods.Should().Be(1);
    }

    [Fact]
    public void GetFileMetadata_ReturnsDefaultsOnError()
    {
        var (lines, methods) = _sut.GetFileMetadata(Path.Combine(_tempDir, "nonexistent.cs"));

        lines.Should().Be(1);
        methods.Should().Be(0);
    }

    // --- IsExcludedPath ---

    [Theory]
    [InlineData("/src/obj/Debug/file.cs", true)]
    [InlineData("/src/bin/Release/file.cs", true)]
    [InlineData("/src/Migrations/001.cs", true)]
    [InlineData("/src/.mcp-coverage/state", true)]
    [InlineData("/src/TestResults-abc/file.xml", true)]
    [InlineData("/src/coveragereport-abc/index.html", true)]
    [InlineData("/src/Models/User.cs", false)]
    [InlineData("C:\\src\\OBJ\\file.cs", true)]
    [InlineData("C:\\src\\BIN\\file.cs", true)]
    public void IsExcludedPath_ClassifiesCorrectly(string path, bool expected)
    {
        _sut.IsExcludedPath(path).Should().Be(expected);
    }
}
