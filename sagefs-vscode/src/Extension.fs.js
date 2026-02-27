import { defaultOf, equals, comparePrimitives, createAtom } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { add, iterate, remove, tryFind, empty } from "./fable_modules/fable-library-js.4.29.0/Map.js";
import { Workspace_onDidChangeConfiguration, Window_onDidChangeActiveTextEditor, Window_onDidChangeVisibleTextEditors, Languages_registerCompletionItemProvider, Languages_registerCodeLensProvider, Window_showInputBox, Languages_createDiagnosticCollection, Window_createStatusBarItem, Window_createOutputChannel, Commands_executeCommand, Commands_registerCommand, Window_createWebviewPanel, Window_withProgress, Window_getActiveTextEditor, Window_showWarningMessage, Window_showErrorMessage, Window_showInformationMessage, newThemeColor, Window_createTextEditorDecorationType, newRange, Window_showQuickPick, Workspace_asRelativePath, Workspace_findFiles, Workspace_getConfiguration, Workspace_workspaceFolders } from "./Vscode.fs.js";
import { tryFindIndex, last, tryFind as tryFind_1, tryHead, map, append, item } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { PromiseBuilder__For_1565554B, PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { substring, split, join, printf, toText, trimEnd } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { map as map_1, bind, orElse, toArray, defaultArg, some } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { updatePorts, getDependencyGraph, getRecentEvents, setRunPolicy, runTests, disableLiveTesting, enableLiveTesting, create, dashboardUrl, stopSession, switchSession, createSession, hardReset, resetSession, evalCode, listSessions, getSystemStatus, getStatus, isRunning } from "./SageFsClient.fs.js";
import { register, setSession } from "./HotReloadTreeProvider.fs.js";
import { register as register_1, setSession as setSession_1 } from "./SessionContextTreeProvider.fs.js";
import { iterate as iterate_1 } from "./fable_modules/fable-library-js.4.29.0/Seq.js";
import { start } from "./DiagnosticsListener.fs.js";
import { rangeDouble } from "./fable_modules/fable-library-js.4.29.0/Range.js";
import { toString } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { create as create_1 } from "./TypeExplorerProvider.fs.js";
import { create as create_2 } from "./CodeLensProvider.fs.js";
import { updateState, create as create_3 } from "./TestCodeLensProvider.fs.js";
import { create as create_4 } from "./CompletionProvider.fs.js";
import { create as create_5 } from "./TestControllerAdapter.fs.js";
import { dispose, updateDiagnostics, applyCoverageToAllEditors, applyToAllEditors, initialize } from "./TestDecorations.fs.js";
import { LiveTestingCallbacks, start as start_1 } from "./LiveTestingListener.fs.js";
import { VscLiveTestStateModule_empty } from "./LiveTestingTypes.fs.js";

export let client = createAtom(undefined);

export let outputChannel = createAtom(undefined);

export let statusBarItem = createAtom(undefined);

export let testStatusBarItem = createAtom(undefined);

export let diagnosticsDisposable = createAtom(undefined);

export let sseDisposable = createAtom(undefined);

export let diagnosticCollection = createAtom(undefined);

export let blockDecorations = createAtom(empty({
    Compare: comparePrimitives,
}));

export let activeSessionId = createAtom(undefined);

export let liveTestListener = createAtom(undefined);

export let testAdapter = createAtom(undefined);

export let dashboardPanel = createAtom(undefined);

export let typeExplorer = createAtom(undefined);

export let daemonProcess = createAtom(undefined);

export function getClient() {
    if (client() == null) {
        throw new Error("SageFs not activated");
    }
    else {
        return client();
    }
}

export function getOutput() {
    if (outputChannel() == null) {
        throw new Error("SageFs not activated");
    }
    else {
        return outputChannel();
    }
}

export function getStatusBar() {
    if (statusBarItem() == null) {
        throw new Error("SageFs not activated");
    }
    else {
        return statusBarItem();
    }
}

export function getWorkingDirectory() {
    let fs;
    const matchValue = Workspace_workspaceFolders();
    let matchResult, fs_1;
    if (matchValue != null) {
        if ((fs = matchValue, fs.length > 0)) {
            matchResult = 0;
            fs_1 = matchValue;
        }
        else {
            matchResult = 1;
        }
    }
    else {
        matchResult = 1;
    }
    switch (matchResult) {
        case 0:
            return item(0, fs_1).uri.fsPath;
        default:
            return undefined;
    }
}

export function findProject() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const config = Workspace_getConfiguration("sagefs");
        const configured = config.get("projectPath", "");
        return (configured !== "") ? (Promise.resolve(configured)) : (Workspace_findFiles("**/*.{sln,slnx}", "**/node_modules/**", 5).then((_arg) => (Workspace_findFiles("**/*.fsproj", "**/node_modules/**", 10).then((_arg_1) => {
            const all = append(map(Workspace_asRelativePath, _arg), map(Workspace_asRelativePath, _arg_1));
            return (all.length === 0) ? (Promise.resolve(undefined)) : ((all.length === 1) ? (Promise.resolve(item(0, all))) : (Window_showQuickPick(all, "Select a solution or project for SageFs").then((_arg_2) => (Promise.resolve(_arg_2)))));
        }))));
    }));
}

export function getCodeBlock(editor) {
    const doc = editor.document;
    const pos = editor.selection.active;
    let startLine = ~~pos.line;
    while (startLine > 0) {
        const prevText = trimEnd(doc.lineAt(startLine - 1).text);
        if (prevText.endsWith(";;")) {
            startLine = (startLine | 0);
        }
        else {
            startLine = ((startLine - 1) | 0);
        }
    }
    let endLine = ~~pos.line;
    while (endLine < (~~doc.lineCount - 1)) {
        const lineText = trimEnd(doc.lineAt(endLine).text);
        if (lineText.endsWith(";;")) {
            endLine = (endLine | 0);
        }
        else {
            endLine = ((endLine + 1) | 0);
        }
    }
    const range = newRange(startLine, 0, endLine, doc.lineAt(endLine).text.length);
    return doc.getText(range);
}

export function clearBlockDecoration(line) {
    const matchValue = tryFind(line, blockDecorations());
    if (matchValue == null) {
    }
    else {
        const deco = matchValue;
        const value = deco.dispose();
        blockDecorations(remove(line, blockDecorations()));
    }
}

export function clearAllDecorations() {
    iterate((_arg, deco) => {
        const value = deco.dispose();
    }, blockDecorations());
    blockDecorations(empty({
        Compare: comparePrimitives,
    }));
}

export function showInlineResult(editor, text) {
    let summary, arg_2;
    const trimmed = text.trim();
    if (trimmed === "") {
    }
    else {
        const line = (editor.selection.isEmpty ? ~~editor.selection.active.line : ~~editor.selection.end.line) | 0;
        clearBlockDecoration(line);
        const lines = trimmed.split("\n");
        const firstLine = (lines.length > 0) ? item(0, lines) : "";
        const deco = Window_createTextEditorDecorationType({
            after: {
                contentText: (lines.length <= 1) ? toText(printf("  // → %s"))(firstLine) : ((summary = ((lines.length <= 4) ? join("  │  ", lines) : ((arg_2 = (lines.length | 0), toText(printf("%s  │  ... (%d lines)"))(firstLine)(arg_2)))), toText(printf("  // → %s"))(summary))),
                color: newThemeColor("sagefs.successForeground"),
                fontStyle: "italic",
            },
        });
        const lineText = editor.document.lineAt(line).text;
        const endCol = lineText.length | 0;
        const range = newRange(line, endCol, line, endCol);
        editor.setDecorations(deco, [range]);
        blockDecorations(add(line, deco, blockDecorations()));
        setTimeout((() => {
            clearBlockDecoration(line);
        }), 30000);
    }
}

export function showInlineDiagnostic(editor, text) {
    let firstLine;
    const parts = text.split("\n");
    firstLine = ((parts.length > 0) ? item(0, parts).trim() : "");
    if (firstLine === "") {
    }
    else {
        const line = (editor.selection.isEmpty ? ~~editor.selection.active.line : ~~editor.selection.end.line) | 0;
        clearBlockDecoration(line);
        const deco = Window_createTextEditorDecorationType({
            after: {
                contentText: toText(printf("  // ❌ %s"))(firstLine),
                color: newThemeColor("sagefs.errorForeground"),
                fontStyle: "italic",
            },
        });
        const lineText = editor.document.lineAt(line).text;
        const endCol = lineText.length | 0;
        const range = newRange(line, endCol, line, endCol);
        editor.setDecorations(deco, [range]);
        blockDecorations(add(line, deco, blockDecorations()));
        setTimeout((() => {
            clearBlockDecoration(line);
        }), 30000);
    }
}

export function updateTestStatusBar(summary) {
    if (testStatusBarItem() != null) {
        const sb = testStatusBarItem();
        if (summary.Total === 0) {
            sb.text = "$(beaker) No tests";
            sb.backgroundColor = undefined;
        }
        else if (summary.Failed > 0) {
            sb.text = toText(printf("$(testing-error-icon) %d/%d failed"))(summary.Failed)(summary.Total);
            sb.backgroundColor = some(newThemeColor("statusBarItem.errorBackground"));
        }
        else if (summary.Running > 0) {
            sb.text = toText(printf("$(sync~spin) Running %d/%d"))(summary.Running)(summary.Total);
            sb.backgroundColor = undefined;
        }
        else if (summary.Stale > 0) {
            sb.text = toText(printf("$(warning) %d/%d stale"))(summary.Stale)(summary.Total);
            sb.backgroundColor = some(newThemeColor("statusBarItem.warningBackground"));
        }
        else {
            sb.text = toText(printf("$(testing-passed-icon) %d/%d passed"))(summary.Passed)(summary.Total);
            sb.backgroundColor = undefined;
        }
        sb.show();
    }
}

export function refreshStatus() {
    PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const c = getClient();
        const sb = getStatusBar();
        return PromiseBuilder__Delay_62FBFDE1(promise, () => (isRunning(c).then((_arg) => {
            if (!_arg) {
                sb.text = "$(circle-slash) SageFs: offline";
                sb.backgroundColor = undefined;
                sb.show();
                activeSessionId(undefined);
                setSession(c, undefined);
                setSession_1(c, undefined);
                return Promise.resolve();
            }
            else {
                return getStatus(c).then((_arg_1) => (getSystemStatus(c).then((_arg_2) => {
                    let s_1, s_3;
                    const sys = _arg_2;
                    const supervised = (sys != null) ? (sys.supervised ? ((s_1 = sys, " $(shield)")) : "") : "";
                    const restarts = (sys != null) ? ((sys.restartCount > 0) ? ((s_3 = sys, toText(printf(" %d↻"))(s_3.restartCount))) : "") : "";
                    return (_arg_1.connected ? (listSessions(c).then((_arg_3) => {
                        let s_5, projLabel, evalLabel;
                        const sessions = _arg_3;
                        let session;
                        if (activeSessionId() == null) {
                            session = tryHead(sessions);
                        }
                        else {
                            const id = activeSessionId();
                            session = tryFind_1((s_4) => (s_4.id === id), sessions);
                        }
                        return ((session == null) ? ((activeSessionId(undefined), (sb.text = toText(printf("$(zap) SageFs: ready (no session)%s%s"))(supervised)(restarts), Promise.resolve()))) : ((s_5 = session, (activeSessionId(s_5.id), (projLabel = ((s_5.projects.length > 0) ? join(",", map((p) => {
                            const name = last(split(p, ["/", "\\"]));
                            if (name.endsWith(".fsproj")) {
                                return name.slice(undefined, (name.length - 8) + 1);
                            }
                            else if (name.endsWith(".slnx")) {
                                return name.slice(undefined, (name.length - 6) + 1);
                            }
                            else if (name.endsWith(".sln")) {
                                return name.slice(undefined, (name.length - 5) + 1);
                            }
                            else {
                                return name;
                            }
                        }, s_5.projects)) : "session"), (evalLabel = ((s_5.evalCount > 0) ? toText(printf(" [%d]"))(s_5.evalCount) : ""), (sb.text = toText(printf("$(zap) SageFs: %s%s%s%s"))(projLabel)(evalLabel)(supervised)(restarts), Promise.resolve()))))))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                            sb.backgroundColor = undefined;
                            const activeId = activeSessionId();
                            setSession(c, activeId);
                            setSession_1(c, activeId);
                            return Promise.resolve();
                        }));
                    })) : ((sb.text = "$(loading~spin) SageFs: starting...", Promise.resolve()))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                        sb.show();
                        return Promise.resolve();
                    }));
                })));
            }
        }))).catch((_arg_4) => {
            sb.text = "$(circle-slash) SageFs: offline";
            sb.show();
            return Promise.resolve();
        });
    }));
}

export function startDaemon() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const c = getClient();
        return isRunning(c).then((_arg) => {
            if (_arg) {
                Window_showInformationMessage("SageFs daemon is already running.", []);
                refreshStatus();
                return Promise.resolve();
            }
            else {
                return findProject().then((_arg_1) => {
                    const projPath = _arg_1;
                    if (projPath != null) {
                        const proj = projPath;
                        const out = getOutput();
                        out.show(true);
                        out.appendLine(toText(printf("Starting SageFs daemon with %s..."))(proj));
                        const workDir = defaultArg(getWorkingDirectory(), ".");
                        let ext;
                        const i = proj.lastIndexOf(".") | 0;
                        ext = ((i >= 0) ? substring(proj, i) : "");
                        const flag = ((ext === ".sln") ? true : (ext === ".slnx")) ? "--sln" : "--proj";
                        const proc = require('child_process').spawn("sagefs", [flag, proj], {
                            cwd: workDir,
                            detached: true,
                            stdio: ["ignore", "pipe", "pipe"],
                            shell: true,
                        });
                        proc.on('error', function(e) { ((msg) => {
                            out.appendLine(toText(printf("[SageFs spawn error] %s"))(msg));
                            const sb = getStatusBar();
                            sb.text = "$(error) SageFs: spawn failed";
                        })(e.message || String(e)) });
                        proc.on('exit', function(code, signal) { ((code, _signal) => {
                            out.appendLine(toText(printf("[SageFs] process exited (code %d)"))(code));
                        })(code == null ? -1 : code, signal == null ? '' : signal) });
                        const stderr = proc.stderr;
                        return (!(stderr == null) ? ((stderr.on('data', function(d) { if (d != null) ((chunk) => {
                            out.appendLine(chunk);
                        })(String(d)) }), Promise.resolve())) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                            const stdout = proc.stdout;
                            return (!(stdout == null) ? ((stdout.on('data', function(d) { if (d != null) ((chunk_1) => {
                                out.appendLine(chunk_1);
                            })(String(d)) }), Promise.resolve())) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                proc.unref();
                                daemonProcess(some(proc));
                                const sb_1 = getStatusBar();
                                sb_1.text = "$(loading~spin) SageFs starting...";
                                sb_1.show();
                                let attempts = 0;
                                let intervalId = undefined;
                                const id_2 = setInterval((() => {
                                    let arg_3;
                                    attempts = ((attempts + 1) | 0);
                                    sb_1.text = ((arg_3 = ((attempts * 2) | 0), toText(printf("$(loading~spin) SageFs starting... (%ds)"))(arg_3)));
                                    const pr = isRunning(c);
                                    void (pr.then((ready) => {
                                        if (ready) {
                                            iterate_1((id) => {
                                                clearInterval(id);
                                            }, toArray(intervalId));
                                            out.appendLine("SageFs daemon is ready.");
                                            Window_showInformationMessage("SageFs daemon started.", []);
                                            if (diagnosticCollection() == null) {
                                            }
                                            else {
                                                const dc = diagnosticCollection();
                                                iterate_1((d) => {
                                                    d.dispose();
                                                }, toArray(diagnosticsDisposable()));
                                                diagnosticsDisposable(start(c.mcpPort, dc));
                                            }
                                            refreshStatus();
                                        }
                                        else if (attempts > 60) {
                                            iterate_1((id_1) => {
                                                clearInterval(id_1);
                                            }, toArray(intervalId));
                                            out.appendLine("Timed out waiting for SageFs daemon after 120s.");
                                            Window_showErrorMessage("SageFs daemon failed to start after 120s.", []);
                                            sb_1.text = "$(error) SageFs: offline";
                                        }
                                    }));
                                }), 2000);
                                intervalId = some(id_2);
                                return Promise.resolve();
                            }));
                        }));
                    }
                    else {
                        Window_showErrorMessage("No .fsproj or .sln found. Open an F# project first.", []);
                        return Promise.resolve();
                    }
                });
            }
        });
    }));
}

export function ensureRunning() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const c = getClient();
        return isRunning(c).then((_arg) => (_arg ? (Promise.resolve(true)) : (Window_showWarningMessage("SageFs daemon is not running.", ["Start SageFs", "Cancel"]).then((_arg_1) => (equals(_arg_1, "Start SageFs") ? (startDaemon().then(() => {
            let ready = false;
            return PromiseBuilder__For_1565554B(promise, rangeDouble(0, 1, 14), (_arg_3) => (!ready ? ((new Promise(resolve => setTimeout(resolve, 2000))).then(() => (isRunning(c).then((_arg_5) => {
                if (_arg_5) {
                    ready = true;
                    return Promise.resolve();
                }
                else {
                    return Promise.resolve();
                }
            })))) : (Promise.resolve()))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => ((!ready ? ((void Window_showErrorMessage("SageFs didn\'t start in time.", []), Promise.resolve())) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => (Promise.resolve(ready)))))));
        })) : (Promise.resolve(false)))))));
    }));
}

export function evalSelection() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const matchValue = Window_getActiveTextEditor();
        if (matchValue != null) {
            const ed = matchValue;
            return ensureRunning().then((_arg) => {
                if (_arg) {
                    let code = !ed.selection.isEmpty ? (ed.document.getText(newRange(~~ed.selection.start.line, ~~ed.selection.start.character, ~~ed.selection.end.line, ~~ed.selection.end.character))) : getCodeBlock(ed);
                    return (code.trim() !== "") ? ((!trimEnd(code).endsWith(";;") ? ((code = (trimEnd(code) + ";;"), Promise.resolve())) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                        const workDir = getWorkingDirectory();
                        const out = getOutput();
                        return Window_withProgress(10, "SageFs: evaluating...", (_progress, _token) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
                            out.appendLine("──── eval ────");
                            out.appendLine(code);
                            out.appendLine("");
                            return PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                const c = getClient();
                                return evalCode(code, workDir, c).then((_arg_1) => {
                                    const result = _arg_1;
                                    if (!result.success) {
                                        const errMsg = defaultArg(orElse(result.error, result.result), "Unknown error");
                                        out.appendLine(toText(printf("❌ Error:\n%s"))(errMsg));
                                        out.show(true);
                                        showInlineDiagnostic(ed, errMsg);
                                        return Promise.resolve();
                                    }
                                    else {
                                        const output = defaultArg(result.result, "");
                                        out.appendLine(output);
                                        showInlineResult(ed, output);
                                        return Promise.resolve();
                                    }
                                });
                            }).catch((_arg_2) => {
                                let arg_1;
                                out.appendLine((arg_1 = toString(_arg_2), toText(printf("❌ Connection error: %s"))(arg_1)));
                                out.show(true);
                                Window_showErrorMessage("Cannot reach SageFs daemon. Is it running?", []);
                                return Promise.resolve();
                            });
                        }))).then(() => (Promise.resolve(undefined)));
                    }))) : (Promise.resolve());
                }
                else {
                    return Promise.resolve();
                }
            });
        }
        else {
            Window_showWarningMessage("No active editor.", []);
            return Promise.resolve();
        }
    }));
}

export function evalFile() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const matchValue = Window_getActiveTextEditor();
        if (matchValue != null) {
            const ed = matchValue;
            return ensureRunning().then((_arg) => {
                let arg;
                if (_arg) {
                    const code = ed.document.getText();
                    if (code.trim() !== "") {
                        const workDir = getWorkingDirectory();
                        const out = getOutput();
                        out.show(true);
                        out.appendLine((arg = ed.document.fileName, toText(printf("──── eval file: %s ────"))(arg)));
                        return PromiseBuilder__Delay_62FBFDE1(promise, () => {
                            const c = getClient();
                            return evalCode(code, workDir, c).then((_arg_1) => {
                                let arg_1;
                                const result = _arg_1;
                                if (!result.success) {
                                    out.appendLine((arg_1 = defaultArg(orElse(result.error, result.result), "Unknown error"), toText(printf("❌ Error:\n%s"))(arg_1)));
                                    return Promise.resolve();
                                }
                                else {
                                    out.appendLine(defaultArg(result.result, ""));
                                    return Promise.resolve();
                                }
                            });
                        }).catch((_arg_2) => {
                            let arg_2;
                            out.appendLine((arg_2 = toString(_arg_2), toText(printf("❌ Connection error: %s"))(arg_2)));
                            return Promise.resolve();
                        });
                    }
                    else {
                        return Promise.resolve();
                    }
                }
                else {
                    return Promise.resolve();
                }
            });
        }
        else {
            return Promise.resolve();
        }
    }));
}

export function evalRange(args) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const matchValue = Window_getActiveTextEditor();
        if (matchValue != null) {
            const ed = matchValue;
            return ensureRunning().then((_arg) => {
                if (_arg) {
                    const code = ed.document.getText(args);
                    if (code.trim() !== "") {
                        const workDir = getWorkingDirectory();
                        const out = getOutput();
                        out.show(true);
                        out.appendLine("──── eval block ────");
                        out.appendLine(code);
                        out.appendLine("");
                        return PromiseBuilder__Delay_62FBFDE1(promise, () => {
                            const c = getClient();
                            return evalCode(code, workDir, c).then((_arg_1) => {
                                let arg;
                                const result = _arg_1;
                                if (!result.success) {
                                    out.appendLine((arg = defaultArg(orElse(result.error, result.result), "Unknown error"), toText(printf("❌ Error:\n%s"))(arg)));
                                    return Promise.resolve();
                                }
                                else {
                                    out.appendLine(defaultArg(result.result, ""));
                                    return Promise.resolve();
                                }
                            });
                        }).catch((_arg_2) => {
                            let arg_1;
                            out.appendLine((arg_1 = toString(_arg_2), toText(printf("❌ Connection error: %s"))(arg_1)));
                            return Promise.resolve();
                        });
                    }
                    else {
                        return Promise.resolve();
                    }
                }
                else {
                    return Promise.resolve();
                }
            });
        }
        else {
            return Promise.resolve();
        }
    }));
}

export function resetSessionCmd() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (ensureRunning().then((_arg) => {
        if (_arg) {
            const c = getClient();
            return resetSession(c).then((_arg_1) => {
                const result = _arg_1;
                const msg = defaultArg(orElse(result.result, result.error), "Reset complete");
                Window_showInformationMessage(toText(printf("SageFs: %s"))(msg), []);
                refreshStatus();
                return Promise.resolve();
            });
        }
        else {
            return Promise.resolve();
        }
    }))));
}

export function hardResetCmd() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (ensureRunning().then((_arg) => {
        if (_arg) {
            const c = getClient();
            return hardReset(true, c).then((_arg_1) => {
                const result = _arg_1;
                const msg = defaultArg(orElse(result.result, result.error), "Hard reset complete");
                Window_showInformationMessage(toText(printf("SageFs: %s"))(msg), []);
                refreshStatus();
                return Promise.resolve();
            });
        }
        else {
            return Promise.resolve();
        }
    }))));
}

export function createSessionCmd() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (ensureRunning().then((_arg) => (_arg ? (findProject().then((_arg_1) => {
        const projPath = _arg_1;
        if (projPath != null) {
            const proj = projPath;
            const workDir = defaultArg(getWorkingDirectory(), ".");
            return Window_withProgress(15, "SageFs: Creating session...", (_p, _t) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
                const c = getClient();
                return createSession(proj, workDir, c).then((_arg_2) => {
                    let arg_1;
                    const result = _arg_2;
                    return (result.success ? ((void Window_showInformationMessage(toText(printf("SageFs: Session created for %s"))(proj), []), Promise.resolve())) : ((void Window_showErrorMessage((arg_1 = defaultArg(result.error, "Failed to create session"), toText(printf("SageFs: %s"))(arg_1)), []), Promise.resolve()))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                        refreshStatus();
                        return Promise.resolve();
                    }));
                });
            }))).then(() => (Promise.resolve(undefined)));
        }
        else {
            Window_showErrorMessage("No .fsproj or .sln found. Open an F# project first.", []);
            return Promise.resolve();
        }
    })) : (Promise.resolve()))))));
}

export function switchSessionCmd() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (ensureRunning().then((_arg) => {
        if (_arg) {
            const c = getClient();
            return listSessions(c).then((_arg_1) => {
                const sessions = _arg_1;
                if (sessions.length === 0) {
                    Window_showInformationMessage("No sessions available.", []);
                    return Promise.resolve();
                }
                else {
                    const items = map((s) => {
                        const proj = (s.projects.length > 0) ? join(", ", s.projects) : "no project";
                        return toText(printf("%s (%s) [%s]"))(s.id)(proj)(s.status);
                    }, sessions);
                    return Window_showQuickPick(items, "Select a session").then((_arg_2) => {
                        const picked = _arg_2;
                        if (picked == null) {
                            return Promise.resolve();
                        }
                        else {
                            const label = picked;
                            const idx = tryFindIndex((i) => (i === label), items);
                            if (idx == null) {
                                return Promise.resolve();
                            }
                            else {
                                const sess = item(idx, sessions);
                                return switchSession(sess.id, c).then((_arg_3) => ((_arg_3 ? ((activeSessionId(sess.id), (void Window_showInformationMessage(toText(printf("Switched to session %s"))(sess.id), []), Promise.resolve()))) : ((void Window_showErrorMessage("Failed to switch session.", []), Promise.resolve()))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                    refreshStatus();
                                    return Promise.resolve();
                                }))));
                            }
                        }
                    });
                }
            });
        }
        else {
            return Promise.resolve();
        }
    }))));
}

export function stopSessionCmd() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (ensureRunning().then((_arg) => {
        if (_arg) {
            const c = getClient();
            return listSessions(c).then((_arg_1) => {
                const sessions = _arg_1;
                if (sessions.length === 0) {
                    Window_showInformationMessage("No sessions available.", []);
                    return Promise.resolve();
                }
                else {
                    const items = map((s) => {
                        const proj = (s.projects.length > 0) ? join(", ", s.projects) : "no project";
                        return toText(printf("%s (%s) [%s]"))(s.id)(proj)(s.status);
                    }, sessions);
                    return Window_showQuickPick(items, "Select a session to stop").then((_arg_2) => {
                        const picked = _arg_2;
                        if (picked == null) {
                            return Promise.resolve();
                        }
                        else {
                            const label = picked;
                            const idx = tryFindIndex((i) => (i === label), items);
                            if (idx == null) {
                                return Promise.resolve();
                            }
                            else {
                                const sess = item(idx, sessions);
                                return stopSession(sess.id, c).then((_arg_3) => ((_arg_3 ? ((equals(activeSessionId(), sess.id) ? ((activeSessionId(undefined), Promise.resolve())) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                    Window_showInformationMessage(toText(printf("Stopped session %s"))(sess.id), []);
                                    return Promise.resolve();
                                }))) : ((void Window_showErrorMessage("Failed to stop session.", []), Promise.resolve()))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                    refreshStatus();
                                    return Promise.resolve();
                                }))));
                            }
                        }
                    });
                }
            });
        }
        else {
            return Promise.resolve();
        }
    }))));
}

export function stopDaemon() {
    iterate_1((proc) => {
        proc.kill();
    }, toArray(daemonProcess()));
    daemonProcess(undefined);
    Window_showInformationMessage("SageFs: stop the daemon from its terminal or use `sagefs stop`.", []);
    refreshStatus();
}

export function openDashboard() {
    const dashUrl = dashboardUrl(getClient());
    if (dashboardPanel() == null) {
        const panel_1 = Window_createWebviewPanel("sagefsDashboard", "SageFs Dashboard", 2, {
            enableScripts: true,
        });
        panel_1.webview.html = toText(printf("<!DOCTYPE html>\r\n<html style=\"height:100%%;margin:0;padding:0\">\r\n<body style=\"height:100%%;margin:0;padding:0\">\r\n<iframe src=\"%s\" style=\"width:100%%;height:100%%;border:none\"></iframe>\r\n</body>\r\n</html>"))(dashUrl);
        panel_1.onDidDispose(() => {
            dashboardPanel(undefined);
        });
        dashboardPanel(panel_1);
    }
    else {
        const panel = dashboardPanel();
        panel.reveal(1);
    }
}

export function promptAutoStart() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (findProject().then((_arg) => {
        const projPath = _arg;
        if (projPath != null) {
            const proj = projPath;
            return Window_showInformationMessage(toText(printf("SageFs daemon is not running. Start it for %s?"))(proj), ["Start SageFs", "Open Dashboard", "Not Now"]).then((_arg_1) => {
                const choice = _arg_1;
                let matchResult;
                if (choice != null) {
                    switch (choice) {
                        case "Start SageFs": {
                            matchResult = 0;
                            break;
                        }
                        case "Open Dashboard": {
                            matchResult = 1;
                            break;
                        }
                        default:
                            matchResult = 2;
                    }
                }
                else {
                    matchResult = 2;
                }
                switch (matchResult) {
                    case 0:
                        return startDaemon().then(() => (Promise.resolve(undefined)));
                    case 1: {
                        openDashboard();
                        return Promise.resolve();
                    }
                    default: {
                        return Promise.resolve();
                    }
                }
            });
        }
        else {
            return Promise.resolve();
        }
    }))));
}

export function hijackIonideSendToFsi(subs) {
    const arr = ["fsi.SendSelection", "fsi.SendLine", "fsi.SendFile"];
    for (let idx = 0; idx <= (arr.length - 1); idx++) {
        const cmd = item(idx, arr);
        try {
            const disp = Commands_registerCommand(cmd, (_arg) => {
                if (cmd === "fsi.SendFile") {
                    Commands_executeCommand("sagefs.evalFile");
                }
                else {
                    Commands_executeCommand("sagefs.eval");
                }
            });
            void (subs.push(disp));
        }
        catch (matchValue) {
        }
    }
}

export function activate(context) {
    const config = Workspace_getConfiguration("sagefs");
    const c = create(config.get("mcpPort", 37749), config.get("dashboardPort", 37750));
    client(c);
    const out = Window_createOutputChannel("SageFs");
    outputChannel(out);
    const sb = Window_createStatusBarItem(1, 50);
    sb.command = "sagefs.openDashboard";
    sb.tooltip = "Click to open SageFs dashboard";
    statusBarItem(sb);
    void (context.subscriptions.push(sb));
    const tsb = Window_createStatusBarItem(1, 49);
    tsb.text = "$(beaker) No tests";
    tsb.tooltip = "SageFs live testing — click to enable";
    tsb.command = "sagefs.enableLiveTesting";
    testStatusBarItem(tsb);
    void (context.subscriptions.push(tsb));
    const dc = Languages_createDiagnosticCollection("sagefs");
    diagnosticCollection(dc);
    void (context.subscriptions.push(dc));
    register(context);
    setSession(c, undefined);
    register_1(context);
    setSession_1(c, undefined);
    typeExplorer(create_1(context, client()));
    const reg = (cmd, handler) => {
        void (context.subscriptions.push(Commands_registerCommand(cmd, handler)));
    };
    reg("sagefs.eval", (_arg) => {
        evalSelection();
    });
    reg("sagefs.evalFile", (_arg_1) => {
        evalFile();
    });
    reg("sagefs.evalRange", (args) => {
        evalRange(args);
    });
    reg("sagefs.start", (_arg_2) => {
        startDaemon();
    });
    reg("sagefs.stop", (_arg_3) => {
        stopDaemon();
    });
    reg("sagefs.openDashboard", (_arg_4) => {
        openDashboard();
    });
    reg("sagefs.resetSession", (_arg_5) => {
        resetSessionCmd();
    });
    reg("sagefs.hardReset", (_arg_6) => {
        hardResetCmd();
    });
    reg("sagefs.createSession", (_arg_7) => {
        createSessionCmd();
    });
    reg("sagefs.switchSession", (_arg_8) => {
        switchSessionCmd();
    });
    reg("sagefs.stopSession", (_arg_9) => {
        stopSessionCmd();
    });
    reg("sagefs.clearResults", (_arg_10) => {
        clearAllDecorations();
    });
    reg("sagefs.enableLiveTesting", (_arg_11) => {
        if (client() == null) {
            Window_showWarningMessage("SageFs is not connected", []);
        }
        else {
            const pr = enableLiveTesting(client());
            void (pr.then((result) => {
                const matchValue = result.result;
                if (matchValue == null) {
                }
                else {
                    Window_showInformationMessage(matchValue, []);
                }
            }));
        }
    });
    reg("sagefs.disableLiveTesting", (_arg_12) => {
        if (client() == null) {
            Window_showWarningMessage("SageFs is not connected", []);
        }
        else {
            const pr_1 = disableLiveTesting(client());
            void (pr_1.then((result_1) => {
                const matchValue_1 = result_1.result;
                if (matchValue_1 == null) {
                }
                else {
                    Window_showInformationMessage(matchValue_1, []);
                }
            }));
        }
    });
    reg("sagefs.runTests", (_arg_13) => {
        if (client() == null) {
            Window_showWarningMessage("SageFs is not connected", []);
        }
        else {
            const pr_2 = runTests("", client());
            void (pr_2.then((result_2) => {
                const matchValue_2 = result_2.result;
                if (matchValue_2 == null) {
                }
                else {
                    Window_showInformationMessage(matchValue_2, []);
                }
            }));
        }
    });
    reg("sagefs.setRunPolicy", (_arg_14) => {
        if (client() == null) {
            Window_showWarningMessage("SageFs is not connected", []);
        }
        else {
            const c_4 = client();
            const pr_5 = Window_showQuickPick(["unit", "integration", "browser", "benchmark", "architecture", "property"], "Select test category");
            void (pr_5.then((catOpt) => {
                if (catOpt == null) {
                }
                else {
                    const cat = catOpt;
                    const pr_4 = Window_showQuickPick(["every", "save", "demand", "disabled"], toText(printf("Set policy for %s tests"))(cat));
                    void (pr_4.then((polOpt) => {
                        if (polOpt == null) {
                        }
                        else {
                            const pr_3 = setRunPolicy(cat, polOpt, c_4);
                            void (pr_3.then((result_3) => {
                                const matchValue_3 = result_3.result;
                                if (matchValue_3 == null) {
                                }
                                else {
                                    Window_showInformationMessage(matchValue_3, []);
                                }
                            }));
                        }
                    }));
                }
            }));
        }
    });
    reg("sagefs.showHistory", (_arg_15) => {
        if (client() == null) {
            Window_showWarningMessage("SageFs is not connected", []);
        }
        else {
            const pr_7 = getRecentEvents(30, client());
            void (pr_7.then((bodyOpt) => {
                if (bodyOpt == null) {
                    Window_showWarningMessage("Could not fetch events", []);
                }
                else {
                    const body = bodyOpt;
                    let lines;
                    const array = body.split("\n");
                    lines = array.filter((l) => (l.trim().length > 0));
                    if (lines.length === 0) {
                        Window_showInformationMessage("No recent events", []);
                    }
                    else {
                        const pr_6 = Window_showQuickPick(lines, "Recent SageFs events");
                        void (pr_6.then((_arg_16) => {
                        }));
                    }
                }
            }));
        }
    });
    reg("sagefs.showCallGraph", (_arg_17) => {
        if (client() == null) {
            Window_showWarningMessage("SageFs is not connected", []);
        }
        else {
            const c_6 = client();
            const pr_11 = getDependencyGraph("", c_6);
            void (pr_11.then((bodyOpt_1) => {
                if (bodyOpt_1 == null) {
                    Window_showWarningMessage("Could not fetch dependency graph", []);
                }
                else {
                    const body_1 = bodyOpt_1;
                    const parsed = JSON.parse(body_1);
                    const total = parsed.TotalSymbols | 0;
                    if (total === 0) {
                        Window_showInformationMessage("No dependency graph available yet", []);
                    }
                    else {
                        const pr_10 = Window_showInputBox(toText(printf("Enter symbol name (%d symbols tracked)"))(total));
                        void (pr_10.then((inputOpt) => {
                            let sym;
                            let matchResult, sym_1;
                            if (inputOpt != null) {
                                if ((sym = inputOpt, sym.trim().length > 0)) {
                                    matchResult = 0;
                                    sym_1 = inputOpt;
                                }
                                else {
                                    matchResult = 1;
                                }
                            }
                            else {
                                matchResult = 1;
                            }
                            switch (matchResult) {
                                case 0: {
                                    const pr_9 = getDependencyGraph(sym_1.trim(), c_6);
                                    void (pr_9.then((detailOpt) => {
                                        if (detailOpt == null) {
                                            Window_showWarningMessage("Could not fetch graph", []);
                                        }
                                        else {
                                            const detail = detailOpt;
                                            const parsed2 = JSON.parse(detail);
                                            const tests = parsed2.Tests;
                                            if (tests.length === 0) {
                                                Window_showInformationMessage(toText(printf("No tests cover \'%s\'"))(sym_1), []);
                                            }
                                            else {
                                                const pr_8 = Window_showQuickPick(map((t) => {
                                                    const name = t.TestName;
                                                    const status = t.Status;
                                                    const icon = (status === "passed") ? "✓" : ((status === "failed") ? "✗" : "●");
                                                    return toText(printf("%s %s [%s]"))(icon)(name)(status);
                                                }, tests), toText(printf("Tests covering \'%s\'"))(sym_1));
                                                void (pr_8.then((_arg_18) => {
                                                }));
                                            }
                                        }
                                    }));
                                    break;
                                }
                                case 1: {
                                    break;
                                }
                            }
                        }));
                    }
                }
            }));
        }
    });
    const lensProvider = create_2();
    void (context.subscriptions.push(Languages_registerCodeLensProvider("fsharp", lensProvider)));
    const testLensProvider = create_3();
    void (context.subscriptions.push(Languages_registerCodeLensProvider("fsharp", testLensProvider)));
    const completionProvider = create_4(client, () => bind((folders) => {
        if (folders.length > 0) {
            return item(0, folders).uri.fsPath;
        }
        else {
            return undefined;
        }
    }, Workspace_workspaceFolders()));
    void (context.subscriptions.push(Languages_registerCompletionItemProvider("fsharp", completionProvider, ["."])));
    hijackIonideSendToFsi(context.subscriptions);
    const pr_16 = isRunning(c);
    void (pr_16.then((running) => {
        if (running) {
            diagnosticsDisposable(start(c.mcpPort, dc));
            const adapter = create_5(client);
            testAdapter(adapter);
            initialize();
            let listenerRef = undefined;
            const listener = start_1(c.mcpPort, new LiveTestingCallbacks((changes) => {
                adapter.Refresh(changes);
                const state = defaultArg(map_1((l_1) => l_1.State(), listenerRef), VscLiveTestStateModule_empty);
                applyToAllEditors(state);
                applyCoverageToAllEditors(state);
                updateDiagnostics(state);
                updateState(state);
            }, (summary) => {
                updateTestStatusBar(summary);
            }, () => {
                refreshStatus();
            }));
            listenerRef = listener;
            liveTestListener(listener);
            sseDisposable({
                dispose() {
                    listener.Dispose();
                    return defaultOf();
                },
            });
            void (context.subscriptions.push(Window_onDidChangeVisibleTextEditors((_editors) => {
                const state_1 = defaultArg(map_1((l_2) => l_2.State(), listenerRef), VscLiveTestStateModule_empty);
                applyToAllEditors(state_1);
                applyCoverageToAllEditors(state_1);
            })));
            void (context.subscriptions.push(Window_onDidChangeActiveTextEditor((_editor) => {
                const state_2 = defaultArg(map_1((l_3) => l_3.State(), listenerRef), VscLiveTestStateModule_empty);
                applyToAllEditors(state_2);
                applyCoverageToAllEditors(state_2);
            })));
            const pr_15 = listSessions(c);
            void (pr_15.then((sessions) => {
                if (sessions.length === 0) {
                    const pr_14 = findProject();
                    void (pr_14.then((projOpt) => {
                        if (projOpt == null) {
                        }
                        else {
                            const proj = projOpt;
                            const workDir = defaultArg(getWorkingDirectory(), ".");
                            const pr_13 = Window_showInformationMessage(toText(printf("SageFs is running but has no session. Create one for %s?"))(proj), ["Create Session", "Not Now"]);
                            void (pr_13.then((choice) => {
                                let matchResult_1;
                                if (choice != null) {
                                    if (choice === "Create Session") {
                                        matchResult_1 = 0;
                                    }
                                    else {
                                        matchResult_1 = 1;
                                    }
                                }
                                else {
                                    matchResult_1 = 1;
                                }
                                switch (matchResult_1) {
                                    case 0: {
                                        const pr_12 = createSession(proj, workDir, c);
                                        void (pr_12.then((result_4) => {
                                            if (result_4.success) {
                                                Window_showInformationMessage(toText(printf("SageFs: Session created for %s"))(proj), []);
                                            }
                                            refreshStatus();
                                        }));
                                        break;
                                    }
                                    case 1: {
                                        break;
                                    }
                                }
                            }));
                        }
                    }));
                }
            }));
        }
    }));
    void (context.subscriptions.push(Workspace_onDidChangeConfiguration((e) => {
        if (e.affectsConfiguration("sagefs")) {
            const cfg = Workspace_getConfiguration("sagefs");
            updatePorts(cfg.get("mcpPort", 37749), cfg.get("dashboardPort", 37750), c);
        }
    })));
    refreshStatus();
    const statusInterval = setInterval((() => {
        refreshStatus();
    }), 5000);
    void (context.subscriptions.push({
        dispose() {
            clearInterval(statusInterval);
            return defaultOf();
        },
    }));
    if (config.get("autoStart", true)) {
        const pr_17 = isRunning(c);
        void (pr_17.then((running_1) => {
            if (!running_1) {
                promptAutoStart();
            }
        }));
    }
}

export function deactivate() {
    iterate_1((d) => {
        d.dispose();
    }, toArray(diagnosticsDisposable()));
    iterate_1((d_1) => {
        d_1.dispose();
    }, toArray(sseDisposable()));
    iterate_1((l) => {
        l.Dispose();
    }, toArray(liveTestListener()));
    liveTestListener(undefined);
    iterate_1((a) => {
        a.Dispose();
    }, toArray(testAdapter()));
    testAdapter(undefined);
    dispose();
    clearAllDecorations();
}

