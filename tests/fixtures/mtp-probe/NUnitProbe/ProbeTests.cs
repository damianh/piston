using NUnit.Framework;

namespace NUnitProbe;

public sealed class AlphaTests
{
    [Test]
    [Category("probe-category")]
    public void SameName() { }

    [TestCase(1)]
    [TestCase(2)]
    public void Parameterized(int value) => Assert.That(value, Is.GreaterThan(0));

    [TestCase(1, TestName = "Custom visible name")]
    public void CustomName(int value) { }

    [Test]
    public void AssertionFailure() => Assert.That(1, Is.EqualTo(2));

    [Test]
    public void ThrownException() => throw new InvalidOperationException("probe exception");

    [Test]
    public void WritesOutput() => Console.WriteLine("probe output");

    [Test]
    [Ignore("probe skip reason")]
    public void Skipped() { }
}

public sealed class BetaTests
{
    [Test]
    public void SameName() { }
}
