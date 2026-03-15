import * as vscode from 'vscode';

/** Resolved Piston extension configuration. */
export interface PistonConfig {
  controllerPath: string;
  solutionPath: string;
  args: string[];
  autoStart: boolean;
  coverageEnabled: boolean;
}

/** Read the current Piston configuration from VS Code settings. */
export function readConfig(): PistonConfig {
  const cfg = vscode.workspace.getConfiguration('piston');
  return {
    controllerPath: cfg.get<string>('controllerPath') ?? '',
    solutionPath: cfg.get<string>('solutionPath') ?? '',
    args: cfg.get<string[]>('args') ?? [],
    autoStart: cfg.get<boolean>('autoStart') ?? true,
    coverageEnabled: cfg.get<boolean>('coverage.enabled') ?? true,
  };
}

/** Keys that require a controller restart when changed. */
const RESTART_KEYS = new Set(['piston.controllerPath', 'piston.solutionPath', 'piston.args']);

/**
 * Watch for configuration changes that require a restart.
 * Calls {@link onRestartNeeded} when any restart-triggering key changes.
 */
export function watchConfig(
  onRestartNeeded: () => void,
  disposables: vscode.Disposable[],
): void {
  disposables.push(
    vscode.workspace.onDidChangeConfiguration((e) => {
      for (const key of RESTART_KEYS) {
        if (e.affectsConfiguration(key)) {
          onRestartNeeded();
          return;
        }
      }
    }),
  );
}
