// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Xml.Linq;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

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

        // Build a lookup: testId -> (className, method name) from TestDefinitions
        var methods = doc
            .Descendants(ns + "UnitTest")
            .Select(ut => new
            {
                Id = (string?)ut.Attribute("id") ?? string.Empty,
                Method = ut.Element(ns + "TestMethod"),
            })
            .Where(x => !string.IsNullOrEmpty(x.Id))
            .ToDictionary(
                x => x.Id,
                x => ((string?)x.Method?.Attribute("className") ?? string.Empty, (string?)x.Method?.Attribute("name")));

        // Parse UnitTestResult elements
        var deploymentRoot = (string?)doc.Root?.Element(ns + "TestSettings")
            ?.Element(ns + "Deployment")?.Attribute("runDeploymentRoot");
        var results = doc
            .Descendants(ns + "UnitTestResult")
            .Select(r => ParseTestResult(r, methods, ns, trxFilePath, deploymentRoot))
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
        Dictionary<string, (string ClassName, string? MethodName)> methods,
        XNamespace ns,
        string trxFilePath,
        string? deploymentRoot)
    {
        var testId = (string?)r.Attribute("testId") ?? string.Empty;
        var testName = (string?)r.Attribute("testName") ?? string.Empty;
        var outcome = (string?)r.Attribute("outcome") ?? string.Empty;
        var durationStr = (string?)r.Attribute("duration") ?? "0";

        var (className, methodName) = methods.GetValueOrDefault(testId, (string.Empty, null));
        // Same identity live collector events resolve to (see TestIdentity).
        var fullyQualifiedName = TestIdentity.Qualify(className, testName);

        var status = outcome switch
        {
            "Passed" => TestStatus.Passed,
            "Failed" => TestStatus.Failed,
            "NotExecuted" => TestStatus.Skipped,
            _ => TestStatus.NotRun,
        };

        var duration = TimeSpan.TryParse(durationStr, out var d) ? d : TimeSpan.Zero;

        var output = r.Element(ns + "Output");
        var capturedOutput = string.Join(Environment.NewLine,
            new[] { "StdOut", "DebugTrace" }
                .Select(name => ((string?)output?.Element(ns + name))?.Trim())
                .Where(text => !string.IsNullOrEmpty(text)));
        var capturedErrorOutput = ((string?)output?.Element(ns + "StdErr"))?.Trim();
        var errorMessage = (string?)output?.Element(ns + "ErrorInfo")?.Element(ns + "Message");
        var stackTrace = (string?)output?.Element(ns + "ErrorInfo")?.Element(ns + "StackTrace");
        var resultsDirectory = Path.GetDirectoryName(Path.GetFullPath(trxFilePath))!;
        var relativeResultsDirectory = (string?)r.Attribute("relativeResultsDirectory");
        if (!string.IsNullOrWhiteSpace(deploymentRoot) && !string.IsNullOrWhiteSpace(relativeResultsDirectory))
        {
            resultsDirectory = Path.GetFullPath(
                Path.Combine(deploymentRoot, "In", relativeResultsDirectory), resultsDirectory);
        }

        var attachments = r.Element(ns + "ResultFiles")?.Elements(ns + "ResultFile")
            .Select(file => (string?)file.Attribute("path"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new TestAttachment(
                Path.IsPathFullyQualified(path!)
                    ? Path.GetFullPath(path!)
                    : Path.GetFullPath(path!, resultsDirectory),
                Path.GetFileName(path!)))
            .ToArray() ?? [];

        return new TestResult(
            FullyQualifiedName: fullyQualifiedName,
            DisplayName: testName,
            Status: status,
            Duration: duration,
            Output: capturedOutput.Length == 0
                ? null
                : RetentionLimits.TruncateHeadTail(capturedOutput, RetentionLimits.MaxTestTextLength),
            ErrorMessage: string.IsNullOrWhiteSpace(errorMessage)
                ? null
                : RetentionLimits.TruncateHead(errorMessage.Trim(), RetentionLimits.MaxTestTextLength),
            StackTrace: string.IsNullOrWhiteSpace(stackTrace)
                ? null
                : RetentionLimits.TruncateHead(stackTrace.Trim(), RetentionLimits.MaxTestTextLength),
            Source: null
        )
        {
            // The logger records the test case's fully qualified name less its class as the method name.
            Method = string.IsNullOrEmpty(methodName) ? null : TestIdentity.Qualify(className, methodName),
            ErrorOutput = string.IsNullOrWhiteSpace(capturedErrorOutput)
                ? null
                : RetentionLimits.TruncateHeadTail(capturedErrorOutput, RetentionLimits.MaxTestTextLength),
            Attachments = attachments,
        };
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, out var dt) ? dt : null;
    }
}
