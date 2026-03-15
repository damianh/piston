import * as vscode from 'vscode';
import type { BuildErrorNotification } from '../protocol/types';

// MSBuild error format: file(line,col): error CODE: message
// Example: C:\repo\Foo.cs(42,13): error CS0103: The name 'x' does not exist
const MSBUILD_DIAG_PATTERN =
  /^(.+?)\((\d+),(\d+)\):\s+(error|warning)\s+(\w+):\s+(.+)$/;

/**
 * Maps build errors from `build/error` notifications to VS Code diagnostic entries
 * shown in the Problems panel.
 */
export class BuildDiagnosticsProvider implements vscode.Disposable {
  private readonly _collection: vscode.DiagnosticCollection;

  constructor() {
    this._collection = vscode.languages.createDiagnosticCollection('piston-build');
  }

  /** Apply diagnostics from a build error notification. */
  applyBuildError(notification: BuildErrorNotification): void {
    this._collection.clear();

    const byFile = new Map<string, vscode.Diagnostic[]>();

    const addEntries = (
      messages: readonly string[],
      severity: vscode.DiagnosticSeverity,
    ): void => {
      for (const msg of messages) {
        const match = MSBUILD_DIAG_PATTERN.exec(msg);
        if (match) {
          const [, filePath, lineStr, colStr, , code, message] = match;
          const line = Math.max(0, parseInt(lineStr, 10) - 1);
          const col = Math.max(0, parseInt(colStr, 10) - 1);
          const range = new vscode.Range(line, col, line, col);
          const diagnostic = new vscode.Diagnostic(range, `${code}: ${message}`, severity);
          diagnostic.source = 'Piston';
          const key = filePath.trim();
          if (!byFile.has(key)) {
            byFile.set(key, []);
          }
          byFile.get(key)!.push(diagnostic);
        } else {
          // Unparseable message — attach to a synthetic "no file" entry.
          const diagnostic = new vscode.Diagnostic(
            new vscode.Range(0, 0, 0, 0),
            msg,
            severity,
          );
          diagnostic.source = 'Piston';
          const key = '';
          if (!byFile.has(key)) {
            byFile.set(key, []);
          }
          byFile.get(key)!.push(diagnostic);
        }
      }
    };

    addEntries(notification.build.errors, vscode.DiagnosticSeverity.Error);
    addEntries(notification.build.warnings, vscode.DiagnosticSeverity.Warning);

    for (const [filePath, diagnostics] of byFile) {
      if (filePath) {
        this._collection.set(vscode.Uri.file(filePath), diagnostics);
      }
      // Diagnostics without a file path are omitted (no valid URI to attach them to).
    }
  }

  /** Clear all build diagnostics. */
  clear(): void {
    this._collection.clear();
  }

  dispose(): void {
    this._collection.dispose();
  }
}
