import { trimEnd } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { item } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { newCodeLens, newRange } from "./Vscode.fs.js";

/**
 * Creates a CodeLens provider object compatible with VSCode's API.
 * Returns a plain JS object with the provideCodeLenses method.
 */
export function create() {
    return {
        provideCodeLenses: (doc, _token) => {
            const text = doc.getText();
            const lines = text.split("\n");
            const lenses = [];
            for (let i = 0; i <= (lines.length - 1); i++) {
                const line = trimEnd(item(i, lines));
                if (line.endsWith(";;")) {
                    const range = newRange(i, 0, i, line.length);
                    const cmd = {
                        title: "▶ Eval",
                        command: "sagefs.eval",
                        arguments: [i],
                    };
                    void (lenses.push(newCodeLens(range, cmd)));
                }
            }
            return lenses.slice();
        },
    };
}

