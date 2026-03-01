import { disposeSafe, getEnumerator, comparePrimitives, createAtom } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { add, containsKey, toList, iterate, remove, tryFind, empty } from "./fable_modules/fable-library-js.4.29.0/Map.js";
import { join, printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { map } from "./fable_modules/fable-library-js.4.29.0/List.js";
import { newRange, newThemeColor, Window_createTextEditorDecorationType } from "./Vscode.fs.js";
import { item } from "./fable_modules/fable-library-js.4.29.0/Array.js";

export let blockDecorations = createAtom(empty({
    Compare: comparePrimitives,
}));

export let staleDecorations = createAtom(empty({
    Compare: comparePrimitives,
}));

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

export function clearAllDecorations() {
    iterate((_arg, deco) => {
        const value = deco.dispose();
    }, blockDecorations());
    blockDecorations(empty({
        Compare: comparePrimitives,
    }));
    iterate((_arg_1, deco_1) => {
        const value_1 = deco_1.dispose();
    }, staleDecorations());
    staleDecorations(empty({
        Compare: comparePrimitives,
    }));
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

export function showInlineResult(editor, text, durationMs) {
    let matchValue_1, n, summary;
    const trimmed = text.trim();
    if (trimmed === "") {
    }
    else {
        const line = getEditorLine(editor) | 0;
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
        setTimeout((() => {
            clearBlockDecoration(line);
        }), 30000);
    }
}

export function showInlineDiagnostic(editor, text) {
    let firstLine;
    const parts = text.split("\n");
    firstLine = ((parts.length === 0) ? "" : item(0, parts).trim());
    if (firstLine === "") {
    }
    else {
        const line = getEditorLine(editor) | 0;
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

