using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace MSTestSample;

[TestClass]
public sealed class SampleTests
{
    [TestMethod]
    public void Passes() => Assert.AreEqual(2, Add(1, 1));

    [TestMethod]
    public void Fails() => Assert.AreEqual(3, Add(1, 1), "Deliberate failure for the smoke test.");

    [TestMethod]
    [Ignore("Deliberately skipped for the smoke test.")]
    public void Skipped() { }

    private static int Add(int a, int b) => a + b;
}
