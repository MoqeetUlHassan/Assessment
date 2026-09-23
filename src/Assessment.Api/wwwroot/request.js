'use strict';

(async () => {
  const { api, el, money, when, humanize, problemText, showMessage } = App;
  const me = await App.requireMe();
  App.renderHeader(me);

  const id = new URLSearchParams(location.search).get('id');
  const root = document.getElementById('request');
  const message = document.getElementById('message');

  async function load() {
    const [detail, history] = await Promise.all([
      api('GET', `/api/requests/${encodeURIComponent(id)}`),
      api('GET', `/api/requests/${encodeURIComponent(id)}/history`),
    ]);
    if (!detail.ok) { showMessage(message, problemText(detail)); root.replaceChildren(); return; }
    render(detail.data, history.ok ? history.data : null);
  }

  // Every action posts to the API, then re-renders from the server's answer (the source of truth).
  async function act(method, path, body, done) {
    const result = await api(method, path, body);
    if (result.ok) { showMessage(message, done, false); await load(); }
    else showMessage(message, problemText(result));
  }

  function render(r, history) {
    const a = r.actions;
    const pending = r.pendingRevision;

    root.replaceChildren(
      el('h1', { text: r.description }),
      el('dl', { class: 'grid' },
        el('dt', { text: 'Status' }), el('dd', { class: 'status', text: humanize(r.status) }),
        el('dt', { text: 'Site' }), el('dd', { text: r.siteName }),
        el('dt', { text: 'Requested by' }), el('dd', { text: r.requestedByName }),
        el('dt', { text: 'Estimated cost' }), el('dd', { text: money(r.estimatedCost) }),
        el('dt', { text: 'Actual cost' }), el('dd', { text: money(r.actualCost) }),
        el('dt', { text: 'Approval threshold' }),
        el('dd', { text: `${money(r.approvalThreshold)} (fixed when this request was raised)` }),
        el('dt', { text: 'Raised' }), el('dd', { text: when(r.createdAt) }),
        el('dt', { text: 'Completed' }), el('dd', { text: when(r.completedAt) }),
      ),
      pending ? pendingBox(r, pending, a) : null,
      a.canEdit ? editForm(r) : null,
      a.canSubmitActualCost ? completeForm(r) : null,
      revisionsTable(r.revisions),
      history ? historyTable(history.events) : null,
    );
  }

  function pendingBox(r, p, a) {
    const label = p.kind === 'ActualCost' ? 'Actual cost awaiting approval' : 'Change awaiting approval';
    const comment = el('input', { name: 'comment', maxlength: '1000', placeholder: 'Comment (optional)' });
    const reason = el('input', { name: 'reason', maxlength: '1000', placeholder: 'Reason (required to reject)' });

    return el('div', { class: 'box pending' },
      el('strong', { text: `${label}: ${money(p.amount)}` }),
      el('p', { text: p.description }),
      el('p', { class: 'muted', text: `Submitted by ${p.submittedByName} · ${when(p.createdAt)}${p.reason ? ` · Reason: ${p.reason}` : ''}` }),
      a.canApprove || a.canReject
        ? el('div', { class: 'inline' },
            a.canApprove ? comment : null,
            a.canApprove ? el('button', { type: 'button', text: 'Approve',
              onclick: () => act('POST', `/api/requests/${r.id}/approve`, { revisionId: p.id, comment: comment.value || null }, 'Approved.') }) : null,
            a.canReject ? reason : null,
            a.canReject ? el('button', { type: 'button', text: 'Reject',
              onclick: () => act('POST', `/api/requests/${r.id}/reject`, { revisionId: p.id, reason: reason.value }, 'Rejected.') }) : null)
        : el('p', { class: 'muted', text: 'You can\'t decide this one: you raised it, submitted this change, or lack the approve permission.' }),
    );
  }

  function editForm(r) {
    const form = el('form', { class: 'stack' },
      el('label', {}, 'Description', el('textarea', { name: 'description', required: true, maxlength: '2000', text: r.description })),
      el('label', {}, 'Estimated cost', el('input', { name: 'estimatedCost', type: 'number', min: '0.01', max: '10000000', step: '0.01', required: true, value: String(r.estimatedCost) })),
      el('label', {}, 'Reason for change', el('input', { name: 'reason', required: true, maxlength: '1000' })),
      el('button', { type: 'submit', text: 'Save changes' }),
      el('p', { class: 'muted', text: 'At or above the threshold, a higher cost or a changed description needs re-approval.' }),
    );
    form.addEventListener('submit', event => {
      event.preventDefault();
      act('PUT', `/api/requests/${r.id}`, {
        description: form.description.value,
        estimatedCost: App.numberOrNull(form.estimatedCost.value),
        reason: form.reason.value,
      }, 'Saved.');
    });
    return el('section', {}, el('h2', { text: 'Edit' }), form);
  }

  function completeForm(r) {
    const form = el('form', { class: 'stack' },
      el('label', {}, 'Actual cost', el('input', { name: 'actualCost', type: 'number', min: '0.01', max: '10000000', step: '0.01', required: true })),
      el('label', {}, 'Note (optional)', el('input', { name: 'reason', maxlength: '1000' })),
      el('button', { type: 'submit', text: 'Record actual cost' }),
      el('p', { class: 'muted', text: `At or above ${money(r.approvalThreshold)}, the actual cost needs approval before the request completes.` }),
    );
    form.addEventListener('submit', event => {
      event.preventDefault();
      act('POST', `/api/requests/${r.id}/complete`, {
        actualCost: App.numberOrNull(form.actualCost.value),
        reason: form.reason.value || null,
      }, 'Actual cost recorded.');
    });
    return el('section', {}, el('h2', { text: 'Work done: record actual cost' }), form);
  }

  function revisionsTable(revisions) {
    return el('section', {},
      el('h2', { text: 'Revisions' }),
      el('div', { class: 'scroll' }, el('table', {},
        // Reason = why the submitter asked for this change (edit reason / actual-cost note).
        // Decision note = the approver's comment, or the rejection reason.
        el('thead', {}, el('tr', {}, ['#', 'Kind', 'Amount', 'Description', 'Submitted by', 'Reason', 'Outcome', 'Decided by', 'Decided', 'Decision note']
          .map(h => el('th', { text: h })))),
        el('tbody', {}, revisions.map(v => el('tr', {},
          el('td', { text: String(v.sequence) }),
          el('td', { text: humanize(v.kind) }),
          el('td', { class: 'num', text: money(v.amount) }),
          el('td', { text: v.description }),
          el('td', { text: v.submittedByName }),
          el('td', { text: v.reason || '—' }),
          el('td', { class: 'status', text: humanize(v.outcome) }),
          el('td', { text: v.decidedByName || '—' }),
          el('td', { text: when(v.decidedAt) }),
          el('td', { text: v.decisionComment || '—' }))))))
    );
  }

  function historyTable(events) {
    return el('section', {},
      el('h2', { text: 'Audit trail' }),
      el('div', { class: 'scroll' }, el('table', {},
        el('thead', {}, el('tr', {}, ['When', 'Who', 'Action', 'Status change', 'Details'].map(h => el('th', { text: h })))),
        el('tbody', {}, events.map(e => el('tr', {},
          el('td', { text: when(e.at) }),
          el('td', { text: e.actorName }),
          el('td', { text: humanize(e.action) }),
          el('td', { text: e.toStatus ? `${humanize(e.fromStatus) || '—'} → ${humanize(e.toStatus)}` : '' }),
          el('td', { class: 'muted', text: Object.entries(e.details || {})
            .filter(([k]) => !k.endsWith('Id'))
            .map(([k, v]) => `${k}: ${typeof v === 'object' && v !== null ? JSON.stringify(v) : v}`).join(' · ') }))))))
    );
  }

  await load();
})();
