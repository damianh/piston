import * as path from 'path';
import * as vscode from 'vscode';
import type { PistonClient } from '../protocol/client';
import type { CoverageLineDto, FileCoverageUpdatedNotification, FileCoverageDto } from '../protocol/types';
import * as Methods from '../protocol/methods';

/**
 * Applies inline coverage gutter decorations to VS Code editors.
 */
export class CoverageDecorator implements vscode.Disposable {
  private readonly _covered: vscode.TextEditorDecorationType;
  private readonly _uncovered: vscode.TextEditorDecorationType;
  private readonly _cache = new Map<string, CoverageLineDto[]>();
  private readonly _disposables: vscode.Disposable[] = [];
  private _enabled: boolean;
  private _client: PistonClient | null = null;

  constructor(extensionUri: vscode.Uri, initialEnabled: boolean) {
    this._enabled = initialEnabled;

    const coveredIconPath = vscode.Uri.joinPath(extensionUri, 'media', 'gutter-covered.svg');
    const uncoveredIconPath = vscode.Uri.joinPath(extensionUri, 'media', 'gutter-uncovered.svg');

    this._covered = vscode.window.createTextEditorDecorationType({
      gutterIconPath: coveredIconPath,
      gutterIconSize: 'contain',
      backgroundColor: new vscode.ThemeColor('diffEditor.insertedLineBackground'),
      isWholeLine: true,
    });

    this._uncovered = vscode.window.createTextEditorDecorationType({
      gutterIconPath: uncoveredIconPath,
      gutterIconSize: 'contain',
      backgroundColor: new vscode.ThemeColor('diffEditor.removedLineBackground'),
      isWholeLine: true,
    });

    this._disposables.push(
      vscode.window.onDidChangeActiveTextEditor((editor) => {
        if (editor) {
          this._applyToEditor(editor);
        }
      }),
    );
  }

  /** Attach the PistonClient for sending coverage requests. */
  setClient(client: PistonClient | null): void {
    this._client = client;
  }

  /** Toggle coverage decorations on/off. */
  toggle(): void {
    this._enabled = !this._enabled;
    if (this._enabled) {
      this._applyToAllEditors();
    } else {
      this.clearAll();
    }
  }

  /** Called when coverage data for a file is updated. */
  onFileCoverageUpdated(notification: FileCoverageUpdatedNotification): void {
    this._cache.set(notification.filePath, notification.lines);
    if (this._enabled) {
      this._applyToEditorForFile(notification.filePath);
    }
  }

  /** Request coverage data for the currently open file. */
  async requestCoverage(filePath: string): Promise<void> {
    if (!this._client) {
      return;
    }
    try {
      const result = (await this._client.sendRequest(Methods.CoverageGetForFile, {
        filePath,
      })) as FileCoverageDto | null;
      if (result?.lines) {
        this._cache.set(filePath, result.lines);
        if (this._enabled) {
          this._applyToEditorForFile(filePath);
        }
      }
    } catch {
      // Coverage not available yet — silently ignore.
    }
  }

  /** Clear all coverage decorations and cached data. */
  clearAll(): void {
    this._cache.clear();
    for (const editor of vscode.window.visibleTextEditors) {
      editor.setDecorations(this._covered, []);
      editor.setDecorations(this._uncovered, []);
    }
  }

  dispose(): void {
    this.clearAll();
    this._covered.dispose();
    this._uncovered.dispose();
    for (const d of this._disposables) {
      d.dispose();
    }
  }

  private _applyToAllEditors(): void {
    for (const editor of vscode.window.visibleTextEditors) {
      this._applyToEditor(editor);
    }
  }

  private _applyToEditorForFile(filePath: string): void {
    for (const editor of vscode.window.visibleTextEditors) {
      if (editor.document.uri.fsPath === filePath) {
        this._applyToEditor(editor);
      }
    }
  }

  private _applyToEditor(editor: vscode.TextEditor): void {
    if (!this._enabled) {
      return;
    }

    const filePath = editor.document.uri.fsPath;
    const lines = this._cache.get(filePath);

    if (!lines || lines.length === 0) {
      editor.setDecorations(this._covered, []);
      editor.setDecorations(this._uncovered, []);
      return;
    }

    const coveredRanges: vscode.Range[] = [];
    const uncoveredRanges: vscode.Range[] = [];

    for (const line of lines) {
      const lineIndex = line.lineNumber - 1; // Convert to 0-based.
      if (lineIndex < 0) {
        continue;
      }
      const range = new vscode.Range(lineIndex, 0, lineIndex, 0);
      if (line.status === 'covered') {
        coveredRanges.push(range);
      } else {
        uncoveredRanges.push(range);
      }
    }

    editor.setDecorations(this._covered, coveredRanges);
    editor.setDecorations(this._uncovered, uncoveredRanges);
  }
}
