import * as vscode from 'vscode';
import type { PistonPhase } from '../protocol/types';

const PHASE_ICONS: Record<PistonPhase, string> = {
  Idle: '$(circle-outline)',
  Watching: '$(eye)',
  Analyzing: '$(search)',
  Building: '$(tools)',
  Testing: '$(beaker)',
  Error: '$(error)',
};

/**
 * VS Code status bar item showing the Piston engine phase and test counts.
 */
export class PistonStatusBar implements vscode.Disposable {
  private readonly _item: vscode.StatusBarItem;
  private _phase: PistonPhase = 'Idle';
  private _completedTests = 0;
  private _totalTests = 0;
  private _passed = 0;
  private _failed = 0;
  private _skipped = 0;

  constructor() {
    this._item = vscode.window.createStatusBarItem(
      vscode.StatusBarAlignment.Left,
      100,
    );
    this._item.command = 'piston.toggleOutput';
    this._update();
  }

  /** Update the displayed phase. */
  setPhase(phase: PistonPhase, completed?: number, total?: number): void {
    this._phase = phase;
    if (completed !== undefined) {
      this._completedTests = completed;
    }
    if (total !== undefined) {
      this._totalTests = total;
    }
    this._update();
  }

  /** Update the test counts from a state snapshot. */
  setTestCounts(passed: number, failed: number, skipped: number): void {
    this._passed = passed;
    this._failed = failed;
    this._skipped = skipped;
    this._update();
  }

  /** Show the status bar item. */
  show(): void {
    this._item.show();
  }

  /** Hide the status bar item. */
  hide(): void {
    this._item.hide();
  }

  dispose(): void {
    this._item.dispose();
  }

  private _update(): void {
    const icon = PHASE_ICONS[this._phase] ?? '$(circle-outline)';
    let text = `${icon} Piston`;

    switch (this._phase) {
      case 'Idle':
        text = `${icon} Piston: Idle`;
        break;
      case 'Watching':
        text = `${icon} Piston: Watching`;
        break;
      case 'Analyzing':
        text = `${icon} Piston: Analyzing...`;
        break;
      case 'Building':
        text = `${icon} Piston: Building...`;
        break;
      case 'Testing':
        text = `${icon} Piston: Testing (${this._completedTests}/${this._totalTests})...`;
        break;
      case 'Error':
        text = `${icon} Piston: Error`;
        break;
    }

    const counts =
      this._passed + this._failed + this._skipped > 0
        ? `  $(check) ${this._passed} $(x) ${this._failed} $(dash) ${this._skipped}`
        : '';

    this._item.text = text + counts;
    this._item.tooltip = 'Click to toggle Piston output';
  }
}
