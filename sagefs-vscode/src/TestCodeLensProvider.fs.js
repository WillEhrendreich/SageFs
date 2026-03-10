import { disposeSafe, getEnumerator, defaultOf, comparePrimitives, createAtom } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { VscTestIdModule_value, VscLiveTestStateModule_resultFor, VscLiveTestStateModule_testsForFile, VscLiveTestStateModule_empty } from "./LiveTestingTypes.fs.js";
import { tryFind, ofArray, empty } from "./fable_modules/fable-library-js.4.29.0/Map.js";
import { newCodeLens, newRange, newEventEmitter } from "./Vscode.fs.js";
import { map } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { join, printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";

export let testState = createAtom(VscLiveTestStateModule_empty);

export let narrativeState = createAtom(empty({
    Compare: comparePrimitives,
}));

export let diagnosisState = createAtom(empty({
    Compare: comparePrimitives,
}));

export const changeEmitter = newEventEmitter();

/**
 * Notify VS Code to refresh CodeLens
 */
export function refresh() {
    changeEmitter.fire(defaultOf());
}

/**
 * Update state and refresh
 */
export function updateState(state) {
    testState(state);
    refresh();
}

/**
 * Update narrative state and refresh CodeLens
 */
export function updateNarratives(narratives) {
    narrativeState(narratives);
    refresh();
}

/**
 * Update diagnosis state and refresh CodeLens.
 * Call this when a diagnosis_ready SSE event arrives with per-failure causal symbols.
 */
export function updateDiagnosis(failures) {
    diagnosisState(ofArray(map((f) => [f.TestName, f.CausalSymbols], failures), {
        Compare: comparePrimitives,
    }));
    refresh();
}

/**
 * Format a test result as a CodeLens title
 */
export function formatTitle(result) {
    const matchValue = result.Outcome;
    switch (matchValue.tag) {
        case 1: {
            const msg = matchValue.fields[0];
            const short = (msg.length > 60) ? (msg.slice(undefined, 59 + 1) + "…") : msg;
            return toText(printf("✗ Failed: %s"))(short);
        }
        case 3:
            return "● Running…";
        case 2:
            return toText(printf("⊘ Skipped: %s"))(matchValue.fields[0]);
        case 4:
            return toText(printf("✗ Error: %s"))(matchValue.fields[0]);
        case 5:
            return "◌ Stale";
        case 6:
            return "⊘ Disabled";
        default: {
            const matchValue_1 = result.DurationMs;
            if (matchValue_1 == null) {
                return "✓ Passed";
            }
            else {
                const ms = matchValue_1;
                return toText(printf("✓ Passed (%.0fms)"))(ms);
            }
        }
    }
}

/**
 * Creates a CodeLens provider for test results
 */
export function create() {
    return {
        onDidChangeCodeLenses: changeEmitter.event,
        provideCodeLenses: (doc, _token) => {
            let matchValue_1, matchValue_2, symbols_1;
            const lenses = [];
            const enumerator = getEnumerator(VscLiveTestStateModule_testsForFile(doc.fileName, testState()));
            try {
                while (enumerator["System.Collections.IEnumerator.MoveNext"]()) {
                    const t = enumerator["System.Collections.Generic.IEnumerator`1.get_Current"]();
                    const matchValue = t.Line;
                    if (matchValue == null) {
                    }
                    else {
                        const line = matchValue | 0;
                        const range = newRange(line - 1, 0, line - 1, 0);
                        const result = VscLiveTestStateModule_resultFor(t.Id, testState());
                        const cmd = {
                            title: (result == null) ? "◆ Detected" : formatTitle(result),
                            command: "sagefs.runTests",
                            tooltip: (result == null) ? t.DisplayName : ((matchValue_1 = result.Outcome, (matchValue_1.tag === 5) ? ((matchValue_2 = testState().Freshness, (matchValue_2.tag === 1) ? toText(printf("%s — stale: code edited since last run"))(t.DisplayName) : ((matchValue_2.tag === 2) ? toText(printf("%s — stale: generation mismatch (re-run needed)"))(t.DisplayName) : toText(printf("%s — stale"))(t.DisplayName)))) : ((matchValue_1.tag === 6) ? toText(printf("%s — disabled by policy"))(t.DisplayName) : t.DisplayName))),
                        };
                        void (lenses.push(newCodeLens(range, cmd)));
                        if (result == null) {
                        }
                        else {
                            const matchValue_3 = result.Outcome;
                            switch (matchValue_3.tag) {
                                case 1:
                                case 4: {
                                    const testIdStr = VscTestIdModule_value(t.Id);
                                    const matchValue_4 = tryFind(testIdStr, narrativeState());
                                    let matchResult, n_1;
                                    if (matchValue_4 != null) {
                                        if (matchValue_4.Summary !== "") {
                                            matchResult = 0;
                                            n_1 = matchValue_4;
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
                                            const symbols = join(", ", map((c_1) => c_1.Name, n_1.CausalChanges.filter((c) => (c.Kind === "symbol"))));
                                            const whyCmd = {
                                                title: (symbols === "") ? toText(printf("🔍 %s"))(n_1.Summary) : toText(printf("🔍 because %s changed (%s ago)"))(symbols)(n_1.TimeSinceLastPass),
                                                command: "sagefs.explainTestFailure",
                                                tooltip: (symbols === "") ? n_1.Summary : toText(printf("%s\nChanged: %s\nSince: %s ago"))(n_1.Summary)(symbols)(n_1.TimeSinceLastPass),
                                                arguments: [testIdStr],
                                            };
                                            void (lenses.push(newCodeLens(range, whyCmd)));
                                            break;
                                        }
                                    }
                                    break;
                                }
                                default:
                                    undefined;
                            }
                        }
                        const matchValue_5 = tryFind(t.FullName, diagnosisState());
                        let matchResult_1, symbols_2;
                        if (matchValue_5 != null) {
                            if ((symbols_1 = matchValue_5, symbols_1.length > 0)) {
                                matchResult_1 = 0;
                                symbols_2 = matchValue_5;
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
                                const sym = join(", ", symbols_2);
                                const repairCmd = {
                                    title: toText(printf("✦ %s changed → suggest repair"))(sym),
                                    command: "sagefs.suggestRepair",
                                    tooltip: toText(printf("Auto-repair: %s\nCaused by: %s"))(t.DisplayName)(sym),
                                    arguments: [VscTestIdModule_value(t.Id)],
                                };
                                void (lenses.push(newCodeLens(range, repairCmd)));
                                break;
                            }
                        }
                    }
                }
            }
            finally {
                disposeSafe(enumerator);
            }
            return lenses.slice();
        },
    };
}

