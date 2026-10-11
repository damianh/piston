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
public sealed record ExecutionBudget(int CpuSlots, int? MemoryMb, string Source)
{
    public const string DefaultSource = "default";

    /// <summary>Default when no budget file exists: one CPU slot per logical processor, memory not budgeted.</summary>
    public static ExecutionBudget Default => new(Environment.ProcessorCount, null, DefaultSource);

    /// <summary>Loads <c>budget.json</c>; a missing file yields <see cref="Default"/>, an invalid file throws.</summary>
    public static ExecutionBudget Load(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path))
            {
                return Default;
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Invalid(path, ex.Message, ex);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw Invalid(path, ex.Message, ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(path, "the root must be a JSON object.");
            }

            int? cpuSlots = null;
            int? memoryMb = null;
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
                else
                {
                    throw Invalid(path, $"unknown property '{property.Name}'. Supported: cpuSlots, memoryMb.");
                }
            }

            return new ExecutionBudget(cpuSlots ?? Environment.ProcessorCount, memoryMb, path);
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

/// <summary>What one admitted run of a daemon reserves from the <see cref="ExecutionBudget"/>.</summary>
/// <param name="CpuSlots">The daemon's effective test-process parallelism.</param>
/// <param name="MemoryMb">Configured memory reservation in MiB, if any.</param>
public sealed record ExecutionReservation(int CpuSlots, int? MemoryMb)
{
    /// <summary>Throws when this reservation could never be admitted under <paramref name="budget"/>.</summary>
    public void EnsureAdmissible(ExecutionBudget budget)
    {
        if (CpuSlots <= 0)
        {
            throw new ExecutionCoordinationException($"The CPU slot reservation must be positive; got {CpuSlots}.");
        }

        if (MemoryMb is <= 0)
        {
            throw new ExecutionCoordinationException($"The memory reservation must be positive; got {MemoryMb}.");
        }

        if (CpuSlots > budget.CpuSlots)
        {
            throw new ExecutionCoordinationException(
                $"This daemon reserves {CpuSlots} CPU slot(s) per run (its test-process parallelism), but the machine " +
                $"execution budget ({budget.Source}) allows {budget.CpuSlots}; it could never be admitted. " +
                "Lower 'parallelism' or raise 'cpuSlots' in the budget.");
        }

        if (budget.MemoryMb is { } memoryBudget)
        {
            if (MemoryMb is null)
            {
                throw new ExecutionCoordinationException(
                    $"The machine execution budget ({budget.Source}) limits memory to {memoryBudget} MiB, but this " +
                    "daemon declares no memory reservation. Set 'execution.memoryMb' in .piston.json.");
            }

            if (MemoryMb > memoryBudget)
            {
                throw new ExecutionCoordinationException(
                    $"This daemon reserves {MemoryMb} MiB per run, but the machine execution budget ({budget.Source}) " +
                    $"allows {memoryBudget} MiB; it could never be admitted.");
            }
        }
    }
}
