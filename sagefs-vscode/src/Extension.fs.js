import { compareArrays, equalArrays, equals, defaultOf, disposeSafe, getEnumerator, comparePrimitives, createAtom } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { toArray as toArray_1, isEmpty as isEmpty_1, add, remove, tryFind, iterate as iterate_1, empty as empty_1 } from "./fable_modules/fable-library-js.4.29.0/Map.js";
import { toString, Union } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { float64_type, string_type, union_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";
import { Window_onDidChangeTextEditorSelection, Workspace_onDidChangeConfiguration, Window_onDidChangeActiveTextEditor, Window_onDidChangeVisibleTextEditors, Languages_registerCompletionItemProvider, Languages_registerCodeLensProvider, Window_showInputBox, Workspace_onDidChangeTextDocument, Languages_createDiagnosticCollection, Commands_registerCommand, newSelection, newPosition, Window_createWebviewPanel, uriFile, Workspace_openTextDocumentUri, Window_withProgress, Window_getActiveTextEditor, Window_showWarningMessage, Window_showErrorMessage, Window_showTextDocument, Workspace_openTextDocument, Window_createTerminal, Commands_executeCommand, Window_showOpenDialog, Window_showQuickPick, Workspace_asRelativePath, Workspace_findFiles, Workspace_workspaceFolders, Window_createStatusBarItem, Window_createOutputChannel, Window_getVisibleTextEditors, newRange, newThemeColor, Window_createTextEditorDecorationType, Window_showInformationMessage, Workspace_getConfiguration } from "./Vscode.fs.js";
import { substring, split, join, trimEnd, printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { updateCellHighlight, showEvalInProgress, clearEvalInProgress, clearAllDecorations, markDecorationsStale, blockDecorations, showInlineDiagnostic, showInlineResult, flashEvalRange, formatDuration, clearCellHighlight } from "./InlineDecorations.fs.js";
import { iterate } from "./fable_modules/fable-library-js.4.29.0/Seq.js";
import { value as value_40, bind, map as map_1, defaultArg, some, toArray } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { exists, ofArray, filter, isEmpty } from "./fable_modules/fable-library-js.4.29.0/List.js";
import { fold, sortBy, tryFindIndex, last, choose, tryFind as tryFind_1, tryHead, equalsWith, append, map, item } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { parseFileAnnotations } from "./FileAnnotationsListener.fs.js";
import { PromiseBuilder__For_1565554B, PromiseBuilder__While_2044D34, PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { FeatureCallbacks, formatSparklineStatus } from "./FeatureTypes.fs.js";
import { promiseIgnore, promiseIgnoreLog } from "./JsHelpers.fs.js";
import { updatePorts, checkVersion, exportSessionAsFsx, getDependencyGraph, getRecentEvents, ApiOutcomeModule_message, setRunPolicy, runTests, disableLiveTesting, enableLiveTesting, create, loadScript, cancelEval, dashboardUrl, stopSession, switchSession, createSession, hardReset, resetSession, evalCode, isReady, ApiOutcomeModule_messageOrDefault, isRunning, listSessions, getSystemStatus, getStatus } from "./SageFsClient.fs.js";
import { stopAutoRefresh, register, setSession } from "./HotReloadTreeProvider.fs.js";
import { stopAutoRefresh as stopAutoRefresh_1, register as register_1, setSession as setSession_1 } from "./SessionContextTreeProvider.fs.js";
import { register as register_2, setSession as setSession_2 } from "./SessionsTreeProvider.fs.js";
import { create as create_1, setClient } from "./TypeExplorerProvider.fs.js";
import { rangeDouble } from "./fable_modules/fable-library-js.4.29.0/Range.js";
import { tryCastString, fieldObj, fieldBool, fieldString, fieldArray, fieldInt } from "./SafeInterop.fs.js";
import { updateDiagnosis, updateNarratives, updateState, create as create_3, diagnosisState, narrativeState } from "./TestCodeLensProvider.fs.js";
import { create as create_2 } from "./CodeLensProvider.fs.js";
import { create as create_4 } from "./CompletionProvider.fs.js";
import { start } from "./DiagnosticsListener.fs.js";
import { create as create_5 } from "./TestControllerAdapter.fs.js";
import { dispose, updateDiagnostics, applyCoverageToAllEditors, applyToAllEditors, initialize } from "./TestDecorations.fs.js";
import { VscDiagnosisFailure, VscLiveTestStateModule_empty } from "./LiveTestingTypes.fs.js";
import { start as start_1, LiveTestingCallbacks } from "./LiveTestingListener.fs.js";

export let client = createAtom(undefined);

export let outputChannel = createAtom(undefined);

export let statusBarItem = createAtom(undefined);

export let testStatusBarItem = createAtom(undefined);

export let evalPerfStatusBar = createAtom(undefined);

export let diagnosticsDisposable = createAtom(undefined);

export let sseDisposable = createAtom(undefined);

export let diagnosticCollection = createAtom(undefined);

export let activeSessionId = createAtom(undefined);

export let liveTestListener = createAtom(undefined);

export let testAdapter = createAtom(undefined);

export let dashboardPanel = createAtom(undefined);

export let typeExplorer = createAtom(undefined);

export let wasRunning = createAtom(false);

export let crashPromptShown = createAtom(false);

export let staleDebounceTimer = createAtom(undefined);

export let daemonStderr = createAtom("");

export let evalId = createAtom(0);

export let evalWatchdogTimer = createAtom(undefined);

export let warmupPhase = createAtom(undefined);

export let warmupDetail = createAtom(undefined);

let covPassingDecoType = undefined;

let covFailingDecoType = undefined;

let covNoneDecoType = undefined;

let inlineFailureDecoTypes = empty_1({
    Compare: comparePrimitives,
});

let fileAnnotationsCache = empty_1({
    Compare: comparePrimitives,
});

export class Density extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["Full", "Normal", "Minimal"];
    }
}

export function Density_$reflection() {
    return union_type("SageFs.Vscode.Extension.Density", [], Density, () => [[], [], []]);
}

export let currentDensity = createAtom(new Density(0, []));

export function densityFromString(s) {
    const matchValue = s.toLowerCase();
    switch (matchValue) {
        case "normal":
            return new Density(1, []);
        case "minimal":
            return new Density(2, []);
        default:
            return new Density(0, []);
    }
}

export function densityToString(_arg) {
    switch (_arg.tag) {
        case 1:
            return "normal";
        case 2:
            return "minimal";
        default:
            return "full";
    }
}

export function densityLabel(_arg) {
    switch (_arg.tag) {
        case 1:
            return "Normal";
        case 2:
            return "Minimal";
        default:
            return "Full";
    }
}

export function cycleDensity() {
    let arg;
    const next = (currentDensity().tag === 1) ? (new Density(2, [])) : ((currentDensity().tag === 2) ? (new Density(0, [])) : (new Density(1, [])));
    currentDensity(next);
    const cfg = Workspace_getConfiguration("sagefs");
    cfg.update("density", densityToString(next), 1);
    Window_showInformationMessage((arg = densityLabel(next), toText(printf("SageFs density: %s"))(arg)), []);
    switch (next.tag) {
        case 0: {
            break;
        }
        default:
            clearCellHighlight();
    }
}

export function initFileAnnotationDecoTypes() {
    covPassingDecoType = Window_createTextEditorDecorationType({
        isWholeLine: false,
        gutterIconPath: "",
        overviewRulerColor: newThemeColor("testing.iconPassed"),
        overviewRulerLane: 1,
        before: {
            contentText: "│",
            color: newThemeColor("testing.iconPassed"),
            margin: "0 0.3em 0 0",
        },
    });
    covFailingDecoType = Window_createTextEditorDecorationType({
        isWholeLine: false,
        overviewRulerColor: newThemeColor("testing.iconFailed"),
        overviewRulerLane: 1,
        before: {
            contentText: "│",
            color: newThemeColor("testing.iconFailed"),
            margin: "0 0.3em 0 0",
        },
    });
    covNoneDecoType = Window_createTextEditorDecorationType({
        isWholeLine: false,
        before: {
            contentText: "│",
            color: newThemeColor("disabledForeground"),
            margin: "0 0.3em 0 0",
        },
    });
}

export function disposeFileAnnotationDecoTypes() {
    iterate((d) => {
        d.dispose();
    }, toArray(covPassingDecoType));
    iterate((d_1) => {
        d_1.dispose();
    }, toArray(covFailingDecoType));
    iterate((d_2) => {
        d_2.dispose();
    }, toArray(covNoneDecoType));
    covPassingDecoType = undefined;
    covFailingDecoType = undefined;
    covNoneDecoType = undefined;
    iterate_1((_arg, d_3) => {
        d_3.dispose();
    }, inlineFailureDecoTypes);
    inlineFailureDecoTypes = empty_1({
        Compare: comparePrimitives,
    });
    fileAnnotationsCache = empty_1({
        Compare: comparePrimitives,
    });
}

export function applyFileAnnotationsToEditor(editor) {
    let matchValue_1;
    const filePath = editor.document.fileName;
    const matchValue = tryFind(filePath, fileAnnotationsCache);
    if (matchValue != null) {
        const annotations = matchValue;
        const passRanges = [];
        const failRanges = [];
        const noneRanges = [];
        const enumerator = getEnumerator(annotations.CoverageAnnotations);
        try {
            while (enumerator["System.Collections.IEnumerator.MoveNext"]()) {
                const ann = enumerator["System.Collections.Generic.IEnumerator`1.get_Current"]();
                const decoObj = {
                    range: newRange(ann.Line - 1, 0, ann.Line - 1, 0),
                    hoverMessage: (matchValue_1 = ann.Health, (matchValue_1.tag === 1) ? "Coverage: some tests failing" : ((matchValue_1.tag === 2) ? "No coverage" : "Coverage: all tests passing")),
                };
                const matchValue_2 = ann.Health;
                switch (matchValue_2.tag) {
                    case 1: {
                        void (failRanges.push(decoObj));
                        break;
                    }
                    case 2: {
                        void (noneRanges.push(decoObj));
                        break;
                    }
                    default:
                        void (passRanges.push(decoObj));
                }
            }
        }
        finally {
            disposeSafe(enumerator);
        }
        iterate((dt_3) => {
            editor.setDecorations(dt_3, passRanges);
        }, toArray(covPassingDecoType));
        iterate((dt_4) => {
            editor.setDecorations(dt_4, failRanges);
        }, toArray(covFailingDecoType));
        iterate((dt_5) => {
            editor.setDecorations(dt_5, noneRanges);
        }, toArray(covNoneDecoType));
        const matchValue_3 = tryFind(filePath, inlineFailureDecoTypes);
        if (matchValue_3 == null) {
        }
        else {
            const old = matchValue_3;
            old.dispose();
        }
        const matchValue_4 = annotations.InlineFailures;
        if (isEmpty(matchValue_4)) {
            inlineFailureDecoTypes = remove(filePath, inlineFailureDecoTypes);
        }
        else {
            const deco = Window_createTextEditorDecorationType({
                after: {
                    color: newThemeColor("testing.iconFailed"),
                    fontStyle: "italic",
                    margin: "0 0 0 1.5em",
                },
            });
            const ranges = [];
            const enumerator_1 = getEnumerator(matchValue_4);
            try {
                while (enumerator_1["System.Collections.IEnumerator.MoveNext"]()) {
                    const f = enumerator_1["System.Collections.Generic.IEnumerator`1.get_Current"]();
                    const line = (f.Line - 1) | 0;
                    let text;
                    const matchValue_5 = f.Presentation;
                    text = ((matchValue_5 === "") ? toText(printf("⊘ %s"))(f.TestName) : toText(printf("⊘ %s — %s"))(f.TestName)(matchValue_5));
                    const range_1 = newRange(line, 0, line, 0);
                    void (ranges.push({
                        range: range_1,
                        renderOptions: {
                            after: {
                                contentText: text,
                            },
                        },
                    }));
                }
            }
            finally {
                disposeSafe(enumerator_1);
            }
            editor.setDecorations(deco, ranges);
            inlineFailureDecoTypes = add(filePath, deco, inlineFailureDecoTypes);
        }
    }
    else {
        iterate((dt) => {
            editor.setDecorations(dt, []);
        }, toArray(covPassingDecoType));
        iterate((dt_1) => {
            editor.setDecorations(dt_1, []);
        }, toArray(covFailingDecoType));
        iterate((dt_2) => {
            editor.setDecorations(dt_2, []);
        }, toArray(covNoneDecoType));
    }
}

export function applyFileAnnotationsToAllEditors() {
    const editors = Window_getVisibleTextEditors();
    for (let idx = 0; idx <= (editors.length - 1); idx++) {
        applyFileAnnotationsToEditor(item(idx, editors));
    }
}

export function handleFileAnnotations(data) {
    const matchValue = parseFileAnnotations(data);
    if (matchValue != null) {
        const annotations = matchValue;
        fileAnnotationsCache = add(annotations.FilePath, annotations, fileAnnotationsCache);
        applyFileAnnotationsToAllEditors();
    }
}

export let daemonProcess = createAtom(undefined);

export let isStarting = createAtom(false);

export let onDaemonReady = createAtom(undefined);

const autoOpenNamespacesOptOutTemplate = "{ DirectoryConfig.empty with\r\n  AutoOpenNamespaces = false\r\n}\r\n";

export class WarmupAutoOpenConfigResult extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["Created", "AlreadyDisabled", "RequiresManualEdit"];
    }
}

export function WarmupAutoOpenConfigResult_$reflection() {
    return union_type("SageFs.Vscode.Extension.WarmupAutoOpenConfigResult", [], WarmupAutoOpenConfigResult, () => [[["Item", string_type]], [["Item", string_type]], [["Item", string_type]]]);
}

function trimTrailingSeparators(path) {
    return trimEnd(path, "\\", "/");
}

function combineWindowsPath(basePath, child) {
    const arg = trimTrailingSeparators(basePath);
    return toText(printf("%s\\%s"))(arg)(child);
}

export function getOutput() {
    if (outputChannel() == null) {
        const o_1 = Window_createOutputChannel("SageFs");
        outputChannel(o_1);
        return o_1;
    }
    else {
        return outputChannel();
    }
}

export function getStatusBar() {
    if (statusBarItem() == null) {
        const s_1 = Window_createStatusBarItem(1, 100);
        statusBarItem(s_1);
        return s_1;
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

export let activeProjectPath = createAtom(undefined);

export function scanForProjects() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (Workspace_findFiles("**/*.{sln,slnx}", "**/node_modules/**", 5).then((_arg) => (Workspace_findFiles("**/*.fsproj", "**/node_modules/**", 10).then((_arg_1) => {
        const solutions = map(Workspace_asRelativePath, _arg);
        const projects = map(Workspace_asRelativePath, _arg_1);
        return Promise.resolve(append(solutions, projects));
    }))))));
}

export function persistProjectChoice(projectPath) {
    const config = Workspace_getConfiguration("sagefs");
    config.update("projectPath", projectPath, 1);
    activeProjectPath(projectPath);
}

export function findProject() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const config = Workspace_getConfiguration("sagefs");
        const configured = config.get("projectPath", "");
        if (configured !== "") {
            const c_1 = configured;
            activeProjectPath(c_1);
            return Promise.resolve(c_1);
        }
        else {
            return scanForProjects().then((_arg) => {
                const all = _arg;
                if (!equalsWith((x, y) => (x === y), all, defaultOf()) && (all.length === 0)) {
                    return Promise.resolve(undefined);
                }
                else if (!equalsWith((x_1, y_1) => (x_1 === y_1), all, defaultOf()) && (all.length === 1)) {
                    const single = item(0, all);
                    activeProjectPath(single);
                    return Promise.resolve(single);
                }
                else {
                    return Window_showQuickPick(all, "Select a solution or project for SageFs").then((_arg_1) => {
                        const picked = _arg_1;
                        return ((picked == null) ? (Promise.resolve()) : ((persistProjectChoice(picked), Promise.resolve()))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => (Promise.resolve(picked))));
                    });
                }
            });
        }
    }));
}

/**
 * Check if the file uses ;; delimiters anywhere.
 */
export function hasSemiSemiDelimiters(doc) {
    const lineCount = ~~doc.lineCount | 0;
    let found = false;
    let i = 0;
    while (!found && (i < lineCount)) {
        if (trimEnd(doc.lineAt(i).text).endsWith(";;")) {
            found = true;
        }
        i = ((i + 1) | 0);
    }
    return found;
}

/**
 * Find code block boundaries around a given line in the document.
 * Returns (startLine, endLine).
 */
export function getBlockBounds(doc, curLine) {
    const lineCount = ~~doc.lineCount | 0;
    const isBlank = (n) => (doc.lineAt(n).text.trim() === "");
    const endsWithSS = (n_1) => trimEnd(doc.lineAt(n_1).text).endsWith(";;");
    if (hasSemiSemiDelimiters(doc)) {
        let s = curLine;
        while ((s > 0) && !endsWithSS(s - 1)) {
            s = ((s - 1) | 0);
        }
        let e = curLine;
        while ((e < (lineCount - 1)) && !endsWithSS(e)) {
            e = ((e + 1) | 0);
        }
        return [s, e];
    }
    else {
        let s_1 = curLine;
        while ((s_1 > 0) && !isBlank(s_1 - 1)) {
            s_1 = ((s_1 - 1) | 0);
        }
        let e_1 = curLine;
        while ((e_1 < (lineCount - 1)) && !isBlank(e_1 + 1)) {
            e_1 = ((e_1 + 1) | 0);
        }
        return [s_1, e_1];
    }
}

/**
 * Find the code block boundaries around the cursor.
 * Returns (text, startLine, endLine).
 */
export function getCodeBlock(editor) {
    const doc = editor.document;
    const patternInput = getBlockBounds(doc, ~~editor.selection.active.line);
    const startLine = patternInput[0] | 0;
    const endLine = patternInput[1] | 0;
    const range = newRange(startLine, 0, endLine, doc.lineAt(endLine).text.length);
    return [doc.getText(range), startLine, endLine];
}

export function showOutputPanel() {
    getOutput().show(true);
}

export function browseForProject() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const filters = {
            "F# Projects": ["fsproj", "sln", "slnx"],
        };
        return Window_showOpenDialog(filters, false, "Select Project").then((_arg) => {
            let arr;
            const uris = _arg;
            let matchResult, arr_1;
            if (uris != null) {
                if ((arr = uris, arr.length > 0)) {
                    matchResult = 0;
                    arr_1 = uris;
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
                    const uri = item(0, arr_1);
                    const config = Workspace_getConfiguration("sagefs");
                    return config.update("projectPath", uri.fsPath, 1).then(() => {
                        Commands_executeCommand("sagefs.start");
                        return Promise.resolve();
                    });
                }
                default: {
                    return Promise.resolve();
                }
            }
        });
    }));
}

export function openWorkspace() {
    Commands_executeCommand("vscode.openFolder");
}

export function checkInstallation() {
    const term = Window_createTerminal("SageFs Version Check");
    term.show();
    term.sendText("sagefs --version");
}

export function openQuickFile() {
    Commands_executeCommand("workbench.action.quickOpen");
}

export function openGettingStarted() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const content = "// ── SageFs Getting Started ─────────────────────────────────\n// Welcome! Select each expression and press Alt+Enter (Ctrl+Enter on Mac)\n// to evaluate it. Results appear inline, right next to your code.\n\n// ── Step 1: Simple expressions ──\n1 + 1;;\n\n\"Hello from SageFs!\";;\n\n// ── Step 2: Let bindings ──\nlet greeting = \"Welcome to live F# development\";;\ngreeting.ToUpper();;\n\n// ── Step 3: Functions and pipelines ──\nlet square x = x * x;;\nsquare 7;;\n\n[1..10] |> List.filter (fun n -> n % 2 = 0) |> List.map square;;\n\n// ── Step 4: Records and pattern matching ──\ntype Shape =\n  | Circle of radius: float\n  | Rectangle of width: float * height: float;;\n\nlet area shape =\n  match shape with\n  | Circle r -> System.Math.PI * r * r\n  | Rectangle (w, h) -> w * h;;\n\narea (Circle 5.0);;\narea (Rectangle (3.0, 4.0));;\n\n// ── Step 5: Try editing! ──\n// Change the values above and re-evaluate. SageFs keeps your session\n// alive — previous definitions stay available.\n//\n// Next steps:\n//   • Save an .fs file to trigger hot reload + live test updates\n//   • Check the SageFs sidebar for test results and sessions\n//   • Try \'SageFs: Show Call Graph\' from the command palette\n//   • Explore samples/ in the SageFs repo for more examples\n";
        return Workspace_openTextDocument(content, "fsharp").then((_arg) => (Window_showTextDocument(_arg).then((_arg_1) => (Promise.resolve(undefined)))));
    }));
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

export function updateEvalPerfBar(stats) {
    let clo_1, clo_2, clo_3, clo_4;
    if (evalPerfStatusBar() != null) {
        const sb = evalPerfStatusBar();
        const text = formatSparklineStatus(stats);
        if (text === "") {
            sb.hide();
        }
        else {
            let bg;
            const matchValue = stats.P50Ms;
            let matchResult, ms_2, ms_3;
            if (matchValue != null) {
                if (matchValue > 500) {
                    matchResult = 0;
                    ms_2 = matchValue;
                }
                else if (matchValue > 100) {
                    matchResult = 1;
                    ms_3 = matchValue;
                }
                else {
                    matchResult = 2;
                }
            }
            else {
                matchResult = 2;
            }
            switch (matchResult) {
                case 0: {
                    bg = some(newThemeColor("statusBarItem.errorBackground"));
                    break;
                }
                case 1: {
                    bg = some(newThemeColor("statusBarItem.warningBackground"));
                    break;
                }
                default:
                    bg = undefined;
            }
            sb.text = text;
            sb.backgroundColor = bg;
            sb.tooltip = join("\n", filter((s) => (s !== ""), ofArray([toText(printf("Eval Performance Timeline (%d evals)"))(stats.Count), defaultArg(map_1((clo_1 = toText(printf("P50: %.1f ms")), clo_1), stats.P50Ms), ""), defaultArg(map_1((clo_2 = toText(printf("P95: %.1f ms")), clo_2), stats.P95Ms), ""), defaultArg(map_1((clo_3 = toText(printf("P99: %.1f ms")), clo_3), stats.P99Ms), ""), defaultArg(map_1((clo_4 = toText(printf("Mean: %.1f ms")), clo_4), stats.MeanMs), "")])));
            sb.show();
        }
    }
}

export function refreshStatus() {
    promiseIgnoreLog((msg_1) => {
        getOutput().appendLine(msg_1);
    }, PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const matchValue = client();
        const matchValue_1 = statusBarItem();
        let matchResult, c, sb;
        if (matchValue != null) {
            if (matchValue_1 != null) {
                matchResult = 0;
                c = matchValue;
                sb = matchValue_1;
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
                return PromiseBuilder__Delay_62FBFDE1(promise, () => (getStatus(c).then((_arg) => {
                    const status = _arg;
                    if (status.connected) {
                        wasRunning(true);
                        crashPromptShown(false);
                        return getSystemStatus(c).then((_arg_2) => {
                            let s_1, s_3, matchValue_6, phase, phaseLabel, d, matchValue_12, err;
                            const sys = _arg_2;
                            const supervised = (sys != null) ? (sys.supervised ? ((s_1 = sys, " $(shield)")) : "") : "";
                            const restarts = (sys != null) ? ((sys.restartCount > 0) ? ((s_3 = sys, toText(printf(" %d↻"))(s_3.restartCount))) : "") : "";
                            return ((matchValue_6 = status.status, (matchValue_6 != null) ? ((matchValue_6 === "Ready") ? ((warmupPhase(undefined), (warmupDetail(undefined), listSessions(c).then((_arg_3) => {
                                let s_5, projLabel, matchValue_7, s_6, projFile, matchValue_9, sessionCount, evalLabel, matchValue_11, tooltipText;
                                const sessions = _arg_3;
                                let session;
                                if (activeSessionId() == null) {
                                    session = tryHead(sessions);
                                }
                                else {
                                    const id = activeSessionId();
                                    session = tryFind_1((s_4) => (s_4.id === id), sessions);
                                }
                                return ((session == null) ? ((activeSessionId(undefined), (sb.text = toText(printf("$(zap) SageFs: ready (no session)%s%s"))(supervised)(restarts), Promise.resolve()))) : ((s_5 = session, (activeSessionId(s_5.id), (projLabel = ((matchValue_7 = s_5.projects, (!equalsWith((x, y) => (x === y), matchValue_7, defaultOf()) && (matchValue_7.length === 0)) ? "session" : ((s_6 = join(",", choose((p_1) => {
                                    let name, n_3, n_4, n_5;
                                    if ((p_1 == null)) {
                                        return undefined;
                                    }
                                    else {
                                        return (name = last(split(p_1, ["/", "\\"])), ((name == null)) ? "" : (name.endsWith(".fsproj") ? ((n_3 = name, n_3.slice(undefined, (n_3.length - 8) + 1))) : (name.endsWith(".slnx") ? ((n_4 = name, n_4.slice(undefined, (n_4.length - 6) + 1))) : (name.endsWith(".sln") ? ((n_5 = name, n_5.slice(undefined, (n_5.length - 5) + 1))) : name))));
                                    }
                                }, matchValue_7)), (s_6 === "") ? "session" : s_6)))), (projFile = ((activeProjectPath() == null) ? ((matchValue_9 = s_5.projects, (!equalsWith((x_2, y_1) => (x_2 === y_1), matchValue_9, defaultOf()) && (matchValue_9.length === 0)) ? "" : defaultArg(tryHead(choose((p_3) => {
                                    if ((p_3 == null)) {
                                        return undefined;
                                    }
                                    else {
                                        return last(split(p_3, ["/", "\\"]));
                                    }
                                }, matchValue_9)), ""))) : last(split(activeProjectPath(), ["/", "\\"]))), (sessionCount = (sessions.length | 0), (evalLabel = ((matchValue_11 = (s_5.evalCount | 0), (matchValue_11 === 0) ? "" : toText(printf(" [%d]"))(matchValue_11))), (sb.text = toText(printf("$(zap) SageFs: %s%s%s%s"))(projLabel)(evalLabel)(supervised)(restarts), (tooltipText = ((projFile === "") ? toText(printf("SageFs — %d session(s) — click for session menu"))(sessionCount) : toText(printf("SageFs: %s — %d session(s) — click for session menu"))(projFile)(sessionCount)), (sb.tooltip = tooltipText, Promise.resolve()))))))))))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                    sb.backgroundColor = undefined;
                                    const activeId = activeSessionId();
                                    setSession(c, activeId);
                                    setSession_1(c, activeId);
                                    setSession_2(c, activeId);
                                    setClient(c);
                                    return Promise.resolve();
                                }));
                            })))) : ((matchValue_6 === "Evaluating") ? ((warmupPhase(undefined), (warmupDetail(undefined), listSessions(c).then((_arg_3) => {
                                let s_5, projLabel, matchValue_7, s_6, projFile, matchValue_9, sessionCount, evalLabel, matchValue_11, tooltipText;
                                const sessions = _arg_3;
                                let session;
                                if (activeSessionId() == null) {
                                    session = tryHead(sessions);
                                }
                                else {
                                    const id = activeSessionId();
                                    session = tryFind_1((s_4) => (s_4.id === id), sessions);
                                }
                                return ((session == null) ? ((activeSessionId(undefined), (sb.text = toText(printf("$(zap) SageFs: ready (no session)%s%s"))(supervised)(restarts), Promise.resolve()))) : ((s_5 = session, (activeSessionId(s_5.id), (projLabel = ((matchValue_7 = s_5.projects, (!equalsWith((x, y) => (x === y), matchValue_7, defaultOf()) && (matchValue_7.length === 0)) ? "session" : ((s_6 = join(",", choose((p_1) => {
                                    let name, n_3, n_4, n_5;
                                    if ((p_1 == null)) {
                                        return undefined;
                                    }
                                    else {
                                        return (name = last(split(p_1, ["/", "\\"])), ((name == null)) ? "" : (name.endsWith(".fsproj") ? ((n_3 = name, n_3.slice(undefined, (n_3.length - 8) + 1))) : (name.endsWith(".slnx") ? ((n_4 = name, n_4.slice(undefined, (n_4.length - 6) + 1))) : (name.endsWith(".sln") ? ((n_5 = name, n_5.slice(undefined, (n_5.length - 5) + 1))) : name))));
                                    }
                                }, matchValue_7)), (s_6 === "") ? "session" : s_6)))), (projFile = ((activeProjectPath() == null) ? ((matchValue_9 = s_5.projects, (!equalsWith((x_2, y_1) => (x_2 === y_1), matchValue_9, defaultOf()) && (matchValue_9.length === 0)) ? "" : defaultArg(tryHead(choose((p_3) => {
                                    if ((p_3 == null)) {
                                        return undefined;
                                    }
                                    else {
                                        return last(split(p_3, ["/", "\\"]));
                                    }
                                }, matchValue_9)), ""))) : last(split(activeProjectPath(), ["/", "\\"]))), (sessionCount = (sessions.length | 0), (evalLabel = ((matchValue_11 = (s_5.evalCount | 0), (matchValue_11 === 0) ? "" : toText(printf(" [%d]"))(matchValue_11))), (sb.text = toText(printf("$(zap) SageFs: %s%s%s%s"))(projLabel)(evalLabel)(supervised)(restarts), (tooltipText = ((projFile === "") ? toText(printf("SageFs — %d session(s) — click for session menu"))(sessionCount) : toText(printf("SageFs: %s — %d session(s) — click for session menu"))(projFile)(sessionCount)), (sb.tooltip = tooltipText, Promise.resolve()))))))))))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                    sb.backgroundColor = undefined;
                                    const activeId = activeSessionId();
                                    setSession(c, activeId);
                                    setSession_1(c, activeId);
                                    setSession_2(c, activeId);
                                    setClient(c);
                                    return Promise.resolve();
                                }));
                            })))) : ((matchValue_6 === "Starting") ? (((warmupPhase() == null) ? ((sb.text = "$(loading~spin) SageFs: warming up...", Promise.resolve())) : ((phase = warmupPhase(), (phaseLabel = ((phase === "creating_fsi") ? "Creating FSI..." : ((phase === "scanning_sources") ? "Scanning sources..." : ((phase === "loading_assemblies") ? "Loading assemblies..." : ((phase === "opening_namespaces") ? ((warmupDetail() == null) ? "Opening namespaces..." : ((d = warmupDetail(), toText(printf("Opening namespaces (%s)"))(d)))) : ((phase === "finalizing") ? "Finalizing..." : "Warming up..."))))), (sb.text = toText(printf("$(loading~spin) SageFs: %s"))(phaseLabel), Promise.resolve()))))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                sb.backgroundColor = undefined;
                                return Promise.resolve();
                            }))) : ((matchValue_6 === "Restarting") ? (((warmupPhase() == null) ? ((sb.text = "$(loading~spin) SageFs: warming up...", Promise.resolve())) : ((phase = warmupPhase(), (phaseLabel = ((phase === "creating_fsi") ? "Creating FSI..." : ((phase === "scanning_sources") ? "Scanning sources..." : ((phase === "loading_assemblies") ? "Loading assemblies..." : ((phase === "opening_namespaces") ? ((warmupDetail() == null) ? "Opening namespaces..." : ((d = warmupDetail(), toText(printf("Opening namespaces (%s)"))(d)))) : ((phase === "finalizing") ? "Finalizing..." : "Warming up..."))))), (sb.text = toText(printf("$(loading~spin) SageFs: %s"))(phaseLabel), Promise.resolve()))))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                sb.backgroundColor = undefined;
                                return Promise.resolve();
                            }))) : ((matchValue_6 === "Faulted") ? (((matchValue_12 = status.error, (matchValue_12 == null) ? ((sb.text = "$(error) SageFs: session error", Promise.resolve())) : ((err = matchValue_12, (sb.text = "$(error) SageFs: session error", (sb.tooltip = err.message, Window_showErrorMessage(err.message, [err.suggestedAction, "Show Output"]).then((_arg_4) => {
                                const choice_1 = _arg_4;
                                let matchResult_1, action_1;
                                if (choice_1 != null) {
                                    if (choice_1 === err.suggestedAction) {
                                        matchResult_1 = 0;
                                        action_1 = choice_1;
                                    }
                                    else if (choice_1 === "Show Output") {
                                        matchResult_1 = 1;
                                    }
                                    else {
                                        matchResult_1 = 2;
                                    }
                                }
                                else {
                                    matchResult_1 = 2;
                                }
                                switch (matchResult_1) {
                                    case 0: {
                                        getOutput().appendLine(toText(printf("[SageFs] Suggested action: %s"))(err.suggestedAction));
                                        return Promise.resolve();
                                    }
                                    case 1: {
                                        showOutputPanel();
                                        return Promise.resolve();
                                    }
                                    default: {
                                        return Promise.resolve();
                                    }
                                }
                            }))))))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                sb.backgroundColor = some(newThemeColor("statusBarItem.errorBackground"));
                                return Promise.resolve();
                            }))) : ((matchValue_6 === "Stopped") ? (((matchValue_12 = status.error, (matchValue_12 == null) ? ((sb.text = "$(error) SageFs: session error", Promise.resolve())) : ((err = matchValue_12, (sb.text = "$(error) SageFs: session error", (sb.tooltip = err.message, Window_showErrorMessage(err.message, [err.suggestedAction, "Show Output"]).then((_arg_4) => {
                                const choice_1 = _arg_4;
                                let matchResult_2, action_1;
                                if (choice_1 != null) {
                                    if (choice_1 === err.suggestedAction) {
                                        matchResult_2 = 0;
                                        action_1 = choice_1;
                                    }
                                    else if (choice_1 === "Show Output") {
                                        matchResult_2 = 1;
                                    }
                                    else {
                                        matchResult_2 = 2;
                                    }
                                }
                                else {
                                    matchResult_2 = 2;
                                }
                                switch (matchResult_2) {
                                    case 0: {
                                        getOutput().appendLine(toText(printf("[SageFs] Suggested action: %s"))(err.suggestedAction));
                                        return Promise.resolve();
                                    }
                                    case 1: {
                                        showOutputPanel();
                                        return Promise.resolve();
                                    }
                                    default: {
                                        return Promise.resolve();
                                    }
                                }
                            }))))))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                sb.backgroundColor = some(newThemeColor("statusBarItem.errorBackground"));
                                return Promise.resolve();
                            }))) : ((matchValue_6 === "error") ? (((matchValue_12 = status.error, (matchValue_12 == null) ? ((sb.text = "$(error) SageFs: session error", Promise.resolve())) : ((err = matchValue_12, (sb.text = "$(error) SageFs: session error", (sb.tooltip = err.message, Window_showErrorMessage(err.message, [err.suggestedAction, "Show Output"]).then((_arg_4) => {
                                const choice_1 = _arg_4;
                                let matchResult_3, action_1;
                                if (choice_1 != null) {
                                    if (choice_1 === err.suggestedAction) {
                                        matchResult_3 = 0;
                                        action_1 = choice_1;
                                    }
                                    else if (choice_1 === "Show Output") {
                                        matchResult_3 = 1;
                                    }
                                    else {
                                        matchResult_3 = 2;
                                    }
                                }
                                else {
                                    matchResult_3 = 2;
                                }
                                switch (matchResult_3) {
                                    case 0: {
                                        getOutput().appendLine(toText(printf("[SageFs] Suggested action: %s"))(err.suggestedAction));
                                        return Promise.resolve();
                                    }
                                    case 1: {
                                        showOutputPanel();
                                        return Promise.resolve();
                                    }
                                    default: {
                                        return Promise.resolve();
                                    }
                                }
                            }))))))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                sb.backgroundColor = some(newThemeColor("statusBarItem.errorBackground"));
                                return Promise.resolve();
                            }))) : ((matchValue_6 === "no session") ? ((sb.text = "$(circle-slash) SageFs: no session", (sb.backgroundColor = undefined, Promise.resolve()))) : ((sb.text = "$(loading~spin) SageFs: starting...", Promise.resolve())))))))))) : ((sb.text = "$(loading~spin) SageFs: starting...", Promise.resolve())))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                sb.show();
                                return Promise.resolve();
                            }));
                        });
                    }
                    else {
                        return ((wasRunning() && !crashPromptShown()) ? ((crashPromptShown(true), Window_showWarningMessage("SageFs daemon has stopped.", ["Restart", "Dismiss"]).then((_arg_1) => {
                            const choice = _arg_1;
                            let matchResult_4;
                            if (choice != null) {
                                if (choice === "Restart") {
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
                                case 0: {
                                    crashPromptShown(false);
                                    promiseIgnoreLog((msg) => {
                                        getOutput().appendLine(msg);
                                    }, Commands_executeCommand("sagefs.start"));
                                    return Promise.resolve();
                                }
                                default: {
                                    return Promise.resolve();
                                }
                            }
                        }))) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                            wasRunning(false);
                            sb.text = "$(circle-slash) SageFs: offline";
                            sb.backgroundColor = undefined;
                            sb.show();
                            activeSessionId(undefined);
                            setSession(c, undefined);
                            setSession_1(c, undefined);
                            setSession_2(c, undefined);
                            return Promise.resolve();
                        }));
                    }
                }))).catch((_arg_5) => {
                    c.log(toText(printf("[warn] refreshStatus: %O"))(_arg_5));
                    sb.text = "$(circle-slash) SageFs: offline";
                    sb.show();
                    return Promise.resolve();
                });
            default: {
                return Promise.resolve();
            }
        }
    })));
}

export function startDaemon() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        if (isStarting()) {
            return Promise.resolve();
        }
        else {
            isStarting(true);
            if (client() != null) {
                const c = client();
                return isRunning(c).then((_arg_1) => {
                    if (_arg_1) {
                        isStarting(false);
                        refreshStatus();
                        return Promise.resolve();
                    }
                    else {
                        return findProject().then((_arg_2) => {
                            let x_1, x_3;
                            const projPath = _arg_2;
                            if (projPath != null) {
                                const proj = projPath;
                                const out = getOutput();
                                out.show(true);
                                out.appendLine(toText(printf("Starting SageFs daemon with %s..."))(proj));
                                daemonStderr("");
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
                                proc.on('error', function(e) { ((msg_1) => {
                                    out.appendLine(toText(printf("[SageFs spawn error] %s"))(msg_1));
                                    daemonStderr((daemonStderr() + msg_1) + "\n");
                                    isStarting(false);
                                    const sb = getStatusBar();
                                    sb.text = "$(error) SageFs: spawn failed";
                                })(e.message || String(e)) });
                                proc.on('exit', function(code, signal) { ((code, _signal) => {
                                    out.appendLine(toText(printf("[SageFs] process exited (code %d)"))(code));
                                    isStarting(false);
                                })(code == null ? -1 : code, signal == null ? '' : signal) });
                                iterate((s) => {
                                    s.on('data', function(d) { if (d != null) ((chunk) => {
                                        out.appendLine(chunk);
                                        daemonStderr((daemonStderr() + chunk) + "\n");
                                    })(String(d)) });
                                }, toArray((x_1 = (proc.stderr), ((x_1 == null)) ? undefined : some(x_1))));
                                iterate((s_1) => {
                                    s_1.on('data', function(d) { if (d != null) ((chunk_1) => {
                                        out.appendLine(chunk_1);
                                    })(String(d)) });
                                }, toArray((x_3 = (proc.stdout), ((x_3 == null)) ? undefined : some(x_3))));
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
                                    sb_1.text = ((arg_3 = (attempts | 0), toText(printf("$(loading~spin) SageFs starting... (%ds)"))(arg_3)));
                                    promiseIgnoreLog((msg_2) => {
                                        out.appendLine(msg_2);
                                    }, PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (isRunning(c).then((_arg_4) => {
                                        if (_arg_4) {
                                            iterate((id) => {
                                                clearInterval(id);
                                            }, toArray(intervalId));
                                            isStarting(false);
                                            out.appendLine("SageFs daemon is ready.");
                                            iterate((f) => {
                                                f(c);
                                            }, toArray(onDaemonReady()));
                                            refreshStatus();
                                            return Promise.resolve();
                                        }
                                        else if (attempts > 120) {
                                            iterate((id_1) => {
                                                clearInterval(id_1);
                                            }, toArray(intervalId));
                                            isStarting(false);
                                            let stderrSnippet;
                                            const matchValue = daemonStderr().trim();
                                            if (matchValue === "") {
                                                stderrSnippet = "";
                                            }
                                            else {
                                                const s_2 = matchValue;
                                                const arg_4 = (s_2.length > 500) ? (substring(s_2, 0, 500) + "…") : s_2;
                                                stderrSnippet = toText(printf("\n\nDaemon output:\n%s"))(arg_4);
                                            }
                                            out.appendLine(toText(printf("Timed out waiting for SageFs daemon after 120s.%s"))(stderrSnippet));
                                            out.show(false);
                                            return Window_showErrorMessage(toText(printf("SageFs daemon failed to start after 120s.%s"))(stderrSnippet), ["Retry", "Show Full Output", "Check Installation"]).then((_arg_5) => {
                                                const choice_2 = _arg_5;
                                                return ((choice_2 != null) ? ((choice_2 === "Retry") ? ((void Commands_executeCommand("sagefs.restart"), Promise.resolve())) : ((choice_2 === "Show Full Output") ? ((showOutputPanel(), Promise.resolve())) : ((choice_2 === "Check Installation") ? ((checkInstallation(), Promise.resolve())) : (Promise.resolve())))) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                                    sb_1.text = "$(error) SageFs: offline";
                                                    return Promise.resolve();
                                                }));
                                            });
                                        }
                                        else {
                                            return Promise.resolve();
                                        }
                                    })))));
                                }), 1000);
                                intervalId = some(id_2);
                                return Promise.resolve();
                            }
                            else {
                                isStarting(false);
                                return Window_showErrorMessage("No .fsproj or .sln found. Open an F# project first.", ["Browse for Project", "Open Workspace"]).then((_arg_3) => {
                                    const choice_1 = _arg_3;
                                    let matchResult;
                                    if (choice_1 != null) {
                                        switch (choice_1) {
                                            case "Browse for Project": {
                                                matchResult = 0;
                                                break;
                                            }
                                            case "Open Workspace": {
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
                                        case 0: {
                                            promiseIgnoreLog((msg) => {
                                                getOutput().appendLine(msg);
                                            }, browseForProject());
                                            return Promise.resolve();
                                        }
                                        case 1: {
                                            openWorkspace();
                                            return Promise.resolve();
                                        }
                                        default: {
                                            return Promise.resolve();
                                        }
                                    }
                                });
                            }
                        });
                    }
                });
            }
            else {
                isStarting(false);
                return Window_showErrorMessage("SageFs not activated.", ["Retry", "Show Output"]).then((_arg) => {
                    const choice = _arg;
                    let matchResult_1;
                    if (choice != null) {
                        switch (choice) {
                            case "Retry": {
                                matchResult_1 = 0;
                                break;
                            }
                            case "Show Output": {
                                matchResult_1 = 1;
                                break;
                            }
                            default:
                                matchResult_1 = 2;
                        }
                    }
                    else {
                        matchResult_1 = 2;
                    }
                    switch (matchResult_1) {
                        case 0: {
                            Commands_executeCommand("sagefs.start");
                            return Promise.resolve();
                        }
                        case 1: {
                            showOutputPanel();
                            return Promise.resolve();
                        }
                        default: {
                            return Promise.resolve();
                        }
                    }
                });
            }
        }
    }));
}

export function ensureRunning() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        if (client() != null) {
            const c = client();
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
                            let attempts = 0;
                            return PromiseBuilder__While_2044D34(promise, () => (!ready && (attempts < 30)), PromiseBuilder__Delay_62FBFDE1(promise, () => ((new Promise(resolve => setTimeout(resolve, 1000))).then(() => (isRunning(c).then((_arg_4) => {
                                ready = _arg_4;
                                attempts = ((attempts + 1) | 0);
                                return Promise.resolve();
                            })))))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => ((!ready ? (Window_showErrorMessage("SageFs didn\'t start in time.", ["Retry", "Show Output", "Check Installation"]).then((_arg_5) => {
                                const choice_1 = _arg_5;
                                let matchResult_1;
                                if (choice_1 != null) {
                                    switch (choice_1) {
                                        case "Retry": {
                                            matchResult_1 = 0;
                                            break;
                                        }
                                        case "Show Output": {
                                            matchResult_1 = 1;
                                            break;
                                        }
                                        case "Check Installation": {
                                            matchResult_1 = 2;
                                            break;
                                        }
                                        default:
                                            matchResult_1 = 3;
                                    }
                                }
                                else {
                                    matchResult_1 = 3;
                                }
                                switch (matchResult_1) {
                                    case 0: {
                                        Commands_executeCommand("sagefs.restart");
                                        return Promise.resolve();
                                    }
                                    case 1: {
                                        showOutputPanel();
                                        return Promise.resolve();
                                    }
                                    case 2: {
                                        checkInstallation();
                                        return Promise.resolve();
                                    }
                                    default: {
                                        return Promise.resolve();
                                    }
                                }
                            })) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => (Promise.resolve(ready)))))));
                        });
                    default:
                        return Promise.resolve(false);
                }
            }))));
        }
        else {
            return Promise.resolve(false);
        }
    }));
}

/**
 * Wraps the ensureRunning + getClient boilerplate.
 */
export function withClient(action) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (ensureRunning().then((_arg) => {
        const matchValue = client();
        let matchResult;
        if (_arg) {
            if (matchValue != null) {
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
            case 0: {
                const c = matchValue;
                return action(c).then(() => (Promise.resolve(undefined)));
            }
            default: {
                return Promise.resolve();
            }
        }
    }))));
}

/**
 * Fire a client action that returns ApiOutcome, show brief status bar flash, then refresh.
 */
export function simpleCommand(defaultMsg, action) {
    return withClient((c) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (action(c).then((_arg) => {
        let sb;
        const msg = ApiOutcomeModule_messageOrDefault(defaultMsg, _arg);
        return ((statusBarItem() == null) ? ((void Window_showInformationMessage(toText(printf("SageFs: %s"))(msg), []), Promise.resolve())) : ((sb = statusBarItem(), (sb.text = toText(printf("$(check) %s"))(msg), (void (setTimeout((() => {
            const value = refreshStatus();
        }), 3000)), Promise.resolve()))))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
            refreshStatus();
            return Promise.resolve();
        }));
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

/**
 * Wait for session to reach Ready state (up to ~60s with 2s intervals).
 * Returns true if ready, false if timed out.
 */
export function waitForSessionReady() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        if (client() != null) {
            const c = client();
            let ready = false;
            let attempts = 0;
            return PromiseBuilder__While_2044D34(promise, () => (!ready && (attempts < 30)), PromiseBuilder__Delay_62FBFDE1(promise, () => (isReady(c).then((_arg) => {
                ready = _arg;
                return !ready ? ((new Promise(resolve => setTimeout(resolve, 2000))).then(() => {
                    attempts = ((attempts + 1) | 0);
                    return Promise.resolve();
                })) : (Promise.resolve());
            })))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => (Promise.resolve(ready))));
        }
        else {
            return Promise.resolve(false);
        }
    }));
}

export function evalCore(code, filePath, evalMode, blockStartLine) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        evalId(evalId() + 1);
        const myId = evalId() | 0;
        return PromiseBuilder__Delay_62FBFDE1(promise, () => {
            if (client() != null) {
                const c = client();
                return isReady(c).then((_arg) => {
                    if (!_arg) {
                        getOutput().appendLine("Session not ready, waiting for warmup...");
                        return waitForSessionReady().then((_arg_1) => {
                            if (!_arg_1) {
                                return ((evalId() === myId) ? ((evalId(0), Promise.resolve())) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => (Promise.resolve(new EvalResult(1, ["Session did not become ready in time. Check the dashboard for status."])))));
                            }
                            else {
                                getOutput().appendLine("Session ready, evaluating...");
                                const workDir = getWorkingDirectory();
                                const startTime = performance.now();
                                return evalCode(code, workDir, filePath, evalMode, blockStartLine, c).then((_arg_2) => {
                                    const result = _arg_2;
                                    return ((evalId() === myId) ? ((evalId(0), Promise.resolve())) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                        const elapsed = (performance.now()) - startTime;
                                        return (result.tag === 0) ? (Promise.resolve(new EvalResult(0, [defaultArg(result.fields[0], ""), elapsed]))) : (Promise.resolve(new EvalResult(1, [result.fields[0]])));
                                    }));
                                });
                            }
                        });
                    }
                    else {
                        const workDir_1 = getWorkingDirectory();
                        const startTime_1 = performance.now();
                        return evalCode(code, workDir_1, filePath, evalMode, blockStartLine, c).then((_arg_3) => {
                            const result_1 = _arg_3;
                            return ((evalId() === myId) ? ((evalId(0), Promise.resolve())) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                const elapsed_1 = (performance.now()) - startTime_1;
                                return (result_1.tag === 0) ? (Promise.resolve(new EvalResult(0, [defaultArg(result_1.fields[0], ""), elapsed_1]))) : (Promise.resolve(new EvalResult(1, [result_1.fields[0]])));
                            }));
                        });
                    }
                });
            }
            else {
                return ((evalId() === myId) ? ((evalId(0), Promise.resolve())) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => (Promise.resolve(new EvalResult(2, ["SageFs not activated"])))));
            }
        }).catch((_arg_4) => (((evalId() === myId) ? ((evalId(0), Promise.resolve())) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => (Promise.resolve(new EvalResult(2, [toString(_arg_4)])))))));
    }));
}

/**
 * Log eval result to output channel. Auto-shows output on error.
 */
export function logEvalResult(out, result) {
    let arg_1;
    switch (result.tag) {
        case 1: {
            out.appendLine(toText(printf("❌ Error:\n%s"))(result.fields[0]));
            out.show(true);
            break;
        }
        case 2: {
            out.appendLine(toText(printf("❌ Connection error: %s"))(result.fields[0]));
            out.show(true);
            break;
        }
        default:
            out.appendLine((arg_1 = formatDuration(result.fields[1]), toText(printf("%s  (%s)"))(result.fields[0])(arg_1)));
    }
    return result;
}

/**
 * Get code from selection or code block, append ;; if needed.
 * Returns (code, startLine, endLine) — server handles module context.
 */
export function getEvalCode(ed) {
    const doc = ed.document;
    let patternInput;
    if (!ed.selection.isEmpty) {
        const startLine = ~~ed.selection.start.line | 0;
        const endLine = ~~ed.selection.end.line | 0;
        patternInput = [doc.getText(newRange(startLine, ~~ed.selection.start.character, endLine, ~~ed.selection.end.character)), startLine, endLine];
    }
    else {
        patternInput = getCodeBlock(ed);
    }
    const raw = patternInput[0];
    if (raw.trim() === "") {
        return undefined;
    }
    else {
        return [trimEnd(raw).endsWith(";;") ? raw : (trimEnd(raw) + ";;"), patternInput[1], patternInput[2]];
    }
}

export function evalSelection() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const matchValue = Window_getActiveTextEditor();
        if (matchValue != null) {
            const ed = matchValue;
            return ensureRunning().then((_arg_1) => {
                const matchValue_1 = getEvalCode(ed);
                let matchResult;
                if (_arg_1) {
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
                        const code = matchValue_1[0];
                        const blockStart = matchValue_1[1] | 0;
                        const blockEnd = matchValue_1[2] | 0;
                        const filePath = ed.document.fileName;
                        const blockLine = blockStart + 1;
                        flashEvalRange(ed, blockStart, blockEnd);
                        const out = getOutput();
                        return Window_withProgress(10, "SageFs: evaluating...", (_progress, _token) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
                            out.appendLine("──── eval ────");
                            out.appendLine(code);
                            out.appendLine("");
                            return evalCore(code, filePath, "block", blockLine).then((_arg_2) => {
                                const matchValue_3 = logEvalResult(out, _arg_2);
                                switch (matchValue_3.tag) {
                                    case 0: {
                                        showInlineResult(ed, matchValue_3.fields[0], matchValue_3.fields[1], blockEnd);
                                        return Promise.resolve();
                                    }
                                    case 2: {
                                        out.show(true);
                                        return Window_showErrorMessage("Cannot reach SageFs daemon. Is it running?", ["Show Output", "Restart"]).then((_arg_3) => {
                                            const choice_1 = _arg_3;
                                            let matchResult_1;
                                            if (choice_1 != null) {
                                                if (choice_1 === "Restart") {
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
                                                    promiseIgnoreLog((msg) => {
                                                        getOutput().appendLine(msg);
                                                    }, Commands_executeCommand("sagefs.restart"));
                                                    return Promise.resolve();
                                                }
                                                default: {
                                                    return Promise.resolve();
                                                }
                                            }
                                        });
                                    }
                                    default: {
                                        out.show(true);
                                        showInlineDiagnostic(ed, matchValue_3.fields[0], blockEnd);
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
            return Window_showWarningMessage("No active editor.", ["Open File"]).then((_arg) => {
                const choice = _arg;
                let matchResult_2;
                if (choice != null) {
                    if (choice === "Open File") {
                        matchResult_2 = 0;
                    }
                    else {
                        matchResult_2 = 1;
                    }
                }
                else {
                    matchResult_2 = 1;
                }
                switch (matchResult_2) {
                    case 0: {
                        openQuickFile();
                        return Promise.resolve();
                    }
                    default: {
                        return Promise.resolve();
                    }
                }
            });
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
                        const filePath = ed.document.fileName;
                        const out = getOutput();
                        out.show(true);
                        out.appendLine((arg = ed.document.fileName, toText(printf("──── eval file: %s ────"))(arg)));
                        return evalCore(code, filePath, "file", undefined).then((_arg_1) => {
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
                const range = args;
                const raw = ed.document.getText(range);
                const endLine = ~~range.end.line | 0;
                const matchValue_1 = raw.trim();
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
                        const code = trimEnd(raw).endsWith(";;") ? raw : (trimEnd(raw) + ";;");
                        const startLine = ~~range.start.line | 0;
                        const filePath = ed.document.fileName;
                        const blockLine = startLine + 1;
                        const out = getOutput();
                        out.show(true);
                        out.appendLine("──── eval block ────");
                        out.appendLine(code);
                        out.appendLine("");
                        return evalCore(code, filePath, "block", blockLine).then((_arg_1) => {
                            const matchValue_3 = logEvalResult(out, _arg_1);
                            switch (matchValue_3.tag) {
                                case 0: {
                                    showInlineResult(ed, matchValue_3.fields[0], matchValue_3.fields[1], endLine);
                                    return Promise.resolve();
                                }
                                case 1: {
                                    showInlineDiagnostic(ed, matchValue_3.fields[0], endLine);
                                    return Promise.resolve();
                                }
                                default: {
                                    return Promise.resolve();
                                }
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

/**
 * Evaluate all code blocks in the file sequentially (top to bottom).
 */
export function evalAllBlocks() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const matchValue = Window_getActiveTextEditor();
        if (matchValue != null) {
            const ed = matchValue;
            return ensureRunning().then((_arg_1) => {
                let arg, blockStart, inBlock, blockStart_1;
                if (_arg_1) {
                    const doc = ed.document;
                    const lineCount = ~~doc.lineCount | 0;
                    const out = getOutput();
                    out.appendLine((arg = doc.fileName, toText(printf("──── eval all blocks: %s ────"))(arg)));
                    const useSemiSemi = hasSemiSemiDelimiters(doc);
                    const blocks = [];
                    return (useSemiSemi ? ((blockStart = 0, PromiseBuilder__For_1565554B(promise, rangeDouble(0, 1, lineCount - 1), (_arg_2) => {
                        const i = _arg_2 | 0;
                        if (trimEnd(doc.lineAt(i).text).endsWith(";;")) {
                            void (blocks.push([blockStart, i]));
                            blockStart = ((i + 1) | 0);
                            return Promise.resolve();
                        }
                        else {
                            return Promise.resolve();
                        }
                    }))) : ((inBlock = false, (blockStart_1 = 0, PromiseBuilder__For_1565554B(promise, rangeDouble(0, 1, lineCount - 1), (_arg_3) => {
                        const i_1 = _arg_3 | 0;
                        const empty = doc.lineAt(i_1).text.trim() === "";
                        const inBlock_1 = inBlock;
                        let matchResult;
                        if (empty) {
                            if (inBlock_1) {
                                matchResult = 1;
                            }
                            else {
                                matchResult = 2;
                            }
                        }
                        else if (inBlock_1) {
                            matchResult = 2;
                        }
                        else {
                            matchResult = 0;
                        }
                        switch (matchResult) {
                            case 0: {
                                blockStart_1 = (i_1 | 0);
                                inBlock = true;
                                return Promise.resolve();
                            }
                            case 1: {
                                void (blocks.push([blockStart_1, i_1 - 1]));
                                inBlock = false;
                                return Promise.resolve();
                            }
                            default: {
                                return Promise.resolve();
                            }
                        }
                    }).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                        if (inBlock) {
                            void (blocks.push([blockStart_1, lineCount - 1]));
                            return Promise.resolve();
                        }
                        else {
                            return Promise.resolve();
                        }
                    })))))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                        let errorCount = 0;
                        return PromiseBuilder__For_1565554B(promise, blocks, (_arg_4) => {
                            const blockStart_2 = _arg_4[0] | 0;
                            const blockEnd = _arg_4[1] | 0;
                            const range = newRange(blockStart_2, 0, blockEnd, doc.lineAt(blockEnd).text.length);
                            const raw = doc.getText(range);
                            if (raw.trim() === "") {
                                return Promise.resolve();
                            }
                            else {
                                const code = trimEnd(raw).endsWith(";;") ? raw : (trimEnd(raw) + ";;");
                                const filePath = doc.fileName;
                                const blockLine = blockStart_2 + 1;
                                flashEvalRange(ed, blockStart_2, blockEnd);
                                return evalCore(code, filePath, "block", blockLine).then((_arg_5) => {
                                    const matchValue_3 = logEvalResult(out, _arg_5);
                                    switch (matchValue_3.tag) {
                                        case 1: {
                                            errorCount = ((errorCount + 1) | 0);
                                            showInlineDiagnostic(ed, matchValue_3.fields[0], blockEnd);
                                            return Promise.resolve();
                                        }
                                        case 2: {
                                            errorCount = ((errorCount + 1) | 0);
                                            return Promise.resolve();
                                        }
                                        default: {
                                            showInlineResult(ed, matchValue_3.fields[0], matchValue_3.fields[1], blockEnd);
                                            return Promise.resolve();
                                        }
                                    }
                                });
                            }
                        }).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                            let summary;
                            if (errorCount === 0) {
                                const arg_1 = blocks.length | 0;
                                summary = toText(printf("✓ All %d blocks evaluated"))(arg_1);
                            }
                            else {
                                const n = errorCount | 0;
                                const arg_3 = blocks.length | 0;
                                summary = toText(printf("⚠ %d of %d blocks had errors"))(n)(arg_3);
                            }
                            out.appendLine(summary);
                            Window_showInformationMessage(summary, []);
                            return Promise.resolve();
                        }));
                    }));
                }
                else {
                    return Promise.resolve();
                }
            });
        }
        else {
            return Window_showWarningMessage("No active editor.", ["Open File"]).then((_arg) => {
                const choice = _arg;
                let matchResult_1;
                if (choice != null) {
                    if (choice === "Open File") {
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
                        openQuickFile();
                        return Promise.resolve();
                    }
                    default: {
                        return Promise.resolve();
                    }
                }
            });
        }
    }));
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
            return Window_withProgress(15, "SageFs: Creating session...", (_p, _t) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (createSession(proj, workDir, c).then((_arg_2) => {
                const result = _arg_2;
                return ((result.tag === 1) ? (Window_showErrorMessage(toText(printf("SageFs: %s"))(result.fields[0]), ["Show Output", "Retry"]).then((_arg_3) => {
                    const choice_1 = _arg_3;
                    let matchResult;
                    if (choice_1 != null) {
                        switch (choice_1) {
                            case "Show Output": {
                                matchResult = 0;
                                break;
                            }
                            case "Retry": {
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
                        case 0: {
                            showOutputPanel();
                            return Promise.resolve();
                        }
                        case 1: {
                            Commands_executeCommand("sagefs.createSession");
                            return Promise.resolve();
                        }
                        default: {
                            return Promise.resolve();
                        }
                    }
                })) : ((void Window_showInformationMessage(toText(printf("SageFs: Session created for %s"))(proj), []), Promise.resolve()))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                    refreshStatus();
                    return Promise.resolve();
                }));
            }))))).then(() => (Promise.resolve(undefined)));
        }
        else {
            return Window_showErrorMessage("No .fsproj or .sln found. Open an F# project first.", ["Browse for Project", "Open Workspace"]).then((_arg_1) => {
                const choice = _arg_1;
                let matchResult_1;
                if (choice != null) {
                    switch (choice) {
                        case "Browse for Project": {
                            matchResult_1 = 0;
                            break;
                        }
                        case "Open Workspace": {
                            matchResult_1 = 1;
                            break;
                        }
                        default:
                            matchResult_1 = 2;
                    }
                }
                else {
                    matchResult_1 = 2;
                }
                switch (matchResult_1) {
                    case 0: {
                        promiseIgnoreLog((msg) => {
                            getOutput().appendLine(msg);
                        }, browseForProject());
                        return Promise.resolve();
                    }
                    case 1: {
                        openWorkspace();
                        return Promise.resolve();
                    }
                    default: {
                        return Promise.resolve();
                    }
                }
            });
        }
    })))));
}

export function configureWarmupAutoOpenCmd() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const matchValue = getWorkingDirectory();
        if (matchValue != null) {
            const configDir = combineWindowsPath(matchValue, ".SageFs");
            const configPath = combineWindowsPath(configDir, "config.fsx");
            let result;
            const matchValue_1 = require('fs').existsSync(configPath);
            if (matchValue_1) {
                const content = require('fs').readFileSync(configPath, 'utf8');
                result = (((content.indexOf("AutoOpenNamespaces = false") >= 0) ? true : (content.indexOf("AutoOpenNamespaces=false") >= 0)) ? (new WarmupAutoOpenConfigResult(1, [configPath])) : (new WarmupAutoOpenConfigResult(2, [configPath])));
            }
            else {
                require('fs').mkdirSync(configDir, { recursive: true });
                require('fs').writeFileSync(configPath, autoOpenNamespacesOptOutTemplate, 'utf8');
                result = (new WarmupAutoOpenConfigResult(0, [configPath]));
            }
            return Workspace_openTextDocumentUri(uriFile(configPath)).then((_arg_1) => (Window_showTextDocument(_arg_1).then((_arg_2) => {
                switch (result.tag) {
                    case 1: {
                        Window_showInformationMessage(toText(printf("Warmup auto-open is already disabled in %s."))(result.fields[0]), []);
                        return Promise.resolve();
                    }
                    case 2: {
                        Window_showWarningMessage(toText(printf("Existing config opened at %s. Set AutoOpenNamespaces = false; it was not overwritten."))(result.fields[0]), []);
                        return Promise.resolve();
                    }
                    default: {
                        Window_showInformationMessage(toText(printf("Created %s with AutoOpenNamespaces = false."))(result.fields[0]), []);
                        return Promise.resolve();
                    }
                }
            })));
        }
        else {
            return Window_showErrorMessage("Open an F# project or workspace first.", ["Browse for Project", "Open Workspace"]).then((_arg) => {
                const choice = _arg;
                let matchResult;
                if (choice != null) {
                    switch (choice) {
                        case "Browse for Project": {
                            matchResult = 0;
                            break;
                        }
                        case "Open Workspace": {
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
                    case 0: {
                        promiseIgnoreLog((msg) => {
                            getOutput().appendLine(msg);
                        }, browseForProject());
                        return Promise.resolve();
                    }
                    case 1: {
                        openWorkspace();
                        return Promise.resolve();
                    }
                    default: {
                        return Promise.resolve();
                    }
                }
            });
        }
    }));
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
            return Window_showInformationMessage("No sessions available.", ["Create Session", "Start Daemon"]).then((_arg_1) => {
                const choice = _arg_1;
                let matchResult;
                if (choice != null) {
                    switch (choice) {
                        case "Create Session": {
                            matchResult = 0;
                            break;
                        }
                        case "Start Daemon": {
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
                    case 0: {
                        Commands_executeCommand("sagefs.createSession");
                        return Promise.resolve();
                    }
                    case 1: {
                        Commands_executeCommand("sagefs.start");
                        return Promise.resolve();
                    }
                    default: {
                        return Promise.resolve();
                    }
                }
            });
        }
        else {
            const items = map(formatSessionLabel, sessions);
            return Window_showQuickPick(items, prompt).then((_arg_2) => {
                const picked = _arg_2;
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
                        return action(sess, c).then((_arg_3) => {
                            const result = _arg_3;
                            return ((result.tag === 1) ? (Window_showErrorMessage(toText(printf("Failed: %s"))(result.fields[0]), ["Show Diagnostics", "Show Output"]).then((_arg_5) => {
                                const choice_1 = _arg_5;
                                let matchResult_1;
                                if (choice_1 != null) {
                                    switch (choice_1) {
                                        case "Show Diagnostics": {
                                            matchResult_1 = 0;
                                            break;
                                        }
                                        case "Show Output": {
                                            matchResult_1 = 1;
                                            break;
                                        }
                                        default:
                                            matchResult_1 = 2;
                                    }
                                }
                                else {
                                    matchResult_1 = 2;
                                }
                                switch (matchResult_1) {
                                    case 0: {
                                        showOutputPanel();
                                        return Promise.resolve();
                                    }
                                    case 1: {
                                        showOutputPanel();
                                        return Promise.resolve();
                                    }
                                    default: {
                                        return Promise.resolve();
                                    }
                                }
                            })) : ((onSuccess(sess), (void Window_showInformationMessage(ApiOutcomeModule_messageOrDefault(prompt, result), []), Promise.resolve())))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
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

/**
 * Context-aware session menu — the primary entry point from the status bar.
 */
export function sessionMenu() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        if (client() != null) {
            const c = client();
            return getStatus(c).then((_arg_1) => {
                const status = _arg_1;
                const items = [];
                return (status.connected ? (listSessions(c).then((_arg_2) => {
                    const sessions = _arg_2;
                    return ((sessions.length === 0) ? ((void (items.push("$(add) Create New Session")), Promise.resolve())) : (PromiseBuilder__For_1565554B(promise, sessions, (_arg_3) => {
                        const s = _arg_3;
                        const icon = ((activeSessionId() == null) ? false : (activeSessionId() === s.id)) ? "$(star-full)" : "$(terminal)";
                        let proj;
                        const matchValue_2 = s.projects;
                        proj = ((!equalsWith((x, y) => (x === y), matchValue_2, defaultOf()) && (matchValue_2.length === 0)) ? "no project" : join(", ", map((p) => {
                            const name = last(split(p, ["/", "\\"]));
                            if (name.endsWith(".fsproj")) {
                                const n_1 = name;
                                return n_1.slice(undefined, (n_1.length - 8) + 1);
                            }
                            else {
                                return name;
                            }
                        }, matchValue_2)));
                        let evals;
                        const matchValue_3 = s.evalCount | 0;
                        evals = ((matchValue_3 === 0) ? "" : toText(printf(" [%d]"))(matchValue_3));
                        const label = toText(printf("%s %s — %s%s"))(icon)(proj)(s.status)(evals);
                        void (items.push(label));
                        return Promise.resolve();
                    }).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                        void (items.push("──────────"));
                        void (items.push("$(add) Create New Session"));
                        return Promise.resolve();
                    })))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                        let matchValue_4;
                        return ((matchValue_4 = status.status, (matchValue_4 != null) ? ((matchValue_4 === "Ready") ? ((void (items.push("$(debug-restart) Reset Session")), (void (items.push("$(refresh) Hard Reset (Rebuild)")), Promise.resolve()))) : ((matchValue_4 === "Evaluating") ? ((void (items.push("$(debug-restart) Reset Session")), (void (items.push("$(refresh) Hard Reset (Rebuild)")), Promise.resolve()))) : (Promise.resolve()))) : (Promise.resolve()))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                            void (items.push("$(dashboard) Open Dashboard"));
                            void (items.push("$(gear) Cycle Density"));
                            return Promise.resolve();
                        }));
                    }));
                })) : ((void (items.push("$(play) Start SageFs")), Promise.resolve()))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => (Window_showQuickPick(items.slice(), "SageFs").then((_arg_4) => {
                    let s_3;
                    const picked = _arg_4;
                    if (picked != null) {
                        const choice_1 = picked;
                        if (choice_1.indexOf("Start SageFs") >= 0) {
                            return startDaemon().then(() => (Promise.resolve(undefined)));
                        }
                        else if (choice_1.indexOf("Create New Session") >= 0) {
                            return createSessionCmd().then(() => (Promise.resolve(undefined)));
                        }
                        else if ((s_3 = choice_1, (s_3.indexOf("Reset Session") >= 0) && !(s_3.indexOf("Hard") >= 0))) {
                            return resetSessionCmd().then(() => (Promise.resolve(undefined)));
                        }
                        else if (choice_1.indexOf("Hard Reset") >= 0) {
                            return hardResetCmd().then(() => (Promise.resolve(undefined)));
                        }
                        else if (choice_1.indexOf("Open Dashboard") >= 0) {
                            Commands_executeCommand("sagefs.openDashboard");
                            return Promise.resolve();
                        }
                        else if (choice_1.indexOf("Cycle Density") >= 0) {
                            cycleDensity();
                            return Promise.resolve();
                        }
                        else if (choice_1.indexOf("──") >= 0) {
                            return Promise.resolve();
                        }
                        else if (client() != null) {
                            const c2 = client();
                            return listSessions(c2).then((_arg_9) => {
                                const picked_1 = tryFind_1((sess) => {
                                    let matchValue_5, name_1, n_5;
                                    return choice_1.indexOf((matchValue_5 = sess.projects, (!equalsWith((x_1, y_1) => (x_1 === y_1), matchValue_5, defaultOf()) && (matchValue_5.length === 0)) ? "no project" : ((name_1 = last(split(item(0, matchValue_5), ["/", "\\"])), name_1.endsWith(".fsproj") ? ((n_5 = name_1, n_5.slice(undefined, (n_5.length - 8) + 1))) : name_1)))) >= 0;
                                }, _arg_9);
                                if (picked_1 == null) {
                                    return Promise.resolve();
                                }
                                else {
                                    const sess_1 = picked_1;
                                    return switchSession(sess_1.id, c2).then((_arg_10) => {
                                        activeSessionId(sess_1.id);
                                        Window_showInformationMessage(toText(printf("Switched to %s"))(sess_1.id), []);
                                        refreshStatus();
                                        return Promise.resolve();
                                    });
                                }
                            });
                        }
                        else {
                            return Promise.resolve();
                        }
                    }
                    else {
                        return Promise.resolve();
                    }
                }))));
            });
        }
        else {
            return Window_showWarningMessage("SageFs is not connected.", ["Start SageFs", "Show Output"]).then((_arg) => {
                const choice = _arg;
                let matchResult;
                if (choice != null) {
                    switch (choice) {
                        case "Start SageFs": {
                            matchResult = 0;
                            break;
                        }
                        case "Show Output": {
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
                    case 0: {
                        Commands_executeCommand("sagefs.start");
                        return Promise.resolve();
                    }
                    case 1: {
                        showOutputPanel();
                        return Promise.resolve();
                    }
                    default: {
                        return Promise.resolve();
                    }
                }
            });
        }
    }));
}

export function stopDaemon() {
    iterate((proc) => {
        proc.kill();
    }, toArray(daemonProcess()));
    daemonProcess(undefined);
    Window_showInformationMessage("SageFs: stop the daemon from its terminal or use `sagefs stop`.", []);
    refreshStatus();
}

export function switchProject() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (scanForProjects().then((_arg) => {
        const all = _arg;
        if (!equalsWith((x, y) => (x === y), all, defaultOf()) && (all.length === 0)) {
            Window_showWarningMessage("No .fsproj or .sln files found in workspace.", []);
            return Promise.resolve();
        }
        else {
            return Window_showQuickPick(all, "Switch SageFs to a different project").then((_arg_1) => {
                const picked = _arg_1;
                if (picked == null) {
                    return Promise.resolve();
                }
                else {
                    const p = picked;
                    persistProjectChoice(p);
                    const out = getOutput();
                    out.appendLine(toText(printf("Switching to project: %s"))(p));
                    stopDaemon();
                    return (new Promise(resolve => setTimeout(resolve, 1000))).then(() => (startDaemon().then(() => (Promise.resolve(undefined)))));
                }
            });
        }
    }))));
}

export function openDashboard() {
    if (client() != null) {
        const dashUrl = dashboardUrl(client());
        if (dashboardPanel() == null) {
            const panel_1 = Window_createWebviewPanel("sagefsDashboard", "SageFs Dashboard", 2, {
                enableScripts: true,
            });
            panel_1.webview.html = toText(printf("<!DOCTYPE html>\r\n<html style=\"height:100%%;margin:0;padding:0\">\r\n<head><meta http-equiv=\"Content-Security-Policy\" content=\"default-src \'none\'; frame-src http://localhost:*; style-src \'unsafe-inline\'\"></head>\r\n<body style=\"height:100%%;margin:0;padding:0\">\r\n<iframe src=\"%s\" style=\"width:100%%;height:100%%;border:none\"></iframe>\r\n</body>\r\n</html>"))(dashUrl);
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
}

export function evalAdvance() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const matchValue = Window_getActiveTextEditor();
        if (matchValue != null) {
            const ed = matchValue;
            return ensureRunning().then((_arg_1) => {
                const matchValue_1 = getEvalCode(ed);
                let matchResult;
                if (_arg_1) {
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
                        const code = matchValue_1[0];
                        const blockStart = matchValue_1[1] | 0;
                        const blockEnd = matchValue_1[2] | 0;
                        const filePath = ed.document.fileName;
                        const blockLine = blockStart + 1;
                        flashEvalRange(ed, blockStart, blockEnd);
                        const out = getOutput();
                        return evalCore(code, filePath, "block", blockLine).then((_arg_2) => {
                            const matchValue_3 = logEvalResult(out, _arg_2);
                            switch (matchValue_3.tag) {
                                case 0: {
                                    showInlineResult(ed, matchValue_3.fields[0], matchValue_3.fields[1], blockEnd);
                                    const lineCount = ~~ed.document.lineCount | 0;
                                    let nextLine = blockEnd + 1;
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
                                    showInlineDiagnostic(ed, matchValue_3.fields[0], blockEnd);
                                    return Promise.resolve();
                                }
                            }
                        });
                    }
                }
            });
        }
        else {
            return Window_showWarningMessage("No active editor.", ["Open File"]).then((_arg) => {
                const choice = _arg;
                let matchResult_1;
                if (choice != null) {
                    if (choice === "Open File") {
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
                        openQuickFile();
                        return Promise.resolve();
                    }
                    default: {
                        return Promise.resolve();
                    }
                }
            });
        }
    }));
}

export function cancelEvalCmd() {
    return simpleCommand("Eval cancelled", cancelEval);
}

/**
 * Navigate to the next code block.
 */
export function nextBlock() {
    const matchValue = Window_getActiveTextEditor();
    if (matchValue != null) {
        const ed = matchValue;
        const doc = ed.document;
        const patternInput = getBlockBounds(doc, ~~ed.selection.active.line);
        const lineCount = ~~doc.lineCount | 0;
        let next = patternInput[1] + 1;
        while ((next < lineCount) && (doc.lineAt(next).text.trim() === "")) {
            next = ((next + 1) | 0);
        }
        if (next < lineCount) {
            const pos = newPosition(next, 0);
            ed.selection = newSelection(pos, pos);
            ed.revealRange(newRange(next, 0, next, 0));
        }
    }
}

/**
 * Navigate to the previous code block.
 */
export function prevBlock() {
    const matchValue = Window_getActiveTextEditor();
    if (matchValue != null) {
        const ed = matchValue;
        const doc = ed.document;
        const blockStart = getBlockBounds(doc, ~~ed.selection.active.line)[0] | 0;
        const matchValue_1 = blockStart > 0;
        if (matchValue_1) {
            let prev = blockStart - 1;
            while ((prev > 0) && (doc.lineAt(prev).text.trim() === "")) {
                prev = ((prev - 1) | 0);
            }
            const prevStart = getBlockBounds(doc, prev)[0] | 0;
            const pos = newPosition(prevStart, 0);
            ed.selection = newSelection(pos, pos);
            ed.revealRange(newRange(prevStart, 0, prevStart, 0));
        }
    }
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
                        return Window_showErrorMessage(result.fields[0], ["Show Diagnostics", "Show Output"]).then((_arg_1) => {
                            const choice = _arg_1;
                            let matchResult_1;
                            if (choice != null) {
                                switch (choice) {
                                    case "Show Diagnostics": {
                                        matchResult_1 = 0;
                                        break;
                                    }
                                    case "Show Output": {
                                        matchResult_1 = 1;
                                        break;
                                    }
                                    default:
                                        matchResult_1 = 2;
                                }
                            }
                            else {
                                matchResult_1 = 2;
                            }
                            switch (matchResult_1) {
                                case 0: {
                                    showOutputPanel();
                                    return Promise.resolve();
                                }
                                case 1: {
                                    showOutputPanel();
                                    return Promise.resolve();
                                }
                                default: {
                                    return Promise.resolve();
                                }
                            }
                        });
                    }
                    else {
                        const name = last(split(ed_1.document.fileName, ["/", "\\"]));
                        Window_showInformationMessage(toText(printf("Script loaded: %s"))(name), []);
                        return Promise.resolve();
                    }
                });
            default:
                return Window_showWarningMessage("Open an .fsx file to load it as a script.", ["Open File"]).then((_arg_2) => {
                    const choice_1 = _arg_2;
                    let matchResult_2;
                    if (choice_1 != null) {
                        if (choice_1 === "Open File") {
                            matchResult_2 = 0;
                        }
                        else {
                            matchResult_2 = 1;
                        }
                    }
                    else {
                        matchResult_2 = 1;
                    }
                    switch (matchResult_2) {
                        case 0: {
                            openQuickFile();
                            return Promise.resolve();
                        }
                        default: {
                            return Promise.resolve();
                        }
                    }
                });
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

export function checkHealth() {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => ((new Promise(function(resolve, reject) { require('child_process').execFile("sagefs", ["--version"], function(err, stdout, stderr) { if (err) reject(err); else resolve(stdout) }) })).then((_arg) => {
        const trimmed = _arg.trim();
        Window_showInformationMessage(toText(printf("SageFs CLI found: %s"))(trimmed), []);
        return Promise.resolve();
    }))).catch((_arg_1) => {
        Window_showErrorMessage("SageFs CLI not found. Install it with: dotnet tool install --global SageFs", []);
        return Promise.resolve();
    }))));
}

export function hijackIonideSendToFsi(subs) {
    let arg_1;
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
        catch (ex) {
            console.debug('[SageFs]', ((arg_1 = toString(ex), toText(printf("Could not hijack %s: %s"))(cmd)(arg_1))));
        }
    }
}

export function activate(context) {
    const config = Workspace_getConfiguration("sagefs");
    const mcpPort = config.get("mcpPort", 37749) | 0;
    const dashboardPort = config.get("dashboardPort", 37750) | 0;
    const c = create(mcpPort, dashboardPort, (msg) => {
        getOutput().appendLine(msg);
    });
    client(c);
    currentDensity(densityFromString(config.get("density", "full")));
    const out = Window_createOutputChannel("SageFs");
    outputChannel(out);
    process.on('unhandledRejection', (reason) => { const msg = reason && reason.stack ? reason.stack : String(reason); ((msg_1) => {
        out.appendLine(msg_1);
    })('[SageFs] Unhandled rejection: ' + msg); });
    const sb = Window_createStatusBarItem(1, 50);
    sb.command = "sagefs.sessionMenu";
    sb.tooltip = "Click for SageFs session menu";
    statusBarItem(sb);
    void (context.subscriptions.push(sb));
    const tsb = Window_createStatusBarItem(1, 49);
    tsb.text = "$(beaker) No tests";
    tsb.tooltip = "SageFs live testing — click to enable";
    tsb.command = "sagefs.enableLiveTesting";
    testStatusBarItem(tsb);
    void (context.subscriptions.push(tsb));
    const esb = Window_createStatusBarItem(1, 48);
    esb.tooltip = "SageFs eval performance";
    evalPerfStatusBar(esb);
    void (context.subscriptions.push(esb));
    const dc = Languages_createDiagnosticCollection("sagefs");
    diagnosticCollection(dc);
    void (context.subscriptions.push(dc));
    const docChangeSub = Workspace_onDidChangeTextDocument((_evt) => {
        iterate((id) => {
            clearTimeout(id);
        }, toArray(staleDebounceTimer()));
        staleDebounceTimer(some(setTimeout((() => {
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
                    if (!isEmpty_1(blockDecorations())) {
                        markDecorationsStale(ed_1);
                    }
                    break;
                }
                case 1: {
                    break;
                }
            }
        }), 300)));
    });
    void (context.subscriptions.push(docChangeSub));
    register(context);
    setSession(c, undefined);
    register_1(context);
    setSession_1(c, undefined);
    register_2(context);
    setSession_2(c, undefined);
    typeExplorer(create_1(context, client(), activeSessionId));
    const reg = (cmd, handler) => {
        void (context.subscriptions.push(Commands_registerCommand(cmd, handler)));
    };
    const logToOutput = (msg_2) => {
        getOutput().appendLine(msg_2);
    };
    reg("sagefs.eval", (_arg) => {
        promiseIgnoreLog(logToOutput, evalSelection());
    });
    reg("sagefs.evalFile", (_arg_1) => {
        promiseIgnoreLog(logToOutput, evalFile());
    });
    reg("sagefs.evalRange", (args) => {
        promiseIgnoreLog(logToOutput, evalRange(args));
    });
    reg("sagefs.evalAdvance", (_arg_2) => {
        promiseIgnoreLog(logToOutput, evalAdvance());
    });
    reg("sagefs.evalAllBlocks", (_arg_3) => {
        promiseIgnoreLog(logToOutput, evalAllBlocks());
    });
    reg("sagefs.cancelEval", (_arg_4) => {
        promiseIgnoreLog(logToOutput, cancelEvalCmd());
    });
    reg("sagefs.nextBlock", (_arg_5) => {
        nextBlock();
    });
    reg("sagefs.prevBlock", (_arg_6) => {
        prevBlock();
    });
    reg("sagefs.loadScript", (_arg_7) => {
        promiseIgnoreLog(logToOutput, loadScriptCmd());
    });
    reg("sagefs.start", (_arg_8) => {
        promiseIgnoreLog(logToOutput, startDaemon());
    });
    reg("sagefs.stop", (_arg_9) => {
        stopDaemon();
    });
    reg("sagefs.restart", (_arg_10) => {
        promiseIgnoreLog(logToOutput, PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
            const out_1 = getOutput();
            out_1.appendLine("Restarting SageFs daemon...");
            stopDaemon();
            return (new Promise(resolve => setTimeout(resolve, 1000))).then(() => (startDaemon().then(() => (Promise.resolve(undefined)))));
        })));
    });
    reg("sagefs.openDashboard", (_arg_13) => {
        openDashboard();
    });
    reg("sagefs.switchProject", (_arg_14) => {
        promiseIgnoreLog(logToOutput, switchProject());
    });
    reg("sagefs.checkHealth", (_arg_15) => {
        promiseIgnoreLog(logToOutput, checkHealth());
    });
    reg("sagefs.openGettingStarted", (_arg_16) => {
        promiseIgnoreLog(logToOutput, openGettingStarted());
    });
    reg("sagefs.sessionMenu", (_arg_17) => {
        promiseIgnoreLog(logToOutput, sessionMenu());
    });
    reg("sagefs.resetSession", (_arg_18) => {
        promiseIgnoreLog(logToOutput, resetSessionCmd());
    });
    reg("sagefs.hardReset", (_arg_19) => {
        promiseIgnoreLog(logToOutput, hardResetCmd());
    });
    reg("sagefs.createSession", (_arg_20) => {
        promiseIgnoreLog(logToOutput, createSessionCmd());
    });
    reg("sagefs.configureWarmupAutoOpen", (_arg_21) => {
        promiseIgnoreLog(logToOutput, configureWarmupAutoOpenCmd());
    });
    reg("sagefs.switchSession", (_arg_22) => {
        promiseIgnoreLog(logToOutput, switchSessionCmd());
    });
    reg("sagefs.stopSession", (_arg_23) => {
        promiseIgnoreLog(logToOutput, stopSessionCmd());
    });
    reg("sagefs.switchToSession", (args_1) => {
        promiseIgnoreLog(logToOutput, PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
            if (client() != null) {
                const c_1 = client();
                let sid;
                try {
                    sid = args_1.sessionId;
                }
                catch (matchValue_1) {
                    sid = "";
                }
                if (sid === "") {
                    return Promise.resolve();
                }
                else {
                    const id_1 = sid;
                    return switchSession(id_1, c_1).then((_arg_24) => {
                        activeSessionId(id_1);
                        refreshStatus();
                        return Promise.resolve();
                    });
                }
            }
            else {
                return Promise.resolve();
            }
        })));
    });
    reg("sagefs.stopSessionInline", (args_2) => {
        promiseIgnoreLog(logToOutput, PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
            if (client() != null) {
                const c_2 = client();
                let sid_1;
                try {
                    sid_1 = args_2.sessionId;
                }
                catch (matchValue_2) {
                    sid_1 = "";
                }
                if (sid_1 === "") {
                    return Promise.resolve();
                }
                else {
                    const id_2 = sid_1;
                    return stopSession(id_2, c_2).then((_arg_25) => {
                        let aid_1;
                        return ((activeSessionId() != null) ? ((activeSessionId() === id_2) ? ((aid_1 = activeSessionId(), (activeSessionId(undefined), Promise.resolve()))) : (Promise.resolve())) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                            refreshStatus();
                            return Promise.resolve();
                        }));
                    });
                }
            }
            else {
                return Promise.resolve();
            }
        })));
    });
    reg("sagefs.resetSessionInline", (_arg_26) => {
        promiseIgnoreLog(logToOutput, resetSessionCmd());
    });
    reg("sagefs.clearResults", (_arg_27) => {
        clearAllDecorations();
    });
    reg("sagefs.cycleDensity", (_arg_28) => {
        cycleDensity();
    });
    reg("sagefs.enableLiveTesting", (_arg_29) => {
        promiseIgnoreLog(logToOutput, simpleCommand("Live testing enabled", enableLiveTesting));
    });
    reg("sagefs.disableLiveTesting", (_arg_30) => {
        promiseIgnoreLog(logToOutput, simpleCommand("Live testing disabled", disableLiveTesting));
    });
    reg("sagefs.runTests", (_arg_31) => {
        promiseIgnoreLog(logToOutput, simpleCommand("Tests queued", (c_5) => runTests("", c_5)));
    });
    reg("sagefs.setRunPolicy", (_arg_32) => {
        promiseIgnoreLog(logToOutput, withClient((c_6) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (Window_showQuickPick(["unit", "integration", "browser", "benchmark", "architecture", "property"], "Select test category").then((_arg_33) => {
            const catOpt = _arg_33;
            if (catOpt == null) {
                return Promise.resolve();
            }
            else {
                const cat = catOpt;
                return Window_showQuickPick(["every", "save", "demand", "disabled"], toText(printf("Set policy for %s tests"))(cat)).then((_arg_34) => {
                    const polOpt = _arg_34;
                    if (polOpt == null) {
                        return Promise.resolve();
                    }
                    else {
                        const pol = polOpt;
                        return setRunPolicy(cat, pol, c_6).then((_arg_35) => {
                            iterate((msg_3) => {
                                Window_showInformationMessage(msg_3, []);
                            }, toArray(ApiOutcomeModule_message(_arg_35)));
                            return Promise.resolve();
                        });
                    }
                });
            }
        }))))));
    });
    reg("sagefs.showHistory", (_arg_37) => {
        promiseIgnoreLog(logToOutput, withClient((c_7) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (getRecentEvents(30, c_7).then((_arg_38) => {
            const bodyOpt = _arg_38;
            if (bodyOpt == null) {
                return Window_showWarningMessage("Could not fetch events", ["Start SageFs", "Show Output"]).then((_arg_39) => {
                    const choice = _arg_39;
                    let matchResult_1;
                    if (choice != null) {
                        switch (choice) {
                            case "Start SageFs": {
                                matchResult_1 = 0;
                                break;
                            }
                            case "Show Output": {
                                matchResult_1 = 1;
                                break;
                            }
                            default:
                                matchResult_1 = 2;
                        }
                    }
                    else {
                        matchResult_1 = 2;
                    }
                    switch (matchResult_1) {
                        case 0: {
                            Commands_executeCommand("sagefs.start");
                            return Promise.resolve();
                        }
                        case 1: {
                            showOutputPanel();
                            return Promise.resolve();
                        }
                        default: {
                            return Promise.resolve();
                        }
                    }
                });
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
                    promiseIgnoreLog(logToOutput, Window_showQuickPick(lines, "Recent SageFs events"));
                    return Promise.resolve();
                }
            }
        }))))));
    });
    reg("sagefs.showCallGraph", (_arg_40) => {
        promiseIgnoreLog(logToOutput, withClient((c_8) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (getDependencyGraph("", c_8).then((_arg_41) => {
            const overviewOpt = _arg_41;
            if (overviewOpt != null) {
                const body_1 = overviewOpt;
                const total = defaultArg(fieldInt("TotalSymbols", JSON.parse(body_1)), 0) | 0;
                if (total === 0) {
                    Window_showInformationMessage("No dependency graph available yet", []);
                    return Promise.resolve();
                }
                else {
                    return Window_showInputBox(toText(printf("Enter symbol name (%d symbols tracked)"))(total)).then((_arg_43) => {
                        let sym;
                        const inputOpt = _arg_43;
                        let matchResult_2, sym_1;
                        if (inputOpt != null) {
                            if ((sym = inputOpt, sym.trim().length > 0)) {
                                matchResult_2 = 0;
                                sym_1 = inputOpt;
                            }
                            else {
                                matchResult_2 = 1;
                            }
                        }
                        else {
                            matchResult_2 = 1;
                        }
                        switch (matchResult_2) {
                            case 0:
                                return getDependencyGraph(sym_1.trim(), c_8).then((_arg_44) => {
                                    const detailOpt = _arg_44;
                                    if (detailOpt != null) {
                                        const detail = detailOpt;
                                        const tests = defaultArg(fieldArray("Tests", JSON.parse(detail)), []);
                                        if (!equalsWith(equals, tests, defaultOf()) && (tests.length === 0)) {
                                            Window_showInformationMessage(toText(printf("No tests cover \'%s\'"))(sym_1), []);
                                            return Promise.resolve();
                                        }
                                        else {
                                            promiseIgnoreLog(logToOutput, Window_showQuickPick(map((t) => {
                                                const name = defaultArg(fieldString("TestName", t), "?");
                                                const status = defaultArg(fieldString("Status", t), "unknown");
                                                const icon = (status === "passed") ? "✓" : ((status === "failed") ? "✗" : "●");
                                                return toText(printf("%s %s [%s]"))(icon)(name)(status);
                                            }, tests), toText(printf("Tests covering \'%s\'"))(sym_1)));
                                            return Promise.resolve();
                                        }
                                    }
                                    else {
                                        return Window_showWarningMessage("Could not fetch graph", ["Start SageFs", "Show Output"]).then((_arg_45) => {
                                            const choice_2 = _arg_45;
                                            let matchResult_3;
                                            if (choice_2 != null) {
                                                switch (choice_2) {
                                                    case "Start SageFs": {
                                                        matchResult_3 = 0;
                                                        break;
                                                    }
                                                    case "Show Output": {
                                                        matchResult_3 = 1;
                                                        break;
                                                    }
                                                    default:
                                                        matchResult_3 = 2;
                                                }
                                            }
                                            else {
                                                matchResult_3 = 2;
                                            }
                                            switch (matchResult_3) {
                                                case 0: {
                                                    Commands_executeCommand("sagefs.start");
                                                    return Promise.resolve();
                                                }
                                                case 1: {
                                                    showOutputPanel();
                                                    return Promise.resolve();
                                                }
                                                default: {
                                                    return Promise.resolve();
                                                }
                                            }
                                        });
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
                return Window_showWarningMessage("Could not fetch dependency graph", ["Start SageFs", "Show Output"]).then((_arg_42) => {
                    const choice_1 = _arg_42;
                    let matchResult_4;
                    if (choice_1 != null) {
                        switch (choice_1) {
                            case "Start SageFs": {
                                matchResult_4 = 0;
                                break;
                            }
                            case "Show Output": {
                                matchResult_4 = 1;
                                break;
                            }
                            default:
                                matchResult_4 = 2;
                        }
                    }
                    else {
                        matchResult_4 = 2;
                    }
                    switch (matchResult_4) {
                        case 0: {
                            Commands_executeCommand("sagefs.start");
                            return Promise.resolve();
                        }
                        case 1: {
                            showOutputPanel();
                            return Promise.resolve();
                        }
                        default: {
                            return Promise.resolve();
                        }
                    }
                });
            }
        }))))));
    });
    reg("sagefs.showBindings", (_arg_46) => {
        let testExpr;
        const matchValue_3 = map_1((l_1) => l_1.Bindings(), liveTestListener());
        let matchResult_5, bindings;
        if (matchValue_3 == null) {
            matchResult_5 = 0;
        }
        else if ((testExpr = matchValue_3, !equalsWith(equals, testExpr, defaultOf()) && (testExpr.length === 0))) {
            matchResult_5 = 0;
        }
        else {
            matchResult_5 = 1;
            bindings = matchValue_3;
        }
        switch (matchResult_5) {
            case 0: {
                Window_showInformationMessage("No FSI bindings yet", []);
                break;
            }
            case 1: {
                promiseIgnoreLog(logToOutput, Window_showQuickPick(choose((b) => {
                    const matchValue_4 = fieldString("Name", b);
                    const matchValue_5 = fieldString("TypeSig", b);
                    let matchResult_6, name_1, typeSig;
                    if (matchValue_4 != null) {
                        if (matchValue_5 != null) {
                            matchResult_6 = 0;
                            name_1 = matchValue_4;
                            typeSig = matchValue_5;
                        }
                        else {
                            matchResult_6 = 1;
                        }
                    }
                    else {
                        matchResult_6 = 1;
                    }
                    switch (matchResult_6) {
                        case 0: {
                            const shadow = defaultArg(fieldInt("ShadowCount", b), 0) | 0;
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
    reg("sagefs.showTestTrace", (_arg_47) => {
        let arg_11, arg_12, arg_13, arg_14, arg_15;
        const matchValue_7 = bind((l_2) => l_2.TestTrace(), liveTestListener());
        if (matchValue_7 == null) {
            Window_showInformationMessage("No test trace data yet", []);
        }
        else {
            const trace = value_40(matchValue_7);
            promiseIgnoreLog(logToOutput, Window_showQuickPick([(arg_11 = defaultArg(fieldBool("Enabled", trace), false), toText(printf("Enabled: %b"))(arg_11)), (arg_12 = defaultArg(fieldBool("IsRunning", trace), false), toText(printf("Running: %b"))(arg_12)), (arg_13 = (defaultArg(bind((obj) => fieldInt("Total", obj), fieldObj("Summary")(trace)), 0) | 0), (arg_14 = (defaultArg(bind((obj_1) => fieldInt("Passed", obj_1), fieldObj("Summary")(trace)), 0) | 0), (arg_15 = (defaultArg(bind((obj_2) => fieldInt("Failed", obj_2), fieldObj("Summary")(trace)), 0) | 0), toText(printf("Total: %d | Passed: %d | Failed: %d"))(arg_13)(arg_14)(arg_15))))], "test trace"));
        }
    });
    reg("sagefs.exportSession", (_arg_48) => {
        promiseIgnoreLog(logToOutput, withClient((c_9) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
            if (activeSessionId() != null) {
                const sid_2 = activeSessionId();
                return exportSessionAsFsx(sid_2, c_9).then((_arg_50) => {
                    const result_1 = _arg_50;
                    if (result_1 != null) {
                        const r = result_1;
                        if (r.evalCount === 0) {
                            Window_showInformationMessage("No evaluations to export", []);
                            return Promise.resolve();
                        }
                        else {
                            return Workspace_openTextDocument(r.content, "fsharp").then((_arg_52) => (Window_showTextDocument(_arg_52).then((_arg_53) => {
                                return Promise.resolve();
                            })));
                        }
                    }
                    else {
                        return Window_showErrorMessage("Failed to export session", ["Show Output"]).then((_arg_51) => {
                            const choice_4 = _arg_51;
                            let matchResult_7;
                            if (choice_4 != null) {
                                if (choice_4 === "Show Output") {
                                    matchResult_7 = 0;
                                }
                                else {
                                    matchResult_7 = 1;
                                }
                            }
                            else {
                                matchResult_7 = 1;
                            }
                            switch (matchResult_7) {
                                case 0: {
                                    showOutputPanel();
                                    return Promise.resolve();
                                }
                                default: {
                                    return Promise.resolve();
                                }
                            }
                        });
                    }
                });
            }
            else {
                return Window_showInformationMessage("No active session", ["Create Session", "Start Daemon"]).then((_arg_49) => {
                    const choice_3 = _arg_49;
                    let matchResult_8;
                    if (choice_3 != null) {
                        switch (choice_3) {
                            case "Create Session": {
                                matchResult_8 = 0;
                                break;
                            }
                            case "Start Daemon": {
                                matchResult_8 = 1;
                                break;
                            }
                            default:
                                matchResult_8 = 2;
                        }
                    }
                    else {
                        matchResult_8 = 2;
                    }
                    switch (matchResult_8) {
                        case 0: {
                            Commands_executeCommand("sagefs.createSession");
                            return Promise.resolve();
                        }
                        case 1: {
                            Commands_executeCommand("sagefs.start");
                            return Promise.resolve();
                        }
                        default: {
                            return Promise.resolve();
                        }
                    }
                });
            }
        }))));
    });
    reg("sagefs.explainTestFailure", (args_3) => {
        promiseIgnoreLog(logToOutput, PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
            let requestedId;
            try {
                if (equals(args_3, defaultOf())) {
                    requestedId = undefined;
                }
                else {
                    const arr = args_3;
                    requestedId = ((arr.length === 0) ? undefined : tryCastString(item(0, arr)));
                }
            }
            catch (matchValue_10) {
                requestedId = undefined;
            }
            let failedWithNarrative;
            const array_3 = toArray_1(narrativeState());
            failedWithNarrative = array_3.filter((tupledArg) => (tupledArg[1].Summary !== ""));
            if (!equalsWith(equalArrays, failedWithNarrative, defaultOf()) && (failedWithNarrative.length === 0)) {
                Window_showInformationMessage("No failure narratives available yet. Run tests with live testing enabled.", []);
                return Promise.resolve();
            }
            else {
                const items_3 = map((tupledArg_1) => {
                    let rid_1;
                    const n_3 = tupledArg_1[1];
                    return [(requestedId != null) ? ((requestedId === tupledArg_1[0]) ? ((rid_1 = requestedId, toText(printf("★ %s"))(n_3.Summary))) : n_3.Summary) : n_3.Summary, n_3];
                }, failedWithNarrative);
                const labels = map((tuple) => tuple[0], items_3);
                return Window_showQuickPick(labels, "Select failed test to explain").then((_arg_55) => {
                    let matchValue_12;
                    const labelOpt = _arg_55;
                    if (labelOpt != null) {
                        const label_1 = labelOpt;
                        const matchValue_11 = tryFind_1((tupledArg_2) => (tupledArg_2[0] === label_1), items_3);
                        if (matchValue_11 != null) {
                            const n_4 = matchValue_11[1];
                            const out_2 = getOutput();
                            out_2.show(true);
                            out_2.appendLine("");
                            out_2.appendLine(toText(printf("═══ Why failed: %s ═══"))(n_4.TestId));
                            out_2.appendLine(toText(printf("  Summary  : %s"))(n_4.Summary));
                            out_2.appendLine(toText(printf("  Since    : %s"))(n_4.TimeSinceLastPass));
                            return ((matchValue_12 = n_4.CausalChanges, (!equalsWith(equals, matchValue_12, defaultOf()) && (matchValue_12.length === 0)) ? ((out_2.appendLine("  Causes   : (no changes detected)"), Promise.resolve())) : ((out_2.appendLine("  Causes   :"), (matchValue_12.forEach((c_10) => {
                                out_2.appendLine(toText(printf("    • [%s] %s"))(c_10.Kind)(c_10.Name));
                            }), Promise.resolve()))))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                out_2.appendLine("");
                                return Promise.resolve();
                            }));
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
        })));
    });
    reg("sagefs.suggestRepair", (args_4) => {
        promiseIgnoreLog(logToOutput, PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
            let requestedId_1;
            try {
                if (equals(args_4, defaultOf())) {
                    requestedId_1 = undefined;
                }
                else {
                    const arr_1 = args_4;
                    requestedId_1 = ((arr_1.length === 0) ? undefined : tryCastString(item(0, arr_1)));
                }
            }
            catch (matchValue_14) {
                requestedId_1 = undefined;
            }
            let diagEntries;
            const array_8 = toArray_1(diagnosisState());
            diagEntries = array_8.filter((tupledArg_3) => (tupledArg_3[1].length > 0));
            if (!equalsWith(equalArrays, diagEntries, defaultOf()) && (diagEntries.length === 0)) {
                Window_showInformationMessage("No repair suggestions yet. Run tests with live testing enabled to generate diagnosis.", []);
                return Promise.resolve();
            }
            else {
                const items_4 = map((tupledArg_4) => {
                    let rid_2, rid_3;
                    const testName = tupledArg_4[0];
                    const symbols_1 = tupledArg_4[1];
                    const sym_2 = join(", ", symbols_1);
                    const label_2 = (requestedId_1 != null) ? (((rid_2 = requestedId_1, testName.indexOf(rid_2) >= 0)) ? ((rid_3 = requestedId_1, toText(printf("★ %s"))(testName))) : testName) : testName;
                    return [toText(printf("%s  ←  %s changed"))(label_2)(sym_2), testName, symbols_1];
                }, diagEntries);
                const labels_1 = map((tupledArg_5) => tupledArg_5[0], items_4);
                return Window_showQuickPick(labels_1, "Select test to repair").then((_arg_60) => {
                    let arg_26;
                    const labelOpt_1 = _arg_60;
                    if (labelOpt_1 != null) {
                        const label_3 = labelOpt_1;
                        const matchValue_15 = tryFind_1((tupledArg_6) => (tupledArg_6[0] === label_3), items_4);
                        if (matchValue_15 != null) {
                            const testName_1 = matchValue_15[1];
                            const symbols_2 = matchValue_15[2];
                            const out_3 = getOutput();
                            out_3.show(true);
                            out_3.appendLine("");
                            out_3.appendLine(toText(printf("═══ Repair Suggestion: %s ═══"))(testName_1));
                            out_3.appendLine((arg_26 = join(", ", symbols_2), toText(printf("  Caused by: %s"))(arg_26)));
                            out_3.appendLine("");
                            out_3.appendLine("  Suggested actions:");
                            out_3.appendLine("    1. Check the changed symbols above for unintended mutations");
                            out_3.appendLine("    2. Use sagefs-explain_test_failure in MCP for full narrative");
                            out_3.appendLine("    3. Use sagefs-preview_what_if to explore hypothetical fixes");
                            out_3.appendLine("    4. Eval the corrected code and watch live tests go green");
                            out_3.appendLine("");
                            Window_showInformationMessage(toText(printf("Repair guidance for \'%s\' written to output."))(testName_1), []);
                            return Promise.resolve();
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
        })));
    });
    let failingTestIndex = 0;
    const navigateToFailingTest = (delta) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        let tests_1;
        if (liveTestListener() != null) {
            const st = liveTestListener().State();
            tests_1 = sortBy((tupledArg_8) => [tupledArg_8[1], tupledArg_8[2]], choose((tupledArg_7) => {
                const matchValue_16 = tupledArg_7[1].Outcome;
                switch (matchValue_16.tag) {
                    case 1:
                    case 4:
                        return bind((info) => {
                            const matchValue_17 = info.FilePath;
                            const matchValue_18 = info.Line;
                            let matchResult_9, fp, ln;
                            if (matchValue_17 != null) {
                                if (matchValue_18 != null) {
                                    matchResult_9 = 0;
                                    fp = matchValue_17;
                                    ln = matchValue_18;
                                }
                                else {
                                    matchResult_9 = 1;
                                }
                            }
                            else {
                                matchResult_9 = 1;
                            }
                            switch (matchResult_9) {
                                case 0:
                                    return [info, fp, ln];
                                default:
                                    return undefined;
                            }
                        }, tryFind(tupledArg_7[0], st.Tests));
                    default:
                        return undefined;
                }
            }, toArray_1(st.Results)), {
                Compare: compareArrays,
            });
        }
        else {
            tests_1 = [];
        }
        const matchValue_20 = tests_1.length | 0;
        if (matchValue_20 === 0) {
            Window_showInformationMessage("No failing tests with source locations", []);
            return Promise.resolve();
        }
        else {
            const count = matchValue_20 | 0;
            failingTestIndex = (((failingTestIndex + delta) % count) | 0);
            return ((failingTestIndex < 0) ? ((failingTestIndex = ((failingTestIndex + count) | 0), Promise.resolve())) : (Promise.resolve())).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                const patternInput = item(failingTestIndex, tests_1);
                const line = patternInput[2] | 0;
                const uri = uriFile(patternInput[1]);
                return Workspace_openTextDocumentUri(uri).then((_arg_63) => (Window_showTextDocument(_arg_63).then((_arg_64) => {
                    let arg_28;
                    const ed_2 = _arg_64;
                    const pos = newPosition(line - 1, 0);
                    const sel = newSelection(pos, pos);
                    ed_2.selection = sel;
                    ed_2.revealRange(newRange(line - 1, 0, line - 1, 0));
                    Window_showInformationMessage((arg_28 = ((failingTestIndex + 1) | 0), toText(printf("Failing test %d/%d: %s"))(arg_28)(count)(patternInput[0].DisplayName)), []);
                    return Promise.resolve();
                })));
            }));
        }
    }));
    reg("sagefs.nextFailingTest", (_arg_65) => {
        promiseIgnoreLog(logToOutput, navigateToFailingTest(1));
    });
    reg("sagefs.prevFailingTest", (_arg_66) => {
        promiseIgnoreLog(logToOutput, navigateToFailingTest(-1));
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
    const connectToRunningDaemon = (c_11) => {
        c_11.log("connectToRunningDaemon: disposing existing connections...");
        iterate((d) => {
            d.dispose();
        }, toArray(sseDisposable()));
        sseDisposable(undefined);
        iterate((l_7) => {
            l_7.Dispose();
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
        fileAnnotationsCache = empty_1({
            Compare: comparePrimitives,
        });
        c_11.log("connectToRunningDaemon: establishing fresh SSE connections...");
        diagnosticsDisposable(start(c_11.mcpPort, dc, (msg_4) => {
            getOutput().appendLine(toText(printf("[Diagnostics SSE] %s"))(msg_4));
        }));
        const adapter = create_5(client, () => defaultArg(map_1((l_8) => l_8.State().FailureNarratives, liveTestListener()), empty_1({
            Compare: comparePrimitives,
        })));
        testAdapter(adapter);
        initialize();
        initFileAnnotationDecoTypes();
        const refreshAllDecorations = () => {
            const state = defaultArg(map_1((l_9) => l_9.State(), liveTestListener()), VscLiveTestStateModule_empty);
            applyToAllEditors(state);
            applyCoverageToAllEditors(state);
            applyFileAnnotationsToAllEditors();
            return state;
        };
        const liveTestCallbacks = new LiveTestingCallbacks((changes_1) => {
            adapter.Refresh(changes_1);
            const state_1 = refreshAllDecorations();
            updateDiagnostics(state_1);
            updateState(state_1);
            if (exists((c_12) => {
                if (c_12.tag === 2) {
                    return c_12.fields[0].some((r_2) => {
                        const matchValue_22 = r_2.Outcome;
                        switch (matchValue_22.tag) {
                            case 1:
                            case 4:
                                return true;
                            default:
                                return false;
                        }
                    });
                }
                else {
                    return false;
                }
            }, changes_1)) {
                if (outputChannel() == null) {
                }
                else {
                    const out_4 = outputChannel();
                    out_4.show(true);
                }
            }
        }, (summary) => {
            updateTestStatusBar(summary);
        }, () => {
            refreshStatus();
        }, (_arg_67) => {
        }, (_arg_68) => {
        }, new FeatureCallbacks((_arg_69) => {
        }, (_arg_70) => {
        }, (_arg_71) => {
        }, (stats) => {
            updateEvalPerfBar(stats);
        }), (filePath_1, blockStartLine, output, durationMs) => {
            const line_1 = (blockStartLine - 1) | 0;
            iterate((ed_4) => {
                clearEvalInProgress(ed_4);
                showInlineResult(ed_4, output, durationMs, line_1);
            }, toArray(tryFind_1((ed_3) => (ed_3.document.fileName === filePath_1), Window_getVisibleTextEditors())));
        }, (filePath_2, blockStartLine_1) => {
            const line_2 = (blockStartLine_1 - 1) | 0;
            iterate((ed_6) => {
                markDecorationsStale(ed_6);
                showEvalInProgress(ed_6, line_2);
            }, toArray(tryFind_1((ed_5) => (ed_5.document.fileName === filePath_2), Window_getVisibleTextEditors())));
        }, (locations) => {
            adapter.UpdateSourceLocations(locations);
        }, (data) => {
            handleFileAnnotations(data);
        }, (narratives_1) => {
            updateNarratives(fold((m, n_5) => add(n_5.TestId, n_5, m), empty_1({
                Compare: comparePrimitives,
            }), narratives_1));
            iterate((a_1) => {
                a_1.RefreshNarratives();
            }, toArray(testAdapter()));
        }, (step, total_1, message, _progress, phase) => {
            warmupPhase(phase);
            const detail_1 = (total_1 > 5) ? toText(printf("%d/%d"))(step)(total_1) : undefined;
            warmupDetail(detail_1);
            refreshStatus();
            if (phase === "finalizing") {
                warmupPhase(undefined);
                warmupDetail(undefined);
            }
        }, (projectName) => {
            Window_showInformationMessage(toText(printf("SageFs: warmup complete for %s — session ready"))(projectName), []);
        }, (filePath_3) => {
            let shortName;
            const parts = split(filePath_3, ["/", "\\"]);
            shortName = ((parts.length > 0) ? item(parts.length - 1, parts) : filePath_3);
            getOutput().appendLine(toText(printf("[SageFs] File reloaded: %s"))(shortName));
        }, (reason) => {
            promiseIgnore(PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (Window_showWarningMessage(toText(printf("SageFs session faulted: %s. Use Restart Session to recover."))(reason), ["Restart Session", "Show Output"]).then((_arg_72) => {
                const choice_5 = _arg_72;
                let matchResult_10;
                if (choice_5 != null) {
                    switch (choice_5) {
                        case "Restart Session": {
                            matchResult_10 = 0;
                            break;
                        }
                        case "Show Output": {
                            matchResult_10 = 1;
                            break;
                        }
                        default:
                            matchResult_10 = 2;
                    }
                }
                else {
                    matchResult_10 = 2;
                }
                switch (matchResult_10) {
                    case 0: {
                        Commands_executeCommand("sagefs.restart");
                        return Promise.resolve();
                    }
                    case 1: {
                        showOutputPanel();
                        return Promise.resolve();
                    }
                    default: {
                        return Promise.resolve();
                    }
                }
            })))));
        }, (_data) => {
        }, (data_1) => {
            const severity = defaultArg(fieldString("Severity", data_1), "unknown");
            const summary_1 = defaultArg(fieldString("Summary", data_1), "");
            getOutput().appendLine(toText(printf("[SageFs Diagnostics] %s: %s"))(severity)(summary_1));
            let failures;
            try {
                failures = map((f) => (new VscDiagnosisFailure((() => {
                    try {
                        throw 1;
                    }
                    catch (matchValue_24) {
                        return "";
                    }
                })(), (() => {
                    try {
                        throw 1;
                    }
                    catch (matchValue_25) {
                        return [];
                    }
                })())), (() => {
                    throw 1;
                })());
            }
            catch (matchValue_26) {
                failures = [];
            }
            if (failures.length === 0) {
            }
            else {
                updateDiagnosis(failures);
            }
        });
        const listener = start_1(c_11.mcpPort, liveTestCallbacks, () => {
            c_11.log("SSE reconnected — refreshing status...");
            iterate((id_4) => {
                clearTimeout(id_4);
            }, toArray(evalWatchdogTimer()));
            evalWatchdogTimer(undefined);
            if (statusBarItem() == null) {
            }
            else {
                const sb_1 = statusBarItem();
                sb_1.text = "$(check) SageFs: connected";
                sb_1.backgroundColor = undefined;
                sb_1.show();
            }
            refreshStatus();
        }, () => {
            c_11.log("SSE disconnected — reconnecting...");
            if (statusBarItem() == null) {
            }
            else {
                const sb_2 = statusBarItem();
                sb_2.text = "$(sync~spin) SageFs: reconnecting...";
                sb_2.backgroundColor = some(newThemeColor("statusBarItem.warningBackground"));
                sb_2.show();
            }
            if (evalId() === 0) {
            }
            else {
                const activeEvalId = evalId() | 0;
                iterate((id_5) => {
                    clearTimeout(id_5);
                }, toArray(evalWatchdogTimer()));
                evalWatchdogTimer(some(setTimeout((() => {
                    let pr;
                    if (evalId() === activeEvalId) {
                        evalId(0);
                        evalWatchdogTimer(undefined);
                        const out_5 = getOutput();
                        out_5.appendLine("⚠ Evaluation interrupted: daemon connection lost");
                        out_5.show(true);
                        const matchValue_29 = Window_getActiveTextEditor();
                        if (matchValue_29 == null) {
                        }
                        else {
                            showInlineDiagnostic(matchValue_29, "⚠ Evaluation interrupted: daemon connection lost", undefined);
                        }
                        promiseIgnore((pr = Window_showWarningMessage("Evaluation interrupted: SageFs daemon connection lost.", ["Reconnect", "Show Output"]), pr.then((choice_6) => {
                            let matchResult_11;
                            if (choice_6 != null) {
                                switch (choice_6) {
                                    case "Reconnect": {
                                        matchResult_11 = 0;
                                        break;
                                    }
                                    case "Show Output": {
                                        matchResult_11 = 1;
                                        break;
                                    }
                                    default:
                                        matchResult_11 = 2;
                                }
                            }
                            else {
                                matchResult_11 = 2;
                            }
                            switch (matchResult_11) {
                                case 0: {
                                    promiseIgnoreLog((msg_5) => {
                                        getOutput().appendLine(msg_5);
                                    }, Commands_executeCommand("sagefs.reconnect"));
                                    break;
                                }
                                case 1: {
                                    showOutputPanel();
                                    break;
                                }
                                case 2: {
                                    break;
                                }
                            }
                        })));
                    }
                    else {
                        evalWatchdogTimer(undefined);
                    }
                }), 5000)));
            }
        }, (msg_6) => {
            getOutput().appendLine(toText(printf("[SSE] %s"))(msg_6));
        });
        liveTestListener(listener);
        c_11.log("connectToRunningDaemon: SSE streams established.");
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
        promiseIgnoreLog(logToOutput, PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => ((new Promise(resolve => setTimeout(resolve, 2000))).then(() => (PromiseBuilder__Delay_62FBFDE1(promise, () => (listSessions(c_11).then((_arg_74) => {
            const sessions = _arg_74;
            if (!equalsWith(equals, sessions, defaultOf()) && (sessions.length === 0)) {
                return findProject().then((_arg_75) => {
                    const projOpt = _arg_75;
                    if (projOpt == null) {
                        return Promise.resolve();
                    }
                    else {
                        const proj = projOpt;
                        const workDir = defaultArg(getWorkingDirectory(), ".");
                        return Window_showInformationMessage(toText(printf("SageFs is running but has no session. Create one for %s?"))(proj), ["Create Session", "Not Now"]).then((_arg_76) => {
                            const choice_7 = _arg_76;
                            let matchResult_12;
                            if (choice_7 != null) {
                                if (choice_7 === "Create Session") {
                                    matchResult_12 = 0;
                                }
                                else {
                                    matchResult_12 = 1;
                                }
                            }
                            else {
                                matchResult_12 = 1;
                            }
                            switch (matchResult_12) {
                                case 0:
                                    return createSession(proj, workDir, c_11).then((_arg_77) => (((_arg_77.tag === 1) ? (Promise.resolve()) : ((void Window_showInformationMessage(toText(printf("SageFs: Session created for %s"))(proj), []), Promise.resolve()))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
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
        }))).catch((_arg_78) => {
            return Promise.resolve();
        })))))));
    };
    const checkAndConnect = (c_13) => {
        promiseIgnoreLog(logToOutput, PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (getSystemStatus(c_13).then((_arg_79) => {
            const sysOpt = _arg_79;
            if (sysOpt != null) {
                const matchValue_30 = checkVersion(sysOpt);
                if (matchValue_30.tag === 1) {
                    const msg_7 = matchValue_30.fields[0];
                    getOutput().appendLine(toText(printf("[SageFs] Version mismatch: %s"))(msg_7));
                    return Window_showErrorMessage(msg_7, ["Update Now", "Show Output", "Ignore"]).then((_arg_80) => {
                        const choice_8 = _arg_80;
                        let matchResult_13;
                        if (choice_8 != null) {
                            switch (choice_8) {
                                case "Update Now": {
                                    matchResult_13 = 0;
                                    break;
                                }
                                case "Show Output": {
                                    matchResult_13 = 1;
                                    break;
                                }
                                default:
                                    matchResult_13 = 2;
                            }
                        }
                        else {
                            matchResult_13 = 2;
                        }
                        switch (matchResult_13) {
                            case 0: {
                                const term = Window_createTerminal("SageFs Update");
                                term.show();
                                term.sendText("dotnet tool update --global SageFs");
                                return Promise.resolve();
                            }
                            case 1: {
                                showOutputPanel();
                                return Promise.resolve();
                            }
                            default: {
                                return Promise.resolve();
                            }
                        }
                    });
                }
                else {
                    connectToRunningDaemon(c_13);
                    return Promise.resolve();
                }
            }
            else {
                connectToRunningDaemon(c_13);
                return Promise.resolve();
            }
        })))));
    };
    onDaemonReady(checkAndConnect);
    const autoStart = config.get("autoStart", true);
    const out_6 = getOutput();
    let extVersion;
    try {
        extVersion = defaultArg(fieldString("version", context.extension.packageJSON), "?");
    }
    catch (matchValue_31) {
        extVersion = "?";
    }
    out_6.appendLine(toText(printf("SageFs v%s activating (mcpPort=%d, dashboardPort=%d, autoStart=%b)"))(extVersion)(mcpPort)(dashboardPort)(autoStart));
    promiseIgnoreLog(logToOutput, PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (PromiseBuilder__Delay_62FBFDE1(promise, () => {
        out_6.appendLine("Checking for running daemon...");
        return isRunning(c).then((_arg_81) => {
            if (_arg_81) {
                out_6.appendLine("Daemon found, connecting SSE streams...");
                return PromiseBuilder__Delay_62FBFDE1(promise, () => {
                    checkAndConnect(c);
                    return Promise.resolve();
                }).catch((_arg_82) => {
                    let arg_47;
                    out_6.appendLine((arg_47 = toString(_arg_82), toText(printf("SSE connection error: %s"))(arg_47)));
                    if (statusBarItem() == null) {
                        return Promise.resolve();
                    }
                    else {
                        const sb_3 = statusBarItem();
                        sb_3.text = "$(warning) SageFs: SSE error";
                        sb_3.show();
                        return Promise.resolve();
                    }
                });
            }
            else if (autoStart) {
                out_6.appendLine("Daemon not found, auto-starting...");
                return findProject().then((_arg_83) => {
                    const projPath = _arg_83;
                    if (projPath == null) {
                        out_6.appendLine("No .fsproj/.sln found, skipping auto-start.");
                        return Promise.resolve();
                    }
                    else {
                        const proj_1 = projPath;
                        out_6.appendLine(toText(printf("Starting daemon for %s"))(proj_1));
                        return startDaemon().then(() => (Promise.resolve(undefined)));
                    }
                });
            }
            else {
                out_6.appendLine("Daemon not running (autoStart=false, waiting for manual start).");
                return Promise.resolve();
            }
        });
    }).catch((_arg_85) => {
        let arg_49;
        out_6.appendLine((arg_49 = toString(_arg_85), toText(printf("SageFs activation error: %s"))(arg_49)));
        out_6.show(false);
        if (statusBarItem() == null) {
            return Promise.resolve();
        }
        else {
            const sb_4 = statusBarItem();
            sb_4.text = "$(error) SageFs: activation failed";
            sb_4.show();
            return Promise.resolve();
        }
    })))));
    void (context.subscriptions.push(Workspace_onDidChangeConfiguration((e) => {
        if (e.affectsConfiguration("sagefs")) {
            const cfg = Workspace_getConfiguration("sagefs");
            updatePorts(cfg.get("mcpPort", 37749), cfg.get("dashboardPort", 37750), c);
            currentDensity(densityFromString(cfg.get("density", "full")));
        }
    })));
    const updateCellHighlightForEditor = (ed_8) => {
        let matchResult_14;
        if (currentDensity().tag === 1) {
            matchResult_14 = 0;
        }
        else if (currentDensity().tag === 0) {
            matchResult_14 = 1;
        }
        else {
            matchResult_14 = 0;
        }
        switch (matchResult_14) {
            case 0: {
                clearCellHighlight();
                break;
            }
            case 1: {
                if ((() => {
                    try {
                        return ed_8.document.languageId;
                    }
                    catch (matchValue_33) {
                        return "";
                    }
                })() === "fsharp") {
                    const curLine = ~~ed_8.selection.active.line | 0;
                    const patternInput_1 = getBlockBounds(ed_8.document, curLine);
                    updateCellHighlight(ed_8, patternInput_1[0], patternInput_1[1]);
                }
                break;
            }
        }
    };
    void (context.subscriptions.push(Window_onDidChangeTextEditorSelection((ed_9) => {
        updateCellHighlightForEditor(ed_9);
    })));
    void (context.subscriptions.push(Window_onDidChangeActiveTextEditor((edOpt) => {
        if (edOpt == null) {
            clearCellHighlight();
        }
        else {
            updateCellHighlightForEditor(edOpt);
        }
    })));
    refreshStatus();
    const statusInterval = setInterval((() => {
        refreshStatus();
    }), 15000);
    void (context.subscriptions.push({
        dispose() {
            clearInterval(statusInterval);
            return defaultOf();
        },
    }));
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
    stopAutoRefresh();
    stopAutoRefresh_1();
    dispose();
    disposeFileAnnotationDecoTypes();
    clearAllDecorations();
}

