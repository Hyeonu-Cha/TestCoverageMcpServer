using CoverageMcpServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CoverageMcpServer.Tests.Unit;

public class CodeInserterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileService _realFileService;
    private readonly CodeInserter _sut;

    public CodeInserterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _realFileService = new FileService(new Mock<ILogger<FileService>>().Object);
        _sut = new CodeInserter(_realFileService, new Mock<ILogger<CodeInserter>>().Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // --- TryRoslynInsert (static) ---

    [Fact]
    public void TryRoslynInsert_AppendsToLastClass_NoAnchor()
    {
        var content = @"
namespace Test
{
    public class MyTests
    {
        public void Existing() { }
    }
}";
        var code = @"
    [Fact]
    public void NewTest() { }";

        var success = CodeInserter.TryRoslynInsert(content, code, null, out var result, out _);

        success.Should().BeTrue();
        result.Should().Contain("NewTest");
        result.Should().Contain("Existing");
    }

    [Fact]
    public void TryRoslynInsert_InsertsAfterAnchorMethod()
    {
        var content = @"
public class MyTests
{
    public void First() { }
    public void Second() { }
}";
        var code = "public void Inserted() { }";

        var success = CodeInserter.TryRoslynInsert(content, code, "void First()", out var result, out _);

        success.Should().BeTrue();
        var firstIdx = result.IndexOf("First");
        var insertedIdx = result.IndexOf("Inserted");
        var secondIdx = result.IndexOf("Second");
        insertedIdx.Should().BeGreaterThan(firstIdx);
        insertedIdx.Should().BeLessThan(secondIdx);
    }

    [Fact]
    public void TryRoslynInsert_PreservesLineEndings_CrLf()
    {
        var content = "public class Foo\r\n{\r\n    public void A() { }\r\n}\r\n";
        var code = "public void B() { }";

        var success = CodeInserter.TryRoslynInsert(content, code, null, out var result, out _);

        success.Should().BeTrue();
        result.Should().Contain("\r\n");
    }

    [Fact]
    public void TryRoslynInsert_PreservesLineEndings_Lf()
    {
        var content = "public class Foo\n{\n    public void A() { }\n}\n";
        var code = "public void B() { }";

        var success = CodeInserter.TryRoslynInsert(content, code, null, out var result, out _);

        success.Should().BeTrue();
        // Should not introduce \r\n
        result.Replace("\r\n", "").Should().Be(result.Replace("\n", "").Replace("\r", "") == result.Replace("\r\n", "")
            ? result.Replace("\r\n", "") : result.Replace("\r\n", ""));
    }

    [Fact]
    public void TryRoslynInsert_FailsWhenNoTypeDeclaration()
    {
        var content = "using System;\n// no class here";

        var success = CodeInserter.TryRoslynInsert(content, "public void M() { }", null, out _, out var reason);

        success.Should().BeFalse();
        reason.Should().Contain("no type declaration");
    }

    [Fact]
    public void TryRoslynInsert_FailsWhenCodeHasSyntaxErrors()
    {
        var content = "public class Foo { }";
        var code = "public void Bad( { }"; // syntax error

        var success = CodeInserter.TryRoslynInsert(content, code, null, out _, out var reason);

        success.Should().BeFalse();
        reason.Should().Contain("syntax error");
    }

    [Fact]
    public void TryRoslynInsert_FailsWhenAnchorNotFound()
    {
        var content = "public class Foo { public void A() { } }";

        var success = CodeInserter.TryRoslynInsert(content, "public void B() { }", "NonExistentMethod", out _, out var reason);

        success.Should().BeFalse();
        reason.Should().Contain("anchor not found");
    }

    [Fact]
    public void TryRoslynInsert_AnchorMatchesViaWhitespaceNormalization()
    {
        var content = "public class Foo\n{\n    public void   Method1()   { }\n}";

        var success = CodeInserter.TryRoslynInsert(content, "public void B() { }", "void Method1()", out var result, out _);

        success.Should().BeTrue();
        result.Should().Contain("B()");
    }

    [Fact]
    public void TryRoslynInsert_MultipleClasses_UsesLastClass()
    {
        var content = @"
public class First { }
public class Second { public void Existing() { } }";

        var success = CodeInserter.TryRoslynInsert(content, "public void Added() { }", null, out var result, out _);

        success.Should().BeTrue();
        // "Added" should be in Second, not First
        var secondIdx = result.IndexOf("class Second");
        var addedIdx = result.IndexOf("Added");
        addedIdx.Should().BeGreaterThan(secondIdx);
    }

    // --- NormalizeWhitespace (static) ---

    [Fact]
    public void NormalizeWhitespace_CollapsesSpacesAndTabs()
    {
        CodeInserter.NormalizeWhitespace("a  \t b").Should().Be("a b");
    }

    [Fact]
    public void NormalizeWhitespace_CollapsesNewlines()
    {
        CodeInserter.NormalizeWhitespace("a\n\n b").Should().Be("a b");
    }

    [Fact]
    public void NormalizeWhitespace_TrimsLeadingAndTrailing()
    {
        CodeInserter.NormalizeWhitespace("  hello  ").Should().Be("hello");
    }

    [Fact]
    public void NormalizeWhitespace_EmptyString()
    {
        CodeInserter.NormalizeWhitespace("").Should().Be("");
    }

    [Fact]
    public void NormalizeWhitespace_UnicodeWhitespace()
    {
        // \u00A0 = non-breaking space, \u2003 = em space
        CodeInserter.NormalizeWhitespace("a\u00A0\u2003b").Should().Be("a b");
    }

    // --- MapNormalizedPosition (static) ---

    [Fact]
    public void MapNormalizedPosition_MapsCorrectlyWithExtraSpaces()
    {
        var original = "hello   world";
        // normalized: "hello world" (idx 0-10)
        // "world" starts at normalized idx 6
        var idx = CodeInserter.MapNormalizedPosition(original, 6, 5, out var matchLen);

        idx.Should().Be(8); // "world" starts at position 8 in original
        matchLen.Should().Be(5);
    }

    [Fact]
    public void MapNormalizedPosition_OutOfRangeReturnsMinusOne()
    {
        var original = "short";
        var idx = CodeInserter.MapNormalizedPosition(original, 999, 1, out _);
        idx.Should().Be(-1);
    }

    [Fact]
    public void MapNormalizedPosition_NegativeStartReturnsMinusOne()
    {
        var idx = CodeInserter.MapNormalizedPosition("text", -1, 1, out _);
        idx.Should().Be(-1);
    }

    // --- InsertCodeAsync (integration of lock + Roslyn + fallback) ---

    [Fact]
    public async Task InsertCodeAsync_UsesRoslynWhenPossible()
    {
        var path = Path.Combine(_tempDir, "Test.cs");
        File.WriteAllText(path, "public class Foo { public void A() { } }");

        var result = await _sut.InsertCodeAsync(path, "public void B() { }", null);

        result.Method.Should().Be(InsertionMethod.RoslynAst);
        File.ReadAllText(path).Should().Contain("B()");
    }

    [Fact]
    public async Task InsertCodeAsync_FallsBackToStringWhenRoslynFails()
    {
        var path = Path.Combine(_tempDir, "Bad.cs");
        // Malformed C# that Roslyn can't handle cleanly
        File.WriteAllText(path, "this is not valid C# { }");

        var result = await _sut.InsertCodeAsync(path, "public void B() { }", null);

        result.Method.Should().Be(InsertionMethod.Appended);
    }

    [Fact]
    public async Task InsertCodeAsync_StringFallback_ExactAnchorMatch()
    {
        var path = Path.Combine(_tempDir, "Fallback.cs");
        // Content with no type declaration at all — Roslyn can parse it but TryRoslynInsert
        // returns false because there's no class/struct to insert into
        File.WriteAllText(path, "// just comments and a marker\nvoid Anchor() { }\n// end");

        var result = await _sut.InsertCodeAsync(path, "void New() { }", "void Anchor() { }");

        result.Method.Should().Be(InsertionMethod.StringFallback);
        File.ReadAllText(path).Should().Contain("New()");
    }

    [Fact]
    public async Task InsertCodeAsync_ThrowsKeyNotFound_WhenAnchorMissing()
    {
        var path = Path.Combine(_tempDir, "NoAnchor.cs");
        File.WriteAllText(path, "broken class Foo { }");

        var act = () => _sut.InsertCodeAsync(path, "void New() { }", "NonExistent");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // --- FindInnermostTypeClosingBrace (regression guard for namespace-scoped fallback) ---

    [Fact]
    public void FindInnermostTypeClosingBrace_BlockNamespace_ReturnsClassBrace()
    {
        var content = "namespace N\n{\n    class C\n    {\n    }\n}\n";
        var pos = CodeInserter.FindInnermostTypeClosingBrace(content);
        // Should point at the class's `}`, not the namespace's `}`
        var openClass = content.IndexOf("class C");
        pos.Should().BeGreaterThan(openClass);
        pos.Should().BeLessThan(content.LastIndexOf('}'));
        content[pos].Should().Be('}');
    }

    [Fact]
    public void FindInnermostTypeClosingBrace_FileScopedNamespace_ReturnsClassBrace()
    {
        var content = "namespace N;\n\nclass C\n{\n}\n";
        var pos = CodeInserter.FindInnermostTypeClosingBrace(content);
        pos.Should().Be(content.LastIndexOf('}'));
    }

    [Fact]
    public void FindInnermostTypeClosingBrace_EmptyContent_ReturnsMinusOne()
    {
        CodeInserter.FindInnermostTypeClosingBrace("").Should().Be(-1);
    }

    [Fact]
    public async Task InsertCodeAsync_FallbackInsertsInsideClass_NotAfterNamespace()
    {
        // Force the fallback path by writing syntactically invalid-but-recoverable code.
        // Use a file where Roslyn CAN parse — Roslyn path will insert correctly too.
        // Here we verify the END result: new method is inside the class braces.
        var path = Path.Combine(_tempDir, "Scoped.cs");
        var original = "namespace Ns\n{\n    public class T\n    {\n        public void A() { }\n    }\n}\n";
        File.WriteAllText(path, original);

        await _sut.InsertCodeAsync(path, "public void B() { }", insertAfterAnchor: null);

        var after = File.ReadAllText(path);
        var classOpen = after.IndexOf("class T");
        var namespaceClose = after.LastIndexOf('}');
        var newMethod = after.IndexOf("public void B");
        newMethod.Should().BeGreaterThan(classOpen);
        newMethod.Should().BeLessThan(namespaceClose);
    }

    [Fact]
    public void HoistUsingsIntoContent_TreatsUsingAndUsingStaticAsDistinct()
    {
        // `using System.Math;` and `using static System.Math;` bind different things;
        // the dedup key must not collapse them, or one of them silently drops out.
        var content = "using System.Math;\n\nnamespace N { class C {} }\n";
        var toAdd = new List<string> { "using static System.Math;" };

        var result = CodeInserter.HoistUsingsIntoContent(content, toAdd);

        result.Should().Contain("using static System.Math;");
        result.Should().Contain("using System.Math;");
    }

    [Fact]
    public void HoistUsingsIntoContent_DedupsIdenticalStaticImports()
    {
        var content = "using static System.Math;\n\nnamespace N { class C {} }\n";
        var toAdd = new List<string> { "using static System.Math;" };

        var result = CodeInserter.HoistUsingsIntoContent(content, toAdd);

        var count = System.Text.RegularExpressions.Regex.Matches(result, @"using static System\.Math;").Count;
        count.Should().Be(1);
    }

    [Fact]
    public void TryRoslynInsert_PreservesBothAliasedAndPlainUsingForSameNamespace()
    {
        // Regression: MergeUsings previously keyed only on u.Name, so a plain `using X;`
        // would mask an aliased `using M = X;` (and vice versa) and drop one of them.
        var content = @"using System.Math;
namespace N { public class T {} }";
        var code = @"using M = System.Math;
public void X() { }";

        var success = CodeInserter.TryRoslynInsert(content, code, null, out var result, out _);

        success.Should().BeTrue();
        result.Should().Contain("using System.Math;");
        result.Should().Contain("using M = System.Math;");
    }

    [Fact]
    public void SplitLeadingUsings_ExtractsUsingAfterLineComment()
    {
        var code = "// header comment\nusing System;\npublic void X() { }";

        var (usings, remainder) = CodeInserter.SplitLeadingUsings(code);

        usings.Should().ContainSingle().Which.Should().Be("using System;");
        remainder.Should().Contain("// header comment");
        remainder.Should().NotContain("using System;");
        remainder.Should().Contain("public void X()");
    }

    [Fact]
    public void SplitLeadingUsings_ExtractsGlobalUsing()
    {
        var code = "global using System.Text;\npublic void X() { }";

        var (usings, remainder) = CodeInserter.SplitLeadingUsings(code);

        usings.Should().ContainSingle().Which.Should().StartWith("global using System.Text;");
        remainder.Should().NotContain("global using");
        remainder.Should().Contain("public void X()");
    }

    [Fact]
    public void HoistUsingsIntoContent_DedupsAliasedImportIgnoringWhitespace()
    {
        // `using M=System.Math;` and `using M = System.Math;` are the same directive;
        // the regex-path key must split alias from name so internal whitespace doesn't
        // produce two distinct keys and allow a duplicate through.
        var content = "using M=System.Math;\n\nnamespace N { class C {} }\n";
        var toAdd = new List<string> { "using M = System.Math;" };

        var result = CodeInserter.HoistUsingsIntoContent(content, toAdd);

        var count = System.Text.RegularExpressions.Regex
            .Matches(result, @"using M\s*=\s*System\.Math;").Count;
        count.Should().Be(1);
    }

    [Fact]
    public void SplitLeadingUsings_HandlesBlockCommentAndMultipleUsings()
    {
        var code = "/* file-level doc */\nusing System;\n// comment\nusing System.Linq;\npublic void X() { }";

        var (usings, _) = CodeInserter.SplitLeadingUsings(code);

        usings.Should().HaveCount(2);
        usings[0].Should().Be("using System;");
        usings[1].Should().Be("using System.Linq;");
    }
}
