const $ = (id) => document.getElementById(id);

async function load() {
  const { panelSide = 'right', visits = {} } = await chrome.storage.local.get(['panelSide', 'visits']);
  document.querySelector(`input[name=side][value=${panelSide}]`).checked = true;
  $('dump').textContent = JSON.stringify(visits, null, 2);
}

document.querySelectorAll('input[name=side]').forEach((el) => {
  el.addEventListener('change', async (e) => {
    await chrome.storage.local.set({ panelSide: e.target.value });
    $('saved').textContent = '已儲存';
    setTimeout(() => ($('saved').textContent = ''), 1500);
  });
});

$('clear').addEventListener('click', async () => {
  await chrome.storage.local.set({ visits: {} });
  load();
});

chrome.storage.onChanged.addListener(load);
load();
