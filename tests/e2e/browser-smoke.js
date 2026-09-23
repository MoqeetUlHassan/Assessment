// Browser smoke test of the static client against a RUNNING app (dotnet run), using an installed Edge or Chrome.
// Run: cd tests/e2e && npm install && node browser-smoke.js   (BROWSER_CHANNEL=chrome to use Chrome)
// Signs in ~8 times; the login rate limit is 10/min per IP, so allow a minute between runs.
const { chromium } = require('playwright-core');
const BASE = process.env.BASE_URL || 'http://localhost:5183';
const PASSWORD = 'ChangeMe-Dev-2026!';
const XSS = '<img src=x onerror="window.__xss=1">Fix chiller';

const results = [];
const passwordBodies = []; // every request body the browser sent that carries a password field
const watchLogins = page => page.on('request', r => {
  const url = r.url();
  if (r.method() === 'POST' && (url.endsWith('/api/auth/login') || url.endsWith('/api/admin/users') || url.endsWith('/password')))
    passwordBodies.push(r.postData() || '');
});
const check = (name, ok, detail = '') => { results.push({ name, ok }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  ' + detail : ''}`); };

async function session(browser, email) {
  const context = await browser.newContext();
  const page = await context.newPage();
  watchLogins(page);
  const problems = [];
  page.on('console', m => { if (m.type() === 'error') problems.push(m.text()); });
  page.on('pageerror', e => problems.push(e.message));
  await page.goto(BASE + '/');
  await page.fill('input[name=email]', email);
  await page.fill('input[name=password]', PASSWORD);
  await page.click('button[type=submit]');
  await Promise.race([
    page.waitForURL('**/requests.html'),
    page.waitForSelector('#message p.error').then(async () => {
      throw new Error(`Login as ${email} failed: ${await page.textContent('#message')}` +
        ' (429 = the per-IP login rate limit of 10/min; wait a minute between runs)');
    }),
  ]);
  return { page, problems };
}

(async () => {
  const browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
  try {
    // Wrong password shows the generic error on the login page.
    {
      const page = await (await browser.newContext()).newPage();
      watchLogins(page);
      await page.goto(BASE + '/');
      await page.fill('input[name=email]', 'requester@acme.test');
      await page.fill('input[name=password]', 'wrong');
      await page.click('button[type=submit]');
      await page.waitForSelector('#message p.error');
      const msg = await page.textContent('#message');
      check('login: password never appears in the URL', !page.url().includes('wrong'), page.url());
      check('login: wrong password shows generic error', msg.includes('Invalid email or password'), JSON.stringify(msg.trim()));
    }

    const requester = await session(browser, 'requester@acme.test');
    check('login: requester lands on requests page', requester.page.url().endsWith('/requests.html'));
    await requester.page.waitForSelector('header strong');
    const header = await requester.page.textContent('header');
    check('header shows org, threshold, user', header.includes('Acme Retail') && header.includes('Riley Requester'), header.replace(/\s+/g, ' ').trim());
    check('header renders no stray "null"', !header.includes('null'));

    // Create an over-threshold request whose description is an HTML/script injection attempt.
    await requester.page.selectOption('#create select[name=siteId]', { label: 'Site 12' });
    await requester.page.fill('#create textarea[name=description]', XSS);
    await requester.page.fill('#create input[name=estimatedCost]', '15000');
    await requester.page.click('#create button[type=submit]');
    await requester.page.waitForURL('**/request.html?id=*');
    const requestUrl = requester.page.url();

    const h1 = await requester.page.textContent('h1');
    const xssRan = await requester.page.evaluate(() => window.__xss === 1);
    const injectedImg = await requester.page.locator('h1 img').count();
    check('XSS: description rendered as text, not HTML', h1 === XSS && injectedImg === 0 && !xssRan, `h1=${JSON.stringify(h1)} img=${injectedImg} ran=${xssRan}`);
    check('detail: status Pending approval', (await requester.page.textContent('dd.status')) === 'Pending Approval');
    check('detail: requester sees no Approve button', (await requester.page.locator('button', { hasText: 'Approve' }).count()) === 0);
    check('detail: requester told why they cannot decide', (await requester.page.textContent('.pending')).includes("can't decide"));

    // Requester edits the pending request with a reason (supersedes the pending revision).
    await requester.page.fill('section:has(h2:has-text("Edit")) input[name=estimatedCost]', '16000');
    await requester.page.fill('section:has(h2:has-text("Edit")) input[name=reason]', 'Vendor quote updated');
    await requester.page.click('button:has-text("Save changes")');
    await requester.page.waitForSelector('p.ok:has-text("Saved.")');
    check('edit: the reason is shown in the Revisions table',
      (await requester.page.textContent('section:has(h2:has-text("Revisions")) tbody')).includes('Vendor quote updated'));

    // Approver approves.
    const approver = await session(browser, 'approver1@acme.test');
    await approver.page.goto(requestUrl);
    await approver.page.fill('.pending input[name=comment]', 'Looks right');
    await approver.page.click('button:has-text("Approve")');
    await approver.page.waitForSelector('p.ok');
    check('approve: status becomes Approved', (await approver.page.textContent('dd.status')) === 'Approved');
    check('approve: the approver comment is shown as the decision note',
      (await approver.page.textContent('section:has(h2:has-text("Revisions")) tbody')).includes('Looks right'));

    // Requester records the actual cost (≥ threshold → needs approval again).
    await requester.page.reload();
    await requester.page.fill('input[name=actualCost]', '12000');
    await requester.page.click('button:has-text("Record actual cost")');
    await requester.page.waitForSelector('p.ok');
    check('complete: actual ≥ threshold goes back to Pending approval', (await requester.page.textContent('dd.status')) === 'Pending Approval');

    // Approver rejects without a reason (server says 400), then approves.
    await approver.page.reload();
    await approver.page.click('button:has-text("Reject")');
    await approver.page.waitForSelector('p.error');
    const rejectError = await approver.page.textContent('p.error');
    check('reject without reason: server validation error shown', rejectError.startsWith('400'), JSON.stringify(rejectError.split('\n')[0]));
    await approver.page.click('button:has-text("Approve")');
    await approver.page.waitForSelector('p.ok');
    check('approve actual: status Completed', (await approver.page.textContent('dd.status')) === 'Completed');

    const auditRows = await approver.page.locator('section:has(h2:has-text("Audit trail")) tbody tr').count();
    const auditText = await approver.page.textContent('section:has(h2:has-text("Audit trail")) tbody');
    // Raised, Submitted, Edited, Superseded, Submitted, Approved, ActualCostSubmitted, Submitted, Approved
    check('audit trail lists every step', auditRows === 9 && auditText.includes('Alex Approver') && auditText.includes('Riley Requester')
      && auditText.includes('Vendor quote updated'), `rows=${auditRows}`);

    // Other tenant: Globex approver opens the Acme request URL.
    const globex = await session(browser, 'approver1@globex.test');
    await globex.page.goto(requestUrl);
    await globex.page.waitForSelector('#message p.error');
    const crossMsg = await globex.page.textContent('#message');
    check('cross-tenant: Globex user gets 404 for Acme request', crossMsg.startsWith('404'), JSON.stringify(crossMsg.trim()));
    await globex.page.goto(BASE + '/requests.html');
    await globex.page.waitForSelector('#requests tbody');
    check('cross-tenant: Acme request absent from Globex list', !(await globex.page.textContent('#requests')).includes('Fix chiller'));

    // Spend report: visible to approvers (reports.spend), not to requesters.
    await approver.page.click('header a:has-text("Spend report")');
    await approver.page.waitForSelector('#report tfoot');
    const reportText = await approver.page.textContent('#report');
    check('report: approver sees per-site table with a total row',
      reportText.includes('Site 12') && reportText.includes('Downtown Store') && reportText.includes('Total'));
    check('report: requester has no Spend report link',
      (await requester.page.locator('header a', { hasText: 'Spend report' }).count()) === 0);

    // Admin panel: hidden from non-admins, refused if opened directly.
    check('admin: requester has no Admin link', (await requester.page.locator('header a', { hasText: 'Admin' }).count()) === 0);
    await requester.page.goto(BASE + '/admin.html');
    await requester.page.waitForSelector('#message p.error');
    check('admin: requester opening admin.html sees no admin sections',
      (await requester.page.locator('#users-section:not([hidden])').count()) === 0);

    const admin = await session(browser, 'admin@acme.test');
    await admin.page.click('header a:has-text("Admin")');
    await admin.page.waitForSelector('#users tbody tr');
    const originalThreshold = await admin.page.textContent('#threshold-current');
    await admin.page.fill('#threshold input[name=amount]', '12345');
    await admin.page.fill('#threshold input[name=reason]', 'Browser smoke test');
    await admin.page.click('#threshold button[type=submit]');
    await admin.page.waitForSelector('#message p.ok');
    check('admin: threshold change shows in header', (await admin.page.textContent('header')).includes('12,345.00'));

    const email = `smoke-${Date.now()}@acme.test`;
    await admin.page.fill('#create-user input[name=displayName]', 'Smoke Tester');
    await admin.page.fill('#create-user input[name=email]', email);
    await admin.page.fill('#create-user input[name=password]', 'Smoke-Test-Password-1');
    await admin.page.selectOption('#create-user select[name=roleId]', { label: 'Requester' });
    await admin.page.click('#create-user button[type=submit]');
    await admin.page.waitForSelector('#message p.ok:has-text("User added.")');
    await admin.page.waitForSelector('#users td:has-text("Smoke Tester")');
    check('admin: created user listed with creator', (await admin.page.textContent('#users')).includes('Smoke Tester'));
    check('admin: audit log shows threshold change and user creation',
      (await admin.page.textContent('#audit')).includes('Threshold Changed') && (await admin.page.textContent('#audit')).includes('User Created'));

    {
      const page = await (await browser.newContext()).newPage();
      await page.goto(BASE + '/');
      await page.fill('input[name=email]', email);
      await page.fill('input[name=password]', 'Smoke-Test-Password-1');
      await page.click('button[type=submit]');
      await page.waitForURL('**/requests.html');
      await page.waitForSelector('header strong');
      check('admin: new user signs in to the admin\'s organization', (await page.textContent('header')).includes('Acme Retail'));
    }

    // Restore the dev threshold so repeated runs leave the seeded data as it was.
    const original = originalThreshold.replace(/[^0-9.]/g, '');
    await admin.page.fill('#threshold input[name=amount]', original);
    await admin.page.fill('#threshold input[name=reason]', 'Restore after browser smoke test');
    await admin.page.click('#threshold button[type=submit]');
    await admin.page.waitForSelector('#message p.ok');

    // Logout ends the session.
    await requester.page.click('button:has-text("Log out")');
    await requester.page.waitForURL('**/index.html');
    await requester.page.goto(BASE + '/requests.html');
    await requester.page.waitForURL('**/index.html');
    check('logout: protected page redirects to sign-in', requester.page.url().endsWith('/index.html'));

    check('password payloads (logins + admin create user) carry only the encrypted password',
      passwordBodies.length >= 6 && passwordBodies.every(b => b.includes('encryptedPassword') && !b.includes('"password"')
        && !b.includes(PASSWORD) && !b.includes('Smoke-Test-Password-1') && !b.includes('wrong"')),
      `${passwordBodies.length} request(s) inspected`);

    const allProblems = [...requester.problems, ...approver.problems, ...globex.problems, ...admin.problems]
      .filter(p => !p.includes('status of 4')); // expected 4xx responses logged by the browser
    check('no JS errors or CSP violations', allProblems.length === 0, allProblems.join(' | '));
  } finally {
    await browser.close();
  }
  const failed = results.filter(r => !r.ok).length;
  console.log(`\n${results.length - failed}/${results.length} passed`);
  process.exit(failed ? 1 : 0);
})().catch(e => { console.error(e); process.exit(2); });
