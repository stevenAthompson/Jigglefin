'use strict';

// This test creates its own profile, random loopback port and synthetic media.
// It never reuses the installed server, user credentials or real media roots.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const net = require('node:net');
const crypto = require('node:crypto');
const { spawn, execFile } = require('node:child_process');
const { promisify } = require('node:util');
const { once } = require('node:events');
const { chromium } = require('playwright');
const root = path.resolve(__dirname, '../..');
const packageDirectory = process.env.JIGGLEFIN_TEST_PACKAGE && path.resolve(process.env.JIGGLEFIN_TEST_PACKAGE);
const auditPython = process.env.JIGGLEFIN_AUDIT_PYTHON;
const networkAudit = auditPython && require('../OfflineNetworkAudit/audit.cjs');
const nativeReports = [], nativeResults = [];
const powershell = path.join(process.env.SystemRoot, 'System32/WindowsPowerShell/v1.0/powershell.exe');
const capture = promisify(execFile);
const dotnet = process.env.JIGGLEFIN_TEST_DOTNET || path.join(process.env.LOCALAPPDATA, 'Microsoft/dotnet/dotnet.exe');
const serverDll = process.env.JIGGLEFIN_TEST_SERVER || path.join(root, 'Jellyfin.Server/bin/Debug/net10.0/jellyfin.dll');
const web = packageDirectory ? path.join(packageDirectory, 'jellyfin-web') : path.join(root, 'Jigglefin.Web/dist');
const ffmpeg = packageDirectory ? path.join(packageDirectory, 'ffmpeg.exe') : process.env.JIGGLEFIN_TEST_FFMPEG || path.join(root, 'publish/ffmpeg-prepared-test/ffmpeg.exe');
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const password = 'Temporary-' + crypto.randomBytes(16).toString('hex');
const username = 'FolderTestAdmin';
let fixture, profile, child, base, browser, token, output = '', successful = false, launchTime, closeNativeTrap, auditRootPid;
const failures = [], externalRequests = [], violations = [], requests = [], httpErrors = [];
const redactUrl = value => { const url = new URL(value); for (const key of [...url.searchParams.keys()]) if (['apikey', 'api_key', 'token', 'access_token'].includes(key.toLowerCase())) url.searchParams.set(key, 'REDACTED'); return url.href; };

async function command(exe, args) {
  const process = spawn(exe, args, { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
  let error = ''; process.stderr.on('data', data => { error = (error + data).slice(-4000); });
  const [code] = await once(process, 'exit'); assert.equal(code, 0, error);
}
async function unusedPort() {
  const listener = net.createServer(); listener.listen(0, '127.0.0.1'); await once(listener, 'listening');
  const port = listener.address().port; await new Promise(resolve => listener.close(resolve)); return port;
}
async function api(url, method = 'GET', body, accessToken = token) {
  const response = await fetch(base + '/' + url, { method, headers: { Authorization: `MediaBrowser Client="Isolated test", Device="CLI", DeviceId="live-web-harness", Version="0.1.0"${accessToken ? `, Token="${accessToken}"` : ''}`, 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
  assert.ok(response.ok, `${method} ${url}: ${response.status} ${await response.clone().text()}`);
  return response.status === 204 ? null : response.json();
}
async function startServer() {
  output = '';
  auditRootPid = undefined;
  const args = ['--service', '--nonetchange', '--configdir', path.join(profile, 'config'), '--cachedir', path.join(profile, 'cache'), '--logdir', path.join(profile, 'logs')];
  // Packaged runs use the actual Windows PowerShell 5.1 launcher, from outside the
  // package, with a spaced profile path and no explicit Web/FFmpeg overrides.
  launchTime = new Date().toISOString();
  const executable = packageDirectory ? powershell : dotnet;
  const launchArgs = packageDirectory
    ? ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', path.join(packageDirectory, 'Start-Jigglefin.ps1'), '-DataDir', profile, ...args]
    : [serverDll, '--datadir', profile, ...args, '--webdir', web, '--ffmpeg', ffmpeg];
  const launchOptions = { cwd: fixture, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, DOTNET_ROOT: path.dirname(dotnet), ASPNETCORE_ENVIRONMENT: 'Production' } };
  if (networkAudit) {
    const report = path.join(fixture, `native-network-${nativeReports.length + 1}.json`);
    nativeReports.push(report);
    child = networkAudit.launch(auditPython, executable, launchArgs, launchOptions, report);
  } else child = spawn(executable, launchArgs, launchOptions);
  child.stdout.on('data', data => {
    output = (output + data).slice(-16000);
    const rootMatch = /JIGGLEFIN_NATIVE_TRACE_ROOT:(\d+)/.exec(output);
    if (rootMatch) auditRootPid = Number(rootMatch[1]);
  }); child.stderr.on('data', data => { output = (output + data).slice(-16000); });
  for (let count = 0; count < 120; count++) {
    if (child.exitCode !== null) throw new Error(`Isolated server exited (${child.exitCode}): ${output.slice(-3500)}`);
    let ready = false;
    try { const response = await fetch(base + '/System/Info/Public'); ready = response.ok && output.includes('Core startup complete'); } catch {}
    if (ready) { if (packageDirectory) await verifyPackagedProcess(false); console.log('Isolated server ready:', base); return; }
    await delay(250);
  }
  throw new Error('Isolated server did not become ready: ' + output.slice(-3500));
}
async function verifyPackagedProcess(stop) {
  // Resolve and validate exact child identity afresh before inspecting/killing it.
  if (networkAudit) assert.ok(Number.isSafeInteger(auditRootPid), 'Native tracer must identify its newly created process.');
  const tracedParent = networkAudit ? `$launchers = @(Get-CimInstance Win32_Process -Filter "ProcessId = ${auditRootPid}" | Where-Object { $_.ExecutablePath -eq '${powershell.replaceAll("'", "''")}' -and $_.CommandLine.Contains($env:JIGGLEFIN_TEST_PROFILE) -and $_.CreationDate.ToUniversalTime() -ge [DateTime]::Parse($env:JIGGLEFIN_TEST_LAUNCH).ToUniversalTime() }); if ($launchers.Count -ne 1) { throw 'Could not resolve isolated traced launcher' }; $launchParent = $launchers[0].ProcessId;` : '';
  const script = `$ErrorActionPreference = 'Stop'; $launchParent = [int]$env:JIGGLEFIN_TEST_PARENT; ${tracedParent} $matched = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $launchParent" | Where-Object { $_.ExecutablePath -eq $env:JIGGLEFIN_TEST_BINARY -and $_.CommandLine.Contains($env:JIGGLEFIN_TEST_PROFILE) -and $_.CreationDate.ToUniversalTime() -ge [DateTime]::Parse($env:JIGGLEFIN_TEST_LAUNCH).ToUniversalTime() }); if ($matched.Count -ne 1) { throw 'Could not resolve the exact isolated launcher child' }; if ($env:JIGGLEFIN_TEST_STOP -eq '1') { Stop-Process -Id $matched[0].ProcessId -Force } else { $listeners = @(Get-NetTCPConnection -LocalPort $env:JIGGLEFIN_TEST_PORT -State Listen); if (-not $listeners.Count -or @($listeners | Where-Object OwningProcess -NE $matched[0].ProcessId).Count) { throw 'Port not exclusively owned by isolated server' } }`;
  await capture(powershell, ['-NoProfile', '-NonInteractive', '-Command', script], { windowsHide: true, timeout: 15000, env: { ...process.env, JIGGLEFIN_TEST_PARENT: String(child.pid), JIGGLEFIN_TEST_BINARY: path.join(packageDirectory, 'jellyfin.exe'), JIGGLEFIN_TEST_PROFILE: profile, JIGGLEFIN_TEST_LAUNCH: launchTime, JIGGLEFIN_TEST_STOP: stop ? '1' : '0', JIGGLEFIN_TEST_PORT: new URL(base).port } });
}
async function packageCliChecks() {
  for (const launcher of [false, true]) for (const option of ['--help', '--version', '--jigglefin-invalid-option']) {
    const invalid = option.includes('invalid');
    const exe = launcher ? powershell : path.join(packageDirectory, 'jellyfin.exe');
    const args = launcher ? ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', path.join(packageDirectory, 'Start-Jigglefin.ps1'), '-DataDir', profile, ...(!invalid ? ['-FfmpegPath', path.join(fixture, 'missing-ffmpeg.exe')] : []), option] : [option, '--datadir', profile];
    const result = await capture(exe, args, { cwd: fixture, windowsHide: true, timeout: 10000 }).then(value => ({ ...value, code: 0 }), error => error);
    const text = (result.stdout || '') + (result.stderr || '');
    assert.ok(!result.killed, text);
    assert.equal(result.code, invalid ? 1 : 0, text);
    assert.match(text, invalid ? /Option 'jigglefin-invalid-option' is unknown/ : option === '--help' ? /--datadir/ : /Jellyfin.Server \d+\.\d+\.\d+/);
  }
  await assert.rejects(fs.stat(profile), { code: 'ENOENT' });
  console.log('Packaged executable/launcher CLI checks passed without creating a profile.');
}
async function stopServer() {
  if (!child || child.exitCode !== null) return;
  const process = child; const exited = once(process, 'exit');
  if (token) await api('System/Shutdown', 'POST').catch(() => {});
  await Promise.race([exited, delay(10000)]);
  if (process.exitCode === null) { if (packageDirectory) await verifyPackagedProcess(true); else process.kill(); await exited; }
  assert.equal(process.exitCode, 0, 'Isolated server/launcher must shut down cleanly.');
  child = null;
  if (networkAudit) nativeResults.push(await networkAudit.check(nativeReports.at(-1)));
}
async function snapshot(directory) {
  const result = {};
  async function walk(current) {
    for (const entry of await fs.readdir(current, { withFileTypes: true })) {
      const file = path.join(current, entry.name);
      if (entry.isDirectory()) await walk(file);
      else { const stat = await fs.stat(file); result[path.relative(directory, file)] = { hash: crypto.createHash('sha256').update(await fs.readFile(file)).digest('hex'), mtime: stat.mtimeMs }; }
    }
  }
  await walk(directory); return result;
}
async function newPage(context) {
  const page = await context.newPage(); page.setDefaultTimeout(15000);
  page.on('pageerror', error => failures.push(error.message));
  page.on('response', response => { if (response.status() >= 400) httpErrors.push(`${response.status()} ${redactUrl(response.url())}`); });
  page.on('request', request => { requests.push(request.url()); if (!request.url().startsWith(base + '/')) externalRequests.push(request.url()); });
  await page.exposeFunction('reportOfflineViolation', event => violations.push(event));
  await page.addInitScript(() => document.addEventListener('securitypolicyviolation', event => window.reportOfflineViolation({ blocked: event.blockedURI, directive: event.violatedDirective })));
  // Record attempted requests before refusing them; a blocked attempt fails the test.
  await page.route('**/*', route => route.request().url().startsWith(base + '/') ? route.continue() : route.abort());
  page.on('dialog', dialog => dialog.accept());
  return page;
}
async function login(page, name = username, accountPassword = password) {
  await page.goto(base + '/web/'); await page.getByLabel('Username', { exact: true }).fill(name); await page.getByLabel('Password', { exact: true }).fill(accountPassword);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click(); await page.getByRole('heading', { name: 'Your folders', exact: true }).waitFor();
}
async function playing(page, minimum = .1) {
  await page.waitForFunction(min => { const video = document.querySelector('#player'); return video && video.currentTime > min && !video.paused && video.readyState >= 3 && !video.error; }, minimum, { timeout: 30000 });
}
async function stopped(page) {
  await page.locator('#stop-button').click();
  await page.waitForFunction(() => document.querySelector('#player-panel').hidden && document.querySelector('#stop-button').getAttribute('aria-busy') !== 'true');
}
async function main() {
  fixture = await fs.mkdtemp(path.join(os.tmpdir(), 'jigglefin-folder-web-'));
  profile = path.join(fixture, 'Server profile');
  console.log('Isolated fixture:', fixture);
  if (packageDirectory) await packageCliChecks();
  const media = path.join(fixture, 'Media'), books = path.join(media, 'Books'), films = path.join(media, 'Films');
  await fs.mkdir(books, { recursive: true }); await fs.mkdir(films, { recursive: true });
  await fs.mkdir(path.join(media, 'Empty')); await fs.mkdir(path.join(media, 'Unvisited/Deep'), { recursive: true });
  for (let index = 0; index < 50; index++) await fs.writeFile(path.join(media, 'Unvisited/Deep', index + '.mp3'), 'never opened by the test UI');
  await command(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=44100', '-t', '120', '-c:a', 'aac', '-b:a', '64000', path.join(books, 'Chapter 01.m4b')]);
  await command(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-stream_loop', '-1', '-i', path.join(root, 'tests/Jellyfin.Server.Integration.Tests/Test Data/JigglefinSample.mp4'), '-t', '20', '-c', 'copy', path.join(films, 'Film.mp4')]);
  await fs.writeFile(path.join(books, 'Chapter 01.nfo'), '<movie><title>The local audiobook</title><plot>&lt;img src="https://example.invalid/tracker.png"&gt; stays plain text.</plot></movie>');
  await fs.writeFile(path.join(films, 'Film.nfo'), '<movie><title>The local film</title><plot>Local film description.</plot></movie>');
  await fs.writeFile(path.join(films, 'Film.en.srt'), '1\n00:00:00,000 --> 00:00:15,000\nOffline subtitle\n');
  await fs.copyFile(path.join(root, 'branding/jigglefin-256.png'), path.join(books, 'folder.png'));
  const tracks = path.join(media, 'Queue'); await fs.mkdir(tracks);
  for (const [index, name] of ['A', 'B', 'C'].entries()) {
    const file = path.join(tracks, name + '.m4b');
    await command(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-i', path.join(books, 'Chapter 01.m4b'), '-t', String(12 + index * 4), '-c', 'copy', file]);
    await fs.utimes(file, new Date(`202${index}-01-01T00:00:00Z`), new Date(`202${index}-01-01T00:00:00Z`));
  }
  await fs.writeFile(path.join(tracks, 'Order.m3u'), '#EXTM3U\nB.m4b\nA.m4b\nB.m4b\nhttps://never-resolve.invalid/remote.mp3\n');
  await fs.writeFile(path.join(tracks, 'Second.pls'), '[playlist]\nFile2=C.m4b\nFile1=A.m4b\nNumberOfEntries=2\n');
  await fs.writeFile(path.join(media, 'notes.txt'), 'Visible but not media.');
  if (networkAudit) closeNativeTrap = await networkAudit.fixtureTrap(media);
  const before = await snapshot(media);
  const port = await unusedPort(); base = `http://127.0.0.1:${port}`;
  await fs.mkdir(path.join(profile, 'config'), { recursive: true });
  await fs.writeFile(path.join(profile, 'config/network.xml'), `<?xml version="1.0"?><NetworkConfiguration><InternalHttpPort>${port}</InternalHttpPort><PublicHttpPort>${port}</PublicHttpPort><AutoDiscovery>false</AutoDiscovery><EnableIPv6>false</EnableIPv6><EnableRemoteAccess>false</EnableRemoteAccess><LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses>${networkAudit ? '<KnownProxies><string>never-resolve.invalid</string></KnownProxies>' : ''}</NetworkConfiguration>`);
  if (networkAudit) {
    // Old opt-in settings must not reactivate removed network features.
    await fs.writeFile(path.join(profile, 'config/system.xml'), '<?xml version="1.0"?><ServerConfiguration><EnableAutoUpdate>true</EnableAutoUpdate><PluginRepositories><RepositoryInfo><Name>Never contact</Name><Url>http://never-resolve.invalid/plugins.json</Url><Enabled>true</Enabled></RepositoryInfo></PluginRepositories></ServerConfiguration>');
    await fs.writeFile(path.join(profile, 'config/livetv.xml'), '<?xml version="1.0"?><LiveTvOptions><TunerHosts><TunerHostInfo><Id>offline-audit</Id><Url>http://never-resolve.invalid/tuner</Url><Type>hdhomerun</Type></TunerHostInfo></TunerHosts></LiveTvOptions>');
    // Old logging destinations are data, not permission to write into media.
    await fs.writeFile(path.join(profile, 'config/logging.json'), JSON.stringify({ Serilog: { WriteTo: [{ Name: 'File', Args: { path: path.join(media, 'must-not-be-a-server-log.txt') } }] } }));
  }
  await startServer();
  browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  let page = await newPage(context);
  const response = await page.goto(base + '/web/');
  assert.match(response.headers()['content-security-policy'], /connect-src 'self'/);
  await page.getByRole('heading', { name: 'Make yourself at home.' }).waitFor();
  await page.getByLabel('Username', { exact: true }).fill(username); await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Create local server' }).click();
  await page.getByRole('heading', { name: 'Choose your folders', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Browse', exact: true }).click();
  const drive = path.parse(media).root;
  await page.getByRole('button', { name: `Browse ${drive}`, exact: true }).click();
  for (const part of media.slice(drive.length).split(path.sep)) await page.getByRole('button', { name: `Browse ${part}`, exact: true }).click();
  await page.locator('#picker-up').click();
  await page.getByRole('button', { name: 'Browse Media', exact: true }).click();
  await page.getByRole('button', { name: 'Browse Books', exact: true }).waitFor();
  await page.screenshot({ path: path.join(fixture, 'setup-picker.png'), fullPage: true });
  await page.locator('#picker-select').click();
  assert.equal(await page.locator('#root-paths').inputValue(), media, 'Setup picker should choose a path without typing it.');
  await page.getByLabel('Folder group name').fill('Test Media');
  const requestsBeforeMount = requests.length;
  await page.getByRole('button', { name: 'Add folder group' }).click();
  await page.getByText('Folder group added. Nothing was scanned or written to the media folders.').waitFor();
  assert.ok(!requests.slice(requestsBeforeMount).some(url => /PlaybackInfo|\/Items\//.test(url)), 'Mounting must not select or probe media.');
  await page.getByRole('button', { name: 'Done', exact: true }).click();
  await page.getByRole('heading', { name: 'Your folders', exact: true }).waitFor();
  token = (await api('Users/AuthenticateByName', 'POST', { Username: username, Pw: password }, null)).AccessToken;
  console.log('Setup with server folder picker passed without typing a path or scanning media.');
  // Query only the local share table to reject aliases of private storage.
  // In the native audit even an attempted lookup/open of the .invalid host fails.
  for (const host of ['localhost', 'jigglefin-share-alias.invalid']) for (const suffix of ['', '\\MISSING~1']) {
    const alias = `\\\\${host}\\${profile[0]}$${profile.slice(2)}${suffix}`;
    const rejected = await fetch(base + '/Library/VirtualFolders?name=Forbidden%20share%20alias', {
      signal: AbortSignal.timeout(15000),
      method: 'POST', headers: { Authorization: `MediaBrowser Client="Isolated test", Device="CLI", DeviceId="live-web-harness", Version="0.1.0", Token="${token}"`, 'Content-Type': 'application/json' },
      body: JSON.stringify({ LibraryOptions: { PathInfos: [{ Path: alias }] } })
    });
    assert.equal(rejected.status, 400, 'UNC aliases must not expose private profile storage');
    assert.match(await rejected.text(), /overlap/i);
  }
  assert.ok(!(await api('UserViews')).Items.some(item => item.Name === 'Forbidden share alias'));
  console.log('Private local-share aliases rejected without contacting the supplied host.');
  // Retain this unvisited, unreachable root through the restart below. Setup,
  // home views and startup must not trigger 8.3 expansion or contact its share.
  await api('Library/VirtualFolders?name=Unvisited%20short%20root', 'POST', {
    LibraryOptions: { PathInfos: [{ Path: '\\\\jigglefin-short-root.invalid\\NoSuchShare\\MEDIA~1' }] }
  });
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  const availableMount = path.join(fixture, 'Available location'), disconnectedMount = path.join(fixture, 'Disconnected location');
  await fs.mkdir(availableMount); await fs.mkdir(disconnectedMount);
  await fs.writeFile(path.join(availableMount, 'Available.txt'), 'Local synthetic fixture');
  await page.getByLabel('Folder group name').fill('Mount recovery');
  await page.getByLabel('Folder locations, one per line').fill(availableMount + '\n' + disconnectedMount);
  await page.getByRole('button', { name: 'Add folder group' }).click();
  await page.getByText('Folder group added. Nothing was scanned or written to the media folders.').waitFor();
  await fs.rmdir(disconnectedMount); // Only this test's own empty directory.
  await page.getByRole('link', { name: 'Mount recovery', exact: true }).click();
  const unavailableMount = page.getByRole('button', { name: 'Open folder Disconnected location', exact: true });
  await unavailableMount.getByText(/^Unavailable folder/).waitFor();
  await page.getByRole('button', { name: 'Open folder Available location', exact: true }).click();
  await page.getByRole('button', { name: 'Select file Available.txt', exact: true }).waitFor();
  await page.locator('#breadcrumbs').getByRole('link', { name: 'Mount recovery', exact: true }).click();
  await unavailableMount.getByText(/^Unavailable folder/).waitFor();
  await fs.mkdir(disconnectedMount); await fs.writeFile(path.join(disconnectedMount, 'Returned.txt'), 'Now available');
  await page.getByRole('button', { name: 'Reload', exact: true }).click();
  await unavailableMount.getByText(/^Unavailable folder/).waitFor({ state: 'detached' });
  await unavailableMount.click(); await page.getByRole('button', { name: 'Select file Returned.txt', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await page.getByRole('button', { name: 'Remove folder group Mount recovery', exact: true }).click();
  await page.getByText('Folder group removed. Its files were not changed.', { exact: true }).waitFor();
  console.log('Disconnected multi-root location remained visible; other roots worked and reconnection needed no scan.');
  await page.locator('.sidebar').getByRole('link', { name: 'Test Media', exact: true }).click();
  await page.getByRole('button', { name: 'Open folder Books', exact: true }).waitFor();
  assert.ok(!requests.some(url => /PlaybackInfo/.test(url)), 'Browsing must not probe media.');
  await page.getByRole('button', { name: 'Select file notes.txt', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Open folder Empty', exact: true }).click(); await page.getByText('This folder is empty.', { exact: true }).waitFor();
  await page.locator('#breadcrumbs').getByRole('link', { name: 'Test Media' }).click();
  const added = path.join(media, 'Added while browsing.txt'); await fs.writeFile(added, 'fresh');
  await page.getByRole('button', { name: 'Reload', exact: true }).click(); await page.getByRole('button', { name: 'Select file Added while browsing.txt', exact: true }).waitFor();
  await fs.unlink(added); await page.getByRole('button', { name: 'Reload', exact: true }).click();
  await page.getByRole('button', { name: 'Select file Added while browsing.txt', exact: true }).waitFor({ state: 'detached' });
  await page.getByRole('button', { name: 'Open folder Books', exact: true }).click();
  await page.getByRole('button', { name: 'Select file Chapter 01.m4b', exact: true }).waitFor();
  await page.getByLabel('Find', { exact: true }).fill('.m4b');
  assert.equal(await page.locator('#file-list .file-row').count(), 1);
  await page.getByLabel('Find', { exact: true }).fill('');
  await page.getByRole('button', { name: 'Select file Chapter 01.m4b', exact: true }).click();
  await page.getByRole('heading', { name: 'The local audiobook', exact: true }).waitFor();
  await page.locator('#detail-art').waitFor({ state: 'visible' });
  await page.waitForFunction(() => document.querySelector('#detail-art').naturalWidth > 0);
  assert.match(await page.locator('#detail-overview').innerText(), /<img src=/);
  await page.screenshot({ path: path.join(fixture, 'folder-desktop.png'), fullPage: true });
  await page.locator('#play-button').click(); await playing(page);
  await page.locator('#player').evaluate(video => { video.currentTime = 31; }); await playing(page, 31);
  await page.getByRole('button', { name: 'Forward 30 seconds', exact: true }).click(); await playing(page, 61);
  await page.getByRole('button', { name: 'Back 30 seconds', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('#player').currentTime < 40); await playing(page, 31);
  await stopped(page); await page.getByRole('button', { name: /Resume at 0:3/ }).waitFor();
  const bookId = (await api('UserItems/Resume')).Items.find(item => item.Name === 'Chapter 01.m4b').Id;
  if (networkAudit) {
    await networkAudit.legacyEndpoints(base, token, bookId);
    console.log('Legacy online/refresh endpoints and hostile Host header exercised under native observation.');
  }
  await page.locator('#favorite-button').click(); await page.getByRole('button', { name: 'Remove favorite', exact: true }).waitFor();
  let saved = (await api('Items/' + bookId)).UserData.PlaybackPositionTicks;
  assert.ok(saved >= 31e7 && saved < 40e7, `Unexpected saved position: ${saved}`);
  await page.getByRole('button', { name: 'Settings', exact: true }).click(); await page.getByRole('button', { name: 'Clear selection cache' }).click();
  await page.getByText('Selection cache cleared. Saved places and favorites are kept.').waitFor();
  await page.getByRole('link', { name: 'Continue' }).click(); await page.getByRole('button', { name: 'Select file Chapter 01.m4b', exact: true }).click();
  await page.getByRole('button', { name: 'Remove favorite', exact: true }).waitFor();
  await page.getByRole('button', { name: /Resume at 0:3/ }).click(); await playing(page, 31);
  await page.getByRole('combobox', { name: 'Playback speed', exact: true }).selectOption('1.5');
  assert.equal(await page.locator('#player').evaluate(video => video.playbackRate), 1.5);
  await page.getByRole('button', { name: 'Convert', exact: true }).click(); await playing(page, 31);
  assert.equal(await page.locator('#player').evaluate(video => video.playbackRate), 1.5);
  assert.match(await page.locator('#play-method').innerText(), /Compatible/);
  await page.locator('#player').evaluate(video => { video.currentTime = 63; }); await playing(page, 63);
  await stopped(page);
  saved = (await api('Items/' + bookId)).UserData.PlaybackPositionTicks; assert.ok(saved >= 63e7 && saved < 75e7, `HLS position drift: ${saved}`);
  console.log('Audio direct/compatible playback, seeking, cache clear and resume passed.');
  await page.getByRole('link', { name: 'Favorites', exact: true }).click();
  await page.getByRole('button', { name: 'Select file Chapter 01.m4b', exact: true }).click();
  await page.getByRole('heading', { name: 'Books', exact: true }).waitFor();
  await page.getByRole('heading', { name: 'The local audiobook', exact: true }).waitFor();
  await page.locator('#folder-favorite').click();
  await page.getByRole('link', { name: 'Favorites', exact: true }).click();
  await page.getByRole('button', { name: 'Open folder Books', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Select file Chapter 01.m4b', exact: true }).waitFor();
  await page.getByRole('link', { name: 'Continue', exact: true }).click();
  await page.getByRole('button', { name: 'Remove Chapter 01.m4b from Continue', exact: true }).click();
  await page.getByRole('button', { name: 'Select file Chapter 01.m4b', exact: true }).waitFor({ state: 'detached' });
  assert.equal((await api('Items/' + bookId)).UserData.PlaybackPositionTicks, saved, 'Dismissal must preserve the bookmark.');
  console.log('Folder/file Favorites shortcuts and individual Continue dismissal passed.');
  await page.locator('.sidebar').getByRole('link', { name: 'Test Media', exact: true }).click(); await page.getByRole('button', { name: 'Open folder Films', exact: true }).click();
  await page.getByRole('button', { name: 'Select file Film.mp4', exact: true }).click(); await page.getByRole('heading', { name: 'The local film', exact: true }).waitFor();
  await page.locator('#play-button').click(); await playing(page);
  await page.locator('#subtitle-select').selectOption({ index: 1 });
  await page.waitForFunction(() => document.querySelector('#player').textTracks[0]?.cues?.length > 0);
  assert.match(await page.locator('#player').evaluate(video => video.textTracks[0].cues[0].text), /Offline subtitle/);
  await page.getByRole('button', { name: 'Convert', exact: true }).click(); await playing(page);
  await stopped(page);
  console.log('Video direct/compatible playback and local WebVTT passed.');
  await page.locator('.sidebar').getByRole('link', { name: 'Test Media', exact: true }).click();
  await page.getByRole('button', { name: 'Open folder Queue', exact: true }).click();
  await page.getByText(/Order.m3u: 3 tracks; 1 unavailable/).waitFor();
  const displayedTracks = () => page.locator('#file-list .file-name').allTextContents().then(names => names.filter(name => name.endsWith('.m4b')));
  assert.deepEqual(await displayedTracks(), ['B.m4b', 'A.m4b', 'C.m4b']);
  for (const sort of ['newest', 'largest', 'desc']) {
    await page.locator('#file-sort').selectOption(sort); assert.deepEqual(await displayedTracks(), ['C.m4b', 'B.m4b', 'A.m4b']);
  }
  for (const sort of ['oldest', 'smallest', 'asc']) {
    await page.locator('#file-sort').selectOption(sort); assert.deepEqual(await displayedTracks(), ['A.m4b', 'B.m4b', 'C.m4b']);
  }
  await page.getByRole('button', { name: 'Select file B.m4b', exact: true }).click();
  await page.getByRole('button', { name: 'Play from here', exact: true }).click(); await playing(page);
  assert.deepEqual(await page.locator('#queue-list button').allTextContents(), ['B.m4b', 'C.m4b']);
  await page.locator('#pause-button').click(); assert.ok(await page.locator('#player').evaluate(video => video.paused));
  await page.locator('#pause-button').click(); await playing(page);
  await page.getByRole('button', { name: 'Next track', exact: true }).click();
  await page.getByText('C.m4b', { exact: true }).filter({ visible: true }).first().waitFor();
  await page.waitForFunction(() => document.querySelector('#playing-title').textContent === 'C.m4b'); await playing(page);
  assert.ok(await page.locator('#next-button').isDisabled());
  await page.getByRole('button', { name: 'Previous track', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('#playing-title').textContent === 'B.m4b'); await playing(page);
  // Hold the old stop report until the next queue is already visible. Its late
  // completion must not clear the new queue or replace the current selection.
  let releaseStop, sawStop;
  const heldStop = new Promise(resolve => { releaseStop = resolve; });
  const stopReached = new Promise(resolve => { sawStop = resolve; });
  await page.route('**/Sessions/Playing/Stopped', async route => { sawStop(); await heldStop; await route.continue(); }, { times: 1 });
  await page.locator('#stop-button').click(); await stopReached;
  await page.locator('#file-sort').selectOption('playlist');
  await page.locator('#folder-play').click();
  await page.waitForFunction(() => document.querySelectorAll('#queue-list button').length === 4 && !document.querySelector('#player-panel').hidden);
  releaseStop(); await playing(page);
  assert.deepEqual(await page.locator('#queue-list button').allTextContents(), ['B.m4b', 'A.m4b', 'B.m4b', 'C.m4b']);
  await page.locator('#queue-details summary').click();
  await page.screenshot({ path: path.join(fixture, 'folder-queue.png'), fullPage: true });
  await page.locator('#repeat-mode').selectOption('one');
  await page.locator('#player').evaluate(video => { video.currentTime = video.duration - .15; });
  await page.waitForFunction(() => document.querySelector('#player').currentTime < 2 && !document.querySelector('#player').paused);
  assert.equal(await page.locator('#playing-title').textContent(), 'B.m4b');
  await page.locator('#repeat-mode').selectOption('off');
  await page.locator('#player').evaluate(video => { video.currentTime = video.duration - .15; });
  await page.waitForFunction(() => document.querySelector('#playing-title').textContent === 'A.m4b'); await playing(page);
  await page.locator('#shuffle-button').click(); assert.equal(await page.locator('#shuffle-button').getAttribute('aria-pressed'), 'true');
  assert.equal(await page.locator('#queue-list button').count(), 4);
  await page.locator('#shuffle-button').click(); assert.deepEqual(await page.locator('#queue-list button').allTextContents(), ['B.m4b', 'A.m4b', 'B.m4b', 'C.m4b']);
  await stopped(page);
  await page.locator('#playlist-select').selectOption({ label: 'Second.pls' });
  await page.getByText(/Second.pls: 2 tracks/).waitFor();
  await page.locator('#folder-shuffle').click(); await playing(page);
  assert.deepEqual((await page.locator('#queue-list button').allTextContents()).sort(), ['A.m4b', 'B.m4b', 'C.m4b']);
  await stopped(page);
  await page.getByRole('button', { name: 'Select file Second.pls', exact: true }).click();
  await page.locator('#play-button').click(); await playing(page);
  assert.deepEqual(await page.locator('#queue-list button').allTextContents(), ['A.m4b', 'C.m4b']);
  await page.locator('#repeat-mode').selectOption('all');
  await page.locator('#previous-button').click();
  await page.waitForFunction(() => document.querySelector('#playing-title').textContent === 'C.m4b'); await playing(page);
  await page.locator('#next-button').click();
  await page.waitForFunction(() => document.querySelector('#playing-title').textContent === 'A.m4b'); await playing(page);
  await page.locator('#repeat-mode').selectOption('off'); await stopped(page);
  await page.getByRole('link', { name: 'Continue', exact: true }).click();
  await page.locator('#clear-continue').click(); await page.getByText('Nothing unfinished yet. Your place will appear here after playback.', { exact: true }).waitFor();
  assert.equal((await api('Items/' + bookId)).UserData.PlaybackPositionTicks, saved);
  assert.ok((await api('Items/' + bookId)).UserData.IsFavorite);
  await page.getByRole('link', { name: 'Favorites', exact: true }).click();
  await page.getByRole('button', { name: 'Select file Chapter 01.m4b', exact: true }).click();
  await page.locator('#play-button').click(); await playing(page, 63); await stopped(page);
  assert.ok((await api('UserItems/Resume')).Items.some(item => item.Id === bookId), 'Playing restores a dismissed Continue entry.');
  console.log('Date/size/name sorting, playlist order/duplicates, Play from here, queues, pause/next/previous/shuffle/repeat/Stop and Clear Continue passed.');
  await page.getByRole('button', { name: 'Settings', exact: true }).click(); await page.getByText('Add an account', { exact: true }).click();
  await page.getByLabel('New username', { exact: true }).fill('LimitedReader'); await page.getByLabel('Initial password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Create account', exact: true }).click(); await page.getByText('Account created with no folder access. Choose the folders it may open.').waitFor();
  const restrictedContext = await browser.newContext({ viewport: { width: 390, height: 844 } }); const restricted = await newPage(restrictedContext); await login(restricted, 'LimitedReader');
  await restricted.getByText('No folders are available to this account. Ask the administrator for folder access.').waitFor();
  assert.equal(await restricted.evaluate(() => {
    const base = new URL('../', location.href), key = `jigglefin:${base.pathname}:`;
    return fetch(new URL('Jigglefin/Cache/Clear', base), { method: 'POST', headers: { Authorization: `MediaBrowser Client="Browser authorization test", Device="CLI", DeviceId="restricted-test", Version="1", Token="${localStorage.getItem(key + 'token')}"` } }).then(response => response.status);
  }), 403);
  assert.equal((await fetch(base + '/Jigglefin/Cache/Clear', { method: 'POST' })).status, 401);
  await page.locator('#access-roots').getByLabel('Test Media', { exact: true }).check(); await page.getByRole('button', { name: 'Save folder access' }).click(); await page.getByText('Folder access saved.').waitFor();
  await restricted.getByRole('button', { name: 'Reload', exact: true }).click(); await restricted.getByRole('button', { name: 'Open folder Test Media', exact: true }).click();
  await restricted.getByRole('button', { name: 'Open folder Books', exact: true }).click(); await restricted.getByRole('button', { name: 'Select file Chapter 01.m4b', exact: true }).click();
  await restricted.locator('#play-button').waitFor();
  assert.equal(await restricted.getByRole('button', { name: /Resume at/ }).count(), 0, 'Bookmarks must be per account.');
  await restricted.screenshot({ path: path.join(fixture, 'folder-mobile.png'), fullPage: true });
  await restricted.getByRole('button', { name: 'Settings', exact: true }).click(); assert.ok(await restricted.locator('#admin-settings').isHidden());
  await restricted.getByLabel('Current password', { exact: true }).fill(password); await restricted.getByLabel('New password', { exact: true }).fill(password + '-new');
  await restricted.getByRole('button', { name: 'Change password', exact: true }).click(); await restricted.getByText('Password changed.', { exact: true }).waitFor();
  await restricted.getByRole('button', { name: 'Sign out', exact: true }).click(); await restricted.getByRole('button', { name: 'Sign in', exact: true }).waitFor();
  await login(restricted, 'LimitedReader', password + '-new');
  await restrictedContext.close();
  console.log('Accounts, explicit folder access, per-user state and mobile layout passed.');
  await page.close(); await stopServer(); await startServer();
  token = (await api('Users/AuthenticateByName', 'POST', { Username: username, Pw: password }, null)).AccessToken;
  assert.ok((await api('UserViews')).Items.some(item => item.Name === 'Unvisited short root'), 'The unvisited short root must survive startup without a filesystem lookup.');
  await api('Library/VirtualFolders?name=Unvisited%20short%20root', 'DELETE');
  console.log('Unvisited short-spelled network root survived setup and restart without being opened.');
  page = await newPage(context); await page.goto(base + '/web/#/resume');
  await page.getByRole('button', { name: 'Select file Chapter 01.m4b', exact: true }).click();
  await page.getByRole('button', { name: /Resume at 1:0/ }).click(); await playing(page, 63);
  await stopped(page);
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await page.getByRole('button', { name: 'Disable folder group Test Media', exact: true }).click();
  await page.getByText('Folder group disabled. Files and saved places are unchanged.', { exact: true }).waitFor();
  assert.deepEqual((await api('UserViews')).Items, []);
  await page.getByRole('button', { name: 'Enable folder group Test Media', exact: true }).click();
  await page.getByText('Folder group enabled. Files and saved places are unchanged.', { exact: true }).waitFor();
  assert.equal((await api('UserItems/Resume')).Items.find(item => item.Id === bookId).Id, bookId);
  await page.getByRole('button', { name: 'Remove folder group Test Media', exact: true }).click();
  await page.getByText('Folder group removed. Its files were not changed.', { exact: true }).waitFor();
  assert.deepEqual((await api('UserViews')).Items, []);
  assert.deepEqual(await snapshot(media), before, 'The media tree must remain byte- and timestamp-identical.');
  assert.deepEqual(failures, [], 'Browser JavaScript errors'); assert.deepEqual(externalRequests, [], 'Attempted external requests'); assert.deepEqual(violations, [], 'Unexpected CSP violations');
  assert.deepEqual(httpErrors.filter(error => error !== `403 ${base}/Jigglefin/Cache/Clear`), [], 'Unexpected failed browser requests');
  assert.ok(!requests.some(url => /RemoteSearch|Packages|Repositories|Library\/Refresh/.test(url)), 'Removed features must not be called by the UI.');
  console.log('Actual server restart/resume, unchanged media and zero external browser requests passed.');
  console.log('Screenshots:', path.join(fixture, 'folder-desktop.png'), path.join(fixture, 'folder-mobile.png'));
  successful = true;
}
main().catch(async error => {
  console.error(error.stack.replace(/([?&](?:ApiKey|api_key|token|access_token)=)[^&"\s]+/gi, '$1REDACTED'));
  if (failures.length) console.error('Browser errors:', failures);
  if (httpErrors.length) console.error('Failed requests:', httpErrors);
  for (const [index, page] of (browser?.contexts().flatMap(context => context.pages()) || []).entries()) {
    await page.screenshot({ path: path.join(fixture, `failure-${index}.png`), fullPage: true }).catch(() => {});
    console.error('Page diagnostics:', await page.evaluate(() => ({ notice: document.querySelector('#notice')?.textContent, image: (() => { const image = document.querySelector('#detail-art'); return { hidden: image?.hidden, complete: image?.complete, width: image?.naturalWidth }; })(), player: (() => { const video = document.querySelector('#player'); return { time: video?.currentTime, paused: video?.paused, ready: video?.readyState, error: video?.error?.message }; })() })).catch(() => ({})));
  }
  process.exitCode = 1;
}).finally(async () => {
  await browser?.close(); await stopServer();
  if (closeNativeTrap) await closeNativeTrap();
  if (successful) {
    await fs.writeFile(path.join(fixture, 'web-smoke-report.json'), JSON.stringify({
      pass: true, packageDirectory: packageDirectory || null, externalRequests, failures, violations, nativeResults,
      checks: ['Picker-based setup without typed paths; live navigation without scans; Favorites shortcuts; Continue single/all dismissal; playlist/date/size/name sorting; M3U/PLS queues and duplicates; Play from here; pause/stop/next/previous/skip/shuffle/repeat/automatic advance; delayed Stop/new queue race; direct/HLS audio and video; subtitles; per-account bookmarks; cache eviction; restart/resume; disconnected roots; unchanged media bytes and timestamps; clean server shutdown.']
    }, null, 2) + '\n');
    console.log('PASS. Isolated fixture retained for visual review:', fixture);
  }
  else console.error('FAIL. Only the isolated fixture was retained for diagnosis:', fixture);
});
