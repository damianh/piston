using NUnit.Framework;

namespace NUnitSample;

public sealed class SampleTests
{
    [Test]
    public void NUnitPasses() => Assert.That(Add(1, 1), Is.EqualTo(2));

    [Test]
    public void NUnitFails() => Assert.That(Add(1, 1), Is.EqualTo(3), "Deliberate failure for the smoke test.");

    [Test]
    [Ignore("Deliberately skipped for the smoke test.")]
    public void NUnitSkipped() { }

    private static int Add(int a, int b) => a + b;
}
