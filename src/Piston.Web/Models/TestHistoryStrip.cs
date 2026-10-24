// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>Presentation helpers for a test's recorded results: status squares and a duration sparkline.</summary>
public static class TestHistoryStrip
{
    /// <summary>
    /// SVG polyline points plotting the durations of the passed and failed results, oldest on the left and the
    /// longest at the top; null when fewer than two results have a duration.
    /// </summary>
    public static string? SparklinePoints(IReadOnlyList<TestHistoryEntryDto> runs, double width, double height)
    {
        var durations = runs.Where(r => r.Status is "Passed" or "Failed").Select(r => r.DurationMs).ToList();
        if (durations.Count < 2)
        {
            return null;
        }

        var max = durations.Max();
        var min = durations.Min();
        var range = max - min;
        var step = width / (durations.Count - 1);
        return string.Join(' ', durations.Select((d, i) =>
        {
            // A flat history draws a line through the middle.
            var y = range <= 0 ? height / 2 : height - ((d - min) / range * height);
            return string.Create(CultureInfo.InvariantCulture, $"{i * step:0.#},{y:0.#}");
        }));
    }

    /// <summary>The Tailwind background class of a result's square.</summary>
    public static string SquareClass(string status) => status switch
    {
        "Passed" => "bg-green-500",
        "Failed" => "bg-red-500",
        _ => "bg-gray-600",
    };

    /// <summary>A short label for a failure kind, or null when the test is not failing.</summary>
    public static string? KindLabel(string? failureKind) => failureKind switch
    {
        "New" => "new failure",
        "PreExisting" => "failing before",
        "Flaky" => "flaky failure",
        "Unknown" => "no earlier result",
        _ => null,
    };
}
