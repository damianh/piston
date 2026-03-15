import * as cp from 'child_process';
import * as path from 'path';
import * as vscode from 'vscode';
import { NdjsonReader, NdjsonWriter } from './protocol/framing';
import { PistonClient } from './protocol/client';

/** Minimal logger interface accepted by ControllerProcess. */
export interface OutputLogger {
  appendLine(line: string): void;
}

/**
 * Manages the lifecycle of the `piston --headless --stdio` child process
 * and exposes a {@link PistonClient} connected to it.
 */
export class ControllerProcess implements vscode.Disposable {
  private _process: cp.ChildProcess | null = null;
  private _client: PistonClient | null = null;
  private _outputChannel: OutputLogger;

  private readonly _onDidStart = new vscode.EventEmitter<PistonClient>();
  private readonly _onDidStop = new vscode.EventEmitter<void>();
  private readonly _onDidCrash = new vscode.EventEmitter<number | null>();

  readonly onDidStart = this._onDidStart.event;
  readonly onDidStop = this._onDidStop.event;
  readonly onDidCrash = this._onDidCrash.event;

  constructor(outputChannel: OutputLogger) {
    this._outputChannel = outputChannel;
  }

  get client(): PistonClient | null {
    return this._client;
  }

  /**
   * Spawns the controller process and returns the connected {@link PistonClient}.
   */
  async start(
    solutionPath: string,
    controllerPath: string,
    extraArgs: string[],
  ): Promise<PistonClient> {
    this._outputChannel.appendLine('[piston] Starting controller...');

    const args = ['--headless', '--stdio', solutionPath, ...extraArgs];
    this._outputChannel.appendLine(`[piston] Command: ${controllerPath} ${args.join(' ')}`);

    const child = cp.spawn(controllerPath, args, {
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
    });

    this._process = child;

    // Pipe stderr to output channel.
    child.stderr?.on('data', (chunk: Buffer) => {
      const lines = chunk.toString('utf8').split(/\r?\n/);
      for (const line of lines) {
        if (line.trim()) {
          this._outputChannel.appendLine(line);
        }
      }
    });

    const reader = new NdjsonReader(child.stdout!);
    const writer = new NdjsonWriter(child.stdin!);
    const client = new PistonClient(reader, writer);
    this._client = client;

    child.on('error', (err) => {
      this._outputChannel.appendLine(`[piston] Process error: ${err.message}`);
      this._onDidCrash.fire(null);
    });

    child.on('exit', (code) => {
      this._outputChannel.appendLine(`[piston] Controller exited with code ${code ?? 'null'}`);
      this._client = null;
      if (code !== 0) {
        this._onDidCrash.fire(code);
      } else {
        this._onDidStop.fire();
      }
    });

    this._outputChannel.appendLine('[piston] Connected.');
    this._onDidStart.fire(client);
    return client;
  }

  /** Stop the controller process gracefully. */
  stop(): void {
    if (this._process) {
      this._outputChannel.appendLine('[piston] Stopping controller...');
      this._client?.dispose();
      this._client = null;
      this._process.kill();
      this._process = null;
    }
  }

  /** Stop and restart the controller. */
  async restart(
    solutionPath: string,
    controllerPath: string,
    extraArgs: string[],
  ): Promise<PistonClient> {
    this.stop();
    return this.start(solutionPath, controllerPath, extraArgs);
  }

  dispose(): void {
    this.stop();
    this._onDidStart.dispose();
    this._onDidStop.dispose();
    this._onDidCrash.dispose();
  }
}

/**
 * Auto-detect the Piston controller binary path.
 * Priority: user setting → PATH → well-known install.
 */
export function resolveControllerPath(): string {
  const config = vscode.workspace.getConfiguration('piston');
  const fromConfig = config.get<string>('controllerPath');
  if (fromConfig && fromConfig.trim().length > 0) {
    return fromConfig.trim();
  }

  // Check if 'piston' is on the PATH (cross-platform).
  const envPath = process.env.PATH ?? '';
  const separator = process.platform === 'win32' ? ';' : ':';
  const ext = process.platform === 'win32' ? '.exe' : '';
  for (const dir of envPath.split(separator)) {
    const candidate = path.join(dir, `piston${ext}`);
    try {
      require('fs').accessSync(candidate);
      return candidate;
    } catch {
      // Not found in this directory.
    }
  }

  // Fall back to bare name and let the OS resolve it.
  return process.platform === 'win32' ? 'piston.exe' : 'piston';
}
