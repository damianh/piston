using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MSTestProbe;

[TestClass]
public sealed class AlphaTests
{
    [TestMethod]
    [TestCategory("probe-category")]
    public void SameName() { }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void Parameterized(int value) => Assert.IsTrue(value > 0);

    [TestMethod(DisplayName = "Custom visible name")]
    public void CustomName() { }

    [TestMethod]
    public void AssertionFailure()
    {
        var expected = 2;
        Assert.AreEqual(expected, 1);
    }

    [TestMethod]
    public void ThrownException() => throw new InvalidOperationException("probe exception");

    [TestMethod]
    public void WritesOutput() => Console.WriteLine("probe output");

    [TestMethod]
    [Ignore("probe skip reason")]
    public void Skipped() { }
}

[TestClass]
public sealed class BetaTests
{
    [TestMethod]
    public void SameName() { }
}
