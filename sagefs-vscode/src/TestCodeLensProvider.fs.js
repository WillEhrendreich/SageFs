import { disposeSafe, getEnumerator, defaultOf, createAtom } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { VscLiveTestStateModule_resultFor, VscLiveTestStateModule_testsForFile, VscLiveTestStateModule_empty } from "./LiveTestingTypes.fs.js";
import { newCodeLens, newRange, newEventEmitter } from "./Vscode.fs.js";
import { printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";

export let testState = createAtom(VscLiveTestStateModule_empty);

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
                            command: "",
                        };
                        void (lenses.push(newCodeLens(range, cmd)));
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

