import type { NdjsonReader, NdjsonWriter } from './framing';

interface JsonRpcResponse {
  jsonrpc: '2.0';
  id: string;
  result?: unknown;
  error?: { code: number; message: string; data?: unknown };
}

interface JsonRpcNotification {
  jsonrpc: '2.0';
  method: string;
  params?: unknown;
}

type JsonRpcMessage = JsonRpcResponse | JsonRpcNotification;

interface PendingRequest {
  resolve: (value: unknown) => void;
  reject: (reason: unknown) => void;
  timer: ReturnType<typeof setTimeout>;
}

/** A disposable object. */
export interface Disposable {
  dispose(): void;
}

const REQUEST_TIMEOUT_MS = 30_000;

/**
 * JSON-RPC 2.0 client over NDJSON transport.
 * Analogous to RemoteEngineClient on the C# side.
 */
export class PistonClient {
  private readonly _reader: NdjsonReader;
  private readonly _writer: NdjsonWriter;
  private readonly _pending = new Map<string, PendingRequest>();
  private readonly _notificationHandlers = new Map<string, Array<(params: unknown) => void>>();
  private _nextId = 1;
  private _disposed = false;

  constructor(reader: NdjsonReader, writer: NdjsonWriter) {
    this._reader = reader;
    this._writer = writer;

    this._reader.onMessage((msg) => this._handleMessage(msg));
    this._reader.onClose(() => this._handleClose());
  }

  /**
   * Send a JSON-RPC request and return a promise that resolves with the result
   * or rejects with an error. Rejects after 30 s if no response arrives.
   */
  sendRequest(method: string, params?: unknown): Promise<unknown> {
    if (this._disposed) {
      return Promise.reject(new Error('PistonClient is disposed.'));
    }

    const id = String(this._nextId++);

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this._pending.delete(id);
        reject(new Error(`Request '${method}' timed out after ${REQUEST_TIMEOUT_MS} ms.`));
      }, REQUEST_TIMEOUT_MS);

      this._pending.set(id, { resolve, reject, timer });

      this._writer.write({ jsonrpc: '2.0', id, method, params: params ?? null });
    });
  }

  /**
   * Register a notification handler for the given method.
   * Returns a Disposable to unregister the handler.
   */
  onNotification(method: string, handler: (params: unknown) => void): Disposable {
    let handlers = this._notificationHandlers.get(method);
    if (!handlers) {
      handlers = [];
      this._notificationHandlers.set(method, handlers);
    }
    handlers.push(handler);

    return {
      dispose: () => {
        const list = this._notificationHandlers.get(method);
        if (list) {
          const idx = list.indexOf(handler);
          if (idx !== -1) {
            list.splice(idx, 1);
          }
        }
      },
    };
  }

  dispose(): void {
    if (this._disposed) {
      return;
    }
    this._disposed = true;

    // Cancel all pending requests.
    for (const [id, pending] of this._pending) {
      clearTimeout(pending.timer);
      pending.reject(new Error('PistonClient disposed.'));
      this._pending.delete(id);
    }

    this._reader.dispose();
  }

  private _handleMessage(msg: unknown): void {
    const message = msg as JsonRpcMessage;
    if (!message || typeof message !== 'object') {
      return;
    }

    if ('id' in message && message.id !== undefined) {
      // Response to a pending request.
      const response = message as JsonRpcResponse;
      const pending = this._pending.get(response.id);
      if (pending) {
        this._pending.delete(response.id);
        clearTimeout(pending.timer);
        if (response.error) {
          pending.reject(new Error(response.error.message));
        } else {
          pending.resolve(response.result);
        }
      }
    } else if ('method' in message) {
      // Notification.
      const notification = message as JsonRpcNotification;
      const handlers = this._notificationHandlers.get(notification.method);
      if (handlers) {
        for (const handler of handlers) {
          handler(notification.params);
        }
      }
    }
  }

  private _handleClose(): void {
    // Reject all pending requests when the stream closes.
    for (const [id, pending] of this._pending) {
      clearTimeout(pending.timer);
      pending.reject(new Error('Connection closed.'));
      this._pending.delete(id);
    }
  }
}
