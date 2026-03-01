// SSE subscriber with exponential backoff reconnect.
// Shared implementation for both simple and typed SSE subscriptions.
const http = require('http');
const zlib = require('zlib');

function createSseSubscriber(url, onMessage) {
  let req;
  let buffer = '';
  let currentEvent = 'message';
  let retryDelay = 1000;
  let inactivityTimer;
  const maxDelay = 30000;
  const inactivityTimeout = 60000;

  const resetInactivity = () => {
    if (inactivityTimer) clearTimeout(inactivityTimer);
    inactivityTimer = setTimeout(() => {
      console.warn('[SageFs SSE] No data for 60s, reconnecting...');
      if (req) req.destroy();
    }, inactivityTimeout);
  };

  const reconnect = () => {
    if (inactivityTimer) clearTimeout(inactivityTimer);
    retryDelay = Math.min(retryDelay * 2, maxDelay);
    const jitter = retryDelay * 0.3 * Math.random();
    setTimeout(startListening, retryDelay + jitter);
  };

  const startListening = () => {
    req = http.get(url, { timeout: 0, headers: { 'Accept-Encoding': 'br, gzip, deflate' } }, (res) => {
      retryDelay = 1000;
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
            try {
              const data = JSON.parse(line.slice(6));
              onMessage(currentEvent, data);
            } catch (e) { console.warn('[SageFs SSE] JSON parse error:', e.message); }
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
