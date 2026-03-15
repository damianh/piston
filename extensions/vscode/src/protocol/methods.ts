/**
 * JSON-RPC method name constants — must match ProtocolMethods.cs exactly.
 */

// Commands (client → server, request/response)

/** Start the engine with a solution path. Rejected in headless mode. */
export const EngineStart = 'engine/start';

/** Force an immediate test run. */
export const EngineForceRun = 'engine/forceRun';

/** Stop the engine. */
export const EngineStop = 'engine/stop';

/** Set the test name filter. */
export const EngineSetFilter = 'engine/setFilter';

/** Clear all test results. */
export const EngineClearResults = 'engine/clearResults';

/** Request per-file coverage data for the given source file. */
export const CoverageGetForFile = 'coverage/getForFile';

// Notifications (server → client, no response)

/** Full state snapshot — sent on connect and after every state change. */
export const EngineStateSnapshot = 'engine/stateSnapshot';

/** Phase transition notification. */
export const EnginePhaseChanged = 'engine/phaseChanged';

/** Incremental test progress during a test run. */
export const TestsProgress = 'tests/progress';

/** Build error notification. */
export const BuildError = 'build/error';

/** Pushed after a test run when per-file coverage data is updated. */
export const CoverageFileUpdated = 'coverage/fileUpdated';
