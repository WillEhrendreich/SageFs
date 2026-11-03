// A client that loses its connection comes back with the last id it saw.
//
// The daemon puts `id: <epoch>:<seq>` on every frame and replays what a client missed when it sends
// `Last-Event-ID`. The subscriber used to read none of that: a drop meant the snapshots and nothing else.
//
// Two scenarios, each against a scripted local server and the real subscriber:
//  1. An event with an id, then an id-only frame, then a hang-up. The reconnect must name the id-only
//     frame's id (the daemon says "you are current through here" that way), and the id-only frame must not
//     reach the handler.
//  2. An id whose event never completed. The connection dies after `id:` and `event:` and before the
//     `data:` line, so that frame was not seen, and the reconnect must name the one before it.
import assert from 'node:assert';
import http from 'node:http';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { createSseSubscriber } = require('./sse-helpers.js');

// What the server writes on each connection, in order. The last one stays open.
function scenario(name, scripts, wantMessages) {
  return new Promise((resolve, reject) => {
    const received = [];
    const seenHeaders = [];
    const logLines = [];
    let connections = 0;
    let subscriber;

    const server = http.createServer((req, res) => {
      const script = scripts[Math.min(connections, scripts.length - 1)];
      connections += 1;
      seenHeaders.push(req.headers['last-event-id']);
      res.writeHead(200, { 'Content-Type': 'text/event-stream' });
      res.write(script.write);
      if (script.hangUp) setTimeout(() => res.destroy(), 20);
    });

    const timeout = setTimeout(() => {
      try { subscriber.dispose(); } catch (e) { /* already gone */ }
      server.close();
      reject(new Error(`${name}: never got ${wantMessages} message(s); got ${JSON.stringify(received)}; log: ${logLines.join(' | ')}`));
    }, 20000);

    server.listen(0, '127.0.0.1', () => {
      const url = `http://127.0.0.1:${server.address().port}/events`;
      subscriber = createSseSubscriber(
        url,
        (event, data) => {
          received.push({ event, data });
          if (received.length >= wantMessages) {
            clearTimeout(timeout);
            try { subscriber.dispose(); } catch (e) { /* the socket is already gone */ }
            server.close();
            resolve({ received, seenHeaders });
          }
        },
        undefined,
        (msg) => logLines.push(String(msg))
      );
    });
  });
}

const one = await scenario(
  'id-only frame moves the id',
  [
    { write: 'id: run-9:1\nevent: state\ndata: {"n":1}\n\nid: run-9:2\n\n', hangUp: true },
    { write: 'id: run-9:3\nevent: state\ndata: {"n":3}\n\n', hangUp: false },
  ],
  2
);
assert.strictEqual(one.seenHeaders[0], undefined, 'the first connection sends no Last-Event-ID');
assert.strictEqual(one.seenHeaders[1], 'run-9:2', 'the reconnect names the id-only frame, the newest id the daemon sent');
assert.deepStrictEqual(
  one.received.map((r) => r.data.n),
  [1, 3],
  'the id-only frame reaches no handler, and both real frames do'
);
console.log('ok: a reconnect sends the newest id, including one carried by an id-only frame');

const two = await scenario(
  'an id without its data is not seen',
  [
    { write: 'id: run-9:1\nevent: state\ndata: {"n":1}\n\nid: run-9:2\nevent: state\n', hangUp: true },
    { write: 'id: run-9:2\nevent: state\ndata: {"n":2}\n\n', hangUp: false },
  ],
  2
);
assert.strictEqual(two.seenHeaders[1], 'run-9:1', 'the frame that never completed is not counted as seen');
console.log('ok: an id whose event never completed is not committed');
process.exit(0);
