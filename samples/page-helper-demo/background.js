// 背景 service worker：設定預設值、回應 content script / popup 的訊息

const DEFAULTS = { showPanel: true, panelSide: 'right', visits: {} };

chrome.runtime.onInstalled.addListener(async () => {
  const current = await chrome.storage.local.get(Object.keys(DEFAULTS));
  const patch = {};
  for (const [k, v] of Object.entries(DEFAULTS)) {
    if (current[k] === undefined) patch[k] = v;
  }
  patch.installedAt = new Date().toISOString();
  await chrome.storage.local.set(patch);
});

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg && msg.type === 'ping') {
    chrome.storage.local.get('installedAt').then(({ installedAt }) => {
      sendResponse({
        ok: true,
        from: 'background',
        time: new Date().toLocaleTimeString(),
        installedAt: installedAt || null,
        senderUrl: sender && sender.url ? sender.url : null,
      });
    });
    return true; // 非同步回應
  }
  return false;
});
