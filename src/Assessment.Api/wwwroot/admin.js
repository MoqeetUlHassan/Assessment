'use strict';

(async () => {
  const { api, el, money, when, humanize, problemText, showMessage } = App;
  const me = await App.requireMe();
  App.renderHeader(me);

  const message = document.getElementById('message');
  const has = p => me.permissions.includes(p);
  let roles = [];
  let catalog = [];

  // Each admin action: call the API, report, reload everything from the server.
  async function act(method, path, body, done) {
    const result = await api(method, path, body);
    if (result.ok) { await loadAll(); showMessage(message, done, false); } // message after the reload, never over stale data
    else showMessage(message, problemText(result));
    return result.ok;
  }

  // New passwords are encrypted like login passwords: the payload never carries them in plain text.
  async function withEncryptedPassword(password, send) {
    let encrypted;
    try { encrypted = await App.encryptPassword(password); }
    catch (e) { showMessage(message, e.message); return; }
    return send(encrypted);
  }

  // --- Threshold ---
  async function loadThreshold() {
    const current = (await api('GET', '/api/me')).data.organization.approvalThreshold;
    document.getElementById('threshold-current').textContent = `Current threshold: ${money(current)}`;
  }
  if (has('admin.settings')) {
    document.getElementById('threshold-section').hidden = false;
    const form = document.getElementById('threshold');
    form.addEventListener('submit', async e => {
      e.preventDefault();
      if (await act('PUT', '/api/admin/settings/threshold',
        { amount: App.numberOrNull(form.amount.value), reason: form.reason.value }, 'Threshold changed.')) form.reset();
    });
  }

  // --- Users ---
  async function loadUsers() {
    const users = (await api('GET', '/api/admin/users')).data || [];
    const table = document.getElementById('users');
    table.replaceChildren(
      el('thead', {}, el('tr', {}, ['Name', 'Email', 'Role', 'Status', 'Created by', 'Actions'].map(h => el('th', { text: h })))),
      el('tbody', {}, users.map(u => {
        const isMe = u.id === me.userId;
        const roleSelect = el('select', { disabled: isMe }, roles.map(r => el('option', { value: r.id, text: r.name, selected: r.id === u.roleId })));
        roleSelect.addEventListener('change', () => act('PUT', `/api/admin/users/${u.id}/role`, { roleId: roleSelect.value }, `Role changed for ${u.displayName}.`));
        const password = el('input', { type: 'password', placeholder: 'New password', minlength: '12', autocomplete: 'new-password' });
        return el('tr', {},
          el('td', { text: u.displayName + (isMe ? ' (you)' : '') }),
          el('td', { text: u.email }),
          el('td', {}, roleSelect),
          el('td', { class: 'status', text: u.isActive ? 'Active' : 'Inactive' }),
          el('td', { text: u.createdByName || '—' }),
          el('td', {}, el('div', { class: 'inline' },
            isMe ? null : el('button', { type: 'button', text: u.isActive ? 'Deactivate' : 'Reactivate',
              onclick: () => act('POST', `/api/admin/users/${u.id}/${u.isActive ? 'deactivate' : 'reactivate'}`, undefined,
                `${u.displayName} ${u.isActive ? 'deactivated' : 'reactivated'}.`) }),
            password,
            el('button', { type: 'button', text: 'Reset password',
              onclick: () => withEncryptedPassword(password.value, encrypted =>
                act('POST', `/api/admin/users/${u.id}/password`, encrypted,
                  `Password reset for ${u.displayName}; their sessions have ended.`)) }))));
      })),
    );
    const roleSelect = document.querySelector('#create-user select[name=roleId]');
    roleSelect.replaceChildren(...roles.map(r => el('option', { value: r.id, text: r.name })));
  }
  if (has('admin.users')) {
    document.getElementById('users-section').hidden = false;
    const form = document.getElementById('create-user');
    form.addEventListener('submit', async e => {
      e.preventDefault();
      await withEncryptedPassword(form.password.value, async encrypted => {
        if (await act('POST', '/api/admin/users', {
          displayName: form.displayName.value, email: form.email.value, roleId: form.roleId.value, ...encrypted,
        }, 'User added.')) form.reset();
      });
    });
  }

  // --- Roles ---
  function permissionCheckboxes(selected, disabledAll) {
    return catalog.map(p => el('label', { class: 'inline' },
      el('input', { type: 'checkbox', value: p.name, checked: selected.includes(p.name), disabled: disabledAll || p.adminOnly }),
      el('span', { text: `${p.name}${p.adminOnly ? ' (OrgAdmin only)' : ''}: ${p.description}` })));
  }
  function renderRoles() {
    document.getElementById('roles').replaceChildren(...roles.map(r => {
      const boxes = permissionCheckboxes(r.permissions, r.isSystemAdmin);
      const box = el('div', { class: 'box' },
        el('strong', { text: `${r.name} · ${r.userCount} user(s)` }),
        r.isSystemAdmin ? el('p', { class: 'muted', text: 'Holds every permission; cannot be edited.' }) : null,
        ...boxes,
        r.isSystemAdmin ? null : el('button', { type: 'button', text: 'Save permissions',
          onclick: () => act('PUT', `/api/admin/roles/${r.id}/permissions`,
            { permissions: boxes.map(b => b.querySelector('input')).filter(i => i.checked).map(i => i.value) },
            `Permissions saved for ${r.name}.`) }));
      return box;
    }));
    document.getElementById('create-role-permissions').replaceChildren(...permissionCheckboxes([], false));
  }
  if (has('admin.roles')) {
    document.getElementById('roles-section').hidden = false;
    const form = document.getElementById('create-role');
    form.addEventListener('submit', async e => {
      e.preventDefault();
      const permissions = [...form.querySelectorAll('input[type=checkbox]:checked')].map(i => i.value);
      if (await act('POST', '/api/admin/roles', { name: form.name.value, permissions }, 'Role added.')) form.reset();
    });
  }

  // --- Audit log ---
  async function loadAudit() {
    const result = await api('GET', '/api/admin/audit?pageSize=50');
    if (!result.ok) return;
    document.getElementById('audit').replaceChildren(
      el('thead', {}, el('tr', {}, ['When', 'Who', 'What', 'Action', 'Details'].map(h => el('th', { text: h })))),
      el('tbody', {}, result.data.items.map(e => el('tr', {},
        el('td', { text: when(e.at) }),
        el('td', { text: e.actorName }),
        el('td', { text: e.entityType }),
        el('td', { text: humanize(e.action) + (e.toStatus ? ` (${humanize(e.fromStatus) || '—'} → ${humanize(e.toStatus)})` : '') }),
        el('td', { class: 'muted', text: Object.entries(e.details || {})
          .filter(([k]) => !k.endsWith('Id'))
          .map(([k, v]) => `${k}: ${Array.isArray(v) ? v.join(', ') : typeof v === 'object' && v !== null ? JSON.stringify(v) : v}`)
          .join(' · ') })))),
    );
  }
  if (has('admin.users')) document.getElementById('audit-section').hidden = false;

  async function loadAll() {
    if (has('admin.roles') || has('admin.users')) {
      [roles, catalog] = await Promise.all([
        has('admin.roles') ? api('GET', '/api/admin/roles').then(r => r.data || []) : Promise.resolve([]),
        has('admin.roles') ? api('GET', '/api/admin/permissions').then(r => r.data || []) : Promise.resolve([]),
      ]);
    }
    const work = [];
    if (has('admin.settings')) work.push(loadThreshold());
    if (has('admin.users')) work.push(loadUsers(), loadAudit());
    if (has('admin.roles')) renderRoles();
    await Promise.all(work);
    App.renderHeader((await api('GET', '/api/me')).data); // threshold shown in the header may have changed
  }

  if (!['admin.users', 'admin.roles', 'admin.settings'].some(has)) {
    showMessage(message, 'You do not have admin permissions.');
    return;
  }
  await loadAll();
})();
