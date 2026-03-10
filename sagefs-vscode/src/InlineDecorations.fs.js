import { newRange, newThemeColor, Window_createTextEditorDecorationType, Workspace_getConfiguration } from "./Vscode.fs.js";
import { disposeSafe, getEnumerator, comparePrimitives, createAtom } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { containsKey, toList, add, iterate as iterate_1, remove, tryFind, empty } from "./fable_modules/fable-library-js.4.29.0/Map.js";
import { iterate } from "./fable_modules/fable-library-js.4.29.0/Seq.js";
import { defaultArgWith, toArray } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { join, printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { map } from "./fable_modules/fable-library-js.4.29.0/List.js";
import { item } from "./fable_modules/fable-library-js.4.29.0/Array.js";

export function getInlineTimeout() {
    const config = Workspace_getConfiguration("sagefs");
    return config.get("inlineResultTimeout", 30000) | 0;
}

export let blockDecorations = createAtom(empty({
    Compare: comparePrimitives,
}));

export let staleDecorations = createAtom(empty({
    Compare: comparePrimitives,
}));

let evalInProgressDecorations = empty({
    Compare: comparePrimitives,
});

let cellHighlightDeco = undefined;

const cellBorderDeco = Window_createTextEditorDecorationType({
    borderWidth: "1px 0 0 0",
    borderStyle: "solid",
    borderColor: newThemeColor("sagefs.cellBorderColor"),
    isWholeLine: true,
});

/**
 * Update the cell highlight to show the block the cursor is in.
 * Call on cursor change. startLine/endLine are the block bounds.
 */
export function updateCellHighlight(editor, startLine, endLine) {
    const config = Workspace_getConfiguration("sagefs");
    const enabled = config.get("cellHighlight", true);
    if (enabled) {
        iterate((d_1) => {
            const value_1 = d_1.dispose();
        }, toArray(cellHighlightDeco));
        const deco = Window_createTextEditorDecorationType({
            backgroundColor: newThemeColor("sagefs.cellHighlightBackground"),
            isWholeLine: true,
        });
        const ranges = [];
        for (let i = startLine; i <= endLine; i++) {
            void (ranges.push(newRange(i, 0, i, 0)));
        }
        editor.setDecorations(deco, ranges);
        cellHighlightDeco = deco;
        editor.setDecorations(cellBorderDeco, [newRange(startLine, 0, startLine, 0)]);
    }
    else {
        iterate((d) => {
            const value = d.dispose();
        }, toArray(cellHighlightDeco));
        cellHighlightDeco = undefined;
        editor.setDecorations(cellBorderDeco, []);
    }
}

export function clearCellHighlight() {
    iterate((d) => {
        const value = d.dispose();
    }, toArray(cellHighlightDeco));
    cellHighlightDeco = undefined;
}

export function formatDuration(ms) {
    if (ms < 1000) {
        const arg = ~~ms | 0;
        return toText(printf("%dms"))(arg);
    }
    else {
        const arg_1 = ms / 1000;
        return toText(printf("%.1fs"))(arg_1);
    }
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
    const matchValue_1 = tryFind(line, staleDecorations());
    if (matchValue_1 == null) {
    }
    else {
        const deco_1 = matchValue_1;
        const value_1 = deco_1.dispose();
        staleDecorations(remove(line, staleDecorations()));
    }
}

export function autoClearAfter(line) {
    const ms = getInlineTimeout() | 0;
    if (ms === 0) {
    }
    else {
        setTimeout((() => {
            clearBlockDecoration(line);
        }), ms);
    }
}

export function clearAllDecorations() {
    iterate_1((_arg, deco) => {
        const value = deco.dispose();
    }, blockDecorations());
    blockDecorations(empty({
        Compare: comparePrimitives,
    }));
    iterate_1((_arg_1, deco_1) => {
        const value_1 = deco_1.dispose();
    }, staleDecorations());
    staleDecorations(empty({
        Compare: comparePrimitives,
    }));
    iterate_1((_arg_2, deco_2) => {
        const value_2 = deco_2.dispose();
    }, evalInProgressDecorations);
    evalInProgressDecorations = empty({
        Compare: comparePrimitives,
    });
}

/**
 * Show an "⏳ evaluating…" ghost-text suffix at the end of the given (0-based) line.
 * Uses a separate decoration type from result/stale markers so it can be cleared independently.
 */
export function showEvalInProgress(editor, line) {
    const matchValue = tryFind(line, evalInProgressDecorations);
    if (matchValue == null) {
    }
    else {
        const existing = matchValue;
        const value = existing.dispose();
        evalInProgressDecorations = remove(line, evalInProgressDecorations);
    }
    const deco = Window_createTextEditorDecorationType({
        after: {
            contentText: "  // ⏳ evaluating…",
            color: newThemeColor("sagefs.staleForeground"),
            fontStyle: "italic",
        },
    });
    const lineText = editor.document.lineAt(line).text;
    const endCol = lineText.length | 0;
    const range = newRange(line, endCol, line, endCol);
    editor.setDecorations(deco, [range]);
    evalInProgressDecorations = add(line, deco, evalInProgressDecorations);
}

/**
 * Remove all "evaluating" decorations (call when eval_result arrives).
 */
export function clearEvalInProgress(_editor) {
    iterate_1((_arg, deco) => {
        const value = deco.dispose();
    }, evalInProgressDecorations);
    evalInProgressDecorations = empty({
        Compare: comparePrimitives,
    });
}

export function markDecorationsStale(editor) {
    const enumerator = getEnumerator(map((tuple) => tuple[0], toList(blockDecorations())));
    try {
        while (enumerator["System.Collections.IEnumerator.MoveNext"]()) {
            const line = enumerator["System.Collections.Generic.IEnumerator`1.get_Current"]() | 0;
            const matchValue = tryFind(line, blockDecorations());
            if (matchValue == null) {
            }
            else {
                const deco = matchValue;
                const value = deco.dispose();
                blockDecorations(remove(line, blockDecorations()));
                if (!containsKey(line, staleDecorations())) {
                    const staleDeco = Window_createTextEditorDecorationType({
                        after: {
                            contentText: "  // ⏸ stale",
                            color: newThemeColor("sagefs.staleForeground"),
                            fontStyle: "italic",
                        },
                    });
                    const lineText = editor.document.lineAt(line).text;
                    const endCol = lineText.length | 0;
                    const range = newRange(line, endCol, line, endCol);
                    editor.setDecorations(staleDeco, [range]);
                    staleDecorations(add(line, staleDeco, staleDecorations()));
                }
            }
        }
    }
    finally {
        disposeSafe(enumerator);
    }
}

function getEditorLine(editor) {
    if (editor.selection.isEmpty) {
        return ~~editor.selection.active.line | 0;
    }
    else {
        return ~~editor.selection.end.line | 0;
    }
}

/**
 * Flash-highlight a range of lines briefly to indicate eval started.
 */
export function flashEvalRange(editor, startLine, endLine) {
    const deco = Window_createTextEditorDecorationType({
        backgroundColor: newThemeColor("sagefs.evalFlashBackground"),
        isWholeLine: true,
    });
    const ranges = [];
    for (let i = startLine; i <= endLine; i++) {
        void (ranges.push(newRange(i, 0, i, 0)));
    }
    editor.setDecorations(deco, ranges);
    setTimeout((() => {
        const value = deco.dispose();
    }), 300);
}

export function showInlineResult(editor, text, durationMs, atLine) {
    let matchValue_1, n, summary;
    const trimmed = text.trim();
    if (trimmed === "") {
    }
    else {
        const line = defaultArgWith(atLine, () => getEditorLine(editor)) | 0;
        clearBlockDecoration(line);
        const lines = trimmed.split("\n");
        const firstLine = (lines.length === 0) ? "" : item(0, lines);
        let durSuffix;
        if (durationMs == null) {
            durSuffix = "";
        }
        else {
            const arg = formatDuration(durationMs);
            durSuffix = toText(printf("  %s"))(arg);
        }
        const deco = Window_createTextEditorDecorationType({
            after: {
                contentText: (matchValue_1 = (lines.length | 0), (matchValue_1 === 0) ? toText(printf("  // → %s%s"))(firstLine)(durSuffix) : ((matchValue_1 === 1) ? toText(printf("  // → %s%s"))(firstLine)(durSuffix) : ((n = (matchValue_1 | 0), (summary = ((n <= 4) ? join("  │  ", lines) : toText(printf("%s  │  ... (%d lines)"))(firstLine)(n)), toText(printf("  // → %s%s"))(summary)(durSuffix)))))),
                color: newThemeColor("sagefs.successForeground"),
                fontStyle: "italic",
            },
        });
        const lineText = editor.document.lineAt(line).text;
        const endCol = lineText.length | 0;
        const range = newRange(line, endCol, line, endCol);
        editor.setDecorations(deco, [range]);
        blockDecorations(add(line, deco, blockDecorations()));
        autoClearAfter(line);
    }
}

export function showInlineDiagnostic(editor, text, atLine) {
    let firstLine;
    const parts = text.split("\n");
    firstLine = ((parts.length === 0) ? "" : item(0, parts).trim());
    if (firstLine === "") {
    }
    else {
        const line = defaultArgWith(atLine, () => getEditorLine(editor)) | 0;
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
        autoClearAfter(line);
    }
}

