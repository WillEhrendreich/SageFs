// SSE subscriber with exponential backoff reconnect.
// Shared implementation for both simple and typed SSE subscriptions.
const http = require('http');
const zlib = require('zlib');

function createSseSubscriber(url, onMessage, onReconnect, logger, onDisconnect) {
  const log = logger || ((msg) => console.log('[SageFs SSE]', msg));
  let req;
  let buffer = '';
  let currentEvent = 'message';
  // The daemon puts `id: <epoch>:<seq>` on every frame, and replays what a client missed when it comes
  // back with `Last-Event-ID`. `lastEventId` is the newest id that frame-by-frame reading has SEEN, and
  // survives a drop. `pendingId` is an id read on a line whose frame has not been delivered yet.
  let lastEventId;
  let pendingId;
  let retryDelay = 1000;
  let inactivityTimer;
  const maxDelay = 30000;
  const inactivityTimeout = 300000; // 5 minutes — warmup/compilation can be quiet for extended periods

  const resetInactivity = () => {
    if (inactivityTimer) clearTimeout(inactivityTimer);
    inactivityTimer = setTimeout(() => {
      log('No data for 5m, reconnecting...');
      if (req) req.destroy();
    }, inactivityTimeout);
  };

  let firstConnect = true;

  const reconnect = () => {
    if (inactivityTimer) clearTimeout(inactivityTimer);
    if (onDisconnect) { try { onDisconnect(); } catch (e) { log(`onDisconnect error: ${e.message || e}`); } }
    retryDelay = Math.min(retryDelay * 2, maxDelay);
    const jitter = retryDelay * 0.3 * Math.random();
    const delaySec = ((retryDelay + jitter) / 1000).toFixed(1);
    log(`Reconnecting in ${delaySec}s...`);
    setTimeout(() => {
      startListening();
    }, retryDelay + jitter);
  };

  const startListening = () => {
    log(`Connecting to ${url}`);
    // A connection that died mid-line must not seed the next one. `buffer` and
    // `currentEvent` are per-connection, and without this the first chunk of a
    // reconnect is concatenated onto the dead connection's partial line: the
    // joined "line" is unparseable, so every later frame that reuses it fails
    // with an unterminated string at a position that is the old frame's length.
    // That is what a 990 KB discovery event looked like from the editor.
    buffer = '';
    currentEvent = 'message';
    pendingId = undefined;
    const headers = { 'Accept-Encoding': 'br, gzip, deflate' };
    if (lastEventId) headers['Last-Event-ID'] = lastEventId;
    req = http.get(url, { timeout: 0, headers }, (res) => {
      // Reconnect truth: announce "connected" only once the replacement
      // connection has SUCCEEDED (HTTP response received). Previously
      // onReconnect fired before startListening, so the UI could show
      // "connected" while the old stream was still dead. The initial
      // connection never fires onReconnect (it fires only after a drop).
      retryDelay = 1000;
      log(`Connected (status ${res.statusCode})`);
      if (!firstConnect && onReconnect) { try { onReconnect(); } catch (e) { log(`onReconnect error: ${e.message || e}`); } }
      firstConnect = false;
      resetInactivity();
      // Decompress if server sent compressed response
      let stream = res;
      const encoding = (res.headers['content-encoding'] || '').trim();
      if (encoding === 'br') {
        stream = res.pipe(zlib.createBrotliDecompress());
      } else if (encoding === 'gzip') {
        stream = res.pipe(zlib.createGunzip());
      } else if (encoding === 'deflate') {
        stream = res.pipe(zlib.createInflate());
      }
      stream.on('data', (chunk) => {
        resetInactivity();
        buffer += chunk.toString();
        const lines = buffer.split('\n');
        buffer = lines.pop() || '';
        // An id counts as seen once its frame is delivered, or at the blank line for a frame that
        // carries only an id. One that dies before either was not seen, and the reconnect asks for it.
        const commitId = () => {
          if (pendingId) { lastEventId = pendingId; pendingId = undefined; }
        };
        for (const line of lines) {
          if (line.startsWith('id:')) {
            // One optional space after the colon belongs to the format. An empty id says nothing.
            const value = line.slice(3).replace(/^ /, '').replace(/\r$/, '');
            if (value !== '') pendingId = value;
          } else if (line.startsWith('event: ')) {
            currentEvent = line.slice(7).trim();
          } else if (line.startsWith('data: ')) {
            let data;
            try {
              data = JSON.parse(line.slice(6));
            } catch (e) {
              log(`JSON parse error: ${e.message} raw: ${line.slice(6, 200)}`);
              currentEvent = 'message';
              commitId();
              continue;
            }
            try {
              onMessage(currentEvent, data);
            } catch (appErr) {
              log(`Event handler error for ${currentEvent}: ${appErr.message}\n${appErr.stack}`);
            }
            currentEvent = 'message';
            commitId();
          } else if (line.trim() === '') {
            currentEvent = 'message';
            commitId();
          }
        }
      });
      stream.on('end', reconnect);
      stream.on('error', reconnect);
      res.on('error', reconnect);
    });
    req.on('error', reconnect);
  };

  startListening();
  return { dispose: () => { if (inactivityTimer) clearTimeout(inactivityTimer); if (req) req.destroy(); } };
}

module.exports = { createSseSubscriber };
