import { subscribeSseWithLogger } from "./JsHelpers.fs.js";
import { printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { iterate } from "./fable_modules/fable-library-js.4.29.0/Seq.js";
import { item } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { toArray, defaultArg } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { fieldArray, fieldInt, fieldString } from "./SafeInterop.fs.js";
import { uriFile, newRange, newDiagnostic } from "./Vscode.fs.js";
import { max } from "./fable_modules/fable-library-js.4.29.0/Double.js";
import { getItemFromDict } from "./fable_modules/fable-library-js.4.29.0/MapUtil.js";
import { disposeSafe, getEnumerator } from "./fable_modules/fable-library-js.4.29.0/Util.js";

export function start(port, dc, log) {
    return subscribeSseWithLogger(toText(printf("http://localhost:%d/diagnostics"))(port), (data) => {
        iterate((diagnostics) => {
            const byFile = new Map([]);
            for (let idx = 0; idx <= (diagnostics.length - 1); idx++) {
                const diag = item(idx, diagnostics);
                iterate((file) => {
                    const message = defaultArg(fieldString("message", diag), "");
                    let severity;
                    const matchValue = defaultArg(fieldString("severity", diag), "");
                    severity = ((matchValue === "error") ? (0) : ((matchValue === "warning") ? (1) : ((matchValue === "info") ? (2) : (3))));
                    const d = newDiagnostic(newRange(max(0, defaultArg(fieldInt("startLine", diag), 1) - 1), max(0, defaultArg(fieldInt("startColumn", diag), 1) - 1), max(0, defaultArg(fieldInt("endLine", diag), 1) - 1), max(0, defaultArg(fieldInt("endColumn", diag), 1) - 1)), message, severity);
                    if (!byFile.has(file)) {
                        byFile.set(file, []);
                    }
                    void (getItemFromDict(byFile, file).push(d));
                }, toArray(fieldString("file", diag)));
            }
            dc.clear();
            let enumerator = getEnumerator(byFile);
            try {
                while (enumerator["System.Collections.IEnumerator.MoveNext"]()) {
                    const kv = enumerator["System.Collections.Generic.IEnumerator`1.get_Current"]();
                    const uri = uriFile(kv[0]);
                    dc.set(uri, kv[1]);
                }
            }
            finally {
                disposeSafe(enumerator);
            }
        }, toArray(fieldArray("diagnostics", data)));
    }, log);
}

