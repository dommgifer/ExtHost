// content script：在頁面右下角注入浮動面板（Shadow DOM，避免和網頁的 CSS 互相影響）
(() => {
  if (window.top !== window) return; // 只在最上層頁面執行
  if (document.getElementById('exthost-demo-host')) return;

  const host = document.createElement('div');
  host.id = 'exthost-demo-host';
  host.style.cssText = 'position:fixed;bottom:16px;z-index:2147483647;';
  const root = host.attachShadow({ mode: 'open' });

  root.innerHTML = `
    <style>
      :host { all: initial; }
      .panel { font: 13px/1.5 "Segoe UI", "Microsoft JhengHei", sans-serif; color: #1C1B19;
               width: 280px; background: #fff; border: 1px solid #D6DFEA; border-radius: 12px;
               box-shadow: 0 10px 28px rgba(20,30,50,.18); overflow: hidden; }
      .head { display: flex; align-items: center; gap: 8px; padding: 10px 12px; background: #F2F6FB;
              border-bottom: 1px solid #D6DFEA; }
      .badge { width: 20px; height: 20px; border-radius: 5px; background: #2E7D6B; }
      .title { font-weight: 700; flex: 1; }
      button { font: inherit; cursor: pointer; }
      .x { border: 0; background: transparent; width: 26px; height: 26px; border-radius: 6px; color: #56606E; }
      .x:hover { background: #E3E9F1; }
      .body { padding: 12px; display: grid; gap: 10px; }
      .grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 6px; }
      .stat { background: #F7F6F3; border-radius: 8px; padding: 6px 8px; }
      .stat b { display: block; font-size: 18px; }
      .stat span { font-size: 11px; color: #6B675F; }
      .row { display: flex; gap: 6px; }
      .btn { flex: 1; height: 32px; border-radius: 8px; border: 1px solid #C5CFDB; background: #fff; }
      .btn:hover { background: #F2F6FB; }
      .out { font: 12px/1.4 Consolas, monospace; background: #F7F6F3; border-radius: 8px; padding: 8px;
             white-space: pre-wrap; word-break: break-all; max-height: 120px; overflow: auto; color: #3D3A35; }
      .collapsed .body { display: none; }
    </style>
    <div class="panel">
      <div class="head">
        <div class="badge"></div>
        <div class="title">頁面小幫手</div>
        <button class="x" id="toggle" title="收合">–</button>
      </div>
      <div class="body">
        <div class="grid">
          <div class="stat"><b id="tables">0</b><span>表格</span></div>
          <div class="stat"><b id="rows">0</b><span>表格列</span></div>
          <div class="stat"><b id="visits">0</b><span>本站造訪</span></div>
        </div>
        <div class="row">
          <button class="btn" id="fetch">測試同源 fetch</button>
          <button class="btn" id="ping">Ping 背景</button>
        </div>
        <div class="out" id="out">content script 已注入 ✓</div>
      </div>
    </div>`;

  const $ = (id) => root.getElementById(id);
  const out = (text) => { $('out').textContent = text; };

  function countTables() {
    const tables = document.querySelectorAll('table');
    let rows = 0;
    tables.forEach((t) => { rows += t.rows ? t.rows.length : 0; });
    $('tables').textContent = tables.length;
    $('rows').textContent = rows;
  }

  async function bumpVisits() {
    const { visits = {} } = await chrome.storage.local.get('visits');
    const key = location.host;
    visits[key] = (visits[key] || 0) + 1;
    await chrome.storage.local.set({ visits });
    $('visits').textContent = visits[key];
  }

  $('toggle').addEventListener('click', () => {
    root.querySelector('.panel').classList.toggle('collapsed');
  });

  // 同源 fetch 會自動帶上登入 Cookie：呼叫內部系統 API 就是用這種方式
  $('fetch').addEventListener('click', async () => {
    out('請求中…');
    try {
      const t0 = performance.now();
      const res = await fetch(location.href, { credentials: 'include' });
      const body = await res.text();
      out(`HTTP ${res.status} · ${body.length.toLocaleString()} 字元 · ${Math.round(performance.now() - t0)} ms\n` +
          `content-type: ${res.headers.get('content-type')}`);
    } catch (e) {
      out('fetch 失敗：' + e.message);
    }
  });

  $('ping').addEventListener('click', async () => {
    try {
      const res = await chrome.runtime.sendMessage({ type: 'ping' });
      out(JSON.stringify(res, null, 2));
    } catch (e) {
      out('傳送失敗：' + e.message);
    }
  });

  async function applySettings() {
    const { showPanel = true, panelSide = 'right' } = await chrome.storage.local.get(['showPanel', 'panelSide']);
    host.style.display = showPanel ? 'block' : 'none';
    host.style.left = panelSide === 'left' ? '16px' : '';
    host.style.right = panelSide === 'left' ? '' : '16px';
  }

  chrome.storage.onChanged.addListener((changes, area) => {
    if (area === 'local' && (changes.showPanel || changes.panelSide)) applySettings();
  });

  document.documentElement.appendChild(host);
  applySettings();
  countTables();
  bumpVisits();
})();
