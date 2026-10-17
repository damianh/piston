// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.JsonRpc;

/// <summary>Wire-protocol method name constants for all JSON-RPC commands and notifications.</summary>
public static class ProtocolMethods
{
    // Commands (client → server, request/response)

    /// <summary>Start the engine with a solution path. Rejected in headless mode.</summary>
    public const string EngineStart = "engine/start";

    /// <summary>Force an immediate test run.</summary>
    public const string EngineForceRun = "engine/forceRun";

    /// <summary>Stop the engine.</summary>
    public const string EngineStop = "engine/stop";

    /// <summary>Set the test name filter.</summary>
    public const string EngineSetFilter = "engine/setFilter";

    /// <summary>Clear all test results.</summary>
    public const string EngineClearResults = "engine/clearResults";
    public const string EngineReset = "engine/reset";

    /// <summary>Request per-file coverage data for the given source file.</summary>
    public const string CoverageGetForFile = "coverage/getForFile";

    // Notifications (server → client, no response)

    /// <summary>Full state snapshot — sent on connect and after every state change.</summary>
    public const string EngineStateSnapshot = "engine/stateSnapshot";

    /// <summary>Phase transition notification.</summary>
    public const string EnginePhaseChanged = "engine/phaseChanged";

    /// <summary>Incremental test progress during a test run.</summary>
    public const string TestsProgress = "tests/progress";

    /// <summary>Build error notification.</summary>
    public const string BuildError = "build/error";

    /// <summary>Pushed after a test run when per-file coverage data is updated.</summary>
    public const string CoverageFileUpdated = "coverage/fileUpdated";

    /// <summary>Unified activity timeline event — discriminated by the Type field in the payload.</summary>
    public const string ActivityEvent = "activity/event";

    // Request/Response (client → server, with response payload)

    /// <summary>Fetch the MCP tool call log (ring buffer).</summary>
    public const string McpGetCallLog = "mcp/getCallLog";

    /// <summary>Fetch the daemon's retained activity timeline (oldest first), for backfilling late-joining clients.</summary>
    public const string ActivityGetLog = "activity/getLog";

    /// <summary>Fetch the log of the test process that produced a test's current result.</summary>
    public const string TestsGetRunLog = "tests/getRunLog";

    /// <summary>Describe the daemon process: PID, solution, start time, version and endpoint URLs.</summary>
    public const string DaemonInfo = "daemon/info";

    /// <summary>Stop the engine and exit the daemon process, releasing its pipe, ports and worktree ownership.</summary>
    public const string DaemonShutdown = "daemon/shutdown";

    /// <summary>Read the machine-wide user settings and the file they live in.</summary>
    public const string SettingsGet = "settings/get";

    /// <summary>Merge the non-null fields of a partial settings object into the machine-wide user settings; returns the result and the file path.</summary>
    public const string SettingsUpdate = "settings/update";
}
