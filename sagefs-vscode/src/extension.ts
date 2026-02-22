import * as vscode from "vscode";
import { SageFsClient, EvalResult } from "./sageFsClient";
import { SageFsCodeLensProvider } from "./codeLensProvider";
import { DiagnosticsListener } from "./diagnosticsListener";
import * as child_process from "child_process";

let client: SageFsClient;
let outputChannel: vscode.OutputChannel;
let statusBarItem: vscode.StatusBarItem;
let daemonProcess: child_process.ChildProcess | undefined;
let codeLensProvider: SageFsCodeLensProvider;
let diagnosticsListener: DiagnosticsListener;

export function activate(context: vscode.ExtensionContext): void {
  const config = vscode.workspace.getConfiguration("sagefs");
  const mcpPort = config.get<number>("mcpPort", 37749);
  const dashboardPort = config.get<number>("dashboardPort", 37750);

  client = new SageFsClient(mcpPort, dashboardPort);
  outputChannel = vscode.window.createOutputChannel("SageFs");

  // Status bar
  statusBarItem = vscode.window.createStatusBarItem(
    vscode.StatusBarAlignment.Left,
    50
  );
  statusBarItem.command = "sagefs.openDashboard";
  statusBarItem.tooltip = "Click to open SageFs dashboard";
  context.subscriptions.push(statusBarItem);

  // Register commands
  context.subscriptions.push(
    vscode.commands.registerCommand("sagefs.eval", () => evalSelection()),
    vscode.commands.registerCommand("sagefs.evalFile", () => evalFile()),
    vscode.commands.registerCommand("sagefs.evalRange", (range: vscode.Range) => evalRange(range)),
    vscode.commands.registerCommand("sagefs.start", () => startDaemon()),
    vscode.commands.registerCommand("sagefs.stop", () => stopDaemon()),
    vscode.commands.registerCommand("sagefs.openDashboard", () => openDashboard()),
    vscode.commands.registerCommand("sagefs.resetSession", () => resetSession()),
    vscode.commands.registerCommand("sagefs.hardReset", () => hardReset())
  );

  // CodeLens provider — shows "▶ Eval" at ;; boundaries
  codeLensProvider = new SageFsCodeLensProvider();
  context.subscriptions.push(
    vscode.languages.registerCodeLensProvider(
      { language: "fsharp" },
      codeLensProvider
    )
  );

  // Hijack Ionide's "Send to FSI" if available
  hijackIonideSendToFsi(context);

  // Diagnostics — subscribe to SageFs's /diagnostics SSE stream
  diagnosticsListener = new DiagnosticsListener(mcpPort);
  context.subscriptions.push({ dispose: () => diagnosticsListener.dispose() });
  client.isRunning().then((running) => {
    if (running) {
      diagnosticsListener.start();
    }
  });

  // Listen for config changes
  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration("sagefs")) {
        const cfg = vscode.workspace.getConfiguration("sagefs");
        client.updatePorts(
          cfg.get<number>("mcpPort", 37749),
          cfg.get<number>("dashboardPort", 37750)
        );
      }
    })
  );

  // Initial status check
  refreshStatus();
  const statusInterval = setInterval(() => refreshStatus(), 5000);
  context.subscriptions.push({ dispose: () => clearInterval(statusInterval) });

  // Auto-start if configured
  const autoStart = config.get<boolean>("autoStart", true);
  if (autoStart) {
    client.isRunning().then((running) => {
      if (!running) {
        promptAutoStart();
      }
    });
  }
}

export function deactivate(): void {
  diagnosticsListener?.dispose();
  // Don't kill the daemon — it's a shared resource
}

// ── Eval Commands ──────────────────────────────────────────────

async function evalSelection(): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) {
    vscode.window.showWarningMessage("No active editor.");
    return;
  }

  if (!(await ensureRunning())) {
    return;
  }

  let code: string;
  if (!editor.selection.isEmpty) {
    code = editor.document.getText(editor.selection);
  } else {
    // Send current line (or block between ;; delimiters)
    code = getCodeBlock(editor);
  }

  if (!code.trim()) {
    return;
  }

  // Ensure code ends with ;;
  if (!code.trimEnd().endsWith(";;")) {
    code = code.trimEnd() + ";;";
  }

  const workDir = getWorkingDirectory();
  outputChannel.show(true);
  outputChannel.appendLine(`──── eval ────`);
  outputChannel.appendLine(code);
  outputChannel.appendLine("");

  try {
    const result = await client.evalCode(code, workDir);

    if (!result.success) {
      outputChannel.appendLine(`❌ Error:\n${result.error ?? result.result ?? "Unknown error"}`);
      showInlineDiagnostic(editor, result.error ?? result.result ?? "Error");
    } else {
      outputChannel.appendLine(`${result.result ?? ""}`);
      showInlineResult(editor, result.result ?? "");
    }
  } catch (err) {
    outputChannel.appendLine(`❌ Connection error: ${err}`);
    vscode.window.showErrorMessage(
      "Cannot reach SageFs daemon. Is it running?"
    );
  }
}

async function evalFile(): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) {
    return;
  }
  if (!(await ensureRunning())) {
    return;
  }

  const code = editor.document.getText();
  if (!code.trim()) {
    return;
  }

  const workDir = getWorkingDirectory();
  outputChannel.show(true);
  outputChannel.appendLine(`──── eval file: ${editor.document.fileName} ────`);

  try {
    const result = await client.evalCode(code, workDir);
    if (!result.success) {
      outputChannel.appendLine(`❌ Error:\n${result.error ?? result.result ?? "Unknown error"}`);
    } else {
      outputChannel.appendLine(result.result ?? "");
    }
  } catch (err) {
    outputChannel.appendLine(`❌ Connection error: ${err}`);
  }
}

async function evalRange(range: vscode.Range): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) {
    return;
  }
  if (!(await ensureRunning())) {
    return;
  }

  const code = editor.document.getText(range);
  if (!code.trim()) {
    return;
  }

  const workDir = getWorkingDirectory();
  outputChannel.show(true);
  outputChannel.appendLine(`──── eval block ────`);
  outputChannel.appendLine(code);
  outputChannel.appendLine("");

  try {
    const result = await client.evalCode(code, workDir);
    if (!result.success) {
      outputChannel.appendLine(`❌ Error:\n${result.error ?? result.result ?? "Unknown error"}`);
    } else {
      outputChannel.appendLine(result.result ?? "");
    }
  } catch (err) {
    outputChannel.appendLine(`❌ Connection error: ${err}`);
  }
}

// ── Session Management ─────────────────────────────────────────

async function resetSession(): Promise<void> {
  if (!(await ensureRunning())) {
    return;
  }
  const result = await client.resetSession();
  vscode.window.showInformationMessage(`SageFs: ${result.result ?? result.error ?? "Reset complete"}`);
  refreshStatus();
}

async function hardReset(): Promise<void> {
  if (!(await ensureRunning())) {
    return;
  }
  const result = await client.hardReset(true);
  vscode.window.showInformationMessage(`SageFs: ${result.result ?? result.error ?? "Hard reset complete"}`);
  refreshStatus();
}

// ── Daemon Lifecycle ───────────────────────────────────────────

async function startDaemon(): Promise<void> {
  const running = await client.isRunning();
  if (running) {
    vscode.window.showInformationMessage("SageFs daemon is already running.");
    refreshStatus();
    return;
  }

  const projPath = await findProject();
  if (!projPath) {
    vscode.window.showErrorMessage("No .fsproj found. Open an F# project first.");
    return;
  }

  outputChannel.show(true);
  outputChannel.appendLine(`Starting SageFs daemon with ${projPath}...`);

  const workDir = getWorkingDirectory() ?? ".";
  daemonProcess = child_process.spawn("sagefs", ["--proj", projPath], {
    cwd: workDir,
    detached: true,
    stdio: "ignore",
    shell: true,
  });
  daemonProcess.unref();

  // Poll until ready
  statusBarItem.text = "$(loading~spin) SageFs starting...";
  statusBarItem.show();

  let attempts = 0;
  const poll = setInterval(async () => {
    attempts++;
    if (await client.isRunning()) {
      clearInterval(poll);
      outputChannel.appendLine("SageFs daemon is ready.");
      vscode.window.showInformationMessage("SageFs daemon started.");
      diagnosticsListener.start();
      refreshStatus();
    } else if (attempts > 60) {
      clearInterval(poll);
      outputChannel.appendLine("Timed out waiting for SageFs daemon.");
      vscode.window.showErrorMessage("SageFs daemon failed to start.");
      statusBarItem.text = "$(error) SageFs: offline";
    }
  }, 2000);
}

async function stopDaemon(): Promise<void> {
  if (daemonProcess) {
    daemonProcess.kill();
    daemonProcess = undefined;
  }
  vscode.window.showInformationMessage("SageFs: stop the daemon from its terminal or use `sagefs stop`.");
  refreshStatus();
}

function openDashboard(): void {
  vscode.env.openExternal(vscode.Uri.parse(client.dashboardUrl));
}

async function promptAutoStart(): Promise<void> {
  const projPath = await findProject();
  if (!projPath) {
    return;
  }

  const choice = await vscode.window.showInformationMessage(
    `SageFs daemon is not running. Start it for ${projPath}?`,
    "Start SageFs",
    "Open Dashboard",
    "Not Now"
  );

  if (choice === "Start SageFs") {
    startDaemon();
  } else if (choice === "Open Dashboard") {
    openDashboard();
  }
}

// ── Status Bar ─────────────────────────────────────────────────

async function refreshStatus(): Promise<void> {
  try {
    const running = await client.isRunning();
    if (!running) {
      statusBarItem.text = "$(circle-slash) SageFs: offline";
      statusBarItem.backgroundColor = undefined;
      statusBarItem.show();
      return;
    }

    const status = await client.getStatus();
    if (status.connected) {
      statusBarItem.text = `$(zap) SageFs: ready`;
      statusBarItem.backgroundColor = undefined;
    } else {
      statusBarItem.text = "$(loading~spin) SageFs: starting...";
    }
    statusBarItem.show();
  } catch {
    statusBarItem.text = "$(circle-slash) SageFs: offline";
    statusBarItem.show();
  }
}

// ── Ensure Daemon Running ──────────────────────────────────────

async function ensureRunning(): Promise<boolean> {
  const running = await client.isRunning();
  if (running) {
    return true;
  }

  const choice = await vscode.window.showWarningMessage(
    "SageFs daemon is not running.",
    "Start SageFs",
    "Cancel"
  );
  if (choice === "Start SageFs") {
    await startDaemon();
    // Wait briefly for startup
    for (let i = 0; i < 15; i++) {
      await sleep(2000);
      if (await client.isRunning()) {
        return true;
      }
    }
    vscode.window.showErrorMessage("SageFs didn't start in time.");
    return false;
  }
  return false;
}

// ── Ionide Integration ─────────────────────────────────────────

function hijackIonideSendToFsi(context: vscode.ExtensionContext): void {
  // Override Ionide's "Send to FSI" commands to route through SageFs.
  // This is opt-in: if Ionide isn't installed, these are no-ops.
  const ionideCommands = [
    "fsi.SendSelection",
    "fsi.SendLine",
    "fsi.SendFile",
  ];

  for (const cmd of ionideCommands) {
    try {
      context.subscriptions.push(
        vscode.commands.registerCommand(cmd, () => {
          // Route to our eval — same behavior, through SageFs
          if (cmd === "fsi.SendFile") {
            vscode.commands.executeCommand("sagefs.evalFile");
          } else {
            vscode.commands.executeCommand("sagefs.eval");
          }
        })
      );
    } catch {
      // Command already registered by Ionide — that's fine, we can't override.
      // User can still use Alt+Enter directly.
    }
  }
}

// ── Helpers ────────────────────────────────────────────────────

function getCodeBlock(editor: vscode.TextEditor): string {
  const doc = editor.document;
  const pos = editor.selection.active;

  // Walk backwards to find start of block (line after ;; or start of file)
  let startLine = pos.line;
  while (startLine > 0) {
    const prevText = doc.lineAt(startLine - 1).text.trimEnd();
    if (prevText.endsWith(";;")) {
      break;
    }
    startLine--;
  }

  // Walk forwards to find end of block (line with ;; or end of file)
  let endLine = pos.line;
  while (endLine < doc.lineCount - 1) {
    const lineText = doc.lineAt(endLine).text.trimEnd();
    if (lineText.endsWith(";;")) {
      break;
    }
    endLine++;
  }

  const range = new vscode.Range(startLine, 0, endLine, doc.lineAt(endLine).text.length);
  return doc.getText(range);
}

function getWorkingDirectory(): string | undefined {
  const folders = vscode.workspace.workspaceFolders;
  return folders?.[0]?.uri.fsPath;
}

async function findProject(): Promise<string | undefined> {
  const config = vscode.workspace.getConfiguration("sagefs");
  const configured = config.get<string>("projectPath", "");
  if (configured) {
    return configured;
  }

  const files = await vscode.workspace.findFiles("**/*.fsproj", "**/node_modules/**", 10);
  if (files.length === 0) {
    return undefined;
  }
  if (files.length === 1) {
    return vscode.workspace.asRelativePath(files[0]);
  }

  // Multiple projects — let user pick
  const items = files.map((f) => vscode.workspace.asRelativePath(f));
  return vscode.window.showQuickPick(items, {
    placeHolder: "Select an F# project for SageFs",
  });
}

let inlineDecoration: vscode.TextEditorDecorationType | undefined;

function showInlineResult(editor: vscode.TextEditor, text: string): void {
  clearInlineDecorations();

  const firstLine = text.split("\n")[0]?.trim() ?? "";
  if (!firstLine) {
    return;
  }

  const line = editor.selection.isEmpty
    ? editor.selection.active.line
    : editor.selection.end.line;

  inlineDecoration = vscode.window.createTextEditorDecorationType({
    after: {
      contentText: `  // → ${firstLine}`,
      color: new vscode.ThemeColor("editorCodeLens.foreground"),
      fontStyle: "italic",
    },
  });

  editor.setDecorations(inlineDecoration, [
    new vscode.Range(line, 0, line, 0),
  ]);

  // Auto-clear after 10 seconds
  setTimeout(() => clearInlineDecorations(), 10000);
}

function showInlineDiagnostic(editor: vscode.TextEditor, text: string): void {
  clearInlineDecorations();

  const firstLine = text.split("\n")[0]?.trim() ?? "";
  if (!firstLine) {
    return;
  }

  const line = editor.selection.isEmpty
    ? editor.selection.active.line
    : editor.selection.end.line;

  inlineDecoration = vscode.window.createTextEditorDecorationType({
    after: {
      contentText: `  // ❌ ${firstLine}`,
      color: new vscode.ThemeColor("errorForeground"),
      fontStyle: "italic",
    },
  });

  editor.setDecorations(inlineDecoration, [
    new vscode.Range(line, 0, line, 0),
  ]);

  setTimeout(() => clearInlineDecorations(), 15000);
}

function clearInlineDecorations(): void {
  if (inlineDecoration) {
    inlineDecoration.dispose();
    inlineDecoration = undefined;
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
