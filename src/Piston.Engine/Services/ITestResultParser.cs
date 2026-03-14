using Piston.Engine.Models;

namespace Piston.Engine.Services;

public interface ITestResultParser
{
    IReadOnlyList<TestSuite> Parse(string trxFilePath);
}
