import { printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { item } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { uriFile, newRange, newDiagnostic } from "./Vscode.fs.js";
import { max } from "./fable_modules/fable-library-js.4.29.0/Double.js";
import { getItemFromDict } from "./fable_modules/fable-library-js.4.29.0/MapUtil.js";
import { disposeSafe, getEnumerator } from "./fable_modules/fable-library-js.4.29.0/Util.js";

export function start(port, dc) {
    const url = toText(printf("http://localhost:%d/diagnostics"))(port);
    return (() => {
  const http = require('http');
  let req;
  let buffer = '';
  let retryDelay = 1000;
  const maxDelay = 30000;
  const startListening = () => {
    req = http.get(url, { timeout: 0 }, (res) => {
      retryDelay = 1000;
      res.on('data', (chunk) => {
        buffer += chunk.toString();
        let lines = buffer.split('\\n');
        buffer = lines.pop() || '';
        for (const line of lines) {
          if (line.startsWith('data: ')) {
            try {
              const data = JSON.parse(line.slice(6));
              ((data) => {
        const rawDiags = data.diagnostics;
        if (rawDiags == null) {
        }
        else {
            const diagnostics = rawDiags;
            const byFile = new Map([]);
            for (let idx = 0; idx <= (diagnostics.length - 1); idx++) {
                const diag = item(idx, diagnostics);
                const file = diag.file;
                if (file == null) {
                }
                else {
                    const msg = diag.message;
                    const message = (msg == null) ? "" : msg;
                    let severity;
                    const matchValue = diag.severity;
                    severity = ((matchValue === "error") ? (0) : ((matchValue === "warning") ? (1) : ((matchValue === "info") ? (2) : (3))));
                    const d = newDiagnostic(newRange(max(0, diag.startLine - 1), max(0, diag.startColumn - 1), max(0, diag.endLine - 1), max(0, diag.endColumn - 1)), message, severity);
                    if (!byFile.has(file)) {
                        byFile.set(file, []);
                    }
                    void (getItemFromDict(byFile, file).push(d));
                }
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
        }
    })(data);
            } catch (_) {}
          }
        }
      });
      res.on('end', () => {
        retryDelay = Math.min(retryDelay * 2, maxDelay);
        setTimeout(startListening, retryDelay);
      });
      res.on('error', () => {
        retryDelay = Math.min(retryDelay * 2, maxDelay);
        setTimeout(startListening, retryDelay);
      });
    });
    req.on('error', () => {
      retryDelay = Math.min(retryDelay * 2, maxDelay);
      setTimeout(startListening, retryDelay);
    });
  };
  startListening();
  return { dispose: () => { if (req) req.destroy(); } };
})();
}

