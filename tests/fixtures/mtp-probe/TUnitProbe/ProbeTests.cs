namespace TUnitProbe;

public sealed class AlphaTests
{
    [Test]
    [Category("probe-category")]
    public void SameName() { }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Parameterized(int value) => await Assert.That(value).IsGreaterThan(0);

    [Test]
    [Arguments(1)]
    public void CustomName(int value) { }

    [Test]
    public async Task AssertionFailure()
    {
        var actual = 1;
        var expected = 2;
        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public void ThrownException() => throw new InvalidOperationException("probe exception");

    [Test]
    public void WritesOutput() => Console.WriteLine("probe output");

    [Test]
    [Skip("probe skip reason")]
    public void Skipped() { }
}

public sealed class BetaTests
{
    [Test]
    public void SameName() { }
}
