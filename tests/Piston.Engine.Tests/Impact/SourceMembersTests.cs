// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Impact;
using Xunit;

namespace Piston.Engine.Tests.Impact;

public sealed class SourceMembersTests
{
    [Fact]
    public void Parse_WhitespaceAndCommentsDoNotChangeMemberBodyIdentity()
    {
        const string original = "namespace A.B { class C { int M(int value) { return value + 1; } } }";
        const string reformatted = """
            namespace A.B
            {
                class C
                {
                    // retained evidence
                    int M ( int value )
                    {
                        return value+1;
                    }
                }
            }
            """;

        var oldMember = Assert.Single(SourceMembers.Parse(original).Members);
        var newMember = Assert.Single(SourceMembers.Parse(reformatted).Members);

        Assert.Equal(oldMember.Key, newMember.Key);
        Assert.Equal(oldMember.BodyHash, newMember.BodyHash);
        Assert.NotEqual(oldMember.TextHash, newMember.TextHash);
    }

    [Fact]
    public void Parse_InsertingAnotherMethodAbovePreservesExistingMemberIdentityAndBody()
    {
        const string original = """
            class C
            {
                void M() { Run(); }
            }
            """;
        const string inserted = """
            class C
            {
                void Added() { }
                void M() { Run(); }
            }
            """;

        var oldMember = SourceMembers.Parse(original).Members.Single(member => member.Key.StartsWith("C.M`", StringComparison.Ordinal));
        var newMember = SourceMembers.Parse(inserted).Members.Single(member => member.Key == oldMember.Key);

        Assert.Equal(oldMember.BodyHash, newMember.BodyHash);
        Assert.True(newMember.Lines.Start > oldMember.Lines.Start);
    }

    [Fact]
    public void Parse_MethodRenameChangesIdentity()
    {
        var original = Assert.Single(SourceMembers.Parse("class C { void M() { Run(); } }").Members);
        var renamed = Assert.Single(SourceMembers.Parse("class C { void Renamed() { Run(); } }").Members);

        Assert.NotEqual(original.Key, renamed.Key);
    }

    [Fact]
    public void Parse_MovingMethodBetweenPartialClassPartsPreservesItsIdentity()
    {
        var first = Assert.Single(SourceMembers.Parse("namespace A { partial class C { void M() { Run(); } } }").Members);
        var second = Assert.Single(SourceMembers.Parse("""
            namespace A
            {
                // another file's partial declaration
                partial class C
                {
                    void M() { Run(); }
                }
            }
            """).Members);

        Assert.Equal(first.Key, second.Key);
        Assert.Equal(first.BodyHash, second.BodyHash);
    }

    [Fact]
    public void Parse_ReorderingEventInitializersChangesResidualIdentity()
    {
        const string original = """
            class C
            {
                event Action A = Create("A");
                event Action B = Create("B");
            }
            """;
        const string reordered = """
            class C
            {
                event Action B = Create("B");
                event Action A = Create("A");
            }
            """;

        var oldMembers = SourceMembers.Parse(original);
        var newMembers = SourceMembers.Parse(reordered);

        Assert.False(oldMembers.HasSameCode(newMembers));
        Assert.NotEqual(oldMembers.ResidualHash, newMembers.ResidualHash);
    }

    [Fact]
    public void RemapUnchangedSpans_MovedMethodsDoNotIncludeAnUnexecutedMethodBetweenThem()
    {
        const string original = "class C\n{\nvoid A() { }\nvoid B() { }\nvoid Cc() { }\n}";
        const string reordered = "class C\n{\nvoid A() { }\nvoid Cc() { }\nvoid B() { }\n}";

        var mapped = SourceMembers.Parse(original).RemapUnchangedSpans(SourceMembers.Parse(reordered), [new(3, 4)]);

        Assert.Equal([new LineSpan(3, 3), new LineSpan(5, 5)], mapped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MapCoverageLinesTo_SplitOrJoinedLinesAreNotReusable(bool reverse)
    {
        const string singleLine = "class C\n{\nint M(bool value) { if (value) return 1; return 2; }\n}";
        const string multipleLines = "class C\n{\nint M(bool value)\n{\nif (value) return 1;\nreturn 2;\n}\n}";
        var oldMembers = SourceMembers.Parse(reverse ? multipleLines : singleLine);
        var currentMembers = SourceMembers.Parse(reverse ? singleLine : multipleLines);

        Assert.True(oldMembers.HasSameCode(currentMembers));
        Assert.Empty(oldMembers.MapCoverageLinesTo(currentMembers));
    }

}
