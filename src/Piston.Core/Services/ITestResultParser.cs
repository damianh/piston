using Piston.Core.Models;

namespace Piston.Core.Services;

public interface ITestResultParser
{
    IReadOnlyList<TestSuite> Parse(string trxFilePath);
}
