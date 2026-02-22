import * as vscode from "vscode";

/**
 * Provides CodeLens actions at ;; boundaries in F# files.
 * Shows "▶ Eval" above each code block.
 */
export class SageFsCodeLensProvider implements vscode.CodeLensProvider {
  private _onDidChangeCodeLenses = new vscode.EventEmitter<void>();
  readonly onDidChangeCodeLenses = this._onDidChangeCodeLenses.event;

  provideCodeLenses(
    document: vscode.TextDocument,
    _token: vscode.CancellationToken
  ): vscode.CodeLens[] {
    if (document.languageId !== "fsharp") {
      return [];
    }

    const lenses: vscode.CodeLens[] = [];
    let blockStart = 0;

    for (let i = 0; i < document.lineCount; i++) {
      const lineText = document.lineAt(i).text.trimEnd();
      if (lineText.endsWith(";;")) {
        const range = new vscode.Range(blockStart, 0, i, lineText.length);
        lenses.push(
          new vscode.CodeLens(range, {
            title: "▶ Eval in SageFs",
            command: "sagefs.evalRange",
            arguments: [range],
          })
        );
        blockStart = i + 1;
      }
    }

    return lenses;
  }

  refresh(): void {
    this._onDidChangeCodeLenses.fire();
  }
}
