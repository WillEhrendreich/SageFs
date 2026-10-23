// A connection that dies mid-line must not seed the next one.
//
// The subscriber's `buffer` and `currentEvent` live outside `startListening`, so without a reset on
// reconnect the first chunk of the replacement stream is concatenated onto the dead connection's
// partial line. That joined line can never parse, and every later frame depending on it fails with an
// unterminated string at a position that is the old frame's length. A 990 KB test discovery event is
// what made it visible from the editor.
//
// So: the server sends half a `data:` line and hangs up; on the second connection it sends a whole
// event. The subscriber must deliver the second one.
import assert from 'node:assert';
import http from 'node:http';
import { createRequire } from 'node:module';

// The subscriber is CommonJS, so reach it the way the extension does.
const require = createRequire(import.meta.url);
const { createSseSubscriber } = require('./sse-helpers.js');

const received = [];
const logLines = [];
let connections = 0;

const server = http.createServer((req, res) => {
  connections += 1;
  res.writeHead(200, { 'Content-Type': 'text/event-stream' });
  if (connections === 1) {
    res.write('data: {"SessionId":"967c9074","Entries":[{"DisplayName":"cut he');
    setTimeout(() => res.destroy(), 20);
  } else {
    res.write('event: state\ndata: {"ok":true}\n\n');
  }
});

let reported = false;
function finish(err) {
  if (reported) return;
  reported = true;
  clearTimeout(timeout);
  try { server.close(); } catch (e) { /* already closed */ }
  if (err) {
    console.error(`FAIL: ${err.message}\n  log: ${logLines.join('\n  ')}`);
    process.exit(1);
  }
  const parseErrors = logLines.filter((l) => l.includes('JSON parse error'));
  // The discriminating assertion. Without the reset the dead connection's partial line is
  // concatenated onto the reconnect's first chunk: that joined line fails to parse (the
  // parser then skips it), so the frame is still delivered and delivery alone cannot tell
  // the two apart. The parse error is what the editor showed.
  assert.strictEqual(
    parseErrors.length,
    0,
    `no frame may fail to parse after a reconnect; got ${parseErrors.join(' | ')}`
  );
  assert.ok(
    received.some((r) => r.data && r.data.ok === true),
    `the frame after the reconnect must be delivered; got ${JSON.stringify(received)}; log: ${logLines.join(' | ')}`
  );
  console.log(`ok: ${received.length} event(s) after a mid-line drop, ${parseErrors.length} parse error(s)`);
  process.exit(0);
}

const timeout = setTimeout(
  () => finish(new Error('the subscriber never delivered the frame after the reconnect')),
  20000
);

server.listen(0, '127.0.0.1', () => {
  const url = `http://127.0.0.1:${server.address().port}/events`;
  const subscriber = createSseSubscriber(
    url,
    (event, data) => {
      received.push({ event, data });
      if (data && data.ok === true) {
        try { subscriber.dispose(); } catch (e) { /* the socket is already gone */ }
        finish();
      }
    },
    undefined,
    (msg) => logLines.push(String(msg))
  );
});
