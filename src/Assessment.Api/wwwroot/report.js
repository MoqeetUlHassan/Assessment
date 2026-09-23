'use strict';

(async () => {
  const { api, el, money, problemText, showMessage } = App;
  const me = await App.requireMe();
  App.renderHeader(me);

  const form = document.getElementById('range');
  const table = document.getElementById('report');
  const message = document.getElementById('message');

  // Default: the current month, in UTC (the report's time basis).
  const now = new Date();
  const iso = d => d.toISOString().slice(0, 10);
  form.from.value = iso(new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1)));
  form.to.value = iso(new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 0)));

  async function load() {
    const query = new URLSearchParams({ from: form.from.value, to: form.to.value });
    const result = await api('GET', `/api/reports/spend?${query}`);
    if (!result.ok) { showMessage(message, problemText(result)); table.replaceChildren(); return; }
    message.replaceChildren();

    const r = result.data;
    table.replaceChildren(
      el('thead', {}, el('tr', {},
        el('th', { text: 'Site' }), el('th', { class: 'num', text: 'Completed requests' }), el('th', { class: 'num', text: 'Total spend' }))),
      el('tbody', {}, r.sites.map(s => el('tr', {},
        el('td', { text: s.siteName }),
        el('td', { class: 'num', text: String(s.completedCount) }),
        el('td', { class: 'num', text: money(s.totalSpend) })))),
      el('tfoot', {}, el('tr', {},
        el('th', { text: `Total ${r.from} to ${r.to}` }),
        el('th', { class: 'num', text: String(r.completedCount) }),
        el('th', { class: 'num', text: money(r.totalSpend) }))),
    );
  }

  form.addEventListener('submit', e => { e.preventDefault(); load(); });
  if (me.permissions.includes('reports.spend')) await load();
  else showMessage(message, 'You do not have permission to view the spend report.');
})();
