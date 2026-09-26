'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const net = require('node:net');
const { once } = require('node:events');
const { spawn } = require('node:child_process');
const outboundKinds = new Set(['connect', 'connect-name', 'datagram', 'resolve', 'http', 'unc-file']);

exports.launch = (python, executable, argv, options, report) => spawn(python,
  [path.join(__dirname, 'trace.py'), '--watch-stdin', '--report', report, '--', executable, ...argv],
  { ...options, stdio: ['pipe', 'pipe', 'pipe'] });

exports.check = async reportPath => {
  const report = JSON.parse(await fs.readFile(reportPath, 'utf8'));
  assert.equal(report.exitCode, 0, 'Traced server must shut down cleanly.');
  assert.deepEqual(report.errors, [], 'Incomplete native observation cannot pass.');
  assert.deepEqual(report.forcedTerminations, [], 'Server shutdown must also finish its native children.');
  for (const process of report.processes) assert.ok(report.events.some(event => event.pid === process.pid && event.kind === 'ready'), 'Every process must be observed from startup.');
  const attempts = report.events.filter(event => outboundKinds.has(event.kind));
  assert.deepEqual(attempts, [], 'Unexpected native network/UNC attempts, including unsuccessful ones.');
  assert.ok(report.events.some(event => event.kind === 'incoming-accept'), 'Server must actually accept requests under observation.');
  const helpers = report.processes.filter(process => /ff(?:mpeg|probe)\.exe$/i.test(process.executable));
  assert.ok(helpers.length > 0, 'Native helpers must be observed, not just the managed server.');
  for (const helper of helpers) assert.ok(report.events.some(event => event.pid === helper.pid && event.kind === 'installed' && event.api === 'ws2_32.dll!connect'), 'Native helper network hooks must be active.');
  return { report: reportPath, processCount: report.processes.length, nativeHelperCount: helpers.length, attemptedOutbound: attempts.length };
};

exports.legacyEndpoints = async (base, token, itemId) => {
  const headers = { Authorization: `MediaBrowser Token="${token}"`, 'Content-Type': 'application/json', Host: 'never-resolve.invalid' };
  const call = async (url, method = 'GET', body, status = 200) => {
    const response = await fetch(base + '/' + url, { method, headers, signal: AbortSignal.timeout(10000), body: body === undefined ? undefined : JSON.stringify(body) });
    assert.equal(response.status, status, `${method} ${url} must remain offline-compatible.`);
    return response.status === 204 ? null : response.json().catch(() => null);
  };
  for (const url of ['Plugins', 'Packages', 'Repositories', `Items/${itemId}/RemoteSearch/Subtitles/en`])
    assert.deepEqual(await call(url), []);
  assert.deepEqual(await call('Items/RemoteSearch/Movie', 'POST', { SearchInfo: { Name: 'Never fetch', ProviderIds: { Imdb: 'tt0000000' } } }), []);
  assert.deepEqual((await call('LiveTv/Channels')).Items, []);
  for (const url of ['Repositories', 'Packages/Installed/Test', `Items/${itemId}/RemoteImages/Download?imageUrl=https%3A%2F%2Fnever-resolve.invalid%2Fimage.jpg&type=Primary`])
    await call(url, 'POST', [], 400);
  await call('LiveTv/TunerHosts', 'POST', { Url: 'http://never-resolve.invalid/tuner', Type: 'hdhomerun' }, 400);
  await call('System/Configuration', 'POST', { EnableAutoUpdate: true, PluginRepositories: [{ Url: 'http://never-resolve.invalid/plugins', Enabled: true }] }, 400);
  await call('Library/Refresh', 'POST', undefined, 204);
  await call(`Items/${itemId}/Refresh?recursive=true&replaceAllMetadata=true&replaceAllImages=true`, 'POST', undefined, 204);
  const range = await fetch(`${base}/Audio/${itemId}/stream?static=true`, { headers: { ...headers, Range: 'bytes=0-63' }, signal: AbortSignal.timeout(10000) });
  assert.equal(range.status, 206);
  assert.equal((await range.arrayBuffer()).byteLength, 64);
  const group = (await call('UserViews')).Items.find(item => item.Name === 'Test Media');
  const entries = (await call(`Items?parentId=${group.Id}`)).Items.filter(item => item.Name.startsWith('__offline-audit-'));
  assert.equal(entries.length, 3);
  for (const item of entries) {
    const playback = await call(`Items/${item.Id}/PlaybackInfo`);
    assert.deepEqual(playback.MediaSources, [], 'Disguised playlists must not become native inputs.');
    assert.equal(playback.ErrorCode, 'NoCompatibleStream');
  }
};

exports.fixtureTrap = async media => {
  let attempts = 0;
  const listener = net.createServer(socket => { attempts++; socket.destroy(); });
  listener.listen(0, '127.0.0.1'); await once(listener, 'listening');
  const target = `http://127.0.0.1:${listener.address().port}/never-fetch`;
  try {
    const playlist = `#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\n${target}\n#EXT-X-ENDLIST\n`;
    await fs.writeFile(path.join(media, '__offline-audit-hls.mp4'), playlist);
    await fs.writeFile(path.join(media, '__offline-audit-hls.m4b'), playlist);
    await fs.writeFile(path.join(media, '__offline-audit-concat.mp4'), `ffconcat version 1.0\nfile '${target}'\n`);
    const nfo = path.join(media, 'Books/Chapter 01.nfo');
    await fs.writeFile(nfo, (await fs.readFile(nfo, 'utf8')).replace('</movie>', `<thumb>${target}/poster.png</thumb><trailer>${target}/trailer.mp4</trailer></movie>`));
    return async () => { await new Promise(resolve => listener.close(resolve)); assert.equal(attempts, 0, 'Native/sidecar loopback trap must receive no connection attempts.'); };
  } catch (error) { listener.close(); throw error; }
};
