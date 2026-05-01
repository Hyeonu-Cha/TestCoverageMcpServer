using FluentAssertions;

namespace CoverageMcpServer.Tests.Unit;

public class ResolveProjectRootTests
{
    [Fact]
    public void WalksUpPastTestResultsAndGuidDirs()
    {
        var root = Path.Combine("C:", "repo");
        var path = Path.Combine(root, "TestResults-abc", Guid.NewGuid().ToString(), "coverage.cobertura.xml");

        var result = CoverageTools.ResolveProjectRoot(path);

        result.Should().Be(root);
    }

    [Fact]
    public void WalksUpPastCoverageReportDir()
    {
        var root = Path.Combine("C:", "repo");
        var path = Path.Combine(root, "coveragereport-xyz", "Summary.json");

        var result = CoverageTools.ResolveProjectRoot(path);

        result.Should().Be(root);
    }

    [Fact]
    public void ReturnsImmediateParentWhenNotInsideTestResults()
    {
        var root = Path.Combine("C:", "repo", "src");
        var path = Path.Combine(root, "coverage.cobertura.xml");

        var result = CoverageTools.ResolveProjectRoot(path);

        result.Should().Be(root);
    }

    [Fact]
    public void ReturnsNullWhenWalkReachesDriveRoot()
    {
        // A path whose only ancestors are TestResults dirs should not silently
        // return the drive root — callers need to surface a clear error.
        var path = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "/", "TestResults-only", "coverage.cobertura.xml");

        var result = CoverageTools.ResolveProjectRoot(path);

        result.Should().BeNull();
    }

    [Fact]
    public void StopsAtFirstNonTestResultsAncestor()
    {
        var guid = Guid.NewGuid().ToString();
        var expected = Path.Combine("C:", "repo", "TestResults", guid, "nested");
        var path = Path.Combine(expected, "coverage.cobertura.xml");

        var result = CoverageTools.ResolveProjectRoot(path);

        // "nested" is not a TestResults/coveragereport/guid-shaped dir, so the walk stops there.
        result.Should().Be(expected);
    }
}
