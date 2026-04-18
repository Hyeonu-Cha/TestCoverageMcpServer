using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CoverageMcpServer.Services;

public record TestRunResult(bool Success, string Output, string Error, int ExitCode, string? CoverageXmlPath);
public record ReportResult(bool Success, string? SummaryPath, string? ErrorDetail);

public interface IProcessRunner
{
    Task<TestRunResult> RunDotnetTestAsync(
        string testProjectPath, string filter, string resultsDir,
        string workingDir, bool forceRestore, string? includeClass, CancellationToken ct);

    Task<ReportResult> RunReportGeneratorAsync(
        string xmlPath, string reportDir, string workingDir, CancellationToken ct = default);
}

public class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;
    private const int ReportGeneratorTimeoutMs = 60_000;
    private static readonly int DotnetTestTimeoutMs = ReadDotnetTestTimeoutMs();
    // Bounded wait for stdout/stderr to drain after the child process has been killed.
    // Long enough for the OS to flush the pipe buffer, short enough not to extend a timeout.
    private static readonly TimeSpan PostKillDrainWait = TimeSpan.FromSeconds(1);

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        _logger = logger;
    }

    public async Task<TestRunResult> RunDotnetTestAsync(
        string testProjectPath, string filter, string resultsDir,
        string workingDir, bool forceRestore, string? includeClass, CancellationToken ct)
    {
        var filterOp = filter.Contains('.') ? "=" : "~";

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        // Use ArgumentList instead of string concatenation to prevent injection
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add(testProjectPath);
        if (!forceRestore) psi.ArgumentList.Add("--no-restore");
        psi.ArgumentList.Add("--blame-hang-timeout");
        psi.ArgumentList.Add("30s");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add($"FullyQualifiedName{filterOp}{filter}");
        psi.ArgumentList.Add("--collect:XPlat Code Coverage");
        psi.ArgumentList.Add("--results-directory");
        psi.ArgumentList.Add(resultsDir);

        // Only scope coverage when caller explicitly opts in. Previously we inferred a
        // class name from the filter, which silently mis-scoped namespace filters.
        if (!string.IsNullOrWhiteSpace(includeClass))
            psi.ArgumentList.Add($"/p:Include=[*]*{includeClass}");

        _logger.LogInformation("Starting dotnet test for {Project} with filter {Filter}", testProjectPath, filter);
        using var process = Process.Start(psi) ?? throw new Exception("Failed to start dotnet test");

        // Combine caller's token with an outer test timeout. Without this, a hung restore
        // or a locked NuGet cache would hang the MCP call until the client cancels.
        using var timeoutCts = new CancellationTokenSource(DotnetTestTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        // Register cancellation to kill the process immediately, before
        // awaiting stdout/stderr — otherwise ReadToEndAsync blocks until
        // the process exits and the cancellation token is never checked.
        using var ctReg = linkedCts.Token.Register(() =>
        {
            _logger.LogWarning("Test run cancelled/timed out, killing process tree for {Project}", testProjectPath);
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync(linkedCts.Token);

        try
        {
            await exitTask;
        }
        catch (OperationCanceledException)
        {
            // Process already killed by ctReg callback — drain remaining output with a
            // bounded wait so we surface useful stderr without hanging on a stuck pipe.
            // Callers compare Error against the string "cancelled"/"timeout", so keep that
            // signal stable and fold any captured stderr into Output for diagnostics.
            var partialOut = await DrainAsync(outTask);
            var partialErr = await DrainAsync(errTask);
            var combined = string.IsNullOrEmpty(partialErr)
                ? partialOut
                : partialOut + "\n--- stderr ---\n" + partialErr;
            var reason = ct.IsCancellationRequested ? "cancelled" : "timeout";
            return new TestRunResult(false, combined, reason, -1, null);
        }

        var output = await outTask;
        var error = await errTask;

        _logger.LogInformation("dotnet test exited with code {ExitCode} for {Project}", process.ExitCode, testProjectPath);

        string? coverageXmlPath = null;
        if (process.ExitCode == 0 && Directory.Exists(resultsDir))
        {
            var xmlPaths = Directory.GetFiles(resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories);
            if (xmlPaths.Length > 0)
                coverageXmlPath = xmlPaths[0];
        }

        return new TestRunResult(process.ExitCode == 0, output, error, process.ExitCode, coverageXmlPath);
    }

    private static int ReadDotnetTestTimeoutMs()
    {
        const int defaultMs = 600_000; // 10 minutes
        var raw = Environment.GetEnvironmentVariable("COVERAGE_MCP_DOTNET_TEST_TIMEOUT_MS");
        return int.TryParse(raw, out var v) && v > 0 ? v : defaultMs;
    }

    // Wait for a read task to complete after the process has been killed, up to the
    // bounded PostKillDrainWait. If it doesn't finish, observe its eventual exception
    // so it never surfaces as an UnobservedTaskException and return an empty string.
    internal static async Task<string> DrainAsync(Task<string> task) =>
        await DrainAsync(task, PostKillDrainWait);

    internal static async Task<string> DrainAsync(Task<string> task, TimeSpan wait)
    {
        try
        {
            return await task.WaitAsync(wait);
        }
        catch (TimeoutException)
        {
            _ = task.ContinueWith(
                t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return "";
        }
        catch
        {
            return "";
        }
    }

    public async Task<ReportResult> RunReportGeneratorAsync(
        string xmlPath, string reportDir, string workingDir, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "reportgenerator",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.ArgumentList.Add($"-reports:{xmlPath}");
        psi.ArgumentList.Add($"-targetdir:{reportDir}");
        psi.ArgumentList.Add("-reporttypes:JsonSummary");

        _logger.LogInformation("Starting reportgenerator for {XmlPath}", xmlPath);
        using var process = Process.Start(psi) ?? throw new Exception("Failed to start reportgenerator");
        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();

        // Combine caller's token with our timeout
        using var timeoutCts = new CancellationTokenSource(ReportGeneratorTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var ctReg = linkedCts.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ReportResult(false, null, "Report generation was cancelled.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("reportgenerator timed out after {Timeout}s, killing process", ReportGeneratorTimeoutMs / 1000);
            return new ReportResult(false, null, $"reportgenerator timed out after {ReportGeneratorTimeoutMs / 1000}s");
        }

        await outTask;
        await errTask;

        var summaryPath = Path.Combine(reportDir, "Summary.json");
        return File.Exists(summaryPath)
            ? new ReportResult(true, summaryPath, null)
            : new ReportResult(false, null, "Report generation failed");
    }
}
