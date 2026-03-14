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

    // Notifications (server → client, no response)

    /// <summary>Full state snapshot — sent on connect and after every state change.</summary>
    public const string EngineStateSnapshot = "engine/stateSnapshot";

    /// <summary>Phase transition notification.</summary>
    public const string EnginePhaseChanged = "engine/phaseChanged";

    /// <summary>Incremental test progress during a test run.</summary>
    public const string TestsProgress = "tests/progress";

    /// <summary>Build error notification.</summary>
    public const string BuildError = "build/error";
}
