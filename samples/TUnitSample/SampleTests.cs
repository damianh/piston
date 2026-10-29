namespace TUnitSample;

public sealed class SampleTests
{
    [Test]
    public async Task Passes() => await Assert.That(Add(1, 1)).IsEqualTo(2);

    [Test]
    public async Task Fails() => await Assert.That(Add(1, 1)).IsEqualTo(3);

    [Test]
    [Skip("Deliberately skipped for the smoke test.")]
    public void Skipped() { }

    private static int Add(int a, int b) => a + b;
}
