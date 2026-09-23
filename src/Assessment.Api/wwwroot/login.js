'use strict';

(() => {
  const form = document.getElementById('login');
  const message = document.getElementById('message');

  // Attach the handler FIRST, synchronously. If it were attached after an awaited call, a quick submit
  // would fall through to a native form submission (method="post" to self, so at least credentials
  // never end up in a URL, history or logs, but the login would silently not happen).
  form.addEventListener('submit', async event => {
    event.preventDefault();
    const data = new FormData(form);
    const result = await App.api('POST', '/api/auth/login', {
      email: data.get('email'),
      password: data.get('password'),
    });
    if (result.ok) location.href = '/requests.html';
    else App.showMessage(message, App.problemText(result));
  });

  // Already signed in? Skip the form. (A 401 here is expected, so plain fetch, not App.api's redirect.)
  fetch('/api/me', { credentials: 'same-origin' }).then(me => { if (me.ok) location.href = '/requests.html'; });
})();
