// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;

namespace Piston.Engine.Execution;

/// <summary>
/// Machine-wide admission budget shared by all daemons using the same coordination directory.
/// Values are admission units: Piston does not cap the CPU or memory actually used by build and test
/// processes; it limits the declared reservations of concurrently admitted runs.
/// </summary>
/// <param name="CpuSlots">Total CPU slots that admitted runs may reserve.</param>
/// <param name="MemoryMb">Total memory reservation in MiB; null when memory is not budgeted.</param>
/// <param name="Source">The budget file path, or <c>default</c>.</param>
/// <param name="ContainerSlots">
/// How many container-using test projects may run at once across the machine; null when unlimited.
/// </param>
public sealed record ExecutionBudget(int CpuSlots, int? MemoryMb, string Source, int? ContainerSlots = null)
{
    public const string DefaultSource = "default";

    /// <summary>Default when no budget file exists: one CPU slot per logical processor, memory not budgeted.</summary>
    public static ExecutionBudget Default => new(Environment.ProcessorCount, null, DefaultSource);

    /// <summary>Loads <c>budget.json</c>; a missing file yields <see cref="Default"/>, an invalid file throws.</summary>
    public static ExecutionBudget Load(string path) => Load(path, Thread.Sleep);

    internal static ExecutionBudget Load(string path, Action<TimeSpan> delay)
    {
        JsonDocument document;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // Allow atomic replacement while this read handle is open, including on Windows.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                document = JsonDocument.Parse(reader.ReadToEnd());
                break;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return Default;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                if (attempt == 5)
                {
                    throw Invalid(path, ex.Message, ex);
                }

                // Truncate-and-write editors can expose empty/partial JSON or briefly deny sharing.
                delay(TimeSpan.FromMilliseconds(20));
            }
            catch (UnauthorizedAccessException ex)
            {
                throw Invalid(path, ex.Message, ex);
            }
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(path, "the root must be a JSON object.");
            }

            int? cpuSlots = null;
            int? memoryMb = null;
            int? containerSlots = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("cpuSlots", StringComparison.OrdinalIgnoreCase))
                {
                    cpuSlots = PositiveInt(path, property);
                }
                else if (property.Name.Equals("memoryMb", StringComparison.OrdinalIgnoreCase))
                {
                    memoryMb = PositiveInt(path, property);
                }
                else if (property.Name.Equals("containerSlots", StringComparison.OrdinalIgnoreCase))
                {
                    containerSlots = PositiveInt(path, property);
                }
                else
                {
                    throw Invalid(path, $"unknown property '{property.Name}'. Supported: cpuSlots, memoryMb, containerSlots.");
                }
            }

            if (cpuSlots is null)
            {
                throw Invalid(path, "'cpuSlots' is required when the budget file exists.");
            }

            return new ExecutionBudget(cpuSlots.Value, memoryMb, path, containerSlots);
        }
    }

    private static int PositiveInt(string path, JsonProperty property)
    {
        if (property.Value.ValueKind == JsonValueKind.Number
            && property.Value.TryGetInt32(out var value)
            && value > 0)
        {
            return value;
        }

        throw Invalid(path, $"'{property.Name}' must be a positive integer.");
    }

    private static ExecutionCoordinationException Invalid(string path, string detail, Exception? inner = null)
    {
        var message = $"Invalid machine execution budget '{path}': {detail}";
        return inner is null ? new(message) : new(message, inner);
    }
}
