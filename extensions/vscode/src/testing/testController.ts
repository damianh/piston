import * as vscode from 'vscode';
import type { PistonClient } from '../protocol/client';
import type { StateSnapshotNotification, TestResultDto } from '../protocol/types';
import * as Methods from '../protocol/methods';
import { updateTestItems } from './stateMapper';

/**
 * Manages the VS Code Test Controller for Piston.
 * Registers a test controller, handles test runs, and keeps the test tree
 * up-to-date from engine state snapshots.
 */
export class PistonTestController implements vscode.Disposable {
  readonly controller: vscode.TestController;
  private readonly _itemMap = new Map<string, vscode.TestItem>();
  private _client: PistonClient | null = null;
  private _activeRun: vscode.TestRun | null = null;
  private readonly _disposables: vscode.Disposable[] = [];

  constructor() {
    this.controller = vscode.tests.createTestController('piston', 'Piston');
    this.controller.resolveHandler = () => undefined;
    this.controller.createRunProfile(
      'Run',
      vscode.TestRunProfileKind.Run,
      (request, token) => this._runHandler(request, token),
      true,
    );
    this._disposables.push(this.controller);
  }

  /** Attach a PistonClient so commands can be sent. */
  setClient(client: PistonClient | null): void {
    this._client = client;
  }

  /** Apply a state snapshot to the test tree. */
  applySnapshot(snapshot: StateSnapshotNotification): void {
    updateTestItems(this.controller, snapshot, this._itemMap);
    this._updateRunResults(snapshot);
  }

  /** Mark all in-progress suites as running in the active test run. */
  applyProgress(snapshot: StateSnapshotNotification): void {
    if (!this._activeRun) {
      return;
    }
    for (const suite of snapshot.inProgressSuites) {
      for (const test of suite.tests) {
        const item = this._itemMap.get(test.fullyQualifiedName);
        if (item) {
          this._activeRun.started(item);
        }
      }
    }
  }

  /** Begin a new test run when the engine enters the Testing phase. */
  beginRun(): void {
    if (this._activeRun) {
      this._activeRun.end();
    }
    const request = new vscode.TestRunRequest();
    this._activeRun = this.controller.createTestRun(request, 'Piston Run', true);
  }

  /** End the current test run. */
  endRun(): void {
    if (this._activeRun) {
      this._activeRun.end();
      this._activeRun = null;
    }
  }

  dispose(): void {
    this.endRun();
    for (const d of this._disposables) {
      d.dispose();
    }
  }

  private async _runHandler(
    request: vscode.TestRunRequest,
    token: vscode.CancellationToken,
  ): Promise<void> {
    if (!this._client) {
      void vscode.window.showWarningMessage('Piston: controller is not running.');
      return;
    }

    const run = this.controller.createTestRun(request);
    token.onCancellationRequested(() => run.end());

    try {
      if (request.include && request.include.length > 0) {
        // Specific tests selected — build a filter from their FQNs.
        const names = request.include.map((item) => item.id).join('|');
        await this._client.sendRequest(Methods.EngineSetFilter, { filter: names });
      }
      await this._client.sendRequest(Methods.EngineForceRun);
    } catch (err) {
      void vscode.window.showErrorMessage(
        `Piston: failed to start run — ${err instanceof Error ? err.message : String(err)}`,
      );
    } finally {
      run.end();
    }
  }

  private _updateRunResults(snapshot: StateSnapshotNotification): void {
    if (!this._activeRun) {
      return;
    }
    for (const suite of snapshot.suites) {
      for (const test of suite.tests) {
        const item = this._itemMap.get(test.fullyQualifiedName);
        if (!item) {
          continue;
        }
        this._reportResult(item, test);
      }
    }
  }

  private _reportResult(item: vscode.TestItem, test: TestResultDto): void {
    if (!this._activeRun) {
      return;
    }
    const duration = test.durationMs;
    switch (test.status) {
      case 'Passed':
        this._activeRun.passed(item, duration);
        break;
      case 'Failed': {
        const msg = buildFailureMessage(test, item);
        this._activeRun.failed(item, msg, duration);
        break;
      }
      case 'Skipped':
        this._activeRun.skipped(item);
        break;
      default:
        break;
    }
  }
}

function buildFailureMessage(
  test: TestResultDto,
  item: vscode.TestItem,
): vscode.TestMessage {
  const parts: string[] = [];
  if (test.errorMessage) {
    parts.push(test.errorMessage);
  }
  if (test.stackTrace) {
    parts.push(test.stackTrace);
  }
  const text = parts.join('\n\n') || 'Test failed.';
  const msg = new vscode.TestMessage(text);
  if (item.uri) {
    msg.location = new vscode.Location(item.uri, new vscode.Position(0, 0));
  }
  return msg;
}
