using Xunit;

namespace XUnitV3Sample;

public sealed class SampleTests
{
    [Fact]
    public void Passes() => Assert.Equal(2, Add(1, 1));

    [Fact]
    public void Fails() => Assert.Equal(3, Add(1, 1));

    [Fact(Skip = "Deliberately skipped for the smoke test.")]
    public void Skipped() { }

    private static int Add(int a, int b) => a + b;
}
