'use strict';
// Temporary loopback-only diagnostic relay; never records authentication values.
const http = require('node:http');
const { NativeUi } = require('./ui.cjs');
(async () => {
  const ui = new NativeUi(process.argv[2]);
  const { base } = await ui.command('status');
  const server = http.createServer((request, response) => {
    const url = new URL(request.url, base);
    if (url.origin !== base) { response.writeHead(403); response.end(); return; }
    const upstream = http.request(url, { method: request.method, headers: request.headers }, incoming => {
      response.writeHead(incoming.statusCode, incoming.headers); incoming.pipe(response);
      for (const key of [...url.searchParams.keys()]) if (/token|key|secret|password/i.test(key)) url.searchParams.set(key, 'REDACTED');
      console.log(JSON.stringify({ method: request.method, path: url.pathname + url.search, status: incoming.statusCode, headers: Object.keys(request.headers), authScheme: request.headers.authorization?.split(' ')[0], authHasToken: /token=/i.test(request.headers.authorization || ''), authHasLegacyToken: /token=/i.test(request.headers['x-emby-authorization'] || '') }));
    });
    upstream.on('error', error => { response.writeHead(502); response.end(); console.error(error.message); });
    request.pipe(upstream);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const guestPort = new URL(base).port;
  await ui.adb('reverse', `tcp:${guestPort}`, `tcp:${server.address().port}`);
  console.log('Owned relay ready');
  setTimeout(async () => { await ui.adb('reverse', `tcp:${guestPort}`, `tcp:${guestPort}`).catch(() => {}); server.close(); }, 10 * 60 * 1000);
})().catch(error => { console.error(error); process.exitCode = 1; });
