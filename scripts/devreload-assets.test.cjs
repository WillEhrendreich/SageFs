const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const resources = ['SageFs.Host', 'SageFs'].map(project => ({
  project,
  source: fs.readFileSync(path.join(__dirname, '..', project, 'Resources', 'devreload.js'), 'utf8')
}));

function browser(source, storage = new Map(), storageAvailable = true, stream = '/__sagefs__/reload') {
  let reloads = 0;
  let eventSource;
  const element = () => ({ dataset: {}, style: {}, setAttribute() {}, appendChild() {} });
  const script = element();
  const document = {
    title: 'Browser asset fixture', head: element(), body: element(),
    createElement: element, addEventListener() {}, querySelectorAll: () => [],
    querySelector: selector => selector.startsWith('script[') ? script : null
  };
  const sessionStorage = {
    getItem(key) {
      if (!storageAvailable) throw new Error('Storage unavailable');
      return storage.get(key) ?? null;
    },
    setItem(key, value) {
      if (!storageAvailable) throw new Error('Storage unavailable');
      storage.set(key, value);
    },
    removeItem(key) { storage.delete(key); }
  };
  const rendered = source.replace(/\{\{([A-Z_]+)\}\}/g, (_, token) => {
    if (token === 'SSE_URL') return stream;
    if (token === 'EDITOR_URL_PATTERN') return 'vscode://file/{file}:{line}:{col}';
    return '10000';
  });
  vm.runInNewContext(rendered, {
    document, sessionStorage,
    window: { scrollY: 0, scrollTo() {}, location: { reload() { reloads++; } } },
    location: { href: 'http://localhost/' },
    EventSource: function () { eventSource = this; },
    setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
    console: { debug() {}, info() {}, warn() {}, error() {} }
  });
  return {
    send: message => eventSource.onmessage({ data: JSON.stringify(message) }),
    reloads: () => reloads
  };
}

const assets = contentHash => ({ type: 'assetsrebuilt', outcome: 'AssetsRebuilt', file: 'Client.fs', assetCount: 1, contentHash });

function assertSingleAssetRefresh(source) {
  const page = browser(source);
  page.send(assets('a'));
  page.send(assets('a'));
  assert.equal(page.reloads(), 1);
}

for (const { project, source } of resources) {
  test(`${project}: rebuilt assets refresh once for duplicate events`, () => assertSingleAssetRefresh(source));

  test(`${project}: duplicate delivery after navigation does not refresh again`, () => {
    const storage = new Map();
    const first = browser(source, storage);
    first.send(assets('a'));
    assert.equal(first.reloads(), 1);
    const next = browser(source, storage);
    next.send(assets('a'));
    assert.equal(next.reloads(), 0);
    next.send(assets('b'));
    assert.equal(next.reloads(), 1);
  });

  test(`${project}: asset hashes are scoped to the worker stream`, () => {
    const storage = new Map();
    browser(source, storage, true, '/worker-one/reload').send(assets('a'));
    const second = browser(source, storage, true, '/worker-two/reload');
    second.send(assets('a'));
    assert.equal(second.reloads(), 1);
  });

  test(`${project}: blocked storage still deduplicates within the page`, () => {
    const page = browser(source, new Map(), false);
    page.send(assets('a'));
    page.send(assets('a'));
    assert.equal(page.reloads(), 1);
  });

  test(`${project}: returning to an earlier asset hash refreshes again`, () => {
    const page = browser(source);
    ['a', 'b', 'a'].forEach(hash => page.send(assets(hash)));
    assert.equal(page.reloads(), 3);
  });

  test(`${project}: failed and unchanged builds never refresh`, () => {
    const page = browser(source);
    page.send({ type: 'compiling', file: 'Client.fs' });
    page.send({ type: 'failed', error: 'Build failed' });
    page.send({ type: 'compiling', file: 'Client.fs' });
    page.send({ type: 'noeffect', outcome: 'Unchanged' });
    assert.equal(page.reloads(), 0);
  });

  test(`${project}: ordinary pending then patched still refreshes once`, () => {
    const page = browser(source);
    page.send({ type: 'pending', considered: 1 });
    page.send({ type: 'patched', patched: 1, considered: 1 });
    page.send({ type: 'neverentered' });
    assert.equal(page.reloads(), 1);
  });

  test(`${project}: each bounded asset and unchanged sequence matches hash transitions`, () => {
    const choices = [assets('a'), assets('b'), { type: 'noeffect' }];
    for (let encoded = 0; encoded < choices.length ** 5; encoded++) {
      const page = browser(source);
      let cursor = encoded;
      let lastHash;
      let expected = 0;
      for (let step = 0; step < 5; step++) {
        const message = choices[cursor % choices.length];
        cursor = Math.floor(cursor / choices.length);
        if (message.type === 'assetsrebuilt' && message.contentHash !== lastHash) {
          expected++;
          lastHash = message.contentHash;
        }
        page.send(message);
      }
      assert.equal(page.reloads(), expected, `sequence ${encoded}`);
    }
  });

  test(`${project}: assertions reject missing asset refresh and missing duplicate guard`, () => {
    const mutants = [
      source.replace("msg.type === 'assetsrebuilt'", "msg.type === 'disabled-assetsrebuilt'"),
      source.replace('msg.contentHash === lastAssetHash', 'false')
    ];
    for (const mutant of mutants) {
      assert.notEqual(mutant, source, 'mutation must change the executed source');
      assert.throws(() => assertSingleAssetRefresh(mutant), assert.AssertionError);
    }
  });
}
