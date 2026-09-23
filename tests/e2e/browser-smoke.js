// Browser smoke test of the static client against a RUNNING app (dotnet run), using an installed Edge or Chrome.
// Run: cd tests/e2e && npm install && node browser-smoke.js   (BROWSER_CHANNEL=chrome to use Chrome)
const { chromium } = require('playwright-core');
const BASE = process.env.BASE_URL || 'http://localhost:5183';
const PASSWORD = 'ChangeMe-Dev-2026!';
const XSS = '<img src=x onerror="window.__xss=1">Fix chiller';

const results = [];
const check = (name, ok, detail = '') => { results.push({ name, ok }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  ' + detail : ''}`); };

async function session(browser, email) {
  const context = await browser.newContext();
  const page = await context.newPage();
  const problems = [];
  page.on('console', m => { if (m.type() === 'error') problems.push(m.text()); });
  page.on('pageerror', e => problems.push(e.message));
  await page.goto(BASE + '/');
  await page.fill('input[name=email]', email);
  await page.fill('input[name=password]', PASSWORD);
  await page.click('button[type=submit]');
  await page.waitForURL('**/requests.html');
  return { page, problems };
}

(async () => {
  const browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
  try {
    // Wrong password shows the generic error on the login page.
    {
      const page = await (await browser.newContext()).newPage();
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

    // Approver approves.
    const approver = await session(browser, 'approver1@acme.test');
    await approver.page.goto(requestUrl);
    await approver.page.fill('.pending input[name=comment]', 'Looks right');
    await approver.page.click('button:has-text("Approve")');
    await approver.page.waitForSelector('p.ok');
    check('approve: status becomes Approved', (await approver.page.textContent('dd.status')) === 'Approved');

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
    check('audit trail lists every step', auditRows === 6 && auditText.includes('Alex Approver') && auditText.includes('Riley Requester'), `rows=${auditRows}`);

    // Other tenant: Globex approver opens the Acme request URL.
    const globex = await session(browser, 'approver1@globex.test');
    await globex.page.goto(requestUrl);
    await globex.page.waitForSelector('#message p.error');
    const crossMsg = await globex.page.textContent('#message');
    check('cross-tenant: Globex user gets 404 for Acme request', crossMsg.startsWith('404'), JSON.stringify(crossMsg.trim()));
    await globex.page.goto(BASE + '/requests.html');
    await globex.page.waitForSelector('#requests tbody');
    check('cross-tenant: Acme request absent from Globex list', !(await globex.page.textContent('#requests')).includes('Fix chiller'));

    // Logout ends the session.
    await requester.page.click('button:has-text("Log out")');
    await requester.page.waitForURL('**/index.html');
    await requester.page.goto(BASE + '/requests.html');
    await requester.page.waitForURL('**/index.html');
    check('logout: protected page redirects to sign-in', requester.page.url().endsWith('/index.html'));

    const allProblems = [...requester.problems, ...approver.problems, ...globex.problems]
      .filter(p => !p.includes('status of 4')); // expected 4xx responses logged by the browser
    check('no JS errors or CSP violations', allProblems.length === 0, allProblems.join(' | '));
  } finally {
    await browser.close();
  }
  const failed = results.filter(r => !r.ok).length;
  console.log(`\n${results.length - failed}/${results.length} passed`);
  process.exit(failed ? 1 : 0);
})().catch(e => { console.error(e); process.exit(2); });
