chrome.runtime.sendMessage('status', (r) => {
  const ok = r && r.connected;
  document.getElementById('dot').className = 'dot ' + (ok ? 'ok' : 'bad');
  document.getElementById('state').textContent = ok ? 'Подключено к программе' : 'Программа Altair не запущена';
});
