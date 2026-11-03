// Altair Assistant — мост между браузером и программой на этом компьютере.
// Соединение только с ws://localhost:47811/altair/ ; программа принимает лишь Origin расширения.
const URL_WS = 'ws://localhost:47811/altair/';
let ws = null;
let connected = false;

function connect() {
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
  try { ws = new WebSocket(URL_WS); } catch (e) { return; }
  ws.onopen = () => {
    connected = true;
    const ua = navigator.userAgent;
    const browser = /YaBrowser/.test(ua) ? 'Яндекс Браузер' : /Edg\//.test(ua) ? 'Edge' : /OPR\//.test(ua) ? 'Opera' : /Vivaldi/.test(ua) ? 'Vivaldi' : 'Chrome';
    ws.send(JSON.stringify({ type: 'hello', browser }));
  };
  ws.onclose = () => { connected = false; ws = null; };
  ws.onerror = () => { try { ws.close(); } catch (e) {} };
  ws.onmessage = async (ev) => {
    let msg; try { msg = JSON.parse(ev.data); } catch (e) { return; }
    if (!msg.id) return;
    let reply;
    try { reply = { id: msg.id, ok: true, ...(await handle(msg)) }; }
    catch (e) { reply = { id: msg.id, ok: false, error: String(e && e.message || e) }; }
    try { ws.send(JSON.stringify(reply)); } catch (e) {}
  };
}

async function activeTab() {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab) throw new Error('Нет активной вкладки');
  if (!/^https?:/.test(tab.url || '')) throw new Error('Служебная страница браузера — агент работает только на сайтах');
  return tab;
}

async function runInPage(func, args) {
  const tab = await activeTab();
  await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ['page.js'] });
  const [res] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func, args: args || [] });
  if (res && res.result && res.result.error) throw new Error(res.result.error);
  return { ...(res ? res.result : {}), url: tab.url, title: tab.title };
}

async function handle(msg) {
  switch (msg.type) {
    case 'snapshot': return runInPage(() => window.__altair.snapshot());
    case 'read': return runInPage(() => ({ text: window.__altair.text() }));
    case 'click': return runInPage((l) => window.__altair.click(l), [msg.label]);
    case 'fill': return runInPage((l, v) => window.__altair.fill(l, v), [msg.label, msg.value]);
    case 'newtab': await chrome.tabs.create({ url: msg.url || 'chrome://newtab' }); return {};
    case 'closetab': { const t = await activeTab(); await chrome.tabs.remove(t.id); return {}; }
    default: throw new Error('Неизвестная команда ' + msg.type);
  }
}

// Service worker засыпает — будим и держим соединение
chrome.alarms.create('altair-keepalive', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(() => { connect(); if (connected) try { ws.send(JSON.stringify({ type: 'ping' })); } catch (e) {} });
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
chrome.runtime.onMessage.addListener((m, _s, send) => { if (m === 'status') { connect(); send({ connected }); } return true; });
connect();
