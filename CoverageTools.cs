using System.Collections.Concurrent;
using System.ComponentModel;
using ModelContextProtocol.Server;
using CoverageMcpServer.Helpers;
using CoverageMcpServer.Services;

[McpServerToolType]
public class CoverageTools
{
    private readonly IProcessRunner _processRunner;
    private readonly ISessionManager _sessionManager;
    private readonly IFileService _fileService;
    private readonly ICoberturaService _coberturaService;
    private readonly ICodeInserter _codeInserter;
    private readonly IPathGuard _pathGuard;

    public CoverageTools(
        IProcessRunner processRunner,
        ISessionManager sessionManager,
        IFileService fileService,
        ICoberturaService coberturaService,
        ICodeInserter codeInserter,
        IPathGuard pathGuard)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(fileService);
        ArgumentNullException.ThrowIfNull(coberturaService);
        ArgumentNullException.ThrowIfNull(codeInserter);
        ArgumentNullException.ThrowIfNull(pathGuard);

        _processRunner = processRunner;
        _sessionManager = sessionManager;
        _fileService = fileService;
        _coberturaService = coberturaService;
        _codeInserter = codeInserter;
        _pathGuard = pathGuard;
    }

    // --- 1. RunTestsWithCoverage ---

    [McpServerTool]
    [Description("Run `dotnet test` with XPlat Code Coverage and generate a JSON summary. Returns paths to Summary.json and Cobertura XML.")]
    public async Task<string> RunTestsWithCoverage(
        string testProjectPath,
        string filter,
        string? workingDir = null,
        bool forceRestore = false,
        string? sessionId = null,
        string? includeClass = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(testProjectPath))
            return JsonHelper.Error("invalidParameter", "testProjectPath must not be empty.");
        if (string.IsNullOrWhiteSpace(filter))
            return JsonHelper.Error("invalidParameter", "filter must not be empty.");

        try
        {
            _pathGuard.Validate(testProjectPath, nameof(testProjectPath));
            if (workingDir != null) _pathGuard.Validate(workingDir, nameof(workingDir));
        }
        catch (UnauthorizedAccessException ex)
        {
            return JsonHelper.Error("pathNotAllowed", ex.Message);
        }

        workingDir ??= Path.GetDirectoryName(testProjectPath) ?? Directory.GetCurrentDirectory();
        var suffix = _sessionManager.ComputeSuffix(sessionId);
        var resultsDir = Path.Combine(workingDir, $"TestResults{suffix}");
        var reportDir = Path.Combine(workingDir, $"coveragereport{suffix}");

        _fileService.SafeDelete(resultsDir);
        _fileService.SafeDelete(reportDir);

        var testResult = await _processRunner.RunDotnetTestAsync(
            testProjectPath, filter, resultsDir, workingDir, forceRestore, includeClass, cancellationToken);

        if (testResult.Error == "cancelled")
            return JsonHelper.Error("cancelled", "Test run was cancelled by the client.");

        if (testResult.Error == "timeout")
            return JsonHelper.Error("timeout", $"Test run timed out. Output: {testResult.Output}");

        if (!testResult.Success)
            return JsonHelper.Error("buildError", $"Test run failed (code {testResult.ExitCode}). Error: {testResult.Error}\nOutput: {testResult.Output}");

        if (testResult.CoverageXmlPath == null)
            return JsonHelper.Error("noCoverage", "No coverage XML found.\n" + testResult.Output);

        _sessionManager.SaveCoverageState(workingDir, testResult.CoverageXmlPath, filter, sessionId);

        ReportResult reportResult;
        try
        {
            reportResult = await _processRunner.RunReportGeneratorAsync(
                testResult.CoverageXmlPath, reportDir, workingDir, cancellationToken);
        }
        catch (Exception ex)
        {
            return JsonHelper.Error("reportFailed",
                $"Could not launch reportgenerator: {ex.Message}\nCobertura XML is still available at: {testResult.CoverageXmlPath}");
        }

        if (!reportResult.Success)
        {
            return reportResult.ErrorDetail?.Contains("timed out") == true
                ? JsonHelper.Error("timeout", $"{reportResult.ErrorDetail}. Cobertura XML is still available at: {testResult.CoverageXmlPath}")
                : JsonHelper.Error("reportFailed", $"{reportResult.ErrorDetail}\n{testResult.Output}");
        }

        return $"Tests completed.\nCoverage JSON at: {reportResult.SummaryPath}\nCobertura XML at: {testResult.CoverageXmlPath}\nOutput: {testResult.Output}";
    }

    // --- 2. GetCoverageSummary ---

    [McpServerTool]
    [Description("Parse Summary.json into class/method coverage data, sorted by branch coverage ascending.")]
    public string GetCoverageSummary(string summaryJsonPath)
    {
        try { _pathGuard.Validate(summaryJsonPath, nameof(summaryJsonPath)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        if (!File.Exists(summaryJsonPath))
            return JsonHelper.Error("fileNotFound", $"Summary.json not found: {summaryJsonPath}");

        try
        {
            var result = _coberturaService.ParseSummary(summaryJsonPath);
            return JsonHelper.Serialize(result);
        }
        catch (InvalidOperationException ex)
        {
            return JsonHelper.Error("parseFailed", ex.Message);
        }
        catch (Exception ex)
        {
            return JsonHelper.Error("parseFailed", $"Failed to parse Summary.json: {ex.Message}");
        }
    }

    // --- 3. GetUncoveredBranches ---

    [McpServerTool]
    [Description("List uncovered branches for methods matching `methodName` in Cobertura XML (partial match supported).")]
    public string GetUncoveredBranches(string coberturaXmlPath, string methodName, string? sessionId = null)
    {
        try { _pathGuard.Validate(coberturaXmlPath, nameof(coberturaXmlPath)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        var resolvedPath = _sessionManager.ResolveCoberturaPath(coberturaXmlPath, sessionId);
        if (resolvedPath == null)
            return JsonHelper.Error("fileNotFound", "Cobertura XML not found and no .coverage-state fallback available.");

        try
        {
            var result = _coberturaService.GetUncoveredBranches(resolvedPath, methodName);
            return JsonHelper.Serialize(result);
        }
        catch (KeyNotFoundException ex)
        {
            return JsonHelper.Error("noMatch", ex.Message);
        }
        catch (Exception ex)
        {
            return JsonHelper.Error("parseFailed", $"Failed to parse Cobertura XML: {ex.Message}");
        }
    }

    // --- 4. AppendTestCode ---

    [McpServerTool]
    [Description("Insert C# code into a test file via Roslyn AST (string fallback). Pass `insertAfterAnchor` to target a position; omit to append inside the last class.")]
    public async Task<string> AppendTestCode(string testFilePath, string codeToAppend, string? insertAfterAnchor = null)
    {
        try { _pathGuard.Validate(testFilePath, nameof(testFilePath)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        if (!File.Exists(testFilePath))
            return JsonHelper.Error("fileNotFound", $"Test file not found: {testFilePath}");

        try
        {
            var result = await _codeInserter.InsertCodeAsync(testFilePath, codeToAppend, insertAfterAnchor);
            var methodLabel = result.Method switch
            {
                InsertionMethod.RoslynAst => "Roslyn AST",
                InsertionMethod.StringFallback => "string fallback",
                InsertionMethod.StringFallbackNormalized => "string fallback, whitespace-normalized",
                InsertionMethod.Appended => "string fallback, inside last class",
                _ => "unknown"
            };
            return $"Successfully inserted via {methodLabel} in {testFilePath}.\nNew file length: {result.Content.Length} chars";
        }
        catch (KeyNotFoundException ex)
        {
            return JsonHelper.Error("anchorNotFound", ex.Message);
        }
    }

    // --- 5. GetCoverageDiff ---

    [McpServerTool]
    [Description("Diff current coverage against the saved baseline (method-level changes, including added/removed). Updates the baseline afterwards.")]
    public string GetCoverageDiff(string coberturaXmlPath, string? workingDir = null, string? sessionId = null)
    {
        try
        {
            _pathGuard.Validate(coberturaXmlPath, nameof(coberturaXmlPath));
            if (workingDir != null) _pathGuard.Validate(workingDir, nameof(workingDir));
        }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        var resolvedPath = _sessionManager.ResolveCoberturaPath(coberturaXmlPath, sessionId);
        if (resolvedPath == null)
            return JsonHelper.Error("fileNotFound", "Cobertura XML not found and no .coverage-state fallback available.");

        if (workingDir == null)
        {
            workingDir = ResolveProjectRoot(resolvedPath);
            if (workingDir == null)
                return JsonHelper.Error("resolveFailed",
                    "Could not resolve project root from coverage XML path. Pass workingDir explicitly.");
        }

        try { _pathGuard.Validate(workingDir, nameof(workingDir)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        var stateDir = Path.Combine(workingDir, ".mcp-coverage");
        Directory.CreateDirectory(stateDir);
        var suffix = _sessionManager.ComputeSuffix(sessionId);
        var prevPath = Path.Combine(stateDir, $".coverage-prev{suffix}.xml");

        try
        {
            if (!File.Exists(prevPath))
            {
                _fileService.AtomicWriteFile(prevPath, File.ReadAllText(resolvedPath));
                return JsonHelper.Serialize(new { firstRun = true });
            }

            var result = _coberturaService.ComputeDiff(resolvedPath, prevPath);
            _fileService.AtomicWriteFile(prevPath, File.ReadAllText(resolvedPath));
            return JsonHelper.Serialize(result);
        }
        catch (Exception ex)
        {
            return JsonHelper.Error("parseFailed", $"Failed to parse coverage XML: {ex.Message}");
        }
    }

    // --- 6. GetFileCoverage ---

    [McpServerTool]
    [Description("Per-file class/method coverage from Cobertura XML. `allMeetTarget` is true when every class meets `targetRate` for line and branch.")]
    public string GetFileCoverage(string coberturaXmlPath, string sourceFileName, string? sessionId = null, double targetRate = 0.8)
    {
        try { _pathGuard.Validate(coberturaXmlPath, nameof(coberturaXmlPath)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        if (targetRate < 0.0 || targetRate > 1.0)
            return JsonHelper.Error("invalidParameter", "targetRate must be between 0.0 and 1.0.");

        var resolvedPath = _sessionManager.ResolveCoberturaPath(coberturaXmlPath, sessionId);
        if (resolvedPath == null)
            return JsonHelper.Error("fileNotFound", "Cobertura XML not found and no .coverage-state fallback available.");

        try
        {
            var result = _coberturaService.GetFileCoverage(resolvedPath, sourceFileName, targetRate);
            return JsonHelper.Serialize(result);
        }
        catch (KeyNotFoundException ex)
        {
            return JsonHelper.Error("noMatch", ex.Message);
        }
        catch (Exception ex)
        {
            return JsonHelper.Error("parseFailed", $"Failed to parse Cobertura XML: {ex.Message}");
        }
    }

    // --- 7. GetSourceFiles ---

    [McpServerTool]
    [Description("Discover .cs files from a file/folder/.csproj/comma-separated list and group into batches sized by `lineBudget`.")]
    public string GetSourceFiles(string path, int lineBudget = 300)
    {
        if (lineBudget < 1)
            return JsonHelper.Error("invalidParameter", "lineBudget must be at least 1.");

        List<string> filePaths;
        string scope;
        string? scopePath = null;

        if (path.Contains(',') || path.Contains(';'))
        {
            scope = "list";
            var paths = path.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries);
            try
            {
                foreach (var p in paths) _pathGuard.Validate(p.Trim(), nameof(path));
            }
            catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

            filePaths = paths
                .Select(p => p.Trim())
                .Where(p => File.Exists(p) && p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f)
                .ToList();

            if (filePaths.Count == 0)
                return JsonHelper.Error("fileNotFound", "None of the files in the provided list were found or are .cs files.");
        }
        else
        {
            try { _pathGuard.Validate(path, nameof(path)); }
            catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

            if (File.Exists(path) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                scope = "file";
                filePaths = [Path.GetFullPath(path)];
            }
            else if (File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                scope = "project";
                scopePath = Path.GetFullPath(path);
                var projectDir = Path.GetDirectoryName(scopePath) ?? Directory.GetCurrentDirectory();
                filePaths = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !_fileService.IsExcludedPath(f))
                    .Select(Path.GetFullPath)
                    .OrderBy(f => f)
                    .ToList();
            }
            else if (Directory.Exists(path))
            {
                scope = "folder";
                scopePath = Path.GetFullPath(path);
                filePaths = Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !_fileService.IsExcludedPath(f))
                    .Select(Path.GetFullPath)
                    .OrderBy(f => f)
                    .ToList();
            }
            else
            {
                return JsonHelper.Error("fileNotFound", $"Path not found or not a .cs file, .csproj file, or directory: {path}");
            }
        }

        var bag = new ConcurrentBag<(string path, int lines, int methodCount)>();
        Parallel.ForEach(filePaths, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
        {
            var (lines, methodCount) = _fileService.GetFileMetadata(f);
            bag.Add((f, lines, methodCount));
        });

        var files = bag
            .OrderBy(f => f.lines)
            .Select(f => new { f.path, f.lines, f.methodCount })
            .ToList();

        var batches = new List<List<object>>();
        var currentBatch = new List<object>();
        var currentBatchLines = 0;

        foreach (var file in files)
        {
            if (file.lines > lineBudget)
            {
                if (currentBatch.Count > 0)
                {
                    batches.Add(currentBatch);
                    currentBatch = [];
                    currentBatchLines = 0;
                }
                batches.Add([(object)file]);
                continue;
            }

            if (currentBatchLines + file.lines > lineBudget && currentBatch.Count > 0)
            {
                batches.Add(currentBatch);
                currentBatch = [];
                currentBatchLines = 0;
            }

            currentBatch.Add(file);
            currentBatchLines += file.lines;
        }

        if (currentBatch.Count > 0)
            batches.Add(currentBatch);

        return JsonHelper.Serialize(new
        {
            scope,
            scopePath,
            totalFiles = files.Count,
            totalLines = files.Sum(f => f.lines),
            lineBudget,
            batchCount = batches.Count,
            batches,
            files
        });
    }

    // --- 8. CleanupSession ---

    [McpServerTool]
    [Description("Remove session state files and TestResults/coveragereport directories. Pass `sessionId` to scope, or omit to clean artifacts older than `maxAgeMinutes`.")]
    public string CleanupSession(string workingDir, string? sessionId = null, int maxAgeMinutes = 120)
    {
        try { _pathGuard.Validate(workingDir, nameof(workingDir)); }
        catch (UnauthorizedAccessException ex) { return JsonHelper.Error("pathNotAllowed", ex.Message); }

        var removed = _sessionManager.Cleanup(workingDir, sessionId, maxAgeMinutes);
        return JsonHelper.Serialize(new { removed });
    }

    // --- Private helpers ---

    /// <summary>
    /// Walk up from a coverage XML path to find the project root.
    /// Coverage XMLs live inside TestResults-xxx/guid/, so we walk up
    /// past any TestResults or coveragereport directory to avoid storing
    /// baselines inside directories that get wiped on the next run.
    /// Returns null if no sensible root is found — callers must surface a
    /// clear error rather than silently dropping state into the current
    /// working directory or drive root.
    /// </summary>
    internal static string? ResolveProjectRoot(string resolvedPath)
    {
        var dir = Path.GetDirectoryName(resolvedPath);
        const int maxDepth = 20;
        for (var depth = 0; dir != null && depth < maxDepth; depth++)
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name))
                return null; // reached the drive root without finding a project-shaped directory
            if (name.StartsWith("TestResults", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("coveragereport", StringComparison.OrdinalIgnoreCase)
                || Guid.TryParse(name, out _))
            {
                dir = Path.GetDirectoryName(dir);
                continue;
            }
            return dir;
        }
        return null;
    }
}
