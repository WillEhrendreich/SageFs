import * as vscode from "vscode";
import * as http from "http";

/**
 * Subscribes to SageFs's /diagnostics SSE stream and pushes
 * compiler errors/warnings into VS Code's Problems panel.
 */
export class DiagnosticsListener {
  private collection: vscode.DiagnosticCollection;
  private request: http.ClientRequest | undefined;
  private mcpPort: number;

  constructor(mcpPort: number) {
    this.mcpPort = mcpPort;
    this.collection = vscode.languages.createDiagnosticCollection("sagefs");
  }

  start(): void {
    this.stop();
    this.connect();
  }

  stop(): void {
    if (this.request) {
      this.request.destroy();
      this.request = undefined;
    }
  }

  updatePort(port: number): void {
    this.mcpPort = port;
    this.start();
  }

  dispose(): void {
    this.stop();
    this.collection.dispose();
  }

  private connect(): void {
    const url = `http://localhost:${this.mcpPort}/diagnostics`;

    this.request = http.get(url, { timeout: 0 }, (res) => {
      let buffer = "";

      res.on("data", (chunk: Buffer) => {
        buffer += chunk.toString();

        // Parse SSE events from buffer
        const parts = buffer.split("\n\n");
        buffer = parts.pop() ?? "";

        for (const part of parts) {
          const dataLine = part
            .split("\n")
            .find((l) => l.startsWith("data: "));
          if (!dataLine) {
            continue;
          }
          const json = dataLine.slice(6);
          this.handleDiagnostics(json);
        }
      });

      res.on("end", () => {
        // Reconnect after 2 seconds
        setTimeout(() => this.connect(), 2000);
      });
    });

    this.request.on("error", () => {
      // SageFs not running — retry later
      setTimeout(() => this.connect(), 5000);
    });
  }

  private handleDiagnostics(json: string): void {
    try {
      const items = JSON.parse(json);
      if (!Array.isArray(items)) {
        return;
      }

      // Group diagnostics by file
      const byFile = new Map<string, vscode.Diagnostic[]>();

      for (const item of items) {
        const file = item.file ?? item.fileName ?? "";
        const line = Math.max(0, (item.line ?? item.startLine ?? 1) - 1);
        const col = Math.max(0, (item.column ?? item.startColumn ?? 1) - 1);
        const endLine = Math.max(line, (item.endLine ?? line + 1) - 1);
        const endCol = Math.max(col, (item.endColumn ?? col + 1) - 1);
        const message = item.message ?? item.text ?? "";
        const severity = this.mapSeverity(item.severity ?? "error");

        const range = new vscode.Range(line, col, endLine, endCol);
        const diag = new vscode.Diagnostic(range, message, severity);
        diag.source = "SageFs";

        const existing = byFile.get(file) ?? [];
        existing.push(diag);
        byFile.set(file, existing);
      }

      // Clear old diagnostics and set new ones
      this.collection.clear();
      for (const [file, diags] of byFile) {
        if (file) {
          this.collection.set(vscode.Uri.file(file), diags);
        }
      }
    } catch {
      // Malformed JSON — ignore
    }
  }

  private mapSeverity(s: string): vscode.DiagnosticSeverity {
    switch (s.toLowerCase()) {
      case "error":
        return vscode.DiagnosticSeverity.Error;
      case "warning":
        return vscode.DiagnosticSeverity.Warning;
      case "info":
      case "information":
        return vscode.DiagnosticSeverity.Information;
      case "hint":
        return vscode.DiagnosticSeverity.Hint;
      default:
        return vscode.DiagnosticSeverity.Error;
    }
  }
}
