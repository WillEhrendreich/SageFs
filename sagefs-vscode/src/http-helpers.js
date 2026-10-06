// HTTP helpers for SageFs VS Code extension
// Extracted from SageFsClient.fs [<Emit>] blocks for debuggability

const http = require('http');

/**
 * @param {string} url
 * @param {number} timeout
 * @returns {Promise<{statusCode: number, body: string}>}
 */
function httpGet(url, timeout) {
  return new Promise((resolve, reject) => {
    const req = http.get(url, { timeout }, (res) => {
      let data = '';
      res.on('data', (chunk) => data += chunk);
      res.on('end', () => resolve({ statusCode: res.statusCode || 0, body: data }));
    });
    req.on('error', reject);
    req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); });
  });
}

/**
 * @param {string} url
 * @param {string} body
 * @param {number} timeout
 * @returns {Promise<{statusCode: number, body: string}>}
 */
function httpPost(url, body, timeout) {
  return new Promise((resolve, reject) => {
    const parsed = new URL(url);
    const req = http.request({
      hostname: parsed.hostname,
      port: parsed.port,
      path: parsed.pathname,
      method: 'POST',
      // Content-Length is explicit, in BYTES. Without it Node sends the body chunked, and the
      // daemon's workflow route and live-testing session targeting read only a body that
      // declares a length: Switch Workflow answered "unknown workflow ''" for exactly this.
      headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) },
      timeout
    }, (res) => {
      let data = '';
      res.on('data', (chunk) => data += chunk);
      res.on('end', () => resolve({ statusCode: res.statusCode || 0, body: data }));
    });
    req.on('error', reject);
    req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); });
    req.write(body);
    req.end();
  });
}

/**
 * POST with caller-supplied request headers, resolving with the RESPONSE headers as well as
 * the body. The MCP streamable-HTTP handshake mints its `Mcp-Session-Id` in the initialize
 * response's HEADERS, so a client that cannot read headers cannot hold an MCP session —
 * this is the one helper that can.
 * @param {string} url
 * @param {string} body
 * @param {number} timeout
 * @param {Record<string, string>} headers
 * @returns {Promise<{statusCode: number, headers: object, body: string}>}
 */
function httpPostHeaders(url, body, timeout, headers) {
  return new Promise((resolve, reject) => {
    const parsed = new URL(url);
    const req = http.request({
      hostname: parsed.hostname,
      port: parsed.port,
      path: parsed.pathname + parsed.search,
      method: 'POST',
      headers: Object.assign(
        { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) },
        headers || {}
      ),
      timeout
    }, (res) => {
      let data = '';
      res.on('data', (chunk) => data += chunk);
      res.on('end', () => resolve({ statusCode: res.statusCode || 0, headers: res.headers, body: data }));
    });
    req.on('error', reject);
    req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); });
    req.write(body);
    req.end();
  });
}

module.exports = { httpGet, httpPost, httpPostHeaders };
