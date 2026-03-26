using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ModelContextProtocol.Server;

[McpServerToolType]
public class CoverageTools
{
    [McpServerTool]
    public string RunTestsWithCoverage(
        string testProjectPath,
        string filter,
        string? workingDir = null)
    {
        workingDir ??= Path.GetDirectoryName(testProjectPath) ?? Directory.GetCurrentDirectory();
        var resultsDir = Path.Combine(workingDir, "TestResults");
        var reportDir = Path.Combine(workingDir, "coveragereport");

        SafeDelete(resultsDir);
        SafeDelete(reportDir);

        var filterOp = filter.Contains('.') ? "=" : "~";
        var className = StripTestSuffix(filter.Split('.').Last());

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"test \"{testProjectPath}\" " +
                        $"--no-restore " +
                        $"--blame-hang-timeout 30s " +
                        $"--filter \"FullyQualifiedName{filterOp}{filter}\" " +
                        $"--collect:\"XPlat Code Coverage\" " +
                        $"--results-directory \"{resultsDir}\" " +
                        $"/p:Include=\"[*]*{className}\"",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi) ?? throw new Exception("Failed to start dotnet test");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            return $"Test run failed (code {process.ExitCode}). Error: {error}\nOutput: {output}";

        var xmlPaths = Directory.GetFiles(resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories);
        if (xmlPaths.Length == 0) return "No coverage XML found.\n" + output;

        var xmlPath = xmlPaths[0];

        // Persist XML path for tools that need it without being given it explicitly
        File.WriteAllText(Path.Combine(workingDir, ".coverage-state"), xmlPath);

        var reportPsi = new ProcessStartInfo
        {
            FileName = "reportgenerator",
            Arguments = $"-reports:\"{xmlPath}\" -targetdir:\"{reportDir}\" -reporttypes:JsonSummary",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        using var rp = Process.Start(reportPsi) ?? throw new Exception("Failed to start reportgenerator");
        rp.WaitForExit();

        var summaryPath = Path.Combine(reportDir, "Summary.json");

        return File.Exists(summaryPath)
            ? $"Tests completed.\nCoverage JSON at: {summaryPath}\nCobertura XML at: {xmlPath}\nOutput: {output}"
            : "Report generation failed.\n" + output;
    }

    [McpServerTool]
    public string GetCoverageSummary(string summaryJsonPath)
    {
        if (!File.Exists(summaryJsonPath)) return $"Summary.json not found: {summaryJsonPath}";

        var json = File.ReadAllText(summaryJsonPath);
        var root = JsonNode.Parse(json);

        var assemblies = root?["coverage"]?["assemblies"]?.AsArray();
        if (assemblies == null)
            return $"Unexpected Summary.json structure — could not find coverage.assemblies.";

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

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
    }

    [McpServerTool]
    public string GetUncoveredBranches(string coberturaXmlPath, string methodName)
    {
        // Self-heal: if the given path doesn't exist, fall back to .coverage-state
        if (!File.Exists(coberturaXmlPath))
        {
            var stateFile = Path.Combine(
                Path.GetDirectoryName(coberturaXmlPath) is { Length: > 0 } d
                    ? d
                    : Directory.GetCurrentDirectory(),
                ".coverage-state");

            if (File.Exists(stateFile))
                coberturaXmlPath = File.ReadAllText(stateFile).Trim();

            if (!File.Exists(coberturaXmlPath))
                return $"{{\"error\":\"Cobertura XML not found: {coberturaXmlPath}\"}}";
        }

        var doc = XDocument.Load(coberturaXmlPath);

        var matchedMethods = doc.Descendants("method")
            .Where(m => m.Attribute("name")?.Value?.Contains(methodName, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (matchedMethods.Count == 0)
            return $"{{\"error\":\"No method matching '{methodName}' found in coverage report.\"}}";

        // Use the first match (most specific if there's only one)
        var method = matchedMethods[0];
        var methodFullName = method.Attribute("name")?.Value ?? methodName;

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

        var result = new
        {
            method = methodFullName,
            uncoveredBranches
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
    }

    [McpServerTool]
    public string AppendTestCode(string testFilePath, string codeToAppend, string? insertAfterAnchor = null)
    {
        if (!File.Exists(testFilePath)) return $"Test file not found: {testFilePath}";

        var content = File.ReadAllText(testFilePath).TrimEnd();

        if (insertAfterAnchor != null)
        {
            var idx = content.LastIndexOf(insertAfterAnchor, StringComparison.Ordinal);
            if (idx < 0) return $"Anchor not found in file: \"{insertAfterAnchor}\"";
            var insertPos = idx + insertAfterAnchor.Length;
            var newContent = content[..insertPos] + "\n\n" + codeToAppend.Trim() + "\n" + content[insertPos..] + "\n";
            File.WriteAllText(testFilePath, newContent);
            return $"Successfully inserted after anchor in {testFilePath}.\nNew file length: {newContent.Length} chars";
        }

        var appended = content + "\n\n" + codeToAppend.Trim() + "\n";
        File.WriteAllText(testFilePath, appended);
        return $"Successfully appended to {testFilePath}.\nAdded:\n{codeToAppend}\nNew file length: {appended.Length} chars";
    }

    [McpServerTool]
    public string GetCoverageDiff(string coberturaXmlPath, string? workingDir = null)
    {
        if (!File.Exists(coberturaXmlPath))
            return $"{{\"error\":\"Cobertura XML not found: {coberturaXmlPath}\"}}";

        workingDir ??= Path.GetDirectoryName(coberturaXmlPath) ?? Directory.GetCurrentDirectory();
        var prevPath = Path.Combine(workingDir, ".coverage-prev.xml");

        var currentDoc = XDocument.Load(coberturaXmlPath);

        if (!File.Exists(prevPath))
        {
            File.Copy(coberturaXmlPath, prevPath, true);
            return JsonSerializer.Serialize(new { firstRun = true });
        }

        var prevDoc = XDocument.Load(prevPath);

        // Aggregate line/branch rate from <coverage> root element
        double ParseRate(XDocument d, string attr) =>
            double.TryParse(d.Root?.Attribute(attr)?.Value, out var v) ? v : 0;

        var prevLineRate = ParseRate(prevDoc, "line-rate");
        var prevBranchRate = ParseRate(prevDoc, "branch-rate");
        var curLineRate = ParseRate(currentDoc, "line-rate");
        var curBranchRate = ParseRate(currentDoc, "branch-rate");

        // Build lookup: key -> rates from previous
        string MethodKey(XElement m) =>
            $"{m.Parent?.Parent?.Attribute("name")?.Value}.{m.Attribute("name")?.Value}({m.Attribute("signature")?.Value})";

        var prevMethods = prevDoc.Descendants("method")
            .ToDictionary(
                MethodKey,
                m => (
                    LineRate: double.TryParse(m.Attribute("line-rate")?.Value, out var lr) ? lr : 0,
                    BranchRate: double.TryParse(m.Attribute("branch-rate")?.Value, out var br) ? br : 0
                ));

        var changedMethods = new List<object>();
        var unchangedMethods = new List<string>();

        foreach (var method in currentDoc.Descendants("method"))
        {
            var key = MethodKey(method);
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

        // Save current as baseline for next diff
        File.Copy(coberturaXmlPath, prevPath, true);

        var result = new
        {
            cycleImprovement = new
            {
                lineDelta = Math.Round(curLineRate - prevLineRate, 4),
                branchDelta = Math.Round(curBranchRate - prevBranchRate, 4)
            },
            changedMethods,
            unchanged = unchangedMethods
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
    }

    private static string StripTestSuffix(string className)
    {
        if (className.EndsWith("UnitTests")) return className[..^9];
        if (className.EndsWith("Tests")) return className[..^5];
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
}
