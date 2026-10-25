using Xunit;

namespace XUnitV3Sample;

public sealed class SampleTests
{
    [Fact]
    public void XUnitPasses() => Assert.Equal(2, Add(1, 1));

    [Fact]
    public void XUnitFails() => Assert.Equal(3, Add(1, 1));

    [Fact(Skip = "Deliberately skipped for the smoke test.")]
    public void XUnitSkipped() { }

    private static int Add(int a, int b) => a + b;
}
