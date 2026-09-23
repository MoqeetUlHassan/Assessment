'use strict';

(async () => {
  const { api, el, money, when, humanize, problemText, showMessage } = App;
  const me = await App.requireMe();
  App.renderHeader(me);

  const sites = (await api('GET', '/api/sites')).data || [];
  const filter = document.getElementById('filter');
  const table = document.getElementById('requests');
  const pager = document.getElementById('pager');
  const listMessage = document.getElementById('list-message');
  let page = 1;

  for (const site of sites) filter.siteId.append(el('option', { value: site.id, text: site.name }));

  // --- Create (shown only with requests.create; the API enforces it regardless) ---
  if (me.permissions.includes('requests.create')) {
    const section = document.getElementById('create-section');
    const form = document.getElementById('create');
    const message = document.getElementById('create-message');
    section.hidden = false;
    for (const site of sites) form.siteId.append(el('option', { value: site.id, text: site.name }));

    form.addEventListener('submit', async event => {
      event.preventDefault();
      const result = await api('POST', '/api/requests', {
        siteId: form.siteId.value,
        description: form.description.value,
        estimatedCost: App.numberOrNull(form.estimatedCost.value),
      });
      if (result.ok) location.href = `/request.html?id=${encodeURIComponent(result.data.id)}`;
      else showMessage(message, problemText(result));
    });
  }

  // --- List ---
  async function load() {
    const query = new URLSearchParams({ page: String(page), pageSize: '20' });
    if (filter.status.value) query.set('status', filter.status.value);
    if (filter.siteId.value) query.set('siteId', filter.siteId.value);

    const result = await api('GET', `/api/requests?${query}`);
    if (!result.ok) { showMessage(listMessage, problemText(result)); return; }
    listMessage.replaceChildren();

    const { items, totalCount, pageSize } = result.data;
    table.replaceChildren(
      el('thead', {}, el('tr', {},
        ['Created', 'Site', 'Description', 'Requested by', 'Estimate', 'Actual', 'Status']
          .map((h, i) => el('th', { class: i >= 4 && i <= 5 ? 'num' : null, text: h })))),
      el('tbody', {}, items.length === 0
        ? el('tr', {}, el('td', { colspan: '7', class: 'muted', text: 'No requests.' }))
        : items.map(r => el('tr', {},
            el('td', { text: when(r.createdAt) }),
            el('td', { text: r.siteName }),
            el('td', {}, el('a', { href: `/request.html?id=${encodeURIComponent(r.id)}`, text: r.description })),
            el('td', { text: r.requestedByName }),
            el('td', { class: 'num', text: money(r.estimatedCost) }),
            el('td', { class: 'num', text: money(r.actualCost) }),
            el('td', { class: 'status', text: humanize(r.status) }))))
    );

    const pages = Math.max(1, Math.ceil(totalCount / pageSize));
    pager.replaceChildren(
      el('button', { type: 'button', text: 'Previous', disabled: page <= 1, onclick: () => { page--; load(); } }),
      el('span', { text: `Page ${page} of ${pages} · ${totalCount} request(s)` }),
      el('button', { type: 'button', text: 'Next', disabled: page >= pages, onclick: () => { page++; load(); } }),
    );
  }

  filter.addEventListener('submit', event => { event.preventDefault(); page = 1; load(); });
  await load();
})();
