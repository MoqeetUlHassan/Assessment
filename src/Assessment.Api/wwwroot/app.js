'use strict';

// Shared helpers. Security notes:
// - All user-controlled text is rendered with textContent / text nodes (see el()), never innerHTML.
// - The session is an HttpOnly cookie the browser sends automatically; JS never sees it.
// - Showing or hiding a button here is a convenience only. The API enforces every rule.
const App = (() => {
  async function api(method, path, body) {
    const res = await fetch(path, {
      method,
      credentials: 'same-origin',
      headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    let data = null;
    if (text) { try { data = JSON.parse(text); } catch { data = text; } }
    if (res.status === 401 && path !== '/api/auth/login') {
      // Session gone (logged out, expired, deactivated): go to sign-in and stop here quietly.
      location.href = '/index.html';
      return new Promise(() => {});
    }
    return { ok: res.ok, status: res.status, data };
  }

  function problemText(result) {
    const d = result.data && typeof result.data === 'object' ? result.data : {};
    let message = d.title || `Request failed (${result.status})`;
    if (d.errors) {
      message += '\n' + Object.entries(d.errors).map(([field, msgs]) => `• ${field}: ${msgs.join(' ')}`).join('\n');
    }
    return `${result.status}: ${message}`;
  }

  // Builds DOM safely: string children become text nodes, so they can never be parsed as HTML.
  function el(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(props)) {
      if (value === false || value === null || value === undefined) continue;
      if (key === 'text') node.textContent = value;
      else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
      else node.setAttribute(key, value === true ? '' : value);
    }
    for (const child of children.flat()) {
      if (child !== null && child !== undefined && child !== false) node.append(child);
    }
    return node;
  }

  const moneyFormat = new Intl.NumberFormat(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const money = n => (n === null || n === undefined ? '—' : moneyFormat.format(n));
  const when = s => (s ? new Date(s).toLocaleString() : '—');
  const humanize = s => (s || '').replace(/([a-z])([A-Z])/g, '$1 $2');

  function showMessage(target, text, isError = true) {
    target.replaceChildren(el('p', { class: isError ? 'error' : 'ok', text }));
  }

  async function requireMe() {
    const result = await api('GET', '/api/me');
    return result.data;
  }

  function renderHeader(me) {
    const header = document.querySelector('header');
    const logout = el('button', {
      type: 'button', text: 'Log out',
      onclick: async () => { await api('POST', '/api/auth/logout'); location.href = '/index.html'; },
    });
    // replaceChildren() would print a null as the text "null", so optional items are filtered out first.
    header.replaceChildren(...[
      el('strong', { text: me.organization.name }),
      el('span', { class: 'muted', text: `Approval threshold: ${money(me.organization.approvalThreshold)}` }),
      el('a', { href: '/requests.html', text: 'Requests' }),
      me.permissions.some(p => p.startsWith('admin.')) ? el('a', { href: '/admin.html', text: 'Admin' }) : null,
      el('span', { class: 'spacer' }),
      el('span', { text: `${me.displayName} (${me.role})` }),
      logout,
    ].filter(Boolean));
  }

  function numberOrNull(value) {
    if (value === '' || value === null || value === undefined) return null;
    const n = Number(value);
    return Number.isFinite(n) ? n : null;
  }

  return { api, problemText, el, money, when, humanize, showMessage, requireMe, renderHeader, numberOrNull };
})();
