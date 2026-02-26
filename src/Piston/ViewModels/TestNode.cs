using Piston.Core.Models;

namespace Piston.ViewModels;

/// <summary>
/// Discriminated union for tree node kinds — stored in TreeNode.Tag.
/// </summary>
public abstract class TestNodeTag
{
    private TestNodeTag() { }

    /// <summary>Suite (test project) node.</summary>
    public sealed class Suite(TestSuite suite) : TestNodeTag
    {
        public TestSuite TestSuite { get; } = suite;
    }

    /// <summary>Namespace or class grouping node.</summary>
    public sealed class Group(string name) : TestNodeTag
    {
        public string Name { get; } = name;
    }

    /// <summary>Leaf node representing a single test method.</summary>
    public sealed class Test(TestResult result) : TestNodeTag
    {
        public TestResult Result { get; } = result;
    }
}
