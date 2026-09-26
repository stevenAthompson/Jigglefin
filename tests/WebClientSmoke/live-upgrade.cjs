'use strict';

// Black-box upgrade using two extracted self-contained ZIPs. Only generated
// fixture media and private copied profiles are passed to either server.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const net = require('node:net');
const { spawn, execFile } = require('node:child_process');
const { once } = require('node:events');
const { promisify } = require('node:util');
const { DatabaseSync } = require('node:sqlite');
const { chromium } = require('playwright');
const capture = promisify(execFile);
const root = path.resolve(__dirname, '../..');
const fixture = path.resolve(process.env.JIGGLEFIN_UPGRADE_FIXTURE || 'missing-fixture');
assert.ok(path.basename(fixture).startsWith('jigglefin-zip-upgrade-'), 'Use the owning PowerShell ZIP harness.');
const oldPackage = path.resolve(process.env.JIGGLEFIN_OLD_PACKAGE || 'missing-old');
const newPackage = path.resolve(process.env.JIGGLEFIN_NEW_PACKAGE || 'missing-new');
for (const location of [oldPackage, newPackage]) assert.ok(location.startsWith(fixture + path.sep), 'Packages must be isolated extracted copies.');
const powershell = path.join(process.env.SystemRoot, 'System32/WindowsPowerShell/v1.0/powershell.exe');
const oldProfile = path.join(fixture, 'Old profile'), upgradedProfile = path.join(fixture, 'Upgraded profile');
const media = path.join(fixture, 'Media, with spaces & punctuation');
const password = 'Temporary-' + crypto.randomBytes(16).toString('hex') + '!1';
const username = 'UpgradeAdmin', limitedName = 'UpgradeReader';
const adminPosition = 31e7, limitedPosition = 12e7;
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const canon = id => id.replaceAll('-', '').toLowerCase();
let base, child, profile, packageDirectory, launchTime, token, output = '', locker, browser;
const report = { oldArchiveSha256: process.env.JIGGLEFIN_OLD_ARCHIVE_SHA256, newArchiveSha256: process.env.JIGGLEFIN_NEW_ARCHIVE_SHA256, checks: [], externalBrowserRequests: [] };

async function request(url, method = 'GET', body, accessToken = token) {
  return fetch(base + '/' + url, { method, signal: AbortSignal.timeout(30000), headers: { Authorization: `MediaBrowser Client="Isolated ZIP upgrade", Device="CLI", DeviceId="isolated-zip-upgrade", Version="0.1.0"${accessToken ? `, Token="${accessToken}"` : ''}`, 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
}
async function api(url, method = 'GET', body, accessToken = token) {
  const response = await request(url, method, body, accessToken);
  assert.ok(response.ok, `${method} ${url}: ${response.status} ${await response.clone().text()}`);
  return response.status === 204 ? null : response.json();
}
async function login(name = username) { return api('Users/AuthenticateByName', 'POST', { Username: name, Pw: password }, null); }
async function unusedPort() {
  const server = net.createServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const port = server.address().port; await new Promise(resolve => server.close(resolve)); return port;
}
async function verifyProcess(stop = false) {
  const command = `$ErrorActionPreference = 'Stop'; $matched = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $env:JIGGLEFIN_CHECK_PARENT" | Where-Object { $_.ExecutablePath -eq $env:JIGGLEFIN_CHECK_BINARY -and $_.CommandLine.Contains($env:JIGGLEFIN_CHECK_PROFILE) -and $_.CreationDate.ToUniversalTime() -ge [DateTime]::Parse($env:JIGGLEFIN_CHECK_LAUNCH).ToUniversalTime() }); if ($matched.Count -ne 1) { throw 'Cannot identify the isolated package child' }; if ($env:JIGGLEFIN_CHECK_STOP -eq '1') { Stop-Process -Id $matched[0].ProcessId -Force } else { $listeners = @(Get-NetTCPConnection -LocalPort $env:JIGGLEFIN_CHECK_PORT -State Listen); if (-not $listeners.Count -or @($listeners | Where-Object OwningProcess -NE $matched[0].ProcessId).Count) { throw 'Wrong listener owner' } }`;
  await capture(powershell, ['-NoProfile', '-NonInteractive', '-Command', command], { windowsHide: true, timeout: 15000, env: { ...process.env, JIGGLEFIN_CHECK_PARENT: String(child.pid), JIGGLEFIN_CHECK_BINARY: path.join(packageDirectory, 'jellyfin.exe'), JIGGLEFIN_CHECK_PROFILE: profile, JIGGLEFIN_CHECK_LAUNCH: launchTime, JIGGLEFIN_CHECK_PORT: new URL(base).port, JIGGLEFIN_CHECK_STOP: stop ? '1' : '0' } });
}
async function start(selectedPackage, selectedProfile) {
  packageDirectory = selectedPackage; profile = selectedProfile; output = ''; launchTime = new Date().toISOString();
  child = spawn(powershell, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', path.join(packageDirectory, 'Start-Jigglefin.ps1'), '-DataDir', profile, '--service', '--nonetchange', '--configdir', path.join(profile, 'config'), '--cachedir', path.join(profile, 'cache'), '--logdir', path.join(profile, 'logs'), '--webdir', path.join(packageDirectory, 'jellyfin-web')], { cwd: fixture, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  child.stdout.on('data', data => { output = (output + data).slice(-32000); }); child.stderr.on('data', data => { output = (output + data).slice(-32000); });
  for (let count = 0; count < 240; count++) {
    if (child.exitCode !== null) throw new Error(`Isolated package exited (${child.exitCode}): ${output.slice(-8000)}`);
    let ready = false;
    try { const response = await fetch(base + '/System/Info/Public', { signal: AbortSignal.timeout(1000) }); ready = response.ok && output.includes('Core startup complete'); } catch {}
    if (ready) { await verifyProcess(); console.log('Package ready:', selectedPackage === oldPackage ? 'older release' : 'live replacement'); return; }
    await delay(250);
  }
  throw new Error('Package startup timed out: ' + output.slice(-8000));
}
async function stop() {
  if (!child || child.exitCode !== null) return;
  const process = child, exited = once(process, 'exit');
  if (token) await api('System/Shutdown', 'POST').catch(() => {});
  await Promise.race([exited, delay(10000)]);
  if (process.exitCode === null) { await verifyProcess(true); await exited; }
  child = null;
  assert.equal(process.exitCode, 0, 'The isolated package must shut down cleanly.');
}
async function snapshot(directory) {
  const result = {};
  async function walk(current) {
    for (const entry of await fs.readdir(current, { withFileTypes: true })) {
      const file = path.join(current, entry.name);
      assert.ok(!entry.isSymbolicLink(), 'No links belong in this generated fixture.');
      if (entry.isDirectory()) await walk(file);
      else { const stat = await fs.stat(file); result[path.relative(directory, file)] = { sha256: crypto.createHash('sha256').update(await fs.readFile(file)).digest('hex'), modified: stat.mtimeMs }; }
    }
  }
  await walk(directory); return result;
}
function sql(file, query) { const db = new DatabaseSync(file, { readOnly: true }); try { return db.prepare(query).all(); } finally { db.close(); } }
async function mainDatabase(directory) {
  const candidates = (await fs.readdir(path.join(directory, 'data'))).filter(name => name.endsWith('.db'));
  for (const name of candidates) { const file = path.join(directory, 'data', name); if (sql(file, "SELECT name FROM sqlite_master WHERE type='table' AND name='BaseItems'").length) return file; }
  throw new Error('No private legacy catalog database found.');
}
async function lockMedia() {
  locker = spawn(powershell, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', path.join(__dirname, 'lock-upgrade-media.ps1'), '-Fixture', fixture, '-MediaRoot', media], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let text = '', error = ''; locker.stdout.on('data', data => { text += data; }); locker.stderr.on('data', data => { error += data; });
  for (let count = 0; count < 80; count++) { if (text.includes('LOCKED ')) return; if (locker.exitCode !== null) throw new Error('Media lock failed: ' + error); await delay(50); }
  throw new Error('Media lock did not become ready.');
}
async function unlockMedia() { if (!locker) return; const exiting = once(locker, 'exit'); locker.stdin.end('\n'); await exiting; assert.equal(locker.exitCode, 0); locker = null; }
async function findOldFile(groupId, filename) {
  for (let attempt = 0; attempt < 90; attempt++) {
    const queue = [groupId], visited = new Set();
    while (queue.length) {
      const parent = queue.shift(); if (visited.has(parent)) continue; visited.add(parent);
      const entries = (await api(`Items?parentId=${parent}&fields=Path&recursive=false`)).Items;
      for (const item of entries) { if (item.Path?.toLowerCase() === filename.toLowerCase() && !item.IsFolder) return item; if (item.IsFolder) queue.push(item.Id); }
    }
    await delay(500);
  }
  throw new Error('Older release did not expose synthetic file: ' + filename);
}
async function addOldGroup(name, folder, enabled = true) {
  await api(`Library/VirtualFolders?name=${encodeURIComponent(name)}&collectionType=books&refreshLibrary=true`, 'POST', { LibraryOptions: { Enabled: enabled, PathInfos: [{ Path: folder }], EnableRealtimeMonitor: false, SaveLocalMetadata: false, SaveSubtitlesWithMedia: false, MetadataSavers: [], SubtitleFetchers: [], TypeOptions: ['AudioBook', 'Audio', 'Book', 'MusicAlbum', 'MusicArtist', 'Movie', 'Series', 'Episode'].map(Type => ({ Type, MetadataFetchers: [], ImageFetchers: [] })) } });
  return (await api('Library/VirtualFolders')).find(group => group.Name === name);
}
async function main() {
  console.log('Isolated ZIP upgrade fixture:', fixture);
  const books = path.join(media, 'Books'), restricted = path.join(media, 'Restricted'), offline = path.join(media, 'Offline'), disabled = path.join(media, 'Disabled');
  const novel = path.join(books, 'Novel'), chapter = path.join(novel, 'Chapter 01.m4b');
  for (const directory of [novel, restricted, offline, disabled]) await fs.mkdir(directory, { recursive: true });
  await capture(path.join(newPackage, 'ffmpeg.exe'), ['-hide_banner', '-loglevel', 'error', '-nostdin', '-f', 'lavfi', '-i', 'sine=frequency=523:sample_rate=44100', '-t', '120', '-c:a', 'aac', '-b:a', '64000', chapter], { windowsHide: true, timeout: 30000 });
  for (const file of [path.join(novel, 'Chapter 02.m4b'), path.join(restricted, 'Private.m4b'), path.join(offline, 'Missing.m4b'), path.join(disabled, 'Disabled.m4b')]) await fs.copyFile(chapter, file);
  await fs.writeFile(path.join(novel, 'Chapter 01.nfo'), '<book><title>Local upgraded chapter</title><plot>Local metadata after a real package upgrade.</plot></book>');
  await fs.copyFile(path.join(root, 'branding/jigglefin-256.png'), path.join(novel, 'folder.png'));
  await fs.mkdir(path.join(oldProfile, 'config'), { recursive: true });
  const port = await unusedPort(); base = `http://127.0.0.1:${port}`;
  await fs.writeFile(path.join(oldProfile, 'config/network.xml'), `<?xml version="1.0"?><NetworkConfiguration><InternalHttpPort>${port}</InternalHttpPort><PublicHttpPort>${port}</PublicHttpPort><AutoDiscovery>false</AutoDiscovery><EnableIPv6>false</EnableIPv6><EnableRemoteAccess>false</EnableRemoteAccess><LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses></NetworkConfiguration>`);
  await fs.writeFile(path.join(oldProfile, 'config/system.xml'), '<?xml version="1.0"?><ServerConfiguration><IsStartupWizardCompleted>false</IsStartupWizardCompleted><PluginRepositories /><EnableAutoUpdate>false</EnableAutoUpdate></ServerConfiguration>');
  await start(oldPackage, oldProfile);
  await api('Startup/User', 'GET', undefined, null); // Older wizard creates its initial account here.
  await api('Startup/User', 'POST', { Name: username, Password: password }, null); await api('Startup/Complete', 'POST', undefined, null);
  const admin = await login(); token = admin.AccessToken;
  const groups = [];
  for (const [name, folder, enabled] of [['Books', books, true], ['Restricted', restricted, true], ['Offline', offline, true], ['Disabled', disabled, false]]) groups.push(await addOldGroup(name, folder, enabled));
  const oldFile = await findOldFile(groups[0].ItemId, chapter), oldOffline = await findOldFile(groups[2].ItemId, path.join(offline, 'Missing.m4b'));
  await api(`UserItems/${oldFile.Id}/UserData`, 'POST', { PlaybackPositionTicks: adminPosition, IsFavorite: true, Played: false, PlayCount: 2, LastPlayedDate: new Date().toISOString() });
  await api(`UserItems/${oldOffline.Id}/UserData`, 'POST', { PlaybackPositionTicks: 18e7, Played: false, PlayCount: 1 });
  const limited = await api('Users/New', 'POST', { Name: limitedName, Password: password });
  await api(`Users/${limited.Id}/Policy`, 'POST', { ...limited.Policy, IsAdministrator: false, EnableAllFolders: false, EnabledFolders: [groups[0].ItemId], BlockedMediaFolders: [], AllowedTags: [], BlockedTags: [] });
  const limitedToken = (await login(limitedName)).AccessToken;
  await api(`UserItems/${oldFile.Id}/UserData`, 'POST', { PlaybackPositionTicks: limitedPosition, Played: false, PlayCount: 1 }, limitedToken);
  assert.equal((await api(`Items/${oldFile.Id}`)).UserData.PlaybackPositionTicks, adminPosition);
  assert.equal((await api(`Items/${oldFile.Id}`, 'GET', undefined, limitedToken)).UserData.PlaybackPositionTicks, limitedPosition);
  report.oldItemId = oldFile.Id; report.oldGroups = groups.map(group => ({ name: group.Name, id: group.ItemId }));
  await stop();
  const oldDatabase = await mainDatabase(oldProfile);
  const oldCount = sql(oldDatabase, 'SELECT COUNT(*) AS Count FROM BaseItems')[0].Count;
  assert.ok(oldCount > 5, 'The real older binary must have created catalog rows.');
  const oldState = sql(oldDatabase, 'SELECT * FROM UserData');
  assert.ok(oldState.length >= 3);
  const oldProfileSnapshot = await snapshot(oldProfile);
  await fs.cp(oldProfile, upgradedProfile, { recursive: true, preserveTimestamps: true, errorOnExist: true });
  await fs.rename(offline, offline + ' unplugged');
  const mediaSnapshot = await snapshot(media);
  const migratedDatabase = await mainDatabase(upgradedProfile);
  await lockMedia();
  await start(newPackage, upgradedProfile);
  assert.equal((await api('Users/Me')).Id, admin.User.Id, 'Existing token and account must survive upgrade.');
  const views = (await api('UserViews')).Items;
  assert.deepEqual(views.map(item => item.Name).sort(), ['Books', 'Offline', 'Restricted']);
  for (const group of groups.filter(group => group.Name !== 'Disabled')) assert.equal(canon(views.find(item => item.Name === group.Name).Id), canon(group.ItemId));
  const limitedViews = (await api('UserViews', 'GET', undefined, limitedToken)).Items;
  assert.deepEqual(limitedViews.map(item => item.Name), ['Books']);
  assert.equal(sql(migratedDatabase, 'SELECT COUNT(*) AS Count FROM BaseItems')[0].Count, oldCount, 'Upgrade must not rebuild/import the media catalog.');
  assert.deepEqual(sql(migratedDatabase, 'SELECT * FROM UserData'), oldState, 'Legacy state remains intact for recovery.');
  const liveDirectory = path.join(upgradedProfile, 'data', 'live-folders');
  const addressCount = sql(path.join(liveDirectory, 'live-folders.db'), 'SELECT COUNT(*) AS Count FROM Addresses')[0].Count;
  assert.equal(addressCount, 3, 'Only saved file addresses and their parent paths may be imported.');
  const resume = (await api('UserItems/Resume')).Items;
  const selected = resume.find(item => item.Path?.toLowerCase() === chapter.toLowerCase());
  assert.ok(selected); assert.equal(selected.UserData.PlaybackPositionTicks, adminPosition); assert.equal(selected.UserData.IsFavorite, true);
  assert.ok(selected.RunTimeTicks > 100e7); assert.notEqual(canon(selected.Id), canon(oldFile.Id));
  assert.equal((await api('UserItems/Resume', 'GET', undefined, limitedToken)).Items[0].UserData.PlaybackPositionTicks, limitedPosition);
  await unlockMedia();
  assert.ok(!resume.some(item => item.Path?.toLowerCase() === path.join(offline, 'Missing.m4b').toLowerCase()), 'Unavailable bookmarks must not become playable resume entries.');
  await fs.rename(offline + ' unplugged', offline);
  const returned = (await api('UserItems/Resume')).Items.find(item => item.Path?.toLowerCase() === path.join(offline, 'Missing.m4b').toLowerCase());
  assert.ok(returned, 'Reconnecting the existing location must recover its bookmark without scanning.');
  assert.equal(returned.UserData.PlaybackPositionTicks, 18e7);
  await fs.rename(offline, offline + ' unplugged');
  assert.equal((await request(`Items/${oldFile.Id}`)).status, 404, 'Stale catalog IDs cannot resolve old paths.');
  assert.equal((await request(`Items?parentId=${groups[1].ItemId}`, 'GET', undefined, limitedToken)).status, 404);
  assert.equal((await request(`Items?parentId=${groups[3].ItemId}`)).status, 404);
  const children = (await api(`Items?parentId=${groups[0].ItemId}`)).Items;
  const selectedFolder = children.find(item => item.Name === 'Novel'); assert.equal(selectedFolder.Type, 'Folder');
  assert.ok((await api(`Items?parentId=${selectedFolder.Id}`)).Items.some(item => item.Id === selected.Id));
  assert.equal((await api(`Items/${selected.Id}`)).Name, 'Local upgraded chapter');
  await api(`Items/${selected.Id}/PlaybackInfo`);
  const range = await fetch(`${base}/Audio/${selected.Id}/stream?static=true&ApiKey=${token}`, { headers: { Range: 'bytes=0-1023' } });
  assert.equal(range.status, 206); assert.equal((await range.arrayBuffer()).byteLength, 1024);
  await api('Jigglefin/Cache/Clear', 'POST');
  assert.equal((await api(`Items/${selected.Id}`)).UserData.PlaybackPositionTicks, adminPosition);
  report.checks.push('Actual older ZIP profile, users/tokens/permissions/bookmarks migrated while media content was locked; only three addresses imported; legacy catalog/state intact; range playback and cache eviction passed.');
  await stop(); await start(newPackage, upgradedProfile);
  assert.equal((await api('UserItems/Resume')).Items.find(item => item.Id === selected.Id).UserData.PlaybackPositionTicks, adminPosition);
  browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  await context.route('**/*', route => { if (new URL(route.request().url()).origin !== base) { report.externalBrowserRequests.push(route.request().url()); return route.abort(); } return route.continue(); });
  const page = await context.newPage(); page.setDefaultTimeout(20000);
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto(base + '/web/'); await page.getByRole('button', { name: 'Sign in', exact: true }).waitFor();
  await page.getByLabel('Username', { exact: true }).fill(username); await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('link', { name: 'Continue' }).click();
  await page.getByRole('button', { name: 'Select file Chapter 01.m4b', exact: true }).click();
  await page.getByRole('button', { name: /Resume at 0:31/ }).click();
  await page.waitForFunction(() => { const player = document.querySelector('#player'); return player.currentTime >= 31 && player.currentTime < 45 && !player.paused && player.readyState >= 3 && !player.error; });
  report.browserResumeSeconds = await page.locator('#player').evaluate(player => player.currentTime);
  await page.locator('#player').evaluate(player => { player.currentTime = 47; });
  await page.waitForFunction(() => { const player = document.querySelector('#player'); return player.currentTime >= 47.2 && !player.paused && !player.error; });
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('#stop-button').getAttribute('aria-busy') !== 'true');
  const newPosition = (await api(`Items/${selected.Id}`)).UserData.PlaybackPositionTicks; assert.ok(newPosition >= 47e7);
  report.updatedPositionTicks = newPosition;
  await page.screenshot({ path: path.join(fixture, 'upgraded-folder-ui.png'), fullPage: true });
  await browser.close(); browser = null;
  assert.deepEqual(errors, []); assert.deepEqual(report.externalBrowserRequests, []);
  await stop(); await start(newPackage, upgradedProfile);
  assert.equal((await api(`Items/${selected.Id}`)).UserData.PlaybackPositionTicks, newPosition, 'Migration retry/startup cannot overwrite newer progress.');
  assert.equal((await api(`Items/${selected.Id}`, 'GET', undefined, limitedToken)).UserData.PlaybackPositionTicks, limitedPosition);
  await stop();
  assert.deepEqual(await snapshot(media), mediaSnapshot, 'Upgrade/playback must not alter media bytes/mtimes.');
  assert.deepEqual(await snapshot(oldProfile), oldProfileSnapshot, 'The original older profile is a byte/mtime-identical rollback copy.');
  report.checks.push('Fresh browser resumed the migrated audiobook at its saved position; repeat restart kept newer progress and separate account positions; original profile and media unchanged.');
  report.pass = true;
  await fs.writeFile(path.join(fixture, 'upgrade-report.json'), JSON.stringify(report, null, 2) + '\n');
  console.log('PASS. Actual ZIP upgrade and fresh-browser resume verified:', path.join(fixture, 'upgrade-report.json'));
}
main().catch(async error => {
  console.error(error.stack.replace(/([?&](?:ApiKey|api_key|token|access_token)=)[^&"\s]+/gi, '$1REDACTED'));
  console.error(output.slice(-6000)); process.exitCode = 1;
}).finally(async () => {
  if (browser) await browser.close().catch(() => {});
  await unlockMedia().catch(error => console.error('Lock cleanup:', error.message));
  await stop().catch(error => { console.error('Isolated server cleanup:', error.message); process.exitCode = 1; });
});
