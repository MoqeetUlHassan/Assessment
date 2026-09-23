// Command-line login for curl users. The API only accepts an encrypted password field, so this does what
// the browser does: fetch a challenge, RSA-OAEP-SHA256-encrypt (nonce || password), post, and save the
// session cookie to a curl cookie jar.
//
// Usage: node scripts/login.mjs <email> <password> [jar-file=jar.txt] [base-url=http://localhost:5183]
// Then:  curl -b jar.txt http://localhost:5183/api/me
import { webcrypto } from 'node:crypto';
import { writeFileSync } from 'node:fs';

const [email, password, jar = 'jar.txt', base = 'http://localhost:5183'] = process.argv.slice(2);
if (!email || !password) {
  console.error('Usage: node scripts/login.mjs <email> <password> [jar-file] [base-url]');
  process.exit(2);
}

const challenge = await (await fetch(`${base}/api/auth/login-challenge`)).json();
const key = await webcrypto.subtle.importKey('spki', Buffer.from(challenge.publicKey, 'base64'),
  { name: 'RSA-OAEP', hash: 'SHA-256' }, false, ['encrypt']);
const plain = Buffer.concat([Buffer.from(challenge.nonce, 'base64'), Buffer.from(password, 'utf8')]);
const encryptedPassword = Buffer.from(await webcrypto.subtle.encrypt({ name: 'RSA-OAEP' }, key, plain)).toString('base64');

const response = await fetch(`${base}/api/auth/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email, keyId: challenge.keyId, encryptedPassword }),
});
if (!response.ok) {
  console.error(`Login failed: ${response.status} ${await response.text()}`);
  process.exit(1);
}

const [name, ...rest] = response.headers.get('set-cookie').split(';')[0].split('=');
const host = new URL(base).hostname;
writeFileSync(jar, `# Netscape HTTP Cookie File\n#HttpOnly_${host}\tFALSE\t/\tFALSE\t0\t${name}\t${rest.join('=')}\n`);
const me = await response.json();
console.log(`Signed in as ${me.displayName} (${me.role}, ${me.organization.name}); session saved to ${jar}`);
