using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ModelContextProtocol.Server;

[McpServerToolType]
public class CoverageTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Per-file locks to prevent concurrent AppendTestCode from losing each other's writes.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new();

    private const int ReportGeneratorTimeoutMs = 60_000; // 60 seconds

    // --- 1. RunTestsWithCoverage (async, deadlock-safe) ---

    [McpServerTool]
    [Description("Run dotnet test with XPlat Code Coverage and generate a JSON summary. Returns paths to Summary.json and coverage.cobertura.xml. Use broad filter (containing * or ,) for multi-file coverage; use class-specific filter for single-class scoping. Set forceRestore=true after scaffolding a new test project or adding NuGet packages. Pass sessionId to isolate output directories when multiple agents run concurrently.")]
    public async Task<string> RunTestsWithCoverage(
        string testProjectPath,
        string filter,
        string? workingDir = null,
        bool forceRestore = false,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        workingDir ??= Path.GetDirectoryName(testProjectPath) ?? Directory.GetCurrentDirectory();
        var suffix = sessionId != null ? $"-{SessionKey(sessionId)}" : "";
        var resultsDir = Path.Combine(workingDir, $"TestResults{suffix}");
        var reportDir = Path.Combine(workingDir, $"coveragereport{suffix}");

        SafeDelete(resultsDir);
        SafeDelete(reportDir);

        var filterOp = filter.Contains('.') ? "=" : "~";
        var className = StripTestSuffix(filter.Split('.').Last());

        var restoreFlag = forceRestore ? "" : "--no-restore ";
        var escapedProjectPath = EscapeProcessArg(testProjectPath);
        var escapedResultsDir = EscapeProcessArg(resultsDir);
        var args = $"test {escapedProjectPath} " +
                   restoreFlag +
                   $"--blame-hang-timeout 30s " +
                   $"--filter \"FullyQualifiedName{filterOp}{filter}\" " +
                   $"--collect:\"XPlat Code Coverage\" " +
                   $"--results-directory {escapedResultsDir}";

        // Only scope coverage to a single class when filter looks class-specific (no wildcards, no '*')
        // Skip /p:Include for broad filters to collect coverage across all source files
        if (!filter.Contains('*') && !filter.Contains(','))
            args += $" /p:Include=\"[*]*{className}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi) ?? throw new Exception("Failed to start dotnet test");

        // Read stderr async to prevent deadlock when pipe buffer fills
        var errorTask = process.StandardError.ReadToEndAsync();
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await errorTask;

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return JsonError("cancelled", "Test run was cancelled by the client.");
        }

        if (process.ExitCode != 0)
            return JsonError("buildError", $"Test run failed (code {process.ExitCode}). Error: {error}\nOutput: {output}");

        var xmlPaths = Directory.GetFiles(resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories);
        if (xmlPaths.Length == 0)
            return JsonError("noCoverage", "No coverage XML found.\n" + output);

        var xmlPath = xmlPaths[0];

        // Persist XML path for tools that need it without being given it explicitly
        // All state files go into .mcp-coverage/ to keep the project root clean
        var stateDir = Path.Combine(workingDir, ".mcp-coverage");
        Directory.CreateDirectory(stateDir);
        var sessionKey = SessionKey(filter);
        AtomicWriteFile(Path.Combine(stateDir, $".coverage-state-{sessionKey}"), xmlPath);
        AtomicWriteFile(Path.Combine(stateDir, ".coverage-state"), xmlPath);

        var escapedXmlPath = EscapeProcessArg(xmlPath);
        var escapedReportDir = EscapeProcessArg(reportDir);
        var reportPsi = new ProcessStartInfo
        {
            FileName = "reportgenerator",
            Arguments = $"-reports:{escapedXmlPath} -targetdir:{escapedReportDir} -reporttypes:JsonSummary",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var rp = Process.Start(reportPsi) ?? throw new Exception("Failed to start reportgenerator");
        var rpOutTask = rp.StandardOutput.ReadToEndAsync();
        var rpErrTask = rp.StandardError.ReadToEndAsync();

        // Timeout reportgenerator to prevent indefinite hangs (corrupted XML, disk full, etc.)
        using var cts = new CancellationTokenSource(ReportGeneratorTimeoutMs);
        try
        {
            await rp.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { rp.Kill(entireProcessTree: true); } catch { }
            return JsonError("timeout", $"reportgenerator timed out after {ReportGeneratorTimeoutMs / 1000}s. Cobertura XML is still available at: {xmlPath}");
        }

        await rpOutTask;
        await rpErrTask;

        var summaryPath = Path.Combine(reportDir, "Summary.json");

        return File.Exists(summaryPath)
            ? $"Tests completed.\nCoverage JSON at: {summaryPath}\nCobertura XML at: {xmlPath}\nOutput: {output}"
            : JsonError("reportFailed", "Report generation failed.\n" + output);
    }

    // --- 2. GetCoverageSummary ---

    [McpServerTool]
    [Description("Parse Summary.json into structured class/method coverage data sorted by branch coverage (lowest first). Returns JSON array with lineCoverage and branchCoverage per class and method.")]
    public string GetCoverageSummary(string summaryJsonPath)
    {
        if (!File.Exists(summaryJsonPath))
            return JsonError("fileNotFound", $"Summary.json not found: {summaryJsonPath}");

        try
        {
            var json = File.ReadAllText(summaryJsonPath);
            var root = JsonNode.Parse(json);

            var assemblies = root?["coverage"]?["assemblies"]?.AsArray();
            if (assemblies == null)
                return JsonError("parseFailed", "Unexpected Summary.json structure — could not find coverage.assemblies.");

            var result = new List<object>();

            foreach (var assembly in assemblies)
            {
                var classes = assembly?["classes"]?.AsArray();
                if (classes == null) continue;

                foreach (var cls in classes)
                {
                    var methods = cls?["methods"]?.AsArray();
                    var methodList = new List<(string name, double line, double branch)>();

                    if (methods != null)
                    {
                        foreach (var method in methods)
                        {
                            var linePct = method?["linecoverage"]?.GetValue<double>() ?? 0;
                            var branchPct = method?["branchcoverage"]?.GetValue<double>() ?? 0;
                            methodList.Add((
                                name: method?["name"]?.GetValue<string>() ?? "",
                                line: Math.Round(linePct / 100.0, 4),
                                branch: Math.Round(branchPct / 100.0, 4)
                            ));
                        }

                        methodList = methodList.OrderBy(m => m.branch).ToList();
                    }

                    var classLinePct = cls?["linecoverage"]?.GetValue<double>() ?? 0;
                    var classBranchPct = cls?["branchcoverage"]?.GetValue<double>() ?? 0;

                    result.Add(new
                    {
                        @class = cls?["name"]?.GetValue<string>() ?? "",
                        lineCoverage = Math.Round(classLinePct / 100.0, 4),
                        branchCoverage = Math.Round(classBranchPct / 100.0, 4),
                        methods = methodList.Select(m => new { m.name, m.line, m.branch })
                    });
                }
            }

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonError("parseFailed", $"Failed to parse Summary.json: {ex.Message}");
        }
    }

    // --- 3. GetUncoveredBranches (returns all matches, not just first) ---

    [McpServerTool]
    [Description("Find uncovered branch conditions for methods matching the given name in Cobertura XML. Returns all matching methods with their uncovered branches. Supports partial method name matching. Pass sessionId to resolve the correct coverage state when multiple agents run concurrently.")]
    public string GetUncoveredBranches(string coberturaXmlPath, string methodName, string? sessionId = null)
    {
        coberturaXmlPath = ResolveCoberturaPath(coberturaXmlPath, sessionId) ?? coberturaXmlPath;
        if (!File.Exists(coberturaXmlPath))
            return JsonError("fileNotFound", "Cobertura XML not found and no .coverage-state fallback available.");

        try
        {
            var doc = XDocument.Load(coberturaXmlPath);

            var matchedMethods = doc.Descendants("method")
                .Where(m => m.Attribute("name")?.Value?.Contains(methodName, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            if (matchedMethods.Count == 0)
                return JsonError("noMatch", $"No method matching '{methodName}' found in coverage report.");

            var results = matchedMethods.Select(method =>
            {
                var methodFullName = method.Attribute("name")?.Value ?? methodName;
                var className = method.Parent?.Parent?.Attribute("name")?.Value ?? "";

                var uncoveredBranches = method.Descendants("line")
                    .Where(l => l.Attribute("branch")?.Value == "True")
                    .Select(l => new
                    {
                        line = int.TryParse(l.Attribute("number")?.Value, out var n) ? n : 0,
                        missing = l.Descendants("condition")
                            .Where(c => c.Attribute("coverage")?.Value == "0%")
                            .Select(c => $"condition {c.Attribute("number")?.Value} ({c.Attribute("type")?.Value})")
                            .ToList()
                    })
                    .Where(b => b.missing.Count > 0)
                    .ToList();

                return new { method = methodFullName, @class = className, uncoveredBranches };
            }).ToList();

            var result = new
            {
                matchCount = results.Count,
                methods = results
            };

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonError("parseFailed", $"Failed to parse Cobertura XML: {ex.Message}");
        }
    }

    // --- 4. AppendTestCode (file-locked, atomic writes) ---

    [McpServerTool]
    [Description("Insert or append C# test code into a test file. Use insertAfterAnchor to place code after a specific string, or omit to append to end of file. Uses file-level locking to prevent concurrent writes from losing changes.")]
    public async Task<string> AppendTestCode(string testFilePath, string codeToAppend, string? insertAfterAnchor = null)
    {
        if (!File.Exists(testFilePath))
            return JsonError("fileNotFound", $"Test file not found: {testFilePath}");

        // Acquire per-file lock to prevent concurrent appends from losing each other's writes
        var normalizedPath = Path.GetFullPath(testFilePath).ToLowerInvariant();
        var fileLock = FileLocks.GetOrAdd(normalizedPath, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync();
        try
        {
            var content = File.ReadAllText(testFilePath).TrimEnd();

            if (insertAfterAnchor != null)
            {
                // Try exact match first
                var idx = content.LastIndexOf(insertAfterAnchor, StringComparison.Ordinal);

                // Fallback: whitespace-normalized match (collapse runs of whitespace to single space)
                if (idx < 0)
                {
                    var normalizedAnchor = NormalizeWhitespace(insertAfterAnchor);
                    var normalizedContent = NormalizeWhitespace(content);
                    var normalizedIdx = normalizedContent.LastIndexOf(normalizedAnchor, StringComparison.Ordinal);

                    if (normalizedIdx >= 0)
                    {
                        // Map normalized position back to original content
                        idx = MapNormalizedPosition(content, normalizedIdx, normalizedAnchor.Length, out var matchLength);
                        if (idx >= 0)
                        {
                            var insertPos = idx + matchLength;
                            var newContent = content[..insertPos] + "\n\n" + codeToAppend.Trim() + "\n" + content[insertPos..] + "\n";
                            AtomicWriteFile(testFilePath, newContent);
                            return $"Successfully inserted after anchor (whitespace-normalized match) in {testFilePath}.\nNew file length: {newContent.Length} chars";
                        }
                    }

                    return JsonError("anchorNotFound", $"Anchor not found in file (tried exact and whitespace-normalized match): \"{insertAfterAnchor}\"");
                }

                {
                    var insertPos = idx + insertAfterAnchor.Length;
                    var newContent = content[..insertPos] + "\n\n" + codeToAppend.Trim() + "\n" + content[insertPos..] + "\n";
                    AtomicWriteFile(testFilePath, newContent);
                    return $"Successfully inserted after anchor in {testFilePath}.\nNew file length: {newContent.Length} chars";
                }
            }

            var appended = content + "\n\n" + codeToAppend.Trim() + "\n";
            AtomicWriteFile(testFilePath, appended);
            return $"Successfully appended to {testFilePath}.\nAdded:\n{codeToAppend}\nNew file length: {appended.Length} chars";
        }
        finally
        {
            fileLock.Release();
        }
    }

    // --- 5. GetCoverageDiff (self-healing, duplicate-safe, detects removed methods) ---

    [McpServerTool]
    [Description("Compare current coverage against the previous baseline. Shows method-level changes including new and removed methods. Saves current as new baseline after comparison. Use sessionId to isolate concurrent runs.")]
    public string GetCoverageDiff(string coberturaXmlPath, string? workingDir = null, string? sessionId = null)
    {
        coberturaXmlPath = ResolveCoberturaPath(coberturaXmlPath, sessionId) ?? coberturaXmlPath;
        if (!File.Exists(coberturaXmlPath))
            return JsonError("fileNotFound", "Cobertura XML not found and no .coverage-state fallback available.");

        workingDir ??= Path.GetDirectoryName(coberturaXmlPath) ?? Directory.GetCurrentDirectory();
        var stateDir = Path.Combine(workingDir, ".mcp-coverage");
        Directory.CreateDirectory(stateDir);
        var suffix = sessionId != null ? $"-{SessionKey(sessionId)}" : "";
        var prevPath = Path.Combine(stateDir, $".coverage-prev{suffix}.xml");

        try
        {
            var currentDoc = XDocument.Load(coberturaXmlPath);

            if (!File.Exists(prevPath))
            {
                AtomicWriteFile(prevPath, File.ReadAllText(coberturaXmlPath));
                return JsonSerializer.Serialize(new { firstRun = true }, JsonOptions);
            }

            var prevDoc = XDocument.Load(prevPath);

            // Aggregate line/branch rate from <coverage> root element
            double ParseRate(XDocument d, string attr) =>
                double.TryParse(d.Root?.Attribute(attr)?.Value, out var v) ? v : 0;

            var prevLineRate = ParseRate(prevDoc, "line-rate");
            var prevBranchRate = ParseRate(prevDoc, "branch-rate");
            var curLineRate = ParseRate(currentDoc, "line-rate");
            var curBranchRate = ParseRate(currentDoc, "branch-rate");

            // Build lookup: key -> rates from previous (duplicate-safe with first-wins)
            string MethodKey(XElement m) =>
                $"{m.Parent?.Parent?.Attribute("name")?.Value}.{m.Attribute("name")?.Value}({m.Attribute("signature")?.Value})";

            var prevMethods = new Dictionary<string, (double LineRate, double BranchRate)>();
            foreach (var m in prevDoc.Descendants("method"))
            {
                var key = MethodKey(m);
                prevMethods.TryAdd(key, (
                    LineRate: double.TryParse(m.Attribute("line-rate")?.Value, out var lr) ? lr : 0,
                    BranchRate: double.TryParse(m.Attribute("branch-rate")?.Value, out var br) ? br : 0
                ));
            }

            var changedMethods = new List<object>();
            var unchangedMethods = new List<string>();
            var seenKeys = new HashSet<string>();

            foreach (var method in currentDoc.Descendants("method"))
            {
                var key = MethodKey(method);
                if (!seenKeys.Add(key)) continue; // skip duplicates in current

                var name = method.Attribute("name")?.Value ?? key;
                var curLine = double.TryParse(method.Attribute("line-rate")?.Value, out var cl) ? cl : 0;
                var curBranch = double.TryParse(method.Attribute("branch-rate")?.Value, out var cb) ? cb : 0;

                if (prevMethods.TryGetValue(key, out var prev))
                {
                    if (Math.Abs(curLine - prev.LineRate) > 0.001 || Math.Abs(curBranch - prev.BranchRate) > 0.001)
                    {
                        changedMethods.Add(new
                        {
                            name,
                            lineBefore = Math.Round(prev.LineRate, 4),
                            lineAfter = Math.Round(curLine, 4),
                            branchBefore = Math.Round(prev.BranchRate, 4),
                            branchAfter = Math.Round(curBranch, 4)
                        });
                    }
                    else
                    {
                        unchangedMethods.Add(name);
                    }
                }
                else
                {
                    changedMethods.Add(new
                    {
                        name,
                        lineBefore = 0.0,
                        lineAfter = Math.Round(curLine, 4),
                        branchBefore = 0.0,
                        branchAfter = Math.Round(curBranch, 4)
                    });
                }
            }

            // Detect removed methods — extract name from key (ClassName.MethodName(signature))
            var removedMethods = prevMethods.Keys
                .Where(k => !seenKeys.Contains(k))
                .Select(k => new
                {
                    name = k,
                    lineBefore = Math.Round(prevMethods[k].LineRate, 4),
                    lineAfter = 0.0,
                    branchBefore = Math.Round(prevMethods[k].BranchRate, 4),
                    branchAfter = 0.0
                })
                .ToList();

            // Save current as baseline for next diff (atomic to prevent corruption on crash)
            AtomicWriteFile(prevPath, File.ReadAllText(coberturaXmlPath));

            var result = new
            {
                cycleImprovement = new
                {
                    lineDelta = Math.Round(curLineRate - prevLineRate, 4),
                    branchDelta = Math.Round(curBranchRate - prevBranchRate, 4)
                },
                changedMethods,
                removedMethods,
                unchanged = unchangedMethods
            };

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonError("parseFailed", $"Failed to parse coverage XML: {ex.Message}");
        }
    }

    // --- 6. GetFileCoverage ---

    [McpServerTool]
    [Description("Get coverage for a single source file from Cobertura XML. Returns per-class and per-method rates with allMeetTarget (true when all classes meet targetRate for both line and branch). Instant — just XML parsing. Pass sessionId to resolve the correct coverage state when multiple agents run concurrently.")]
    public string GetFileCoverage(string coberturaXmlPath, string sourceFileName, string? sessionId = null, double targetRate = 0.8)
    {
        coberturaXmlPath = ResolveCoberturaPath(coberturaXmlPath, sessionId) ?? coberturaXmlPath;
        if (!File.Exists(coberturaXmlPath))
            return JsonError("fileNotFound", "Cobertura XML not found and no .coverage-state fallback available.");

        try
        {
            var doc = XDocument.Load(coberturaXmlPath);

            // Match classes whose filename attribute ends with the given source file name
            var matchedClasses = doc.Descendants("class")
                .Where(c =>
                {
                    var filename = c.Attribute("filename")?.Value ?? "";
                    return filename.EndsWith(sourceFileName, StringComparison.OrdinalIgnoreCase)
                        || filename.EndsWith(sourceFileName.Replace("/", "\\"), StringComparison.OrdinalIgnoreCase)
                        || filename.EndsWith(sourceFileName.Replace("\\", "/"), StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (matchedClasses.Count == 0)
                return JsonError("noMatch", $"No classes found for source file '{sourceFileName}' in coverage report.");

            var classes = new List<object>();
            var allMeetTarget = true;

            foreach (var cls in matchedClasses)
            {
                var className = cls.Attribute("name")?.Value ?? "";
                var lineRate = double.TryParse(cls.Attribute("line-rate")?.Value, out var lr) ? lr : 0;
                var branchRate = double.TryParse(cls.Attribute("branch-rate")?.Value, out var br) ? br : 0;
                var meetsTarget = lineRate >= targetRate && branchRate >= targetRate;

                if (!meetsTarget) allMeetTarget = false;

                var methods = cls.Descendants("method")
                    .Select(m => new
                    {
                        name = m.Attribute("name")?.Value ?? "",
                        lineRate = double.TryParse(m.Attribute("line-rate")?.Value, out var mlr) ? Math.Round(mlr, 4) : 0,
                        branchRate = double.TryParse(m.Attribute("branch-rate")?.Value, out var mbr) ? Math.Round(mbr, 4) : 0
                    })
                    .OrderBy(m => m.branchRate)
                    .ToList();

                classes.Add(new
                {
                    @class = className,
                    lineRate = Math.Round(lineRate, 4),
                    branchRate = Math.Round(branchRate, 4),
                    meetsTarget,
                    methods
                });
            }

            var result = new
            {
                sourceFile = sourceFileName,
                allMeetTarget,
                classes
            };

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonError("parseFailed", $"Failed to parse Cobertura XML: {ex.Message}");
        }
    }

    // --- 7. GetSourceFiles ---

    [McpServerTool]
    [Description("Discover .cs source files from a file path, folder, or .csproj project. Returns file metadata (lines, methodCount) and smart batches grouped by lineBudget. Small files are grouped together, large files get their own batch.")]
    public string GetSourceFiles(string path, int lineBudget = 300)
    {
        List<string> filePaths;
        string scope;
        string? scopePath = null;

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
                .Where(f => !IsExcludedPath(f))
                .Select(Path.GetFullPath)
                .OrderBy(f => f)
                .ToList();
        }
        else if (Directory.Exists(path))
        {
            scope = "folder";
            scopePath = Path.GetFullPath(path);
            filePaths = Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsExcludedPath(f))
                .Select(Path.GetFullPath)
                .OrderBy(f => f)
                .ToList();
        }
        else
        {
            return JsonError("fileNotFound", $"Path not found or not a .cs file, .csproj file, or directory: {path}");
        }

        // Build file metadata
        var files = filePaths.Select(f =>
        {
            var content = File.ReadAllText(f);
            var lines = content.Split('\n').Length;
            var methodCount = CountPublicMethods(content);
            return new { path = f, lines, methodCount };
        })
        .OrderBy(f => f.lines) // smallest files first for efficient batching
        .ToList();

        // Build batches based on line budget
        var batches = new List<List<object>>();
        var currentBatch = new List<object>();
        var currentBatchLines = 0;

        foreach (var file in files)
        {
            // Large file gets its own batch
            if (file.lines > lineBudget)
            {
                if (currentBatch.Count > 0)
                {
                    batches.Add(currentBatch);
                    currentBatch = [];
                    currentBatchLines = 0;
                }
                batches.Add([file]);
                continue;
            }

            // Would adding this file exceed budget?
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

        var result = new
        {
            scope,
            scopePath,
            totalFiles = files.Count,
            totalLines = files.Sum(f => f.lines),
            lineBudget,
            batchCount = batches.Count,
            batches,
            files
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    // --- 8. CleanupSession ---

    [McpServerTool]
    [Description("Remove stale state files from .mcp-coverage/. Call with a specific sessionId to clean up that session's files, or omit to remove all state files older than maxAgeMinutes (default 120). Returns count of files removed.")]
    public string CleanupSession(string workingDir, string? sessionId = null, int maxAgeMinutes = 120)
    {
        var stateDir = Path.Combine(workingDir, ".mcp-coverage");
        if (!Directory.Exists(stateDir))
            return JsonSerializer.Serialize(new { removed = 0, message = "No .mcp-coverage directory found." }, JsonOptions);

        var removed = 0;

        if (sessionId != null)
        {
            // Clean up specific session's files
            var key = SessionKey(sessionId);
            string[] patterns = [$".coverage-state-{key}", $".coverage-prev-{key}.xml"];
            foreach (var pattern in patterns)
            {
                var filePath = Path.Combine(stateDir, pattern);
                if (File.Exists(filePath))
                {
                    try { File.Delete(filePath); removed++; } catch { }
                }
            }
        }
        else
        {
            // Age-based cleanup: remove files older than maxAgeMinutes
            var cutoff = DateTime.UtcNow.AddMinutes(-maxAgeMinutes);
            foreach (var file in Directory.GetFiles(stateDir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch { }
            }
        }

        return JsonSerializer.Serialize(new { removed }, JsonOptions);
    }

    // --- Shared helpers ---

    /// <summary>
    /// Resolves the Cobertura XML path with .coverage-state fallback.
    /// Returns null if not found anywhere.
    /// </summary>
    private static string? ResolveCoberturaPath(string coberturaXmlPath, string? sessionId = null)
    {
        if (File.Exists(coberturaXmlPath))
            return coberturaXmlPath;

        var dir = Path.GetDirectoryName(coberturaXmlPath) is { Length: > 0 } d
            ? d
            : Directory.GetCurrentDirectory();
        var stateDir = Path.Combine(dir, ".mcp-coverage");

        // Try session-scoped state file first (prevents cross-agent pollution)
        if (sessionId != null)
        {
            var scopedStateFile = Path.Combine(stateDir, $".coverage-state-{SessionKey(sessionId)}");
            if (File.Exists(scopedStateFile))
            {
                var resolved = File.ReadAllText(scopedStateFile).Trim();
                if (File.Exists(resolved))
                    return resolved;
            }
        }

        // Fall back to default state file
        var stateFile = Path.Combine(stateDir, ".coverage-state");
        if (File.Exists(stateFile))
        {
            var resolved = File.ReadAllText(stateFile).Trim();
            if (File.Exists(resolved))
                return resolved;
        }

        return null;
    }

    /// <summary>
    /// Structured error with errorType for agent recovery decisions.
    /// errorType values: fileNotFound, parseFailed, buildError, noCoverage, reportFailed, timeout, noMatch, anchorNotFound
    /// </summary>
    private static string JsonError(string errorType, string message) =>
        JsonSerializer.Serialize(new { error = message, errorType }, JsonOptions);

    private static bool IsExcludedPath(string filePath)
    {
        var normalized = filePath.Replace("\\", "/");
        string[] excludedSegments = ["/obj/", "/bin/", "/Migrations/", "/.mcp-coverage/"];
        if (excludedSegments.Any(seg => normalized.Contains(seg, StringComparison.OrdinalIgnoreCase)))
            return true;
        // Prefix-match for session-scoped directories (TestResults-xxx, coveragereport-xxx)
        var parts = normalized.Split('/');
        return parts.Any(p => p.StartsWith("TestResults", StringComparison.OrdinalIgnoreCase)
                           || p.StartsWith("coveragereport", StringComparison.OrdinalIgnoreCase));
    }

    private static string StripTestSuffix(string className)
    {
        if (className.EndsWith("IntegrationTests")) return className[..^16];
        if (className.EndsWith("UnitTests")) return className[..^9];
        if (className.EndsWith("Tests")) return className[..^5];
        if (className.EndsWith("Specs")) return className[..^5];
        if (className.EndsWith("Spec")) return className[..^4];
        if (className.EndsWith("Test")) return className[..^4];
        return className;
    }

    private static void SafeDelete(string path)
    {
        if (Directory.Exists(path))
        {
            try { Directory.Delete(path, true); }
            catch { /* ignore for robustness */ }
        }
    }

    /// <summary>
    /// Write content to a file atomically using write-to-temp-then-rename.
    /// Prevents half-written files from race conditions or process crashes.
    /// </summary>
    private static void AtomicWriteFile(string targetPath, string content)
    {
        var dir = Path.GetDirectoryName(targetPath) ?? Directory.GetCurrentDirectory();
        var tempPath = Path.Combine(dir, $".tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            // Clean up temp file if rename fails
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Escape a path for safe use as a process argument.
    /// Wraps in quotes and escapes embedded quotes.
    /// </summary>
    private static string EscapeProcessArg(string arg)
    {
        // Escape any embedded double quotes, then wrap in double quotes
        var escaped = arg.Replace("\\\"", "\\\\\"").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    /// <summary>
    /// Count public methods in C# source code, skipping string literals and comments.
    /// More accurate than a naive regex — avoids counting matches inside strings/comments.
    /// </summary>
    private static int CountPublicMethods(string content)
    {
        // Strip single-line comments, multi-line comments, and string literals before counting
        var stripped = Regex.Replace(content, @"//[^\n]*", "");                      // single-line comments
        stripped = Regex.Replace(stripped, @"/\*[\s\S]*?\*/", "");                    // multi-line comments
        stripped = Regex.Replace(stripped, "@\"(?:\"\"|[^\"])*\"", "\"\"");           // verbatim strings
        stripped = Regex.Replace(stripped, "\"(?:\\\\.|[^\"\\\\])*\"", "\"\"");       // regular strings

        return Regex.Matches(
            stripped,
            @"public\s+(?:(?:async|virtual|override|static|sealed|new|abstract)\s+)*" +
            @"[\w<>\[\]?,\s]+\s+\w+\s*\(").Count;
    }

    /// <summary>
    /// Collapse all runs of whitespace (spaces, tabs, newlines) into a single space.
    /// </summary>
    private static string NormalizeWhitespace(string input) =>
        Regex.Replace(input, @"\s+", " ").Trim();

    /// <summary>
    /// Build a mapping from each normalized-string index to the corresponding original-string index.
    /// Whitespace runs collapse to a single entry; non-whitespace maps 1:1.
    /// </summary>
    private static List<int> BuildNormalizedToOriginalMap(string original)
    {
        var map = new List<int>(); // map[normalizedIndex] = originalIndex
        var inWhitespace = false;

        for (var i = 0; i < original.Length; i++)
        {
            if (char.IsWhiteSpace(original[i]))
            {
                if (!inWhitespace)
                {
                    map.Add(i); // first char of whitespace run → single normalized space
                    inWhitespace = true;
                }
                // consecutive whitespace chars are skipped (collapsed)
            }
            else
            {
                map.Add(i);
                inWhitespace = false;
            }
        }

        return map;
    }

    /// <summary>
    /// Map a position in normalized text back to the original text.
    /// Uses a pre-built index map so find and translate are separate concerns.
    /// Returns the start index in original and outputs the match length in original.
    /// </summary>
    private static int MapNormalizedPosition(string original, int normalizedStart, int normalizedLength, out int originalMatchLength)
    {
        originalMatchLength = 0;

        var map = BuildNormalizedToOriginalMap(original);

        if (normalizedStart >= map.Count || normalizedStart < 0)
            return -1;

        var normalizedEnd = normalizedStart + normalizedLength - 1;
        if (normalizedEnd >= map.Count)
            return -1;

        var originalStart = map[normalizedStart];
        var originalEnd = map[normalizedEnd];

        // Extend originalEnd to include any trailing whitespace that was collapsed
        // (the map points to the first char of a whitespace run; we need the last)
        while (originalEnd + 1 < original.Length && char.IsWhiteSpace(original[originalEnd + 1])
               && (normalizedEnd + 1 >= map.Count || map[normalizedEnd + 1] != originalEnd + 1))
        {
            originalEnd++;
        }

        originalMatchLength = originalEnd - originalStart + 1;
        return originalStart;
    }

    /// <summary>
    /// Generate a short, filesystem-safe session key from a filter string.
    /// </summary>
    private static string SessionKey(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }
}
