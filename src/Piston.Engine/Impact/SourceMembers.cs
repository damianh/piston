// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Piston.Engine.Impact;

/// <summary>A C# member with a stable syntax-derived identity and token hash.</summary>
internal sealed record SourceMember(
    string Key,
    string BodyHash,
    string TextHash,
    LineSpan Lines,
    IReadOnlyList<int> TokenLines);

/// <summary>Member identities and the non-member syntax of one C# source file.</summary>
internal sealed class SourceMembers
{
    private SourceMembers(IReadOnlyList<SourceMember> members, string residualHash)
    {
        Members = members;
        ResidualHash = residualHash;
    }

    public IReadOnlyList<SourceMember> Members { get; }

    public string ResidualHash { get; }

    public bool HasSameCode(SourceMembers other) =>
        string.Equals(ResidualHash, other.ResidualHash, StringComparison.Ordinal)
        && Members.Count == other.Members.Count
        && Members.OrderBy(m => m.Key, StringComparer.Ordinal)
            .Zip(other.Members.OrderBy(m => m.Key, StringComparer.Ordinal))
            .All(pair => pair.First.Key == pair.Second.Key && pair.First.BodyHash == pair.Second.BodyHash);

    public static SourceMembers Parse(string content)
    {
        var tree = CSharpSyntaxTree.ParseText(content);
        var root = tree.GetRoot();
        var sourceText = tree.GetText();
        var declarations = root.DescendantNodes()
            .Where(IsMemberDeclaration)
            .Where(node => !HasMemberAncestor(node))
            .Select(node => (Node: node, Key: GetKey(node)))
            .Where(item => item.Key is not null)
            .ToList();
        var duplicateKeys = declarations
            .GroupBy(item => item.Key!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var members = declarations
            .Where(item => !duplicateKeys.Contains(item.Key!))
            .Select(item =>
            {
                var node = item.Node;
                var startLine = sourceText.Lines.GetLineFromPosition(node.SpanStart).LineNumber + 1;
                var endLine = sourceText.Lines.GetLineFromPosition(Math.Max(node.SpanStart, node.Span.End - 1)).LineNumber + 1;
                var exactText = sourceText.ToString(node.Span);
                var tokenLines = node.DescendantTokens()
                    .Where(token => !token.IsMissing && !token.IsKind(SyntaxKind.EndOfFileToken))
                    .Select(token => sourceText.Lines.GetLineFromPosition(token.SpanStart).LineNumber + 1)
                    .ToArray();
                return new SourceMember(
                    item.Key!,
                    HashNode(node),
                    Hash(exactText),
                    new LineSpan(startLine, endLine),
                    tokenLines);
            })
            .ToList();
        var memberSpans = declarations
            .Where(item => !duplicateKeys.Contains(item.Key!))
            .Select(item => item.Node.Span)
            .OrderBy(span => span.Start)
            .ToArray();
        var residualItems = root.DescendantTokens()
            .Where(token => !token.IsMissing && !token.IsKind(SyntaxKind.EndOfFileToken))
            .Where(token => !Contains(memberSpans, token.Span))
            .Select(token => (Start: token.SpanStart, Text: $"{token.RawKind}:{token.Text}"))
            .Concat(root.DescendantTrivia(descendIntoTrivia: true)
                .Where(IsSignificantTrivia)
                .Where(trivia => !Contains(memberSpans, trivia.Span))
                .Select(trivia => (Start: trivia.SpanStart, Text: $"{trivia.RawKind}:{trivia.ToFullString()}")))
            .OrderBy(item => item.Start)
            .Select(item => item.Text);
        var initializerOrder = members
            .Where(member => IsInitializerMember(declarations, member.Key))
            .Select(member => member.Key);
        var residual = string.Join("\n", residualItems) + "\n--initializers--\n" + string.Join("\n", initializerOrder);
        return new SourceMembers(members, Hash(residual));
    }

    public SourceMember? MemberAt(int line) => Members
        .Where(member => member.Lines.Start <= line && line <= member.Lines.End)
        .OrderBy(member => member.Lines.End - member.Lines.Start)
        .FirstOrDefault();

    public IReadOnlyList<LineSpan> RemapUnchangedSpans(SourceMembers current, IEnumerable<LineSpan> spans)
    {
        var currentByKey = current.Members.ToDictionary(member => member.Key, StringComparer.Ordinal);
        var lineMap = new Dictionary<int, HashSet<int>>();
        foreach (var oldMember in Members)
        {
            if (currentByKey.TryGetValue(oldMember.Key, out var newMember)
                && oldMember.BodyHash == newMember.BodyHash
                && oldMember.TokenLines.Count == newMember.TokenLines.Count)
            {
                for (var index = 0; index < oldMember.TokenLines.Count; index++)
                {
                    if (!lineMap.TryGetValue(oldMember.TokenLines[index], out var mappedTargets))
                    {
                        lineMap[oldMember.TokenLines[index]] = mappedTargets = [];
                    }

                    mappedTargets.Add(newMember.TokenLines[index]);
                }
            }
        }

        var mapped = new List<LineSpan>();
        foreach (var span in spans)
        {
            var mappedLines = lineMap
                .Where(entry => span.Start <= entry.Key && entry.Key <= span.End)
                .SelectMany(entry => entry.Value)
                .ToArray();
            if (mappedLines.Length > 0)
            {
                mapped.Add(new LineSpan(mappedLines.Min(), mappedLines.Max()));
            }
        }

        return LineSpan.Normalize(mapped);
    }

    public IReadOnlyDictionary<int, IReadOnlyList<int>> MapLinesTo(SourceMembers current)
    {
        var currentByKey = current.Members.ToDictionary(member => member.Key, StringComparer.Ordinal);
        var mapping = new Dictionary<int, HashSet<int>>();
        foreach (var oldMember in Members)
        {
            if (!currentByKey.TryGetValue(oldMember.Key, out var newMember)
                || oldMember.BodyHash != newMember.BodyHash
                || oldMember.TokenLines.Count != newMember.TokenLines.Count)
            {
                continue;
            }

            for (var index = 0; index < oldMember.TokenLines.Count; index++)
            {
                if (!mapping.TryGetValue(oldMember.TokenLines[index], out var newLines))
                {
                    mapping[oldMember.TokenLines[index]] = newLines = [];
                }

                newLines.Add(newMember.TokenLines[index]);
            }
        }

        return mapping.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<int>)[.. pair.Value.Order()]);
    }

    private static bool IsMemberDeclaration(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax => true,
        PropertyDeclarationSyntax => true,
        IndexerDeclarationSyntax => true,
        EventDeclarationSyntax => true,
        FieldDeclarationSyntax => true,
        EventFieldDeclarationSyntax => true,
        _ => false,
    };

    private static bool HasMemberAncestor(SyntaxNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (IsMemberDeclaration(parent))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetKey(SyntaxNode node)
    {
        if (node is MethodDeclarationSyntax { Body: null, ExpressionBody: null } partialMethod
            && partialMethod.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            return null;
        }

        var owner = OwnerKey(node);
        return node switch
        {
            MethodDeclarationSyntax method => $"{owner}.{method.Identifier.ValueText}`{method.TypeParameterList?.Parameters.Count ?? 0}({Parameters(method.ParameterList.Parameters)})",
            ConstructorDeclarationSyntax constructor => $"{owner}.{(constructor.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor")}({Parameters(constructor.ParameterList.Parameters)})",
            DestructorDeclarationSyntax destructor => $"{owner}.Finalize()",
            OperatorDeclarationSyntax op => $"{owner}.operator {op.OperatorToken.Text}({Parameters(op.ParameterList.Parameters)})",
            ConversionOperatorDeclarationSyntax conversion => $"{owner}.operator {conversion.Type}({Parameters(conversion.ParameterList.Parameters)})",
            PropertyDeclarationSyntax property => $"{owner}.{property.Identifier.ValueText}",
            IndexerDeclarationSyntax indexer => $"{owner}.this[{Parameters(indexer.ParameterList.Parameters)}]",
            EventDeclarationSyntax evt => $"{owner}.event {evt.Identifier.ValueText}",
            FieldDeclarationSyntax field => string.Join("|", field.Declaration.Variables.Select(variable => $"{owner}.field {variable.Identifier.ValueText}")),
            EventFieldDeclarationSyntax evtField => string.Join("|", evtField.Declaration.Variables.Select(variable => $"{owner}.event {variable.Identifier.ValueText}")),
            _ => null,
        };
    }

    private static string OwnerKey(SyntaxNode node)
    {
        var types = node.Ancestors().Where(IsTypeDeclaration).Reverse().Select(TypeKey);
        var namespaces = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(declaration => declaration.Name.ToString());
        var prefix = string.Join(".", namespaces);
        if (prefix.Length > 0)
        {
            prefix += ".";
        }
        return prefix + string.Join("+", types);
    }

    private static bool IsTypeDeclaration(SyntaxNode node) => node is BaseTypeDeclarationSyntax;

    private static string TypeKey(SyntaxNode node)
    {
        var identifier = node switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            _ => node.ToString(),
        };
        var arity = node switch
        {
            TypeDeclarationSyntax type => type.TypeParameterList?.Parameters.Count ?? 0,
            _ => 0,
        };
        return arity == 0 ? identifier : $"{identifier}`{arity}";
    }

    private static string Parameters(SeparatedSyntaxList<ParameterSyntax> parameters) => string.Join(",", parameters.Select(parameter =>
        $"{string.Join(" ", parameter.Modifiers.Select(modifier => modifier.Text))}{parameter.Type?.WithoutTrivia().ToString() ?? "?"}"));

    private static bool IsInitializerMember(IEnumerable<(SyntaxNode Node, string? Key)> declarations, string key) =>
        declarations.Any(item => item.Key == key && item.Node switch
        {
            FieldDeclarationSyntax field => field.Declaration.Variables.Any(variable => variable.Initializer is not null),
            PropertyDeclarationSyntax property => property.Initializer is not null,
            _ => false,
        });

    private static string HashNode(SyntaxNode node)
    {
        var parts = node.DescendantTokens()
            .Where(token => !token.IsMissing && !token.IsKind(SyntaxKind.EndOfFileToken))
            .Select(token => (Start: token.SpanStart, Text: $"{token.RawKind}:{token.Text}"))
            .Concat(node.DescendantTrivia(descendIntoTrivia: true)
                .Where(IsSignificantTrivia)
                .Select(trivia => (Start: trivia.SpanStart, Text: $"{trivia.RawKind}:{trivia.ToFullString()}")))
            .OrderBy(item => item.Start)
            .Select(item => item.Text);
        return Hash(string.Join("\n", parts));
    }

    private static bool IsSignificantTrivia(SyntaxTrivia trivia) =>
        trivia.IsDirective
        || trivia.IsKind(SyntaxKind.DisabledTextTrivia)
        || trivia.IsKind(SyntaxKind.SkippedTokensTrivia)
        || trivia.GetStructure() is DocumentationCommentTriviaSyntax;

    private static bool Contains(IReadOnlyList<TextSpan> spans, TextSpan candidate)
    {
        var low = 0;
        var high = spans.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var span = spans[middle];
            if (candidate.Start < span.Start)
            {
                high = middle - 1;
            }
            else if (candidate.End > span.End)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
