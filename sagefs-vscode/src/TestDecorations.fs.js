import { stringHash, disposeSafe, getEnumerator, createAtom } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { newDiagnostic, uriFile, Window_getVisibleTextEditors, newRange, Languages_createDiagnosticCollection, newThemeColor, Window_createTextEditorDecorationType } from "./Vscode.fs.js";
import { VscLiveTestStateModule_resultFor, VscLiveTestStateModule_testsForFile } from "./LiveTestingTypes.fs.js";
import { printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { iterate } from "./fable_modules/fable-library-js.4.29.0/Seq.js";
import { toArray } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { toList, tryFind } from "./fable_modules/fable-library-js.4.29.0/Map.js";
import { item } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { List_groupBy } from "./fable_modules/fable-library-js.4.29.0/Seq2.js";
import { choose } from "./fable_modules/fable-library-js.4.29.0/List.js";

export let passedType = createAtom(undefined);

export let failedType = createAtom(undefined);

export let runningType = createAtom(undefined);

export let coveredPassingType = createAtom(undefined);

export let coveredFailingType = createAtom(undefined);

export let notCoveredType = createAtom(undefined);

export let diagnosticCollection = createAtom(undefined);

export function initialize() {
    passedType(Window_createTextEditorDecorationType({
        gutterIconPath: "",
        isWholeLine: true,
        after: {
            contentText: " ✓",
            color: newThemeColor("testing.iconPassed"),
            fontStyle: "italic",
            margin: "0 0 0 1em",
        },
        overviewRulerColor: newThemeColor("testing.iconPassed"),
        overviewRulerLane: 1,
    }));
    failedType(Window_createTextEditorDecorationType({
        isWholeLine: true,
        after: {
            contentText: " ✗",
            color: newThemeColor("testing.iconFailed"),
            fontStyle: "italic",
            margin: "0 0 0 1em",
        },
        overviewRulerColor: newThemeColor("testing.iconFailed"),
        overviewRulerLane: 1,
        backgroundColor: "rgba(244, 71, 71, 0.08)",
    }));
    runningType(Window_createTextEditorDecorationType({
        isWholeLine: true,
        after: {
            contentText: " ●",
            color: newThemeColor("testing.iconQueued"),
            fontStyle: "italic",
            margin: "0 0 0 1em",
        },
    }));
    diagnosticCollection(Languages_createDiagnosticCollection("sagefs-tests"));
    coveredPassingType(Window_createTextEditorDecorationType({
        isWholeLine: false,
        before: {
            contentText: "▸",
            color: newThemeColor("testing.iconPassed"),
            margin: "0 0.5em 0 0",
        },
    }));
    coveredFailingType(Window_createTextEditorDecorationType({
        isWholeLine: false,
        before: {
            contentText: "▸",
            color: newThemeColor("testing.iconFailed"),
            margin: "0 0.5em 0 0",
        },
    }));
    notCoveredType(Window_createTextEditorDecorationType({
        isWholeLine: false,
        before: {
            contentText: "○",
            color: newThemeColor("disabledForeground"),
            margin: "0 0.5em 0 0",
        },
    }));
}

/**
 * Build decoration options for a test at a specific line with hover message
 */
export function decorationRange(line, hoverText) {
    return {
        range: newRange(line - 1, 0, line - 1, 0),
        hoverMessage: hoverText,
    };
}

/**
 * Apply test decorations to a single text editor based on current test state
 */
export function applyToEditor(state, editor) {
    let passedRanges = [];
    let failedRanges = [];
    let runningRanges = [];
    const enumerator = getEnumerator(VscLiveTestStateModule_testsForFile(editor.document.fileName, state));
    try {
        while (enumerator["System.Collections.IEnumerator.MoveNext"]()) {
            const test = enumerator["System.Collections.Generic.IEnumerator`1.get_Current"]();
            const matchValue = test.Line;
            if (matchValue == null) {
            }
            else {
                const line = matchValue | 0;
                const result = VscLiveTestStateModule_resultFor(test.Id, state);
                if (result == null) {
                    void (runningRanges.push(decorationRange(line, toText(printf("◆ %s (not yet run)"))(test.DisplayName))));
                }
                else {
                    const r = result;
                    const matchValue_1 = r.Outcome;
                    switch (matchValue_1.tag) {
                        case 1: {
                            const text = toText(printf("✗ %s: %s"))(test.DisplayName)(matchValue_1.fields[0]);
                            void (failedRanges.push(decorationRange(line, text)));
                            break;
                        }
                        case 4: {
                            const text_1 = toText(printf("✗ %s: %s"))(test.DisplayName)(matchValue_1.fields[0]);
                            void (failedRanges.push(decorationRange(line, text_1)));
                            break;
                        }
                        case 3: {
                            void (runningRanges.push(decorationRange(line, toText(printf("● Running: %s"))(test.DisplayName))));
                            break;
                        }
                        case 2: {
                            void (passedRanges.push(decorationRange(line, toText(printf("⊘ Skipped: %s — %s"))(test.DisplayName)(matchValue_1.fields[0]))));
                            break;
                        }
                        case 5: {
                            void (runningRanges.push(decorationRange(line, toText(printf("◌ Stale: %s"))(test.DisplayName))));
                            break;
                        }
                        case 6: {
                            void (passedRanges.push(decorationRange(line, toText(printf("⊘ Disabled: %s"))(test.DisplayName))));
                            break;
                        }
                        default: {
                            let durationText;
                            const matchValue_2 = r.DurationMs;
                            if (matchValue_2 == null) {
                                durationText = toText(printf("✓ %s"))(test.DisplayName);
                            }
                            else {
                                const ms = matchValue_2;
                                durationText = toText(printf("✓ %s (%.0fms)"))(test.DisplayName)(ms);
                            }
                            void (passedRanges.push(decorationRange(line, durationText)));
                        }
                    }
                }
            }
        }
    }
    finally {
        disposeSafe(enumerator);
    }
    iterate((dt) => {
        editor.setDecorations(dt, passedRanges);
    }, toArray(passedType()));
    iterate((dt_1) => {
        editor.setDecorations(dt_1, failedRanges);
    }, toArray(failedType()));
    iterate((dt_2) => {
        editor.setDecorations(dt_2, runningRanges);
    }, toArray(runningType()));
}

/**
 * Apply coverage decorations to a single text editor
 */
export function applyCoverageToEditor(state, editor) {
    const matchValue = tryFind(editor.document.fileName, state.Coverage);
    if (matchValue != null) {
        const covPassRanges = [];
        const covFailRanges = [];
        const notCovRanges = [];
        const enumerator = getEnumerator(matchValue.LineCoverage);
        try {
            while (enumerator["System.Collections.IEnumerator.MoveNext"]()) {
                const kvp = enumerator["System.Collections.Generic.IEnumerator`1.get_Current"]();
                const line = kvp[0] | 0;
                const matchValue_1 = kvp[1];
                switch (matchValue_1.tag) {
                    case 1: {
                        void (notCovRanges.push(decorationRange(line, "○ Not covered by any test")));
                        break;
                    }
                    case 2: {
                        break;
                    }
                    default: {
                        const testCount = matchValue_1.fields[0] | 0;
                        const health = matchValue_1.fields[1];
                        const hoverText = (health.tag === 1) ? toText(printf("▸ Covered by %d test(s), some failing"))(testCount) : toText(printf("▸ Covered by %d test(s), all passing"))(testCount);
                        if (health.tag === 1) {
                            void (covFailRanges.push(decorationRange(line, hoverText)));
                        }
                        else {
                            void (covPassRanges.push(decorationRange(line, hoverText)));
                        }
                    }
                }
            }
        }
        finally {
            disposeSafe(enumerator);
        }
        iterate((dt_3) => {
            editor.setDecorations(dt_3, covPassRanges);
        }, toArray(coveredPassingType()));
        iterate((dt_4) => {
            editor.setDecorations(dt_4, covFailRanges);
        }, toArray(coveredFailingType()));
        iterate((dt_5) => {
            editor.setDecorations(dt_5, notCovRanges);
        }, toArray(notCoveredType()));
    }
    else {
        iterate((dt) => {
            editor.setDecorations(dt, []);
        }, toArray(coveredPassingType()));
        iterate((dt_1) => {
            editor.setDecorations(dt_1, []);
        }, toArray(coveredFailingType()));
        iterate((dt_2) => {
            editor.setDecorations(dt_2, []);
        }, toArray(notCoveredType()));
    }
}

/**
 * Apply coverage decorations to all visible editors
 */
export function applyCoverageToAllEditors(state) {
    const editors = Window_getVisibleTextEditors();
    for (let idx = 0; idx <= (editors.length - 1); idx++) {
        applyCoverageToEditor(state, item(idx, editors));
    }
}

/**
 * Apply decorations to all visible editors
 */
export function applyToAllEditors(state) {
    const editors = Window_getVisibleTextEditors();
    for (let idx = 0; idx <= (editors.length - 1); idx++) {
        applyToEditor(state, item(idx, editors));
    }
}

/**
 * Update the diagnostics collection with test failures
 */
export function updateDiagnostics(state) {
    if (diagnosticCollection() != null) {
        const dc = diagnosticCollection();
        dc.clear();
        const enumerator = getEnumerator(List_groupBy((tupledArg_1) => tupledArg_1[0], choose((tupledArg) => {
            const matchValue = tupledArg[1].Outcome;
            let matchResult, msg;
            switch (matchValue.tag) {
                case 1: {
                    matchResult = 0;
                    msg = matchValue.fields[0];
                    break;
                }
                case 4: {
                    matchResult = 0;
                    msg = matchValue.fields[0];
                    break;
                }
                default:
                    matchResult = 1;
            }
            switch (matchResult) {
                case 0: {
                    const testInfo = tryFind(tupledArg[0], state.Tests);
                    if (testInfo == null) {
                        return undefined;
                    }
                    else {
                        const info = testInfo;
                        const matchValue_1 = info.FilePath;
                        const matchValue_2 = info.Line;
                        let matchResult_1, fp, line;
                        if (matchValue_1 != null) {
                            if (matchValue_2 != null) {
                                matchResult_1 = 0;
                                fp = matchValue_1;
                                line = matchValue_2;
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
                                return [fp, line, info.DisplayName, msg];
                            default:
                                return undefined;
                        }
                    }
                }
                default:
                    return undefined;
            }
        }, toList(state.Results)), {
            Equals: (x, y) => (x === y),
            GetHashCode: stringHash,
        }));
        try {
            while (enumerator["System.Collections.IEnumerator.MoveNext"]()) {
                const forLoopVar = enumerator["System.Collections.Generic.IEnumerator`1.get_Current"]();
                const uri = uriFile(forLoopVar[0]);
                const diagnostics = [];
                const enumerator_1 = getEnumerator(forLoopVar[1]);
                try {
                    while (enumerator_1["System.Collections.IEnumerator.MoveNext"]()) {
                        const forLoopVar_1 = enumerator_1["System.Collections.Generic.IEnumerator`1.get_Current"]();
                        const line_1 = forLoopVar_1[1] | 0;
                        const diagnostic = newDiagnostic(newRange(line_1 - 1, 0, line_1 - 1, 100), toText(printf("%s: %s"))(forLoopVar_1[2])(forLoopVar_1[3]), 0);
                        void (diagnostics.push(diagnostic));
                    }
                }
                finally {
                    disposeSafe(enumerator_1);
                }
                dc.set(uri, diagnostics);
            }
        }
        finally {
            disposeSafe(enumerator);
        }
    }
}

export function dispose() {
    iterate((dt) => {
        dt.dispose();
    }, toArray(passedType()));
    iterate((dt_1) => {
        dt_1.dispose();
    }, toArray(failedType()));
    iterate((dt_2) => {
        dt_2.dispose();
    }, toArray(runningType()));
    iterate((dt_3) => {
        dt_3.dispose();
    }, toArray(coveredPassingType()));
    iterate((dt_4) => {
        dt_4.dispose();
    }, toArray(coveredFailingType()));
    iterate((dt_5) => {
        dt_5.dispose();
    }, toArray(notCoveredType()));
    iterate((dc) => {
        dc.dispose();
    }, toArray(diagnosticCollection()));
    passedType(undefined);
    failedType(undefined);
    runningType(undefined);
    coveredPassingType(undefined);
    coveredFailingType(undefined);
    notCoveredType(undefined);
    diagnosticCollection(undefined);
}

