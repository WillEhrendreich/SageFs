import { equals, defaultOf, createAtom } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { Workspace_onDidChangeConfiguration, Window_onDidChangeActiveTextEditor, Window_onDidChangeVisibleTextEditors, Languages_registerCompletionItemProvider, Languages_registerCodeLensProvider, Window_showTextDocument, Workspace_openTextDocument, Window_showInputBox, Workspace_onDidChangeTextDocument, Languages_createDiagnosticCollection, Window_createStatusBarItem, Window_createOutputChannel, Commands_executeCommand, Commands_registerCommand, newSelection, newPosition, Window_createWebviewPanel, Window_withProgress, Window_getActiveTextEditor, Window_showWarningMessage, Window_showErrorMessage, Window_showInformationMessage, newThemeColor, newRange, Window_showQuickPick, Workspace_asRelativePath, Workspace_findFiles, Workspace_getConfiguration, Workspace_workspaceFolders } from "./Vscode.fs.js";
import { choose, tryFindIndex, last, tryFind, tryHead, equalsWith, map, append, item } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { PromiseBuilder__While_2044D34, PromiseBuilder__For_1565554B, PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { substring, split, join, printf, toText, trimEnd } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { value as value_29, bind, map as map_1, toArray, defaultArg, some } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { tryField, promiseIgnore } from "./JsHelpers.fs.js";
import { updatePorts, exportSessionAsFsx, getDependencyGraph, getRecentEvents, ApiOutcomeModule_message, setRunPolicy, runTests, disableLiveTesting, enableLiveTesting, create, loadScript, cancelEval, dashboardUrl, stopSession, switchSession, createSession, hardReset, resetSession, evalCode, ApiOutcomeModule_messageOrDefault, listSessions, getSystemStatus, getStatus, isRunning } from "./SageFsClient.fs.js";
import { register, setSession } from "./HotReloadTreeProvider.fs.js";
import { register as register_1, setSession as setSession_1 } from "./SessionContextTreeProvider.fs.js";
import { iterate } from "./fable_modules/fable-library-js.4.29.0/Seq.js";
import { rangeDouble } from "./fable_modules/fable-library-js.4.29.0/Range.js";
import { toString, Union } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { union_type, float64_type, string_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";
import { clearAllDecorations, markDecorationsStale, blockDecorations, showInlineDiagnostic, showInlineResult, formatDuration } from "./InlineDecorations.fs.js";
import { isEmpty } from "./fable_modules/fable-library-js.4.29.0/Map.js";
import { create as create_1 } from "./TypeExplorerProvider.fs.js";
import { create as create_2 } from "./CodeLensProvider.fs.js";
import { updateState, create as create_3 } from "./TestCodeLensProvider.fs.js";
import { create as create_4 } from "./CompletionProvider.fs.js";
import { start } from "./DiagnosticsListener.fs.js";
import { create as create_5 } from "./TestControllerAdapter.fs.js";
import { dispose, updateDiagnostics, applyCoverageToAllEditors, applyToAllEditors, initialize } from "./TestDecorations.fs.js";
import { VscLiveTestStateModule_empty } from "./LiveTestingTypes.fs.js";
import { LiveTestingCallbacks, start as start_1 } from "./LiveTestingListener.fs.js";

export let client = createAtom(undefined);

export let outputChannel = createAtom(undefined);

export let statusBarItem = createAtom(undefined);

export let testStatusBarItem = createAtom(undefined);

export let diagnosticsDisposable = createAtom(undefined);

export let sseDisposable = createAtom(undefined);

export let diagnosticCollection = createAtom(undefined);

export let activeSessionId = createAtom(undefined);

export let liveTestListener = createAtom(undefined);

export let testAdapter = createAtom(undefined);

export let dashboardPanel = createAtom(undefined);

export let typeExplorer = createAtom(undefined);

export let daemonProcess = createAtom(undefined);

export let isStarting = createAtom(false);

export let onDaemonReady = createAtom(undefined);

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
            if (!equalsWith((x, y) => (x === y), all, defaultOf()) && (all.length === 0)) {
                return Promise.resolve(undefined);
            }
            else if (!equalsWith((x_1, y_1) => (x_1 === y_1), all, defaultOf()) && (all.length === 1)) {
                const single = item(0, all);
                return Promise.resolve(single);
            }
            else {
                return Window_showQuickPick(all, "Select a solution or project for SageFs").then((_arg_2) => (Promise.resolve(_arg_2)));
            }
        }))));
    }));
}

export function getCodeBlock(editor) {
    const doc = editor.document;
    const pos = editor.selection.active;
    let startLine = ~~pos.line;
    while ((startLine > 0) && !trimEnd(doc.lineAt(startLine - 1).text).endsWith(";;")) {
        startLine = ((startLine - 1) | 0);
    }
    let endLine = ~~pos.line;
    while ((endLine < (~~doc.lineCount - 1)) && !trimEnd(doc.lineAt(endLine).text).endsWith(";;")) {
        endLine = ((endLine + 1) | 0);
    }
    const range = newRange(startLine, 0, endLine, doc.lineAt(endLine).text.length);
    return doc.getText(range);
}

export function updateTestStatusBar(summary) {
    if (testStatusBarItem() != null) {
        const sb = testStatusBarItem();
        let patternInput;
        if (summary.Total === 0) {
            patternInput = ["$(beaker) No tests", undefined];
        }
        else if (summary.Failed > 0) {
            const s_5 = summary;
            patternInput = [toText(printf("$(testing-error-icon) %d/%d failed"))(s_5.Failed)(s_5.Total), some(newThemeColor("statusBarItem.errorBackground"))];
        }
        else if (summary.Running > 0) {
            const s_6 = summary;
            patternInput = [toText(printf("$(sync~spin) Running %d/%d"))(s_6.Running)(s_6.Total), undefined];
        }
        else if (summary.Stale > 0) {
            const s_7 = summary;
            patternInput = [toText(printf("$(warning) %d/%d stale"))(s_7.Stale)(s_7.Total), some(newThemeColor("statusBarItem.warningBackground"))];
        }
        else {
            const s_8 = summary;
            patternInput = [toText(printf("$(testing-passed-icon) %d/%d passed"))(s_8.Passed)(s_8.Total), undefined];
        }
        sb.text = patternInput[0];
        sb.backgroundColor = patternInput[1];
        sb.show();
    }
}

export function refreshStatus() {
    promiseIgnore(PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
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
                        let s_5, projLabel, matchValue, evalLabel, matchValue_1;
                        const sessions = _arg_3;
                        let session;
                        if (activeSessionId() == null) {
                            session = tryHead(sessions);
                        }
                        else {
                            const id = activeSessionId();
                            session = tryFind((s_4) => (s_4.id === id), sessions);
                        }
                        return ((session == null) ? ((activeSessionId(undefined), (sb.text = toText(printf("$(zap) SageFs: ready (no session)%s%s"))(supervised)(restarts), Promise.resolve()))) : ((s_5 = session, (activeSessionId(s_5.id), (projLabel = ((matchValue = s_5.projects, (!equalsWith((x, y) => (x === y), matchValue, defaultOf()) && (matchValue.length === 0)) ? "session" : join(",", map((p) => {
                            const name = last(split(p, ["/", "\\"]));
                            if (name.endsWith(".fsproj")) {
                                const n_3 = name;
                                return n_3.slice(undefined, (n_3.length - 8) + 1);
                            }
                            else if (name.endsWith(".slnx")) {
                                const n_4 = name;
                                return n_4.slice(undefined, (n_4.length - 6) + 1);
                            }
                            else if (name.endsWith(".sln")) {
                                const n_5 = name;
                                return n_5.slice(undefined, (n_5.length - 5) + 1);
                            }
                            else {
                                return name;
                            }
                        }, matchValue)))), (evalLabel = ((matchValue_1 = (s_5.evalCount | 0), (matchValue_1 === 0) ? "" : toText(printf(" [%d]"))(matchValue_1))), (sb.text = toText(printf("$(zap) SageFs: %s%s%s%s"))(projLabel)(evalLabel)(supervised)(restarts), Promise.resolve()))))))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
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
    })));
}

export function startDaemon() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        if (isStarting()) {
            return Promise.resolve();
        }
        else {
            isStarting(true);
            const c = getClient();
            return isRunning(c).then((_arg) => {
                if (_arg) {
                    isStarting(false);
                    Window_showInformationMessage("SageFs daemon is already running.", []);
                    refreshStatus();
                    return Promise.resolve();
                }
                else {
                    return findProject().then((_arg_1) => {
                        let x_1, x_3;
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
                            const flag = (ext === ".sln") ? "--sln" : ((ext === ".slnx") ? "--sln" : "--proj");
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
                            iterate((s) => {
                                s.on('data', function(d) { if (d != null) ((chunk) => {
                                    out.appendLine(chunk);
                                })(String(d)) });
                            }, toArray((x_1 = (proc.stderr), (x_1 == null) ? undefined : some(x_1))));
                            iterate((s_1) => {
                                s_1.on('data', function(d) { if (d != null) ((chunk_1) => {
                                    out.appendLine(chunk_1);
                                })(String(d)) });
                            }, toArray((x_3 = (proc.stdout), (x_3 == null) ? undefined : some(x_3))));
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
                                promiseIgnore(PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (isRunning(c).then((_arg_2) => {
                                    if (_arg_2) {
                                        iterate((id) => {
                                            clearInterval(id);
                                        }, toArray(intervalId));
                                        isStarting(false);
                                        out.appendLine("SageFs daemon is ready.");
                                        Window_showInformationMessage("SageFs daemon started.", []);
                                        iterate((f) => {
                                            f(c);
                                        }, toArray(onDaemonReady()));
                                        refreshStatus();
                                        return Promise.resolve();
                                    }
                                    else if (attempts > 60) {
                                        iterate((id_1) => {
                                            clearInterval(id_1);
                                        }, toArray(intervalId));
                                        isStarting(false);
                                        out.appendLine("Timed out waiting for SageFs daemon after 120s.");
                                        Window_showErrorMessage("SageFs daemon failed to start after 120s.", []);
                                        sb_1.text = "$(error) SageFs: offline";
                                        return Promise.resolve();
                                    }
                                    else {
                                        return Promise.resolve();
                                    }
                                })))));
                            }), 2000);
                            intervalId = some(id_2);
                            return Promise.resolve();
                        }
                        else {
                            isStarting(false);
                            Window_showErrorMessage("No .fsproj or .sln found. Open an F# project first.", []);
                            return Promise.resolve();
                        }
                    });
                }
            });
        }
    }));
}

export function ensureRunning() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const c = getClient();
        return isRunning(c).then((_arg) => (_arg ? (Promise.resolve(true)) : (Window_showWarningMessage("SageFs daemon is not running.", ["Start SageFs", "Cancel"]).then((_arg_1) => {
            const choice = _arg_1;
            let matchResult;
            if (choice != null) {
                if (choice === "Start SageFs") {
                    matchResult = 0;
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
                    return startDaemon().then(() => {
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
                    });
                default:
                    return Promise.resolve(false);
            }
        }))));
    }));
}

/**
 * Wraps the ensureRunning + getClient boilerplate.
 */
export function withClient(action) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (ensureRunning().then((_arg) => (_arg ? (action(getClient()).then(() => (Promise.resolve(undefined)))) : (Promise.resolve()))))));
}

/**
 * Fire a client action that returns ApiOutcome, show its message, then refresh.
 */
export function simpleCommand(defaultMsg, action) {
    return withClient((c) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (action(c).then((_arg) => {
        const msg = ApiOutcomeModule_messageOrDefault(defaultMsg, _arg);
        Window_showInformationMessage(toText(printf("SageFs: %s"))(msg), []);
        refreshStatus();
        return Promise.resolve();
    })))));
}

export class EvalResult extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["EvalOk", "EvalError", "EvalConnectionError"];
    }
}

export function EvalResult_$reflection() {
    return union_type("SageFs.Vscode.Extension.EvalResult", [], EvalResult, () => [[["output", string_type], ["elapsed", float64_type]], [["message", string_type]], [["message", string_type]]]);
}

export function evalCore(code) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const c = getClient();
        const workDir = getWorkingDirectory();
        const startTime = performance.now();
        return evalCode(code, workDir, c).then((_arg) => {
            const result = _arg;
            const elapsed = (performance.now()) - startTime;
            return (result.tag === 0) ? (Promise.resolve(new EvalResult(0, [defaultArg(result.fields[0], ""), elapsed]))) : (Promise.resolve(new EvalResult(1, [result.fields[0]])));
        });
    }).catch((_arg_1) => (Promise.resolve(new EvalResult(2, [toString(_arg_1)])))))));
}

/**
 * Log eval result to output channel. Returns the result for further handling.
 */
export function logEvalResult(out, result) {
    let arg_1;
    switch (result.tag) {
        case 1: {
            out.appendLine(toText(printf("❌ Error:\n%s"))(result.fields[0]));
            break;
        }
        case 2: {
            out.appendLine(toText(printf("❌ Connection error: %s"))(result.fields[0]));
            break;
        }
        default:
            out.appendLine((arg_1 = formatDuration(result.fields[1]), toText(printf("%s  (%s)"))(result.fields[0])(arg_1)));
    }
    return result;
}

/**
 * Get code from selection or code block, append ;; if needed.
 */
export function getEvalCode(ed) {
    const raw = !ed.selection.isEmpty ? (ed.document.getText(newRange(~~ed.selection.start.line, ~~ed.selection.start.character, ~~ed.selection.end.line, ~~ed.selection.end.character))) : getCodeBlock(ed);
    if (raw.trim() === "") {
        return undefined;
    }
    else if (trimEnd(raw).endsWith(";;")) {
        return raw;
    }
    else {
        return trimEnd(raw) + ";;";
    }
}

export function evalSelection() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const matchValue = Window_getActiveTextEditor();
        if (matchValue != null) {
            const ed = matchValue;
            return ensureRunning().then((_arg) => {
                const matchValue_1 = getEvalCode(ed);
                let matchResult;
                if (_arg) {
                    if (matchValue_1 != null) {
                        matchResult = 1;
                    }
                    else {
                        matchResult = 0;
                    }
                }
                else {
                    matchResult = 0;
                }
                switch (matchResult) {
                    case 0: {
                        return Promise.resolve();
                    }
                    default: {
                        const code = matchValue_1;
                        const out = getOutput();
                        return Window_withProgress(10, "SageFs: evaluating...", (_progress, _token) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
                            out.appendLine("──── eval ────");
                            out.appendLine(code);
                            out.appendLine("");
                            return evalCore(code).then((_arg_1) => {
                                const matchValue_3 = logEvalResult(out, _arg_1);
                                switch (matchValue_3.tag) {
                                    case 0: {
                                        showInlineResult(ed, matchValue_3.fields[0], matchValue_3.fields[1]);
                                        return Promise.resolve();
                                    }
                                    case 2: {
                                        out.show(true);
                                        Window_showErrorMessage("Cannot reach SageFs daemon. Is it running?", []);
                                        return Promise.resolve();
                                    }
                                    default: {
                                        out.show(true);
                                        showInlineDiagnostic(ed, matchValue_3.fields[0]);
                                        return Promise.resolve();
                                    }
                                }
                            });
                        }))).then(() => (Promise.resolve(undefined)));
                    }
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
                const code = ed.document.getText();
                const matchValue_1 = code.trim();
                let matchResult;
                if (_arg) {
                    if (matchValue_1 === "") {
                        matchResult = 0;
                    }
                    else {
                        matchResult = 1;
                    }
                }
                else {
                    matchResult = 0;
                }
                switch (matchResult) {
                    case 0: {
                        return Promise.resolve();
                    }
                    default: {
                        const out = getOutput();
                        out.show(true);
                        out.appendLine((arg = ed.document.fileName, toText(printf("──── eval file: %s ────"))(arg)));
                        return evalCore(code).then((_arg_1) => {
                            logEvalResult(out, _arg_1);
                            return Promise.resolve();
                        });
                    }
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
                const code = ed.document.getText(args);
                const matchValue_1 = code.trim();
                let matchResult;
                if (_arg) {
                    if (matchValue_1 === "") {
                        matchResult = 0;
                    }
                    else {
                        matchResult = 1;
                    }
                }
                else {
                    matchResult = 0;
                }
                switch (matchResult) {
                    case 0: {
                        return Promise.resolve();
                    }
                    default: {
                        const out = getOutput();
                        out.show(true);
                        out.appendLine("──── eval block ────");
                        out.appendLine(code);
                        out.appendLine("");
                        return evalCore(code).then((_arg_1) => {
                            const matchValue_3 = logEvalResult(out, _arg_1);
                            if (matchValue_3.tag === 0) {
                                showInlineResult(ed, matchValue_3.fields[0], matchValue_3.fields[1]);
                                return Promise.resolve();
                            }
                            else {
                                return Promise.resolve();
                            }
                        });
                    }
                }
            });
        }
        else {
            return Promise.resolve();
        }
    }));
}

export function resetSessionCmd() {
    return simpleCommand("Reset complete", resetSession);
}

export function hardResetCmd() {
    return simpleCommand("Hard reset complete", (c) => hardReset(true, c));
}

export function createSessionCmd() {
    return withClient((c) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (findProject().then((_arg) => {
        const projPath = _arg;
        if (projPath != null) {
            const proj = projPath;
            const workDir = defaultArg(getWorkingDirectory(), ".");
            return Window_withProgress(15, "SageFs: Creating session...", (_p, _t) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (createSession(proj, workDir, c).then((_arg_1) => {
                const result = _arg_1;
                return ((result.tag === 1) ? ((void Window_showErrorMessage(toText(printf("SageFs: %s"))(result.fields[0]), []), Promise.resolve())) : ((void Window_showInformationMessage(toText(printf("SageFs: Session created for %s"))(proj), []), Promise.resolve()))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                    refreshStatus();
                    return Promise.resolve();
                }));
            }))))).then(() => (Promise.resolve(undefined)));
        }
        else {
            Window_showErrorMessage("No .fsproj or .sln found. Open an F# project first.", []);
            return Promise.resolve();
        }
    })))));
}

function formatSessionLabel(s) {
    let proj;
    const matchValue = s.projects;
    proj = ((!equalsWith((x, y) => (x === y), matchValue, defaultOf()) && (matchValue.length === 0)) ? "no project" : join(", ", matchValue));
    return toText(printf("%s (%s) [%s]"))(s.id)(proj)(s.status);
}

export function sessionPickCommand(prompt, action, onSuccess) {
    return withClient((c) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (listSessions(c).then((_arg) => {
        const sessions = _arg;
        if (!equalsWith(equals, sessions, defaultOf()) && (sessions.length === 0)) {
            Window_showInformationMessage("No sessions available.", []);
            return Promise.resolve();
        }
        else {
            const items = map(formatSessionLabel, sessions);
            return Window_showQuickPick(items, prompt).then((_arg_1) => {
                const picked = _arg_1;
                if (picked == null) {
                    return Promise.resolve();
                }
                else {
                    const label = picked;
                    const matchValue = tryFindIndex((y_1) => (label === y_1), items);
                    if (matchValue == null) {
                        return Promise.resolve();
                    }
                    else {
                        const sess = item(matchValue, sessions);
                        return action(sess, c).then((_arg_2) => {
                            const result = _arg_2;
                            return ((result.tag === 1) ? ((void Window_showErrorMessage(toText(printf("Failed: %s"))(result.fields[0]), []), Promise.resolve())) : ((onSuccess(sess), (void Window_showInformationMessage(ApiOutcomeModule_messageOrDefault(prompt, result), []), Promise.resolve())))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                refreshStatus();
                                return Promise.resolve();
                            }));
                        });
                    }
                }
            });
        }
    })))));
}

export function switchSessionCmd() {
    return sessionPickCommand("Select a session", (sess, c) => switchSession(sess.id, c), (sess_1) => {
        activeSessionId(sess_1.id);
    });
}

export function stopSessionCmd() {
    return sessionPickCommand("Select a session to stop", (sess, c) => stopSession(sess.id, c), (sess_1) => {
        let matchResult, id_1;
        if (activeSessionId() != null) {
            if (activeSessionId() === sess_1.id) {
                matchResult = 0;
                id_1 = activeSessionId();
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
                activeSessionId(undefined);
                break;
            }
            case 1: {
                break;
            }
        }
    });
}

export function stopDaemon() {
    iterate((proc) => {
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

export function evalAdvance() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const matchValue = Window_getActiveTextEditor();
        if (matchValue != null) {
            const ed = matchValue;
            return ensureRunning().then((_arg) => {
                const matchValue_1 = getEvalCode(ed);
                let matchResult;
                if (_arg) {
                    if (matchValue_1 != null) {
                        matchResult = 1;
                    }
                    else {
                        matchResult = 0;
                    }
                }
                else {
                    matchResult = 0;
                }
                switch (matchResult) {
                    case 0: {
                        return Promise.resolve();
                    }
                    default: {
                        const code = matchValue_1;
                        const out = getOutput();
                        return evalCore(code).then((_arg_1) => {
                            const matchValue_3 = logEvalResult(out, _arg_1);
                            switch (matchValue_3.tag) {
                                case 0: {
                                    showInlineResult(ed, matchValue_3.fields[0], matchValue_3.fields[1]);
                                    const curLine = ~~ed.selection.end.line | 0;
                                    const lineCount = ~~ed.document.lineCount | 0;
                                    let nextLine = curLine + 1;
                                    return PromiseBuilder__While_2044D34(promise, () => ((nextLine < lineCount) && (ed.document.lineAt(nextLine).text.trim() === "")), PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                        nextLine = ((nextLine + 1) | 0);
                                        return Promise.resolve();
                                    })).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                        if (nextLine < lineCount) {
                                            const pos = newPosition(nextLine, 0);
                                            const sel = newSelection(pos, pos);
                                            ed.selection = sel;
                                            ed.revealRange(newRange(nextLine, 0, nextLine, 0));
                                            return Promise.resolve();
                                        }
                                        else {
                                            return Promise.resolve();
                                        }
                                    }));
                                }
                                case 2: {
                                    return Promise.resolve();
                                }
                                default: {
                                    showInlineDiagnostic(ed, matchValue_3.fields[0]);
                                    return Promise.resolve();
                                }
                            }
                        });
                    }
                }
            });
        }
        else {
            Window_showWarningMessage("No active editor.", []);
            return Promise.resolve();
        }
    }));
}

export function cancelEvalCmd() {
    return simpleCommand("Eval cancelled", cancelEval);
}

export function loadScriptCmd() {
    return withClient((c) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        let ed;
        const matchValue = Window_getActiveTextEditor();
        let matchResult, ed_1;
        if (matchValue != null) {
            if ((ed = matchValue, ed.document.fileName.endsWith(".fsx"))) {
                matchResult = 0;
                ed_1 = matchValue;
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
                return loadScript(ed_1.document.fileName, c).then((_arg) => {
                    const result = _arg;
                    if (result.tag === 1) {
                        Window_showErrorMessage(result.fields[0], []);
                        return Promise.resolve();
                    }
                    else {
                        const name = last(split(ed_1.document.fileName, ["/", "\\"]));
                        Window_showInformationMessage(toText(printf("Script loaded: %s"))(name), []);
                        return Promise.resolve();
                    }
                });
            default: {
                Window_showWarningMessage("Open an .fsx file to load it as a script.", []);
                return Promise.resolve();
            }
        }
    })));
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
                    promiseIgnore(Commands_executeCommand("sagefs.evalFile"));
                }
                else {
                    promiseIgnore(Commands_executeCommand("sagefs.eval"));
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
    const docChangeSub = Workspace_onDidChangeTextDocument((_evt) => {
        let ed;
        const matchValue = Window_getActiveTextEditor();
        let matchResult, ed_1;
        if (matchValue != null) {
            if ((ed = matchValue, ed.document.fileName.endsWith(".fs") ? true : ed.document.fileName.endsWith(".fsx"))) {
                matchResult = 0;
                ed_1 = matchValue;
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
                if (!isEmpty(blockDecorations())) {
                    markDecorationsStale(ed_1);
                }
                break;
            }
            case 1: {
                break;
            }
        }
    });
    void (context.subscriptions.push(docChangeSub));
    register(context);
    setSession(c, undefined);
    register_1(context);
    setSession_1(c, undefined);
    typeExplorer(create_1(context, client()));
    const reg = (cmd, handler) => {
        void (context.subscriptions.push(Commands_registerCommand(cmd, handler)));
    };
    reg("sagefs.eval", (_arg) => {
        promiseIgnore(evalSelection());
    });
    reg("sagefs.evalFile", (_arg_1) => {
        promiseIgnore(evalFile());
    });
    reg("sagefs.evalRange", (args) => {
        promiseIgnore(evalRange(args));
    });
    reg("sagefs.evalAdvance", (_arg_2) => {
        promiseIgnore(evalAdvance());
    });
    reg("sagefs.cancelEval", (_arg_3) => {
        promiseIgnore(cancelEvalCmd());
    });
    reg("sagefs.loadScript", (_arg_4) => {
        promiseIgnore(loadScriptCmd());
    });
    reg("sagefs.start", (_arg_5) => {
        promiseIgnore(startDaemon());
    });
    reg("sagefs.stop", (_arg_6) => {
        stopDaemon();
    });
    reg("sagefs.openDashboard", (_arg_7) => {
        openDashboard();
    });
    reg("sagefs.resetSession", (_arg_8) => {
        promiseIgnore(resetSessionCmd());
    });
    reg("sagefs.hardReset", (_arg_9) => {
        promiseIgnore(hardResetCmd());
    });
    reg("sagefs.createSession", (_arg_10) => {
        promiseIgnore(createSessionCmd());
    });
    reg("sagefs.switchSession", (_arg_11) => {
        promiseIgnore(switchSessionCmd());
    });
    reg("sagefs.stopSession", (_arg_12) => {
        promiseIgnore(stopSessionCmd());
    });
    reg("sagefs.clearResults", (_arg_13) => {
        clearAllDecorations();
    });
    reg("sagefs.enableLiveTesting", (_arg_14) => {
        promiseIgnore(simpleCommand("Live testing enabled", enableLiveTesting));
    });
    reg("sagefs.disableLiveTesting", (_arg_15) => {
        promiseIgnore(simpleCommand("Live testing disabled", disableLiveTesting));
    });
    reg("sagefs.runTests", (_arg_16) => {
        promiseIgnore(simpleCommand("Tests queued", (c_3) => runTests("", c_3)));
    });
    reg("sagefs.setRunPolicy", (_arg_17) => {
        promiseIgnore(withClient((c_4) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (Window_showQuickPick(["unit", "integration", "browser", "benchmark", "architecture", "property"], "Select test category").then((_arg_18) => {
            const catOpt = _arg_18;
            if (catOpt == null) {
                return Promise.resolve();
            }
            else {
                const cat = catOpt;
                return Window_showQuickPick(["every", "save", "demand", "disabled"], toText(printf("Set policy for %s tests"))(cat)).then((_arg_19) => {
                    const polOpt = _arg_19;
                    if (polOpt == null) {
                        return Promise.resolve();
                    }
                    else {
                        const pol = polOpt;
                        return setRunPolicy(cat, pol, c_4).then((_arg_20) => {
                            iterate((msg) => {
                                Window_showInformationMessage(msg, []);
                            }, toArray(ApiOutcomeModule_message(_arg_20)));
                            return Promise.resolve();
                        });
                    }
                });
            }
        }))))));
    });
    reg("sagefs.showHistory", (_arg_22) => {
        promiseIgnore(withClient((c_5) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (getRecentEvents(30, c_5).then((_arg_23) => {
            const bodyOpt = _arg_23;
            if (bodyOpt == null) {
                Window_showWarningMessage("Could not fetch events", []);
                return Promise.resolve();
            }
            else {
                const body = bodyOpt;
                let lines;
                const array = body.split("\n");
                lines = array.filter((l) => (l.trim().length > 0));
                if (!equalsWith((x, y) => (x === y), lines, defaultOf()) && (lines.length === 0)) {
                    Window_showInformationMessage("No recent events", []);
                    return Promise.resolve();
                }
                else {
                    promiseIgnore(Window_showQuickPick(lines, "Recent SageFs events"));
                    return Promise.resolve();
                }
            }
        }))))));
    });
    reg("sagefs.showCallGraph", (_arg_24) => {
        promiseIgnore(withClient((c_6) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (getDependencyGraph("", c_6).then((_arg_25) => {
            const overviewOpt = _arg_25;
            if (overviewOpt != null) {
                const body_1 = overviewOpt;
                const total = defaultArg(tryField("TotalSymbols", JSON.parse(body_1)), 0) | 0;
                if (total === 0) {
                    Window_showInformationMessage("No dependency graph available yet", []);
                    return Promise.resolve();
                }
                else {
                    return Window_showInputBox(toText(printf("Enter symbol name (%d symbols tracked)"))(total)).then((_arg_26) => {
                        let sym;
                        const inputOpt = _arg_26;
                        let matchResult_1, sym_1;
                        if (inputOpt != null) {
                            if ((sym = inputOpt, sym.trim().length > 0)) {
                                matchResult_1 = 0;
                                sym_1 = inputOpt;
                            }
                            else {
                                matchResult_1 = 1;
                            }
                        }
                        else {
                            matchResult_1 = 1;
                        }
                        switch (matchResult_1) {
                            case 0:
                                return getDependencyGraph(sym_1.trim(), c_6).then((_arg_27) => {
                                    const detailOpt = _arg_27;
                                    if (detailOpt != null) {
                                        const detail = detailOpt;
                                        const tests = defaultArg(tryField("Tests", JSON.parse(detail)), []);
                                        if (!equalsWith(equals, tests, defaultOf()) && (tests.length === 0)) {
                                            Window_showInformationMessage(toText(printf("No tests cover \'%s\'"))(sym_1), []);
                                            return Promise.resolve();
                                        }
                                        else {
                                            promiseIgnore(Window_showQuickPick(map((t) => {
                                                const name = defaultArg(tryField("TestName", t), "?");
                                                const status = defaultArg(tryField("Status", t), "unknown");
                                                const icon = (status === "passed") ? "✓" : ((status === "failed") ? "✗" : "●");
                                                return toText(printf("%s %s [%s]"))(icon)(name)(status);
                                            }, tests), toText(printf("Tests covering \'%s\'"))(sym_1)));
                                            return Promise.resolve();
                                        }
                                    }
                                    else {
                                        Window_showWarningMessage("Could not fetch graph", []);
                                        return Promise.resolve();
                                    }
                                });
                            default: {
                                return Promise.resolve();
                            }
                        }
                    });
                }
            }
            else {
                Window_showWarningMessage("Could not fetch dependency graph", []);
                return Promise.resolve();
            }
        }))))));
    });
    reg("sagefs.showBindings", (_arg_28) => {
        let testExpr;
        const matchValue_1 = map_1((l_1) => l_1.Bindings(), liveTestListener());
        let matchResult_2, bindings;
        if (matchValue_1 == null) {
            matchResult_2 = 0;
        }
        else if ((testExpr = matchValue_1, !equalsWith(equals, testExpr, defaultOf()) && (testExpr.length === 0))) {
            matchResult_2 = 0;
        }
        else {
            matchResult_2 = 1;
            bindings = matchValue_1;
        }
        switch (matchResult_2) {
            case 0: {
                Window_showInformationMessage("No FSI bindings yet", []);
                break;
            }
            case 1: {
                promiseIgnore(Window_showQuickPick(choose((b) => {
                    const matchValue_2 = tryField("Name", b);
                    const matchValue_3 = tryField("TypeSig", b);
                    let matchResult_3, name_1, typeSig;
                    if (matchValue_2 != null) {
                        if (matchValue_3 != null) {
                            matchResult_3 = 0;
                            name_1 = matchValue_2;
                            typeSig = matchValue_3;
                        }
                        else {
                            matchResult_3 = 1;
                        }
                    }
                    else {
                        matchResult_3 = 1;
                    }
                    switch (matchResult_3) {
                        case 0: {
                            const shadow = defaultArg(tryField("ShadowCount", b), 0) | 0;
                            const shadowLabel = (shadow > 1) ? toText(printf(" (×%d)"))(shadow) : "";
                            return toText(printf("%s : %s%s"))(name_1)(typeSig)(shadowLabel);
                        }
                        default:
                            return undefined;
                    }
                }, bindings), "FSI Bindings"));
                break;
            }
        }
    });
    reg("sagefs.showPipelineTrace", (_arg_29) => {
        let arg_11, arg_12, arg_13, arg_14, arg_15;
        const matchValue_5 = bind((l_2) => l_2.PipelineTrace(), liveTestListener());
        if (matchValue_5 == null) {
            Window_showInformationMessage("No pipeline trace data yet", []);
        }
        else {
            const trace = value_29(matchValue_5);
            promiseIgnore(Window_showQuickPick([(arg_11 = defaultArg(tryField("Enabled", trace), false), toText(printf("Enabled: %b"))(arg_11)), (arg_12 = defaultArg(tryField("IsRunning", trace), false), toText(printf("Running: %b"))(arg_12)), (arg_13 = (defaultArg(bind((obj) => tryField("Total", obj), tryField("Summary", trace)), 0) | 0), (arg_14 = (defaultArg(bind((obj_1) => tryField("Passed", obj_1), tryField("Summary", trace)), 0) | 0), (arg_15 = (defaultArg(bind((obj_2) => tryField("Failed", obj_2), tryField("Summary", trace)), 0) | 0), toText(printf("Total: %d | Passed: %d | Failed: %d"))(arg_13)(arg_14)(arg_15))))], "Pipeline Trace"));
        }
    });
    reg("sagefs.exportSession", (_arg_30) => {
        promiseIgnore(withClient((c_7) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
            if (activeSessionId() != null) {
                const sid = activeSessionId();
                return exportSessionAsFsx(sid, c_7).then((_arg_31) => {
                    const result_1 = _arg_31;
                    if (result_1 != null) {
                        const r = result_1;
                        if (r.evalCount === 0) {
                            Window_showInformationMessage("No evaluations to export", []);
                            return Promise.resolve();
                        }
                        else {
                            return Workspace_openTextDocument(r.content, "fsharp").then((_arg_32) => (Window_showTextDocument(_arg_32).then((_arg_33) => {
                                return Promise.resolve();
                            })));
                        }
                    }
                    else {
                        Window_showErrorMessage("Failed to export session", []);
                        return Promise.resolve();
                    }
                });
            }
            else {
                Window_showInformationMessage("No active session", []);
                return Promise.resolve();
            }
        }))));
    });
    const lensProvider = create_2();
    void (context.subscriptions.push(Languages_registerCodeLensProvider("fsharp", lensProvider)));
    const testLensProvider = create_3();
    void (context.subscriptions.push(Languages_registerCodeLensProvider("fsharp", testLensProvider)));
    const completionProvider = create_4(client, () => bind((folders) => {
        if (!equalsWith(equals, folders, defaultOf()) && (folders.length === 0)) {
            return undefined;
        }
        else {
            return item(0, folders).uri.fsPath;
        }
    }, Workspace_workspaceFolders()));
    void (context.subscriptions.push(Languages_registerCompletionItemProvider("fsharp", completionProvider, ["."])));
    hijackIonideSendToFsi(context.subscriptions);
    const connectToRunningDaemon = (c_8) => {
        iterate((d) => {
            d.dispose();
        }, toArray(sseDisposable()));
        sseDisposable(undefined);
        iterate((l_3) => {
            l_3.Dispose();
        }, toArray(liveTestListener()));
        liveTestListener(undefined);
        iterate((a) => {
            a.Dispose();
        }, toArray(testAdapter()));
        testAdapter(undefined);
        iterate((d_1) => {
            d_1.dispose();
        }, toArray(diagnosticsDisposable()));
        diagnosticsDisposable(undefined);
        diagnosticsDisposable(start(c_8.mcpPort, dc));
        const adapter = create_5(client);
        testAdapter(adapter);
        initialize();
        const refreshAllDecorations = () => {
            const state = defaultArg(map_1((l_4) => l_4.State(), liveTestListener()), VscLiveTestStateModule_empty);
            applyToAllEditors(state);
            applyCoverageToAllEditors(state);
            return state;
        };
        const listener = start_1(c_8.mcpPort, new LiveTestingCallbacks((changes) => {
            adapter.Refresh(changes);
            const state_1 = refreshAllDecorations();
            updateDiagnostics(state_1);
            updateState(state_1);
        }, (summary) => {
            updateTestStatusBar(summary);
        }, () => {
            refreshStatus();
        }, (_arg_34) => {
        }, (_arg_35) => {
        }, undefined));
        liveTestListener(listener);
        sseDisposable({
            dispose() {
                listener.Dispose();
                return defaultOf();
            },
        });
        void (context.subscriptions.push(Window_onDidChangeVisibleTextEditors((_editors) => {
            refreshAllDecorations();
        })));
        void (context.subscriptions.push(Window_onDidChangeActiveTextEditor((_editor) => {
            refreshAllDecorations();
        })));
        promiseIgnore(PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (listSessions(c_8).then((_arg_36) => {
            const sessions = _arg_36;
            if (!equalsWith(equals, sessions, defaultOf()) && (sessions.length === 0)) {
                return findProject().then((_arg_37) => {
                    const projOpt = _arg_37;
                    if (projOpt == null) {
                        return Promise.resolve();
                    }
                    else {
                        const proj = projOpt;
                        const workDir = defaultArg(getWorkingDirectory(), ".");
                        return Window_showInformationMessage(toText(printf("SageFs is running but has no session. Create one for %s?"))(proj), ["Create Session", "Not Now"]).then((_arg_38) => {
                            const choice = _arg_38;
                            let matchResult_4;
                            if (choice != null) {
                                if (choice === "Create Session") {
                                    matchResult_4 = 0;
                                }
                                else {
                                    matchResult_4 = 1;
                                }
                            }
                            else {
                                matchResult_4 = 1;
                            }
                            switch (matchResult_4) {
                                case 0:
                                    return createSession(proj, workDir, c_8).then((_arg_39) => (((_arg_39.tag === 1) ? (Promise.resolve()) : ((void Window_showInformationMessage(toText(printf("SageFs: Session created for %s"))(proj), []), Promise.resolve()))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                        refreshStatus();
                                        return Promise.resolve();
                                    }))));
                                default: {
                                    return Promise.resolve();
                                }
                            }
                        });
                    }
                });
            }
            else {
                return Promise.resolve();
            }
        })))));
    };
    onDaemonReady(connectToRunningDaemon);
    promiseIgnore(PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (isRunning(c).then((_arg_40) => {
        if (_arg_40) {
            connectToRunningDaemon(c);
            return Promise.resolve();
        }
        else {
            return Promise.resolve();
        }
    })))));
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
        promiseIgnore(PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (isRunning(c).then((_arg_41) => (!_arg_41 ? (findProject().then((_arg_42) => {
            if (_arg_42 == null) {
                return Promise.resolve();
            }
            else {
                return startDaemon().then(() => (Promise.resolve(undefined)));
            }
        })) : (Promise.resolve())))))));
    }
}

export function deactivate() {
    iterate((d) => {
        d.dispose();
    }, toArray(diagnosticsDisposable()));
    iterate((d_1) => {
        d_1.dispose();
    }, toArray(sseDisposable()));
    iterate((l) => {
        l.Dispose();
    }, toArray(liveTestListener()));
    liveTestListener(undefined);
    iterate((a) => {
        a.Dispose();
    }, toArray(testAdapter()));
    testAdapter(undefined);
    iterate((te) => {
        te.dispose();
    }, toArray(typeExplorer()));
    typeExplorer(undefined);
    iterate((p) => {
        const value_2 = p.dispose();
    }, toArray(dashboardPanel()));
    dashboardPanel(undefined);
    dispose();
    clearAllDecorations();
}

