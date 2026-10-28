// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;

namespace Piston.Engine.Execution;

internal readonly record struct ExecutionQueueEntry(
    long Sequence, int CpuSlots, int? MemoryMb, int ProcessId, string RunId, string FileName)
{
    public int CompareTo(long sequence, string fileName)
    {
        var bySequence = Sequence.CompareTo(sequence);
        return bySequence != 0 ? bySequence : string.CompareOrdinal(FileName, fileName);
    }

    public static bool TryParse(string fileName, out ExecutionQueueEntry entry)
    {
        entry = default;
        var parts = Path.GetFileNameWithoutExtension(fileName).Split('_');
        if (parts.Length != 5
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var cpu) || cpu <= 0
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var memory) || memory < 0
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0
            || parts[4].Length == 0)
        {
            return false;
        }

        entry = new ExecutionQueueEntry(
            sequence, cpu, memory == 0 ? null : memory, processId, parts[4], Path.GetFileName(fileName));
        return true;
    }
}
