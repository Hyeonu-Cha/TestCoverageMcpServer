using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CoverageMcpServer.Services;

public interface ISessionManager
{
    string ComputeKey(string input);
    string ComputeSuffix(string? sessionId);
    string? ResolveCoberturaPath(string coberturaXmlPath, string? sessionId);
    void SaveCoverageState(string workingDir, string xmlPath, string filter, string? sessionId);
    int Cleanup(string workingDir, string? sessionId, int maxAgeMinutes);
}

public class SessionManager : ISessionManager
{
    private readonly IFileService _fileService;
    private readonly IPathGuard _pathGuard;
    private readonly ILogger<SessionManager> _logger;

    public SessionManager(IFileService fileService, IPathGuard pathGuard, ILogger<SessionManager> logger)
    {
        _fileService = fileService;
        _pathGuard = pathGuard;
        _logger = logger;
    }

    public string ComputeKey(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    public string ComputeSuffix(string? sessionId) =>
        sessionId != null ? $"-{ComputeKey(sessionId)}" : "";

    public string? ResolveCoberturaPath(string coberturaXmlPath, string? sessionId)
    {
        if (File.Exists(coberturaXmlPath) && _pathGuard.IsWithinAllowedRoot(coberturaXmlPath))
            return coberturaXmlPath;

        // Walk up from the supplied path to find .mcp-coverage/.
        // The caller may pass an old path inside a deleted TestResults-xxx/ directory,
        // but .mcp-coverage/ lives at the project root — so we search upward.
        var startDir = Path.GetDirectoryName(coberturaXmlPath) is { Length: > 0 } d
            ? d
            : Directory.GetCurrentDirectory();
        var dir = startDir;

        const int maxDepth = 20;
        for (var depth = 0; dir != null && depth < maxDepth; depth++)
        {
            var stateDir = Path.Combine(dir, ".mcp-coverage");
            if (Directory.Exists(stateDir))
            {
                var resolved = TryResolveFromStateDir(stateDir, sessionId);
                if (resolved != null)
                {
                    if (depth > 0)
                        _logger.LogInformation(
                            "Resolved cobertura path via walk-up: found state in {StateDir} (started from {StartDir})",
                            stateDir, startDir);
                    return resolved;
                }
            }
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private string? TryResolveFromStateDir(string stateDir, string? sessionId)
    {
        if (sessionId != null)
        {
            var scopedStateFile = Path.Combine(stateDir, $".coverage-state-{ComputeKey(sessionId)}");
            var candidate = ReadStateFile(scopedStateFile);
            if (candidate != null) return candidate;
        }

        return ReadStateFile(Path.Combine(stateDir, ".coverage-state"));
    }

    // State files hold arbitrary content. Treat them as untrusted: only return a path
    // that exists AND falls inside the allowed root. Otherwise a stale or poisoned
    // state file would let a caller read/write outside PathGuard's allowlist.
    private string? ReadStateFile(string stateFile)
    {
        if (!File.Exists(stateFile)) return null;
        var resolved = File.ReadAllText(stateFile).Trim();
        if (!File.Exists(resolved)) return null;
        if (!_pathGuard.IsWithinAllowedRoot(resolved))
        {
            _logger.LogWarning("Ignoring state file {State}: resolved path '{Resolved}' is outside the allowed root.",
                stateFile, resolved);
            return null;
        }
        return resolved;
    }

    public void SaveCoverageState(string workingDir, string xmlPath, string filter, string? sessionId)
    {
        var stateDir = Path.Combine(workingDir, ".mcp-coverage");
        Directory.CreateDirectory(stateDir);

        // Always save a filter-keyed state file so ResolveCoberturaPath can find it
        // when sessionId matches the filter (original behavior)
        var filterKey = ComputeKey(filter);
        _fileService.AtomicWriteFile(Path.Combine(stateDir, $".coverage-state-{filterKey}"), xmlPath);

        // If sessionId is different from filter, also save a session-keyed file
        // so other tools can resolve via their sessionId parameter
        if (sessionId != null)
        {
            var sessionKey = ComputeKey(sessionId);
            if (sessionKey != filterKey)
                _fileService.AtomicWriteFile(Path.Combine(stateDir, $".coverage-state-{sessionKey}"), xmlPath);
        }

        // Global fallback
        _fileService.AtomicWriteFile(Path.Combine(stateDir, ".coverage-state"), xmlPath);
    }

    public int Cleanup(string workingDir, string? sessionId, int maxAgeMinutes)
    {
        var removed = 0;

        if (sessionId != null)
        {
            var key = ComputeKey(sessionId);

            var stateDir = Path.Combine(workingDir, ".mcp-coverage");
            if (Directory.Exists(stateDir))
            {
                string[] stateFiles = [$".coverage-state-{key}", $".coverage-prev-{key}.xml"];
                foreach (var name in stateFiles)
                {
                    var filePath = Path.Combine(stateDir, name);
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            File.Delete(filePath);
                            removed++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete session file: {Path}", filePath);
                        }
                    }
                }
            }

            _fileService.SafeDelete(Path.Combine(workingDir, $"TestResults-{key}"));
            _fileService.SafeDelete(Path.Combine(workingDir, $"coveragereport-{key}"));
            // Also clean unsuffixed dirs that may have been created before session isolation
            _fileService.SafeDelete(Path.Combine(workingDir, "TestResults"));
            _fileService.SafeDelete(Path.Combine(workingDir, "coveragereport"));
        }
        else
        {
            var stateDir = Path.Combine(workingDir, ".mcp-coverage");
            if (Directory.Exists(stateDir))
            {
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
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete stale state file: {Path}", file);
                    }
                }
            }

            if (Directory.Exists(workingDir))
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-maxAgeMinutes);
                foreach (var dir in Directory.GetDirectories(workingDir))
                {
                    var dirName = Path.GetFileName(dir);
                    if ((dirName.StartsWith("TestResults-") || dirName.StartsWith("coveragereport-"))
                        && Directory.GetLastWriteTimeUtc(dir) < cutoff)
                    {
                        _fileService.SafeDelete(dir);
                        removed++;
                    }
                }
            }
        }

        _logger.LogInformation("Session cleanup removed {Count} artifacts", removed);
        return removed;
    }
}
