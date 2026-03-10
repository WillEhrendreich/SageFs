import { newCodeLens, newRange, Workspace_getConfiguration } from "./Vscode.fs.js";
import { trimEnd } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { item } from "./fable_modules/fable-library-js.4.29.0/Array.js";

/**
 * Creates a CodeLens provider object compatible with VSCode's API.
 * Shows "▶ Eval" at the start of each code block — either ;; delimited or blank-line separated.
 * Respects density setting: disabled in Minimal and Normal modes.
 */
export function create() {
    return {
        provideCodeLenses: (doc, _token) => {
            const cfg = Workspace_getConfiguration("sagefs");
            const density = cfg.get("density", "full");
            switch (density) {
                case "minimal":
                case "normal":
                    return [];
                default: {
                    const text = doc.getText();
                    const lines = text.split("\n");
                    const lenses = [];
                    if (lines.some((l) => trimEnd(l).endsWith(";;"))) {
                        let blockStart = 0;
                        for (let i = 0; i <= (lines.length - 1); i++) {
                            const line = trimEnd(item(i, lines));
                            if (line.endsWith(";;")) {
                                const range = newRange(blockStart, 0, blockStart, 0);
                                const cmd = {
                                    title: "▶ Eval",
                                    command: "sagefs.eval",
                                    arguments: [blockStart],
                                };
                                void (lenses.push(newCodeLens(range, cmd)));
                                blockStart = ((i + 1) | 0);
                            }
                        }
                    }
                    else {
                        let inBlock = false;
                        for (let i_1 = 0; i_1 <= (lines.length - 1); i_1++) {
                            const empty = item(i_1, lines).trim() === "";
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
                                    const range_1 = newRange(i_1, 0, i_1, 0);
                                    const cmd_1 = {
                                        title: "▶ Eval",
                                        command: "sagefs.eval",
                                        arguments: [i_1],
                                    };
                                    void (lenses.push(newCodeLens(range_1, cmd_1)));
                                    inBlock = true;
                                    break;
                                }
                                case 1: {
                                    inBlock = false;
                                    break;
                                }
                            }
                        }
                    }
                    return lenses.slice();
                }
            }
        },
    };
}

