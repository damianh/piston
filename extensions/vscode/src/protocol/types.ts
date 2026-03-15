/**
 * TypeScript types mirroring the Piston protocol DTOs.
 * All field names are camelCase to match the C# JSON serializer configuration.
 */

/** Phase of the Piston engine. */
export type PistonPhase = 'Idle' | 'Watching' | 'Analyzing' | 'Building' | 'Testing' | 'Error';

/** Status of an individual test result. */
export type TestStatus = 'NotRun' | 'Running' | 'Passed' | 'Failed' | 'Skipped';

/** Status of a build. */
export type BuildStatus = 'None' | 'Building' | 'Succeeded' | 'Failed';

/** Status of a project test run. */
export type ProjectRunStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Crashed';

/** Coverage status for a single line of source code. */
export interface CoverageLineDto {
  /** 1-based line number. */
  lineNumber: number;
  /** Number of times this line was executed. */
  hitCount: number;
  /** 'covered' if hitCount > 0, otherwise 'uncovered'. */
  status: 'covered' | 'uncovered';
}

/** Per-file coverage data containing line-level hit counts. */
export interface FileCoverageDto {
  /** Absolute path to the source file. */
  filePath: string;
  /** Line-level coverage data. */
  lines: CoverageLineDto[];
}

/** An individual test result. */
export interface TestResultDto {
  /** Fully qualified test name (e.g. 'Namespace.ClassName.MethodName'). */
  fullyQualifiedName: string;
  /** Human-readable test display name. */
  displayName: string;
  /** Current test status. */
  status: TestStatus;
  /** Test execution duration in milliseconds. */
  durationMs: number;
  /** Captured test output, if any. */
  output: string | null;
  /** Error message for failed tests. */
  errorMessage: string | null;
  /** Stack trace for failed tests. */
  stackTrace: string | null;
  /** Source file path for navigation. */
  source: string | null;
}

/** A suite of tests belonging to a single test project. */
export interface TestSuiteDto {
  /** Name of the test project/suite. */
  name: string;
  /** All test results in this suite. */
  tests: TestResultDto[];
  /** Timestamp of the last test run for this suite. */
  timestamp: string | null;
  /** Total duration of the last run in milliseconds. */
  totalDurationMs: number;
}

/** Result of a build operation. */
export interface BuildResultDto {
  /** Build outcome. */
  status: BuildStatus;
  /** MSBuild error messages. */
  errors: string[];
  /** MSBuild warning messages. */
  warnings: string[];
  /** Build duration in milliseconds. */
  durationMs: number;
}

/** Full engine state snapshot — sent on connect and after every state change. */
export interface StateSnapshotNotification {
  /** Current engine phase. */
  phase: PistonPhase;
  /** Completed test suites. */
  suites: TestSuiteDto[];
  /** Test suites currently running. */
  inProgressSuites: TestSuiteDto[];
  /** Result of the most recent build. */
  lastBuild: BuildResultDto | null;
  /** Timestamp of the last test run. */
  lastRunTime: string | null;
  /** Duration of the last build in milliseconds. */
  lastBuildDurationMs: number | null;
  /** Duration of the last test run in milliseconds. */
  lastTestDurationMs: number | null;
  /** Error message from the test runner, if any. */
  lastTestRunnerError: string | null;
  /** Total number of tests expected in the current run. */
  totalExpectedTests: number;
  /** Number of tests completed so far. */
  completedTests: number;
  /** Number of verifications since last file change. */
  verifiedSinceChangeCount: number;
  /** Timestamp of the last file change that triggered a run. */
  lastFileChangeTime: string | null;
  /** Path to the solution file. */
  solutionPath: string | null;
  /** Projects affected by the last file change. */
  affectedProjects: string[] | null;
  /** Test projects affected by the last file change. */
  affectedTestProjects: string[] | null;
  /** Files changed in the last change event. */
  lastChangedFiles: string[] | null;
  /** Whether coverage collection is enabled. */
  coverageEnabled: boolean;
  /** Whether coverage data is available. */
  hasCoverageData: boolean;
  /** Coverage impact analysis detail. */
  coverageImpactDetail: string | null;
  /** Total number of test projects. */
  totalTestProjects: number;
  /** Number of test projects that have completed. */
  completedTestProjects: number;
  /** Per-project run status. */
  projectStatuses: Record<string, ProjectRunStatus> | null;
}

/** Notification sent when the engine phase changes. */
export interface PhaseChangedNotification {
  /** New engine phase. */
  phase: PistonPhase;
  /** Optional detail message. */
  detail: string | null;
}

/** Incremental test progress notification during a test run. */
export interface TestProgressNotification {
  /** Test suites currently running. */
  inProgressSuites: TestSuiteDto[];
  /** Number of tests completed so far. */
  completedTests: number;
  /** Total number of tests expected. */
  totalExpectedTests: number;
}

/** Notification sent when a build fails. */
export interface BuildErrorNotification {
  /** The build result containing error details. */
  build: BuildResultDto;
}

/** Notification sent when per-file coverage data is updated. */
export interface FileCoverageUpdatedNotification {
  /** Absolute path to the source file. */
  filePath: string;
  /** Updated line-level coverage data. */
  lines: CoverageLineDto[];
}

/** Request to retrieve coverage data for a specific file. */
export interface GetFileCoverageCommand {
  /** Absolute path to the source file. */
  filePath: string;
}
