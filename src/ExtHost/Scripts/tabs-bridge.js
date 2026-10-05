// ExtHost：讓 popup 與側邊欄的 chrome.tabs.query 認得 ExtHost 目前選取的分頁（由 ExtensionTabsBridge 注入）。
// 查詢條件含 currentWindow / lastFocusedWindow 時，改從全部分頁中挑出網址等於 ExtHost 目前分頁的那一個。
(() => {
  const tabs = globalThis.chrome && globalThis.chrome.tabs;
  if (!tabs || typeof tabs.query !== 'function' || tabs.__exthostPatched) {
    return;
  }
  const original = tabs.query.bind(tabs);
  let activeUrl = null;
  globalThis.__exthostSetActiveTabUrl = url => { activeUrl = url || null; };

  const stripHash = u => (u || '').split('#')[0];
  const urlOf = t => t.url || t.pendingUrl || '';
  const findActive = list => {
    if (!activeUrl) {
      return null;
    }
    return list.find(t => urlOf(t) === activeUrl)
      || list.find(t => stripHash(urlOf(t)) === stripHash(activeUrl))
      || null;
  };

  async function query(info) {
    info = info || {};
    const wantsCurrent = info.currentWindow === true || info.lastFocusedWindow === true || info.windowId === -2;
    if (!wantsCurrent) {
      return original(info);
    }
    const rest = Object.assign({}, info);
    delete rest.currentWindow;
    delete rest.lastFocusedWindow;
    delete rest.windowId;
    delete rest.active;
    // 每個 WebView2 分頁原生都是 active / highlighted，改由 ExtHost 的目前分頁決定後再過濾
    delete rest.highlighted;
    const self = location.href;
    const all = (await original(rest)).filter(t => urlOf(t) !== self);
    const active = findActive(all);
    const marked = all.map(t => Object.assign({}, t, { active: t === active, highlighted: t === active }));
    return marked.filter(t =>
      (typeof info.active !== 'boolean' || t.active === info.active)
      && (typeof info.highlighted !== 'boolean' || t.highlighted === info.highlighted));
  }

  const patched = function (info, callback) {
    const p = query(info);
    if (typeof callback === 'function') {
      p.then(r => callback(r), e => { console.error(e); callback([]); });
      return undefined;
    }
    return p;
  };
  try {
    tabs.query = patched;
  } catch (e) {
  }
  if (tabs.query !== patched) {
    try {
      Object.defineProperty(tabs, 'query', { value: patched, configurable: true, writable: true });
    } catch (e) {
      console.warn('[ExtHost] 無法改寫 chrome.tabs.query', e);
      return;
    }
  }
  tabs.__exthostPatched = true;
})();
