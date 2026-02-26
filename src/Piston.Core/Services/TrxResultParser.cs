using System.Xml.Linq;
using Piston.Core.Models;

namespace Piston.Core.Services;

/// <summary>
/// Parses a single TRX (VSTest XML) file into a <see cref="TestSuite"/>.
/// TRX schema reference: https://github.com/microsoft/vstest/blob/main/src/Microsoft.TestPlatform.Extensions.TrxLogger/TRX.xsd
/// </summary>
public sealed class TrxResultParser : ITestResultParser
{
    // TRX files use a default namespace; we strip it for simpler XPath-style access.
    private const string TrxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public IReadOnlyList<TestSuite> Parse(string trxFilePath)
    {
        var doc = XDocument.Load(trxFilePath);
        XNamespace ns = TrxNamespace;

        // Build a lookup: testId -> className from TestDefinitions
        var classNames = doc
            .Descendants(ns + "UnitTest")
            .Select(ut => new
            {
                Id = (string?)ut.Attribute("id") ?? string.Empty,
                ClassName = (string?)ut.Element(ns + "TestMethod")?.Attribute("className") ?? string.Empty,
            })
            .Where(x => !string.IsNullOrEmpty(x.Id))
            .ToDictionary(x => x.Id, x => x.ClassName);

        // Parse UnitTestResult elements
        var results = doc
            .Descendants(ns + "UnitTestResult")
            .Select(r => ParseTestResult(r, classNames, ns))
            .ToList();

        // Prefer the TestRun/@name attribute (e.g. "MyProject.Tests@machine 2024-01-01 …")
        // which contains the assembly name. Strip everything from '@' onward, then
        // fall back to the TRX filename if the attribute is absent or empty.
        var runName = (string?)doc.Root?.Attribute("name") ?? string.Empty;
        var atIndex = runName.IndexOf('@');
        var suiteName = atIndex > 0
            ? runName[..atIndex].Trim()
            : !string.IsNullOrWhiteSpace(runName)
                ? runName.Trim()
                : Path.GetFileNameWithoutExtension(trxFilePath);

        // Parse suite-level timestamp and total duration from Times element
        var timesEl = doc.Descendants(ns + "Times").FirstOrDefault();
        var startTime = ParseDateTimeOffset(timesEl?.Attribute("start")?.Value);
        var finishTime = ParseDateTimeOffset(timesEl?.Attribute("finish")?.Value);
        var totalDuration = (startTime.HasValue && finishTime.HasValue)
            ? finishTime.Value - startTime.Value
            : results.Aggregate(TimeSpan.Zero, (acc, r) => acc + r.Duration);

        return [new TestSuite(suiteName, results, startTime ?? DateTimeOffset.UtcNow, totalDuration)];
    }

    private static TestResult ParseTestResult(
        XElement r,
        Dictionary<string, string> classNames,
        XNamespace ns)
    {
        var testId = (string?)r.Attribute("testId") ?? string.Empty;
        var testName = (string?)r.Attribute("testName") ?? string.Empty;
        var outcome = (string?)r.Attribute("outcome") ?? string.Empty;
        var durationStr = (string?)r.Attribute("duration") ?? "0";

        var className = classNames.GetValueOrDefault(testId, string.Empty);
        var fullyQualifiedName = string.IsNullOrEmpty(className)
            ? testName
            : $"{className}.{testName}";

        var status = outcome switch
        {
            "Passed" => TestStatus.Passed,
            "Failed" => TestStatus.Failed,
            "NotExecuted" => TestStatus.Skipped,
            _ => TestStatus.NotRun,
        };

        var duration = TimeSpan.TryParse(durationStr, out var d) ? d : TimeSpan.Zero;

        var output = r.Element(ns + "Output");
        var stdOut = (string?)output?.Element(ns + "StdOut");
        var errorMessage = (string?)output?.Element(ns + "ErrorInfo")?.Element(ns + "Message");
        var stackTrace = (string?)output?.Element(ns + "ErrorInfo")?.Element(ns + "StackTrace");

        return new TestResult(
            FullyQualifiedName: fullyQualifiedName,
            DisplayName: testName,
            Status: status,
            Duration: duration,
            Output: string.IsNullOrWhiteSpace(stdOut) ? null : stdOut.Trim(),
            ErrorMessage: string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim(),
            StackTrace: string.IsNullOrWhiteSpace(stackTrace) ? null : stackTrace.Trim(),
            Source: null
        );
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return DateTimeOffset.TryParse(value, out var dt) ? dt : null;
    }
}
