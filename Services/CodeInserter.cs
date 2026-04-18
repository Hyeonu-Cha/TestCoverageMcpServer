using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace CoverageMcpServer.Services;

public enum InsertionMethod { RoslynAst, StringFallback, StringFallbackNormalized, Appended }

public record InsertionResult(InsertionMethod Method, string Content);

public interface ICodeInserter
{
    Task<InsertionResult> InsertCodeAsync(string testFilePath, string codeToAppend, string? insertAfterAnchor);
}

public class CodeInserter : ICodeInserter
{
    private readonly IFileService _fileService;
    private readonly ILogger<CodeInserter> _logger;

    public CodeInserter(IFileService fileService, ILogger<CodeInserter> logger)
    {
        _fileService = fileService;
        _logger = logger;
    }

    public async Task<InsertionResult> InsertCodeAsync(string testFilePath, string codeToAppend, string? insertAfterAnchor)
    {
        InsertionResult? result = null;

        await _fileService.WithFileLockAsync(testFilePath, async () =>
        {
            var content = File.ReadAllText(testFilePath);

            if (TryRoslynInsert(content, codeToAppend, insertAfterAnchor, out var roslynResult, out var roslynError))
            {
                _fileService.AtomicWriteFile(testFilePath, roslynResult);
                result = new InsertionResult(InsertionMethod.RoslynAst, roslynResult);
                return;
            }

            _logger.LogDebug("Roslyn AST insertion failed for {File}, falling back to string-based: {Reason}",
                testFilePath, roslynError ?? "unknown");
            content = content.TrimEnd();
            result = InsertWithStringFallback(testFilePath, content, codeToAppend, insertAfterAnchor);
            await Task.CompletedTask;
        });

        return result!;
    }

    private InsertionResult InsertWithStringFallback(string testFilePath, string content, string codeToAppend, string? insertAfterAnchor)
    {
        // Strip and hoist leading `using` directives — otherwise the fallback would inject them
        // inside the class body, producing invalid C#.
        var (extractedUsings, memberCode) = SplitLeadingUsings(codeToAppend);
        if (extractedUsings.Count > 0)
            content = HoistUsingsIntoContent(content, extractedUsings);
        codeToAppend = memberCode;

        if (insertAfterAnchor != null)
        {
            var idx = content.LastIndexOf(insertAfterAnchor, StringComparison.Ordinal);

            if (idx < 0)
            {
                var normalizedAnchor = NormalizeWhitespace(insertAfterAnchor);
                var normalizedContent = NormalizeWhitespace(content);
                var normalizedIdx = normalizedContent.LastIndexOf(normalizedAnchor, StringComparison.Ordinal);

                if (normalizedIdx >= 0)
                {
                    idx = MapNormalizedPosition(content, normalizedIdx, normalizedAnchor.Length, out var matchLength);
                    if (idx >= 0)
                    {
                        var insertPos = idx + matchLength;
                        var newContent = content[..insertPos] + "\n\n" + codeToAppend.Trim() + "\n" + content[insertPos..] + "\n";
                        _fileService.AtomicWriteFile(testFilePath, newContent);
                        return new InsertionResult(InsertionMethod.StringFallbackNormalized, newContent);
                    }
                }

                throw new KeyNotFoundException(
                    $"Anchor not found (tried Roslyn AST, exact match, whitespace-normalized): \"{insertAfterAnchor}\"");
            }

            {
                var insertPos = idx + insertAfterAnchor.Length;
                var newContent = content[..insertPos] + "\n\n" + codeToAppend.Trim() + "\n" + content[insertPos..] + "\n";
                _fileService.AtomicWriteFile(testFilePath, newContent);
                return new InsertionResult(InsertionMethod.StringFallback, newContent);
            }
        }

        var bracePos = FindInnermostTypeClosingBrace(content);
        string appended;
        if (bracePos >= 0)
        {
            appended = content[..bracePos] + "\n" + codeToAppend.Trim() + "\n" + content[bracePos..] + "\n";
        }
        else
        {
            // No closing brace found (e.g. empty file or only file-scoped globals) — append at EOF.
            appended = content + "\n\n" + codeToAppend.Trim() + "\n";
        }
        _fileService.AtomicWriteFile(testFilePath, appended);
        return new InsertionResult(InsertionMethod.Appended, appended);
    }

    /// <summary>
    /// Finds the insertion point for a new class member when no anchor is provided.
    /// Walks back from EOF through consecutive closing braces (separated only by whitespace)
    /// and returns the position of the innermost one — i.e., the brace that closes the last
    /// type declaration. This keeps inserted code inside the class even when the file uses
    /// a block-scoped namespace. Returns -1 if no suitable brace is found.
    /// </summary>
    internal static int FindInnermostTypeClosingBrace(string content)
    {
        var i = content.Length - 1;
        while (i >= 0 && char.IsWhiteSpace(content[i])) i--;
        if (i < 0 || content[i] != '}') return -1;

        var outerBrace = i;
        var j = i - 1;
        while (j >= 0 && char.IsWhiteSpace(content[j])) j--;
        if (j >= 0 && content[j] == '}')
            return j; // inner type's closing brace inside a block-scoped namespace

        return outerBrace;
    }

    // Matches a single `using X;`, `using static X;`, `using alias = X;`, or `global using X;`
    // directive at the start of a string (after any leading whitespace/comment trivia). We strip
    // these out of codeToAppend before wrapping it in a class, since using directives are invalid
    // inside a class body and would otherwise fail the Roslyn parse.
    private static readonly Regex LeadingUsingRegex = new(
        @"^(?:global\s+)?using\s+(?:static\s+)?[\w.@=\s<>,]+;[^\S\r\n]*\r?\n?",
        RegexOptions.Compiled);

    internal static (List<string> Usings, string Remainder) SplitLeadingUsings(string code)
    {
        var usings = new List<string>();
        var remainder = code;
        while (true)
        {
            var triviaLen = ConsumeLeadingTrivia(remainder);
            var afterTrivia = remainder[triviaLen..];
            var match = LeadingUsingRegex.Match(afterTrivia);
            if (!match.Success || match.Index != 0) break;
            usings.Add(match.Value.Trim());
            // Splice out only the using, preserving leading trivia (comments/whitespace) so the
            // original structure of the remainder is not lost.
            remainder = remainder[..triviaLen] + afterTrivia[match.Length..];
        }
        return (usings, remainder);
    }

    // Advance over whitespace, line comments (//), and block comments (/* */) so SplitLeadingUsings
    // can find `using` directives that are preceded by documentation or header comments.
    private static int ConsumeLeadingTrivia(string s)
    {
        var i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/')
            {
                i += 2;
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                if (i + 1 < s.Length) i += 2; // consume closing */
                continue;
            }
            break;
        }
        return i;
    }

    internal static bool TryRoslynInsert(string content, string codeToAppend, string? insertAfterAnchor, out string result, out string? failureReason)
    {
        result = "";
        failureReason = null;
        try
        {
            var (extractedUsings, memberCode) = SplitLeadingUsings(codeToAppend);

            var tree = CSharpSyntaxTree.ParseText(content);
            var root = tree.GetCompilationUnitRoot();

            var targetType = root.DescendantNodes().OfType<TypeDeclarationSyntax>().LastOrDefault();
            if (targetType == null) { failureReason = "no type declaration found in file"; return false; }

            var wrapperTree = CSharpSyntaxTree.ParseText($"class __RoslynWrapper__ {{\n{memberCode}\n}}");
            var wrapperRoot = wrapperTree.GetCompilationUnitRoot();
            var wrapperType = wrapperRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (wrapperType == null || wrapperType.Members.Count == 0) { failureReason = "codeToAppend produced no parseable members"; return false; }

            var wrapperDiags = wrapperTree.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            if (wrapperDiags.Count > 0) { failureReason = $"codeToAppend has syntax errors: {wrapperDiags[0].GetMessage()}"; return false; }

            var newMembers = wrapperType.Members;
            var eol = content.Contains("\r\n") ? SyntaxFactory.CarriageReturnLineFeed : SyntaxFactory.LineFeed;
            var firstMember = newMembers[0];
            var leadingTrivia = SyntaxFactory.TriviaList(eol, eol);
            newMembers = newMembers.Replace(firstMember,
                firstMember.WithLeadingTrivia(leadingTrivia.AddRange(firstMember.GetLeadingTrivia())));

            TypeDeclarationSyntax updatedType;

            if (insertAfterAnchor != null)
            {
                var anchorMember = targetType.Members
                    .LastOrDefault(m => m.ToFullString().Contains(insertAfterAnchor, StringComparison.Ordinal));

                if (anchorMember == null)
                {
                    var normalizedAnchor = NormalizeWhitespace(insertAfterAnchor);
                    anchorMember = targetType.Members
                        .LastOrDefault(m => NormalizeWhitespace(m.ToFullString()).Contains(normalizedAnchor, StringComparison.Ordinal));
                }

                if (anchorMember == null) { failureReason = $"anchor not found in any class member: \"{insertAfterAnchor}\""; return false; }

                var insertIndex = targetType.Members.IndexOf(anchorMember) + 1;
                var membersList = targetType.Members.ToList();
                membersList.InsertRange(insertIndex, newMembers);
                updatedType = targetType.WithMembers(SyntaxFactory.List(membersList));
            }
            else
            {
                updatedType = targetType.AddMembers([.. newMembers]);
            }

            var newRoot = root.ReplaceNode(targetType, updatedType);

            if (extractedUsings.Count > 0)
                newRoot = MergeUsings(newRoot, extractedUsings);

            result = newRoot.ToFullString();
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"exception: {ex.Message}";
            return false;
        }
    }

    internal static string HoistUsingsIntoContent(string content, List<string> extractedUsings)
    {
        // Insert missing usings after the last existing `using ...;` line, or at the very top.
        // Dedup key distinguishes `static` flag and alias so `using X;`, `using static X;`,
        // and `using M = X;` are all kept as separate directives. Group 2 already captures
        // the alias body (`M = X`) because the char class includes `=`, so for the regex
        // path we feed the whole captured body as the "name" portion and leave alias empty.
        var existing = new HashSet<string>();
        foreach (Match m in Regex.Matches(content, @"^\s*using\s+(static\s+)?([\w.@=\s<>,]+);", RegexOptions.Multiline))
            existing.Add(UsingKey(m.Groups[1].Success, "", m.Groups[2].Value));

        var toAdd = new List<string>();
        foreach (var raw in extractedUsings)
        {
            var tokenMatch = Regex.Match(raw, @"^\s*(?:global\s+)?using\s+(static\s+)?([\w.@=\s<>,]+);");
            if (!tokenMatch.Success) continue;
            var key = UsingKey(tokenMatch.Groups[1].Success, "", tokenMatch.Groups[2].Value);
            if (existing.Add(key))
                toAdd.Add(raw);
        }
        if (toAdd.Count == 0) return content;

        var lastUsing = Regex.Matches(content, @"^\s*using\s+(?:static\s+)?[\w.@=\s<>,]+;\r?\n", RegexOptions.Multiline)
            .LastOrDefault();
        var block = string.Join("\n", toAdd) + "\n";

        if (lastUsing != null)
        {
            var insertAt = lastUsing.Index + lastUsing.Length;
            return content[..insertAt] + block + content[insertAt..];
        }
        return block + "\n" + content;
    }

    private static CompilationUnitSyntax MergeUsings(CompilationUnitSyntax root, List<string> extractedUsings)
    {
        var existing = root.Usings
            .Where(u => u.Name != null)
            .Select(RoslynUsingKey)
            .ToHashSet();

        var toAdd = new List<UsingDirectiveSyntax>();
        foreach (var raw in extractedUsings)
        {
            var parsed = SyntaxFactory.ParseCompilationUnit(raw).Usings.FirstOrDefault();
            if (parsed == null || parsed.Name == null) continue;
            if (existing.Add(RoslynUsingKey(parsed)))
                toAdd.Add(parsed);
        }

        return toAdd.Count == 0 ? root : root.AddUsings([.. toAdd]);
    }

    // Keys must distinguish (a) `using X;` from `using static X;` and
    // (b) `using X;` from `using M = X;`. u.Name is only the RHS, so aliased
    // and non-aliased imports collide unless we mix the alias identifier in.
    private static string RoslynUsingKey(UsingDirectiveSyntax u)
    {
        var isStatic = u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword);
        var alias = u.Alias?.Name.Identifier.Text ?? "";
        return UsingKey(isStatic, alias, u.Name?.ToString() ?? "");
    }

    private static string UsingKey(bool isStatic, string alias, string name) =>
        (isStatic ? "static " : "") + alias + "|" + name.Trim();

    internal static string NormalizeWhitespace(string input) =>
        Regex.Replace(input, @"\s+", " ").Trim();

    internal static int MapNormalizedPosition(string original, int normalizedStart, int normalizedLength, out int originalMatchLength)
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

        while (originalEnd + 1 < original.Length && char.IsWhiteSpace(original[originalEnd + 1])
               && (normalizedEnd + 1 >= map.Count || map[normalizedEnd + 1] != originalEnd + 1))
        {
            originalEnd++;
        }

        originalMatchLength = originalEnd - originalStart + 1;
        return originalStart;
    }

    private static List<int> BuildNormalizedToOriginalMap(string original)
    {
        var map = new List<int>();
        var inWhitespace = false;

        for (var i = 0; i < original.Length; i++)
        {
            if (char.IsWhiteSpace(original[i]))
            {
                if (!inWhitespace)
                {
                    map.Add(i);
                    inWhitespace = true;
                }
            }
            else
            {
                map.Add(i);
                inWhitespace = false;
            }
        }

        return map;
    }
}
