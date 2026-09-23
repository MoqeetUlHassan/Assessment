'use strict';

(() => {
  const form = document.getElementById('login');
  const message = document.getElementById('message');

  // RSA-OAEP-SHA256 with a 3072-bit key fits 318 bytes of plaintext; the 16-byte nonce leaves 302 for the password.
  const MAX_PASSWORD_BYTES = 302;
  const fromBase64 = s => Uint8Array.from(atob(s), c => c.charCodeAt(0));
  const toBase64 = bytes => btoa(String.fromCharCode(...bytes));

  // The password never goes into the request payload in plain text: it is encrypted with the server's public
  // key together with a single-use nonce, so a captured payload neither reveals nor replays the password.
  async function encryptPassword(password) {
    if (!window.crypto || !crypto.subtle) {
      throw new Error('Password encryption needs a secure page (HTTPS or localhost).');
    }
    const challenge = await App.api('GET', '/api/auth/login-challenge');
    if (!challenge.ok) throw new Error(App.problemText(challenge));
    const { keyId, publicKey, nonce } = challenge.data;

    const passwordBytes = new TextEncoder().encode(password);
    if (passwordBytes.length > MAX_PASSWORD_BYTES) throw new Error('Password is too long.');

    const key = await crypto.subtle.importKey('spki', fromBase64(publicKey), { name: 'RSA-OAEP', hash: 'SHA-256' }, false, ['encrypt']);
    const nonceBytes = fromBase64(nonce);
    const plain = new Uint8Array(nonceBytes.length + passwordBytes.length);
    plain.set(nonceBytes);
    plain.set(passwordBytes, nonceBytes.length);
    const cipher = new Uint8Array(await crypto.subtle.encrypt({ name: 'RSA-OAEP' }, key, plain));
    plain.fill(0);
    passwordBytes.fill(0);
    return { keyId, encryptedPassword: toBase64(cipher) };
  }

  // Attach the handler FIRST, synchronously. If it were attached after an awaited call, a quick submit
  // would fall through to a native form submission (method="post" to self, so at least credentials
  // never end up in a URL, history or logs, but the login would silently not happen).
  form.addEventListener('submit', async event => {
    event.preventDefault();
    const data = new FormData(form);
    let encrypted;
    try {
      encrypted = await encryptPassword(String(data.get('password')));
    } catch (e) {
      App.showMessage(message, e.message);
      return;
    }
    const result = await App.api('POST', '/api/auth/login', { email: data.get('email'), ...encrypted });
    if (result.ok) location.href = '/requests.html';
    else App.showMessage(message, App.problemText(result));
  });

  // Already signed in? Skip the form. (A 401 here is expected, so plain fetch, not App.api's redirect.)
  fetch('/api/me', { credentials: 'same-origin' }).then(me => { if (me.ok) location.href = '/requests.html'; });
})();
