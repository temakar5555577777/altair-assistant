// Встраивается в страницу по запросу агента. Видимые интерактивные элементы и действия над ними.
(() => {
  if (window.__altair) return;

  const SENSITIVE = /(pass|парол|card|карт|cvv|cvc|cc-|otp|one-time|код из|sms|смс|pin)/i;

  const visible = (el) => {
    const r = el.getBoundingClientRect();
    if (r.width < 2 || r.height < 2) return false;
    const st = getComputedStyle(el);
    return st.visibility !== 'hidden' && st.display !== 'none' && Number(st.opacity) > 0.05;
  };

  const labelOf = (el) => {
    const aria = el.getAttribute('aria-label') || el.getAttribute('title') || el.getAttribute('placeholder') || el.getAttribute('alt');
    if (aria) return aria.trim();
    if (el.id) { const l = document.querySelector(`label[for="${CSS.escape(el.id)}"]`); if (l) return l.innerText.trim(); }
    const closestLabel = el.closest('label'); if (closestLabel) return closestLabel.innerText.trim();
    if (el.tagName === 'INPUT' && (el.type === 'submit' || el.type === 'button')) return (el.value || '').trim();
    return (el.innerText || el.textContent || el.name || '').trim().replace(/\s+/g, ' ').slice(0, 120);
  };

  const isSensitive = (el) => {
    if (el.tagName !== 'INPUT' && el.tagName !== 'TEXTAREA') return false;
    if (el.type === 'password') return true;
    const hint = [el.name, el.id, el.autocomplete, el.getAttribute('aria-label'), el.placeholder].join(' ');
    return SENSITIVE.test(hint);
  };

  const candidates = () => Array.from(document.querySelectorAll(
    'a[href], button, input, textarea, select, [role=button], [role=link], [role=tab], [role=menuitem], [role=checkbox], [contenteditable=true], summary'
  )).filter(visible);

  const norm = (s) => (s || '').toLowerCase().replace(/ё/g, 'е').replace(/\s+/g, ' ').trim();

  const find = (label) => {
    const q = norm(label);
    let best = null, bestScore = 0;
    for (const el of candidates()) {
      const l = norm(labelOf(el));
      if (!l) continue;
      const s = l === q ? 1 : l.startsWith(q) ? 0.9 : l.includes(q) ? 0.8 : q.includes(l) && l.length > 3 ? 0.6 : 0;
      if (s > bestScore) { best = el; bestScore = s; }
    }
    return bestScore >= 0.6 ? best : null;
  };

  window.__altair = {
    snapshot() {
      const elements = candidates().slice(0, 200).map((el) => ({
        tag: el.tagName.toLowerCase(),
        kind: el.type || el.getAttribute('role') || undefined,
        label: isSensitive(el) ? '(защищённое поле — заполняет пользователь)' : labelOf(el),
      })).filter((e) => e.label);
      return { elements, text: this.text().slice(0, 4000) };
    },
    text() { return (document.body ? document.body.innerText : '').replace(/\n{3,}/g, '\n\n'); },
    click(label) {
      const el = find(label);
      if (!el) return { error: `Не нашёл на странице «${label}»` };
      el.scrollIntoView({ block: 'center' });
      el.focus();
      el.click();
      return {};
    },
    fill(label, value) {
      const el = find(label);
      if (!el) return { error: `Не нашёл поле «${label}»` };
      if (isSensitive(el)) return { error: 'Это поле пароля, карты или кода — введите его сами' };
      el.scrollIntoView({ block: 'center' });
      el.focus();
      if (el.isContentEditable) { el.textContent = value; }
      else {
        const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : el.tagName === 'SELECT' ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
        const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
        setter.call(el, value); // совместимость с React/Vue
      }
      el.dispatchEvent(new Event('input', { bubbles: true }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
      return {};
    },
  };
})();
