// tabs-bridge.js 的回歸測試，由 TabsBridgeShimTests 以 node 執行；失敗時以非 0 結束碼離開。
// 模擬 WebView2：每個 WebView2 是獨立視窗，各一個分頁，而且原生都是 active / highlighted。
'use strict';
const fs = require('fs');
const path = require('path');
const assert = require('assert');

const shim = fs.readFileSync(process.argv[2] || path.join(__dirname, 'Scripts', 'tabs-bridge.js'), 'utf8');

const SELF = 'chrome-extension://abc/popup.html';
const TABS = [
  { id: 1, url: 'https://portal.example.com/x', active: true, highlighted: true, windowId: 10 },
  { id: 2, url: 'https://booking.example.com/search?', active: true, highlighted: true, windowId: 11 },
  { id: 3, url: SELF, active: true, highlighted: true, windowId: 12 },
];

function load() {
  const matches = (t, info) => Object.keys(info).every(k => {
    if (k === 'currentWindow' || k === 'lastFocusedWindow') {
      return t.windowId === 12; // 原生行為：目前視窗就是自己
    }
    return t[k] === info[k];
  });
  const sandbox = {
    location: { href: SELF },
    console,
    chrome: { tabs: { query: async info => TABS.filter(t => matches(t, info || {})).map(t => ({ ...t })) } },
  };
  sandbox.globalThis = sandbox;
  new Function('globalThis', 'location', 'chrome', 'console', shim)(sandbox, sandbox.location, sandbox.chrome, console);
  return sandbox;
}

const ids = list => list.map(t => t.id);

const tests = {
  async 'active + currentWindow 回傳 ExtHost 目前分頁'() {
    const g = load();
    g.__exthostSetActiveTabUrl('https://booking.example.com/search?');
    assert.deepStrictEqual(ids(await g.chrome.tabs.query({ active: true, currentWindow: true })), [2]);
  },
  async 'callback 寫法'() {
    const g = load();
    g.__exthostSetActiveTabUrl('https://booking.example.com/search?');
    const r = await new Promise(resolve => {
      assert.strictEqual(g.chrome.tabs.query({ active: true, currentWindow: true }, resolve), undefined);
    });
    assert.deepStrictEqual(ids(r), [2]);
  },
  async 'currentWindow 不帶 active：列出全部網頁分頁並重新標記 active'() {
    const g = load();
    g.__exthostSetActiveTabUrl('https://booking.example.com/search?');
    const r = await g.chrome.tabs.query({ currentWindow: true });
    assert.deepStrictEqual(r.map(t => [t.id, t.active, t.highlighted]), [[1, false, false], [2, true, true]]);
  },
  async 'active: false 只回傳非目前分頁'() {
    const g = load();
    g.__exthostSetActiveTabUrl('https://booking.example.com/search?');
    assert.deepStrictEqual(ids(await g.chrome.tabs.query({ active: false, currentWindow: true })), [1]);
  },
  async 'highlighted: true 只回傳目前分頁'() {
    const g = load();
    g.__exthostSetActiveTabUrl('https://booking.example.com/search?');
    assert.deepStrictEqual(ids(await g.chrome.tabs.query({ highlighted: true, currentWindow: true })), [2]);
  },
  async 'highlighted: false 回傳其他分頁'() {
    const g = load();
    g.__exthostSetActiveTabUrl('https://booking.example.com/search?');
    assert.deepStrictEqual(ids(await g.chrome.tabs.query({ highlighted: false, currentWindow: true })), [1]);
  },
  async 'lastFocusedWindow 與忽略 hash 的比對'() {
    const g = load();
    g.__exthostSetActiveTabUrl('https://portal.example.com/x#section');
    assert.deepStrictEqual(ids(await g.chrome.tabs.query({ active: true, lastFocusedWindow: true })), [1]);
  },
  async '切換目前分頁後立即反映'() {
    const g = load();
    g.__exthostSetActiveTabUrl('https://portal.example.com/x');
    g.__exthostSetActiveTabUrl('https://booking.example.com/search?');
    assert.deepStrictEqual(ids(await g.chrome.tabs.query({ active: true, currentWindow: true })), [2]);
  },
  async '沒有目前分頁時回傳空陣列'() {
    const g = load();
    g.__exthostSetActiveTabUrl(null);
    assert.deepStrictEqual(await g.chrome.tabs.query({ active: true, currentWindow: true }), []);
  },
  async '不含 currentWindow 的查詢維持原生行為'() {
    const g = load();
    g.__exthostSetActiveTabUrl('https://booking.example.com/search?');
    assert.deepStrictEqual(ids(await g.chrome.tabs.query({})), [1, 2, 3]);
  },
};

(async () => {
  let failed = 0;
  for (const [name, fn] of Object.entries(tests)) {
    try {
      await fn();
      console.log('PASS ' + name);
    } catch (e) {
      failed++;
      console.log('FAIL ' + name + '\n' + (e && e.stack || e));
    }
  }
  process.exit(failed ? 1 : 0);
})();
