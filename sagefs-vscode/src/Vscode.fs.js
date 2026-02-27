import { env, tests, languages, workspace, commands, window as window$ } from "vscode";
import * as vscode from "vscode";

export function Window_getActiveTextEditor() {
    return window$.activeTextEditor;
}

export function Window_getVisibleTextEditors() {
    return window$.visibleTextEditors;
}

export function Window_onDidChangeVisibleTextEditors(handler) {
    return window$.onDidChangeVisibleTextEditors(handler);
}

export function Window_onDidChangeActiveTextEditor(handler) {
    return window$.onDidChangeActiveTextEditor(handler);
}

export function Window_createOutputChannel(name) {
    return window$.createOutputChannel(name);
}

export function Window_createStatusBarItem(alignment, priority) {
    return window$.createStatusBarItem(alignment, priority);
}

export function Window_showInformationMessage(msg, items) {
    return window$.showInformationMessage(msg, ...items);
}

export function Window_showWarningMessage(msg, items) {
    return window$.showWarningMessage(msg, ...items);
}

export function Window_showErrorMessage(msg, items) {
    return window$.showErrorMessage(msg, ...items);
}

export function Window_showQuickPick(items, placeHolder) {
    return window$.showQuickPick(items, {
        placeHolder: placeHolder,
    });
}

export function Window_showInputBox(prompt) {
    return window$.showInputBox({
        prompt: prompt,
    });
}

export function Window_withProgress(location, title, task) {
    return window$.withProgress({
        location: location,
        title: title,
    }, task);
}

export function Window_createTextEditorDecorationType(opts) {
    return window$.createTextEditorDecorationType(opts);
}

export function Window_createTreeView(viewId, opts) {
    return window$.createTreeView(viewId, opts);
}

export function Window_createWebviewPanel(viewType, title, column, opts) {
    return window$.createWebviewPanel(viewType, title, column, opts);
}

export function Commands_registerCommand(command, handler) {
    return commands.registerCommand(command, handler);
}

export function Commands_executeCommand(command) {
    return commands.executeCommand(command);
}

export function Commands_getCommands() {
    return commands.getCommands();
}

export function Workspace_workspaceFolders() {
    return workspace.workspaceFolders;
}

export function Workspace_getConfiguration(section) {
    return workspace.getConfiguration(section);
}

export function Workspace_findFiles(pattern, exclude, maxResults) {
    return workspace.findFiles(pattern, exclude, maxResults);
}

export function Workspace_asRelativePath(uri) {
    return workspace.asRelativePath(uri);
}

export function Workspace_asRelativePathStr(path) {
    return workspace.asRelativePath(path);
}

export function Workspace_onDidChangeConfiguration(handler) {
    return workspace.onDidChangeConfiguration(handler);
}

export function Languages_registerCodeLensProvider(language, provider) {
    return languages.registerCodeLensProvider({
        language: language,
    }, provider);
}

export function Languages_registerCompletionItemProvider(language, provider, triggerChars) {
    return languages.registerCompletionItemProvider({
        language: language,
    }, provider, ...triggerChars);
}

export function Languages_createDiagnosticCollection(name) {
    return languages.createDiagnosticCollection(name);
}

export function Tests_createTestController(id, label) {
    return tests.createTestController(id, label);
}

export function Env_openExternal(uri) {
    return env.openExternal(uri);
}

export function newRange(sl, sc, el, ec) {
    return new vscode.Range(sl, sc, el, ec);
}

export function newPosition(l, c) {
    return new vscode.Position(l, c);
}

export function newThemeColor(id) {
    return new vscode.ThemeColor(id);
}

export function newThemeIcon(id) {
    return new vscode.ThemeIcon(id);
}

export function uriParse(s) {
    return vscode.Uri.parse(s);
}

export function uriFile(path) {
    return vscode.Uri.file(path);
}

export function newDiagnostic(range, message, severity) {
    return new vscode.Diagnostic(range, message, severity);
}

export function newTestMessage(message) {
    return new vscode.TestMessage(message);
}

export function newCodeLens(range, cmd) {
    return new vscode.CodeLens(range, cmd);
}

export function newEventEmitter() {
    return new vscode.EventEmitter();
}

export function newCompletionItem(label, kind) {
    return new vscode.CompletionItem(label, kind);
}

export function newTreeItem(label, collapsibleState) {
    return new vscode.TreeItem(label, collapsibleState);
}

