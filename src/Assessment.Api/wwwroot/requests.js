'use strict';

(async () => {
  const { api, el, money, when, humanize, problemText, showMessage } = App;
  const me = await App.requireMe();
  App.renderHeader(me);

  const sites = (await api('GET', '/api/sites')).data || [];
  const NEW_SITE = '__new__';
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
    form.siteId.append(el('option', { value: NEW_SITE, text: '+ Add a new site…' }));

    // Adding a site: any member of the organization can; the server puts it in the caller's organization.
    const newSite = document.getElementById('new-site');
    const newSiteName = form.newSiteName;
    const showNewSite = on => {
      newSite.hidden = !on;
      if (on) newSiteName.focus();
      else if (form.siteId.value === NEW_SITE) form.siteId.selectedIndex = 0;
    };
    form.siteId.addEventListener('change', () => showNewSite(form.siteId.value === NEW_SITE));
    document.getElementById('cancel-site').addEventListener('click', () => showNewSite(false));
    if (sites.length === 0) showNewSite(true); // a new organization: the only option is adding a site
    document.getElementById('add-site').addEventListener('click', async () => {
      const result = await api('POST', '/api/sites', { name: newSiteName.value });
      if (!result.ok) { showMessage(message, problemText(result)); return; }
      const created = result.data;
      // Insert before the "+ Add a new site…" entry, select it, and offer it in the list filter too.
      form.siteId.insertBefore(el('option', { value: created.id, text: created.name }), form.siteId.lastElementChild);
      form.siteId.value = created.id;
      filter.siteId.append(el('option', { value: created.id, text: created.name }));
      newSiteName.value = '';
      newSite.hidden = true;
      showMessage(message, `Site "${created.name}" added.`, false);
    });

    form.addEventListener('submit', async event => {
      event.preventDefault();
      if (form.siteId.value === NEW_SITE) { showMessage(message, 'Add the new site first, or pick an existing one.'); return; }
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
