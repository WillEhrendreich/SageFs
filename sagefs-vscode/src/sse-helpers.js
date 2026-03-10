// SSE subscriber with exponential backoff reconnect.
// Shared implementation for both simple and typed SSE subscriptions.
const http = require('http');
const zlib = require('zlib');

function createSseSubscriber(url, onMessage, onReconnect, logger, onDisconnect) {
  const log = logger || ((msg) => console.log('[SageFs SSE]', msg));
  let req;
  let buffer = '';
  let currentEvent = 'message';
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

  const reconnect = () => {
    if (inactivityTimer) clearTimeout(inactivityTimer);
    if (onDisconnect) { try { onDisconnect(); } catch (e) { log(`onDisconnect error: ${e.message || e}`); } }
    retryDelay = Math.min(retryDelay * 2, maxDelay);
    const jitter = retryDelay * 0.3 * Math.random();
    const delaySec = ((retryDelay + jitter) / 1000).toFixed(1);
    log(`Reconnecting in ${delaySec}s...`);
    setTimeout(() => {
      if (onReconnect) { try { onReconnect(); } catch (e) { log(`onReconnect error: ${e.message || e}`); } }
      startListening();
    }, retryDelay + jitter);
  };

  const startListening = () => {
    log(`Connecting to ${url}`);
    req = http.get(url, { timeout: 0, headers: { 'Accept-Encoding': 'br, gzip, deflate' } }, (res) => {
      retryDelay = 1000;
      log(`Connected (status ${res.statusCode})`);
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
        for (const line of lines) {
          if (line.startsWith('event: ')) {
            currentEvent = line.slice(7).trim();
          } else if (line.startsWith('data: ')) {
            let data;
            try {
              data = JSON.parse(line.slice(6));
            } catch (e) {
              log(`JSON parse error: ${e.message} raw: ${line.slice(6, 200)}`);
              currentEvent = 'message';
              continue;
            }
            try {
              onMessage(currentEvent, data);
            } catch (appErr) {
              log(`Event handler error for ${currentEvent}: ${appErr.message}\n${appErr.stack}`);
            }
            currentEvent = 'message';
          } else if (line.trim() === '') {
            currentEvent = 'message';
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
