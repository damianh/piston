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

}
