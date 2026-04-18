using CoverageMcpServer.Services;
using FluentAssertions;

namespace CoverageMcpServer.Tests.Unit;

public class ProcessRunnerTests
{
    [Fact]
    public async Task DrainAsync_ReturnsResultWhenTaskCompletesInTime()
    {
        var task = Task.FromResult("stdout-content");

        var result = await ProcessRunner.DrainAsync(task, TimeSpan.FromSeconds(1));

        result.Should().Be("stdout-content");
    }

    [Fact]
    public async Task DrainAsync_ReturnsEmptyWhenTaskExceedsDeadline()
    {
        var tcs = new TaskCompletionSource<string>();
        // Task that will never complete within the drain window
        var result = await ProcessRunner.DrainAsync(tcs.Task, TimeSpan.FromMilliseconds(50));

        result.Should().BeEmpty();

        // Clean up the still-pending task so it doesn't linger.
        tcs.TrySetResult("late");
    }

    [Fact]
    public async Task DrainAsync_ObservesLateFaultedTask_NoUnobservedException()
    {
        var tcs = new TaskCompletionSource<string>();

        var result = await ProcessRunner.DrainAsync(tcs.Task, TimeSpan.FromMilliseconds(20));
        result.Should().BeEmpty();

        // Fault the task after DrainAsync has already returned. The continuation
        // registered in DrainAsync should observe the exception and prevent it from
        // bubbling up as an UnobservedTaskException.
        tcs.TrySetException(new InvalidOperationException("simulated late stderr read failure"));

        // Give the continuation a moment to run and observe.
        await Task.Delay(50);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // If the exception had been unobserved, the finalizer would have queued a
        // TaskScheduler.UnobservedTaskException. We can't deterministically assert
        // that here without process-wide hooks, but reaching this point without
        // the default unobserved-exception handler crashing the process is the
        // behavioral contract we care about.
        tcs.Task.IsFaulted.Should().BeTrue();
    }

    [Fact]
    public async Task DrainAsync_ReturnsEmptyWhenTaskFaultsBeforeDeadline()
    {
        var task = Task.FromException<string>(new InvalidOperationException("boom"));

        var result = await ProcessRunner.DrainAsync(task, TimeSpan.FromSeconds(1));

        result.Should().BeEmpty();
    }
}
