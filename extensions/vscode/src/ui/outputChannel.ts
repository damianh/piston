import * as vscode from 'vscode';

/**
 * Output channel for Piston controller logs and extension diagnostics.
 */
export class PistonOutputChannel implements vscode.Disposable {
  private readonly _channel: vscode.OutputChannel;
  private _visible = false;

  constructor() {
    this._channel = vscode.window.createOutputChannel('Piston');
  }

  /** Append a line to the output channel. */
  appendLine(line: string): void {
    this._channel.appendLine(line);
  }

  /** Show the output channel. */
  show(): void {
    this._channel.show(true /* preserveFocus */);
    this._visible = true;
  }

  /** Hide the output channel. */
  hide(): void {
    this._channel.hide();
    this._visible = false;
  }

  /** Toggle the output channel visibility. */
  toggle(): void {
    if (this._visible) {
      this.hide();
    } else {
      this.show();
    }
  }

  dispose(): void {
    this._channel.dispose();
  }
}
