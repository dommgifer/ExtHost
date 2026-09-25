const $ = (id) => document.getElementById(id);

$('ver').textContent = 'v' + chrome.runtime.getManifest().version;

async function load() {
  const { showPanel = true, visits = {} } = await chrome.storage.local.get(['showPanel', 'visits']);
  $('show').checked = showPanel;
  const values = Object.values(visits);
  $('sites').textContent = values.length;
  $('total').textContent = values.reduce((a, b) => a + b, 0);
}

$('show').addEventListener('change', (e) => {
  chrome.storage.local.set({ showPanel: e.target.checked });
});

$('ping').addEventListener('click', async () => {
  try {
    const res = await chrome.runtime.sendMessage({ type: 'ping' });
    $('out').textContent = JSON.stringify(res, null, 2);
  } catch (e) {
    $('out').textContent = '傳送失敗：' + e.message;
  }
});

$('options').addEventListener('click', (e) => {
  e.preventDefault();
  // chrome.runtime.openOptionsPage 在 WebView2 可能無效，改用 window.open，ExtHost 會開成新分頁
  window.open(chrome.runtime.getURL('options.html'));
  window.close();
});

load();
