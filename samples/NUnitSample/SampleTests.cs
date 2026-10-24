using NUnit.Framework;

namespace NUnitSample;

public sealed class SampleTests
{
    [Test]
    public void Passes() => Assert.That(Add(1, 1), Is.EqualTo(2));

    [Test]
    public void Fails() => Assert.That(Add(1, 1), Is.EqualTo(3), "Deliberate failure for the smoke test.");

    [Test]
    [Ignore("Deliberately skipped for the smoke test.")]
    public void Skipped() { }

    private static int Add(int a, int b) => a + b;
}
