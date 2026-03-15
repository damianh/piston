import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { ControllerProcess, resolveControllerPath } from './controller';
import { PistonOutputChannel } from './ui/outputChannel';
import { PistonStatusBar } from './ui/statusBar';
import { PistonTestController } from './testing/testController';
import { CoverageDecorator } from './coverage/coverageDecorator';
import { BuildDiagnosticsProvider } from './diagnostics/buildDiagnostics';
import { readConfig, watchConfig } from './config';
import * as Methods from './protocol/methods';
import type {
  StateSnapshotNotification,
  PhaseChangedNotification,
  TestProgressNotification,
  BuildErrorNotification,
  FileCoverageUpdatedNotification,
} from './protocol/types';

let outputChannel: PistonOutputChannel | undefined;
let statusBar: PistonStatusBar | undefined;
let testController: PistonTestController | undefined;
let coverageDecorator: CoverageDecorator | undefined;
let buildDiagnostics: BuildDiagnosticsProvider | undefined;
let controllerProcess: ControllerProcess | undefined;
const extensionDisposables: vscode.Disposable[] = [];

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  // ── Create shared components ─────────────────────────────────────────────────
  outputChannel = new PistonOutputChannel();
  statusBar = new PistonStatusBar();
  testController = new PistonTestController();
  coverageDecorator = new CoverageDecorator(context.extensionUri, readConfig().coverageEnabled);
  buildDiagnostics = new BuildDiagnosticsProvider();

  context.subscriptions.push(
    outputChannel,
    statusBar,
    testController,
    coverageDecorator,
    buildDiagnostics,
  );

  // ── Register commands ────────────────────────────────────────────────────────
  registerCommands(context);

  // ── Watch config for restarts ────────────────────────────────────────────────
  watchConfig(() => {
    if (controllerProcess) {
      outputChannel?.appendLine('[piston] Configuration changed — restarting controller...');
      void startController(context);
    }
  }, context.subscriptions);

  // ── Auto-start ───────────────────────────────────────────────────────────────
  const config = readConfig();
  if (config.autoStart) {
    await startController(context);
  }
}

export function deactivate(): void {
  controllerProcess?.stop();
  controllerProcess?.dispose();
  controllerProcess = undefined;

  for (const d of extensionDisposables) {
    d.dispose();
  }
  extensionDisposables.length = 0;
}

// ── Controller lifecycle ──────────────────────────────────────────────────────

async function startController(context: vscode.ExtensionContext): Promise<void> {
  if (!outputChannel || !statusBar || !testController || !coverageDecorator || !buildDiagnostics) {
    return;
  }

  // Clean up previous instance.
  controllerProcess?.stop();
  controllerProcess?.dispose();
  // Clear old notification disposables.
  for (const d of extensionDisposables) {
    d.dispose();
  }
  extensionDisposables.length = 0;

  const config = readConfig();
  const controllerPath = config.controllerPath || resolveControllerPath();
  const solutionPath = config.solutionPath || (await detectSolutionPath());

  if (!solutionPath) {
    outputChannel.appendLine('[piston] No .sln or .slnx file found in workspace. Piston will not start.');
    return;
  }

  const proc = new ControllerProcess(outputChannel);
  controllerProcess = proc;
  context.subscriptions.push(proc);

  proc.onDidCrash((code) => {
    outputChannel?.appendLine(`[piston] Controller crashed (exit code ${code ?? 'null'}). Prompting restart.`);
    void vscode.window
      .showErrorMessage('Piston controller crashed.', 'Restart')
      .then((action) => {
        if (action === 'Restart') {
          void startController(context);
        }
      });
  });

  try {
    const client = await proc.start(solutionPath, controllerPath, config.args);
    testController.setClient(client);
    coverageDecorator.setClient(client);
    statusBar.show();

    // Register notification handlers.
    extensionDisposables.push(
      client.onNotification(Methods.EngineStateSnapshot, (params) => {
        const snapshot = params as StateSnapshotNotification;

        // Count passed/failed/skipped from all suites.
        let passed = 0, failed = 0, skipped = 0;
        for (const suite of snapshot.suites) {
          for (const test of suite.tests) {
            if (test.status === 'Passed') passed++;
            else if (test.status === 'Failed') failed++;
            else if (test.status === 'Skipped') skipped++;
          }
        }
        statusBar?.setTestCounts(passed, failed, skipped);
        testController?.applySnapshot(snapshot);

        // End any active run when we receive a full snapshot with results.
        if (snapshot.phase !== 'Testing') {
          testController?.endRun();
        }
      }),

      client.onNotification(Methods.EnginePhaseChanged, (params) => {
        const notification = params as PhaseChangedNotification;
        statusBar?.setPhase(notification.phase);

        if (notification.phase === 'Testing') {
          testController?.beginRun();
        } else {
          // Clear build diagnostics when returning to idle/watching.
          if (notification.phase === 'Watching' || notification.phase === 'Idle') {
            buildDiagnostics?.clear();
          }
        }
      }),

      client.onNotification(Methods.TestsProgress, (params) => {
        const notification = params as TestProgressNotification;
        statusBar?.setPhase('Testing', notification.completedTests, notification.totalExpectedTests);
        testController?.applyProgress({
          phase: 'Testing',
          suites: [],
          inProgressSuites: notification.inProgressSuites,
          lastBuild: null,
          lastRunTime: null,
          lastBuildDurationMs: null,
          lastTestDurationMs: null,
          lastTestRunnerError: null,
          totalExpectedTests: notification.totalExpectedTests,
          completedTests: notification.completedTests,
          verifiedSinceChangeCount: 0,
          lastFileChangeTime: null,
          solutionPath: null,
          affectedProjects: null,
          affectedTestProjects: null,
          lastChangedFiles: null,
          coverageEnabled: false,
          hasCoverageData: false,
          coverageImpactDetail: null,
          totalTestProjects: 0,
          completedTestProjects: 0,
          projectStatuses: null,
        });
      }),

      client.onNotification(Methods.BuildError, (params) => {
        const notification = params as BuildErrorNotification;
        buildDiagnostics?.applyBuildError(notification);
        void vscode.window.showErrorMessage(
          `Piston: Build failed with ${notification.build.errors.length} error(s).`,
        );
      }),

      client.onNotification(Methods.CoverageFileUpdated, (params) => {
        const notification = params as FileCoverageUpdatedNotification;
        coverageDecorator?.onFileCoverageUpdated(notification);
      }),
    );
  } catch (err) {
    outputChannel.appendLine(
      `[piston] Failed to start controller: ${err instanceof Error ? err.message : String(err)}`,
    );
    void vscode.window.showErrorMessage(
      `Piston: failed to start controller — ${err instanceof Error ? err.message : String(err)}`,
    );
  }
}

// ── Command registration ──────────────────────────────────────────────────────

function registerCommands(context: vscode.ExtensionContext): void {
  context.subscriptions.push(
    vscode.commands.registerCommand('piston.start', () => {
      void startController(context);
    }),

    vscode.commands.registerCommand('piston.stop', () => {
      controllerProcess?.stop();
      statusBar?.hide();
      outputChannel?.appendLine('[piston] Stopped by user.');
    }),

    vscode.commands.registerCommand('piston.forceRun', async () => {
      const client = controllerProcess?.client;
      if (!client) {
        void vscode.window.showWarningMessage('Piston: controller is not running.');
        return;
      }
      try {
        await client.sendRequest(Methods.EngineForceRun);
      } catch (err) {
        void vscode.window.showErrorMessage(
          `Piston: forceRun failed — ${err instanceof Error ? err.message : String(err)}`,
        );
      }
    }),

    vscode.commands.registerCommand('piston.setFilter', async () => {
      const client = controllerProcess?.client;
      if (!client) {
        void vscode.window.showWarningMessage('Piston: controller is not running.');
        return;
      }
      const filter = await vscode.window.showInputBox({
        prompt: 'Enter test filter (substring or regex)',
        placeHolder: 'e.g. MyTest or ^Namespace\\.ClassName',
      });
      if (filter !== undefined) {
        try {
          await client.sendRequest(Methods.EngineSetFilter, { filter: filter || null });
        } catch (err) {
          void vscode.window.showErrorMessage(
            `Piston: setFilter failed — ${err instanceof Error ? err.message : String(err)}`,
          );
        }
      }
    }),

    vscode.commands.registerCommand('piston.clearResults', async () => {
      const client = controllerProcess?.client;
      if (!client) {
        void vscode.window.showWarningMessage('Piston: controller is not running.');
        return;
      }
      try {
        await client.sendRequest(Methods.EngineClearResults);
      } catch (err) {
        void vscode.window.showErrorMessage(
          `Piston: clearResults failed — ${err instanceof Error ? err.message : String(err)}`,
        );
      }
    }),

    vscode.commands.registerCommand('piston.toggleCoverage', () => {
      coverageDecorator?.toggle();
    }),

    vscode.commands.registerCommand('piston.toggleOutput', () => {
      outputChannel?.toggle();
    }),
  );
}

// ── Helpers ───────────────────────────────────────────────────────────────────

async function detectSolutionPath(): Promise<string | undefined> {
  const folders = vscode.workspace.workspaceFolders;
  if (!folders || folders.length === 0) {
    return undefined;
  }

  for (const folder of folders) {
    const folderPath = folder.uri.fsPath;
    const candidates = [
      ...listFiles(folderPath, '.sln'),
      ...listFiles(folderPath, '.slnx'),
    ];
    if (candidates.length === 1) {
      return candidates[0];
    }
    if (candidates.length > 1) {
      // Multiple solutions — ask the user.
      const items = candidates.map((p) => ({
        label: path.basename(p),
        description: p,
        value: p,
      }));
      const picked = await vscode.window.showQuickPick(items, {
        placeHolder: 'Multiple solution files found. Pick one to use with Piston.',
      });
      return picked?.value;
    }
  }

  return undefined;
}

function listFiles(dir: string, ext: string): string[] {
  try {
    return fs
      .readdirSync(dir)
      .filter((f) => f.toLowerCase().endsWith(ext))
      .map((f) => path.join(dir, f));
  } catch {
    return [];
  }
}
