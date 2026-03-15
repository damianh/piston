import * as readline from 'readline';
import type { Readable, Writable } from 'stream';

/**
 * Reads NDJSON-framed messages from a Readable stream line by line.
 * Each complete newline-terminated line is parsed as JSON.
 */
export class NdjsonReader {
  private readonly _rl: readline.Interface;
  private readonly _handlers: Array<(message: unknown) => void> = [];
  private readonly _errorHandlers: Array<(err: Error) => void> = [];

  constructor(stream: Readable) {
    this._rl = readline.createInterface({ input: stream, crlfDelay: Infinity });

    this._rl.on('line', (line) => {
      if (!line.trim()) {
        return;
      }
      try {
        const message = JSON.parse(line) as unknown;
        for (const handler of this._handlers) {
          handler(message);
        }
      } catch (err) {
        const parseError = err instanceof Error ? err : new Error(String(err));
        for (const h of this._errorHandlers) {
          h(parseError);
        }
      }
    });

    this._rl.on('error', (err) => {
      for (const h of this._errorHandlers) {
        h(err);
      }
    });
  }

  /** Register a handler for each parsed JSON message. */
  onMessage(handler: (message: unknown) => void): void {
    this._handlers.push(handler);
  }

  /** Register a handler for parse errors. */
  onError(handler: (err: Error) => void): void {
    this._errorHandlers.push(handler);
  }

  /** Register a handler invoked when the stream closes. */
  onClose(handler: () => void): void {
    this._rl.on('close', handler);
  }

  dispose(): void {
    this._rl.close();
  }
}

/**
 * Writes JSON-RPC messages as NDJSON (JSON + newline) to a Writable stream.
 */
export class NdjsonWriter {
  private readonly _stream: Writable;

  constructor(stream: Writable) {
    this._stream = stream;
  }

  /** Serialise {@link message} and write it followed by a newline. */
  write(message: object): void {
    const line = JSON.stringify(message) + '\n';
    const ok = this._stream.write(line, 'utf8');
    if (!ok) {
      // Backpressure — drain event will resume writing.
      // For simplicity we do not queue here; callers should respect flow control.
    }
  }
}
