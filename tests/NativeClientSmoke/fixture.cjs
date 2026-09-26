'use strict';
// Development-only, owned headless Android/server fixture. Never uses a user's AVD,
// default ADB server, installed server profile or real media directory.
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const net = require('node:net');
const http = require('node:http');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const { spawn, execFile } = require('node:child_process');
const { promisify } = require('node:util');
const { once } = require('node:events');
const capture = promisify(execFile);
const repository = path.resolve(__dirname, '../..');
const sdk = process.env.JIGGLEFIN_ANDROID_SDK || path.join(process.env.LOCALAPPDATA, 'Android/Sdk');
const packageDirectory = path.resolve(process.env.JIGGLEFIN_TEST_PACKAGE || '.');
const apks = [
  { path: 'publish/android-release-client-test/jellyfin-androidtv-v0.19.10-release.apk', sha256: '8510a517c99927076082917f41bf306dd7a2546c59d492dd11bde18d6e2e4628', package: 'org.jellyfin.androidtv', version: '0.19.10' },
  { path: 'publish/android-mobile-client-test/jellyfin-android-v2.7.3-libre-release.apk', sha256: 'f9f4b3ab46cc9434df811194c2d4351417f4db83a4c1a0facd0a662d69533f7f', package: 'org.jellyfin.mobile', version: '2.7.3' }
];
const adbExe = path.join(sdk, 'platform-tools/adb.exe');
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const run = (exe, args, options = {}) => capture(exe, args, { windowsHide: true, timeout: 30000, maxBuffer: 8e6, ...options });
let fixture, server, adbServer, emulator, control, base, token, adbPort, consolePort, env, media, before, avdName;
let stopping = false, ready = false, watchdog;
const logs = { server: '', emulator: '', adb: '' };
const controllerToken = crypto.randomBytes(32).toString('hex');
const username = 'NativeTest';
const password = 'Temporary-' + crypto.randomBytes(12).toString('hex');
async function unusedPort(port = 0) {
  const listener = net.createServer(); listener.listen(port, '127.0.0.1'); await once(listener, 'listening');
  const selected = listener.address().port; await new Promise(resolve => listener.close(resolve)); return selected;
}
function start(executable, args, kind) {
  const child = spawn(executable, args, { cwd: fixture, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  child.stdout.on('data', bytes => { logs[kind] = (logs[kind] + bytes).slice(-150000); });
  child.stderr.on('data', bytes => { logs[kind] = (logs[kind] + bytes).slice(-150000); });
  child.on('error', error => { logs[kind] += error.stack; });
  return child;
}
async function api(route, method = 'GET', body, authorized = true) {
  const response = await fetch(base + '/' + route, { method, signal: AbortSignal.timeout(15000), headers: {
    Authorization: `MediaBrowser Client="Jigglefin Native Test", Device="CLI", DeviceId="${avdName}", Version="1"${authorized && token ? `, Token="${token}"` : ''}`,
    'Content-Type': 'application/json'
  }, body: body === undefined ? undefined : JSON.stringify(body) });
  assert.ok(response.ok, `${method} ${route}: ${response.status}`);
  return response.status === 204 ? null : response.json().catch(() => null);
}
async function adb(args, options) {
  return run(adbExe, ['-H', '127.0.0.1', '-P', String(adbPort), '-s', `emulator-${consolePort}`, ...args], { env, ...options });
}
async function snapshot(directory) {
  const result = {};
  for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) result[entry.name] = await snapshot(file);
    else { const stat = await fs.stat(file); result[entry.name] = { hash: crypto.createHash('sha256').update(await fs.readFile(file)).digest('hex'), mtime: stat.mtimeMs }; }
  }
  return result;
}
async function finish() {
  if (stopping) return; stopping = true; clearTimeout(watchdog);
  const errors = [];
  try { if (token && server?.exitCode === null) await api('System/Shutdown', 'POST'); } catch (error) { errors.push(String(error)); }
  try { if (emulator?.exitCode === null) await adb(['emu', 'kill']); } catch (error) { errors.push(String(error)); }
  for (const child of [server, emulator].filter(Boolean)) {
    for (let count = 0; child.exitCode === null && count < 40; count++) await delay(250);
    if (child.exitCode === null) {
      errors.push(`Owned process ${child.pid} required forced cleanup`);
      // /T is restricted to the still-running child we created, never a name,
      // the default ADB server, or an existing/user emulator.
      await run('taskkill.exe', ['/PID', String(child.pid), '/T', '/F']).catch(error => errors.push(String(error)));
    }
  }
  if (adbServer?.exitCode === null) {
    await run(adbExe, ['-H', '127.0.0.1', '-P', String(adbPort), 'kill-server'], { env }).catch(error => errors.push(String(error)));
  }
  for (const port of [adbPort, consolePort, base && Number(new URL(base).port)].filter(Boolean)) {
    try { await unusedPort(port); } catch { errors.push(`Owned port ${port} remains open after cleanup`); }
  }
  if (fixture) {
    let mediaUnchanged = null;
    if (before) { try { assert.deepEqual(await snapshot(media), before); mediaUnchanged = true; } catch (error) { mediaUnchanged = false; errors.push(String(error)); } }
    for (const [kind, contents] of Object.entries(logs)) await fs.writeFile(path.join(fixture, kind + '.log'), contents.replaceAll(password, 'REDACTED').replaceAll(token || 'NO_TOKEN', 'REDACTED').replace(/(access token\s+")[^"]+/gi, '$1REDACTED'));
    await fs.writeFile(path.join(fixture, 'cleanup-report.json'), JSON.stringify({ ready, mediaUnchanged, errors }, null, 2));
  }
  if (control) await new Promise(resolve => control.close(resolve));
  if (errors.length) { console.error(errors); process.exitCode = 1; }
  console.log('Fixture stopped:', fixture);
}
async function main() {
  assert.equal(process.platform, 'win32', 'This fixture uses the Windows Android SDK');
  assert.ok(process.env.JIGGLEFIN_TEST_PACKAGE, 'Set JIGGLEFIN_TEST_PACKAGE to the exact package under test');
  for (const apk of apks) assert.equal(crypto.createHash('sha256').update(await fs.readFile(path.join(repository, apk.path))).digest('hex'), apk.sha256, `Official release checksum: ${apk.path}`);
  fixture = await fs.mkdtemp(path.join(os.tmpdir(), 'jigglefin-native-client-'));
  watchdog = setTimeout(() => { console.error('Fixture startup lifetime expired'); void finish(); }, 15 * 60 * 1000);
  console.log('Owned native-client fixture:', fixture);
  avdName = 'JigglefinNative' + crypto.randomBytes(4).toString('hex');
  const androidHome = path.join(fixture, 'Android'), avdHome = path.join(androidHome, 'avd'), avd = path.join(avdHome, avdName + '.avd');
  await fs.mkdir(avd, { recursive: true });
  await fs.writeFile(path.join(avdHome, avdName + '.ini'), `avd.ini.encoding=UTF-8\npath=${avd}\ntarget=android-34\n`);
  await fs.writeFile(path.join(avd, 'config.ini'), `AvdId=${avdName}\navd.ini.encoding=UTF-8\nabi.type=x86_64\nhw.cpu.arch=x86_64\nhw.cpu.ncore=2\nhw.ramSize=2048\nhw.lcd.width=1280\nhw.lcd.height=720\nhw.lcd.density=160\nhw.dPad=yes\nhw.keyboard=yes\nhw.mainKeys=yes\nhw.gpu.enabled=yes\nhw.gpu.mode=swiftshader_indirect\nhw.audioInput=no\nhw.audioOutput=no\nhw.camera.back=none\nhw.camera.front=none\nhw.sdCard=no\ndisk.dataPartition.size=3G\nimage.sysdir.1=${path.join(sdk, 'system-images/android-34/google_apis/x86_64')}\\\ntag.id=google_apis\nPlayStore.enabled=false\nshowDeviceFrame=no\n`);
  adbPort = await unusedPort();
  for (let candidate = 5580; candidate < 5680; candidate += 2) {
    try { await unusedPort(candidate); await unusedPort(candidate + 1); consolePort = candidate; break; } catch { /* Keep existing emulators untouched. */ }
  }
  assert.ok(consolePort, 'No unused emulator console pair available');
  env = { ...process.env, ANDROID_HOME: sdk, ANDROID_SDK_ROOT: sdk, ANDROID_USER_HOME: androidHome, ANDROID_AVD_HOME: avdHome,
    ANDROID_ADB_SERVER_PORT: String(adbPort), ADB_SERVER_SOCKET: `tcp:127.0.0.1:${adbPort}` };
  media = path.join(fixture, 'Media'); const profile = path.join(fixture, 'Profile');
  for (const folder of ['Books/Novel', 'Movies/Action', 'Music/Album', 'TV/Show/Season 01', 'Empty']) await fs.mkdir(path.join(media, folder), { recursive: true });
  const ffmpeg = path.join(packageDirectory, 'ffmpeg.exe');
  const book = path.join(media, 'Books/Novel/Chapter 01.m4b'), movie = path.join(media, 'Movies/Action/Film.mp4');
  await run(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-nostdin', '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=44100', '-t', '120', '-c:a', 'aac', '-b:a', '64000', book]);
  await run(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-nostdin', '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=24', '-f', 'lavfi', '-i', 'sine=frequency=660:sample_rate=44100', '-t', '120', '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-threads', '2', '-c:a', 'aac', '-b:a', '64000', '-movflags', '+faststart', movie]);
  await run(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-nostdin', '-i', book, '-c:a', 'libmp3lame', path.join(media, 'Music/Album/Track 01.mp3')]);
  await fs.copyFile(movie, path.join(media, 'TV/Show/Season 01/Episode 01.mp4'));
  await fs.writeFile(path.join(media, 'Books/Novel/Chapter 01.nfo'), '<book><title>Selected local audiobook</title><plot>Native-client local sidecar sentinel.</plot></book>');
  await fs.writeFile(path.join(media, 'Movies/Action/Film.nfo'), '<movie><title>Selected local film</title><plot>Native-client local film sentinel.</plot></movie>');
  await fs.writeFile(path.join(media, 'Movies/Action/Film.en.srt'), '1\n00:00:00,000 --> 00:00:15,000\nLocal native-client subtitle\n');
  await fs.copyFile(path.join(repository, 'branding/jigglefin-256.png'), path.join(media, 'Books/Novel/folder.png'));
  await fs.writeFile(path.join(media, 'Notes.txt'), 'Visible non-media file.');
  before = await snapshot(media);
  const port = await unusedPort(); base = `http://127.0.0.1:${port}`;
  await fs.mkdir(path.join(profile, 'config'), { recursive: true });
  await fs.writeFile(path.join(profile, 'config/network.xml'), `<NetworkConfiguration><InternalHttpPort>${port}</InternalHttpPort><PublicHttpPort>${port}</PublicHttpPort><AutoDiscovery>false</AutoDiscovery><EnableIPv6>false</EnableIPv6><EnableRemoteAccess>false</EnableRemoteAccess><LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses></NetworkConfiguration>`);
  server = start(path.join(packageDirectory, 'jellyfin.exe'), ['--service', '--nonetchange', '--datadir', profile, '--webdir', path.join(packageDirectory, 'jellyfin-web'), '--ffmpeg', ffmpeg], 'server');
  for (let count = 0; count < 120; count++) {
    assert.equal(server.exitCode, null, logs.server.slice(-3000));
    if (logs.server.includes('Core startup complete') && await fetch(base + '/System/Info/Public').then(response => response.ok).catch(() => false)) break;
    await delay(500);
  }
  assert.ok(logs.server.includes('Core startup complete'), logs.server.slice(-3000));
  await api('Startup/User', 'GET', undefined, false);
  await api('Startup/User', 'POST', { Name: username, Password: password }, false);
  await api('Startup/Complete', 'POST', undefined, false);
  token = (await api('Users/AuthenticateByName', 'POST', { Username: username, Pw: password }, false)).AccessToken;
  await api('Library/VirtualFolders?name=Native%20Folders', 'POST', { LibraryOptions: { PathInfos: [{ Path: media }] } });
  const group = (await api('UserViews')).Items.find(item => item.Name === 'Native Folders');
  assert.ok(group); // Leave media unvisited until the real clients browse it.
  control = http.createServer(async (request, response) => {
    if (request.headers.authorization !== 'Bearer ' + controllerToken) { response.writeHead(403); response.end(); return; }
    try {
      let content = ''; for await (const chunk of request) { content += chunk; assert.ok(content.length < 100000); }
      const command = content ? JSON.parse(content) : {};
      let result;
      if (request.url === '/status') result = { fixture, ready, base, serial: `emulator-${consolePort}`, serverExit: server.exitCode, emulatorExit: emulator?.exitCode, logs: { emulator: logs.emulator.slice(-1500) } };
      else if (request.url === '/adb') { assert.ok(ready); const identity = (await adb(['shell', 'getprop', 'ro.boot.qemu.avd_name'])).stdout.trim(); assert.equal(identity, avdName); result = (await adb(command.args)).stdout; }
      else if (request.url === '/api') result = await api(command.route, command.method || 'GET', command.body);
      else if (request.url === '/screen') { assert.ok(ready); const name = path.basename(command.name || 'screen.png'); const file = path.join(fixture, name); await fs.writeFile(file, (await adb(['exec-out', 'screencap', '-p'], { encoding: 'buffer' })).stdout); result = { file }; }
      else if (request.url === '/type') { assert.ok(ready); const value = { username, password, base }[command.field]; assert.ok(value); await adb(['shell', 'input', 'text', value]); result = 'Typed fixture field'; }
      else if (request.url === '/stop') { response.end(JSON.stringify({ stopping: true })); void finish(); return; }
      else throw new Error('Unknown fixture operation');
      response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify(result));
    } catch (error) { response.writeHead(500); response.end(JSON.stringify({ error: String(error) })); }
  });
  control.listen(0, '127.0.0.1'); await once(control, 'listening');
  const controlBase = `http://127.0.0.1:${control.address().port}`;
  await fs.writeFile(path.join(fixture, 'control.json'), JSON.stringify({ base: controlBase, token: controllerToken, adbPort, consolePort, avdName, packageDirectory }));
  // The Windows ADB listener accepts tcp:<port>; without -a it binds loopback.
  adbServer = start(adbExe, ['-L', `tcp:${adbPort}`, '--one-device', `emulator-${consolePort}`, 'server', 'nodaemon'], 'adb');
  await delay(1000); assert.equal(adbServer.exitCode, null, logs.adb);
  emulator = start(path.join(sdk, 'emulator/emulator.exe'), ['-avd', avdName, '-port', String(consolePort), '-no-window', '-no-audio', '-no-snapshot', '-no-boot-anim', '-no-sim', '-gpu', 'swiftshader_indirect', '-http-proxy', controlBase, '-dns-server', '127.0.0.1'], 'emulator');
  for (let count = 0; count < 180; count++) {
    assert.equal(emulator.exitCode, null, logs.emulator.slice(-3000));
    if ((await adb(['shell', 'getprop', 'sys.boot_completed'], { timeout: 5000 }).catch(() => ({ stdout: '' }))).stdout.trim() === '1') break;
    await delay(1000);
  }
  assert.equal((await adb(['shell', 'getprop', 'sys.boot_completed'])).stdout.trim(), '1');
  await adb(['root']); await delay(2000); await adb(['wait-for-device']);
  for (const tool of ['iptables', 'ip6tables']) for (const args of [['-A', 'OUTPUT', '-o', 'lo', '-j', 'ACCEPT'], ['-A', 'OUTPUT', '-m', 'conntrack', '--ctstate', 'ESTABLISHED,RELATED', '-j', 'ACCEPT'], ['-P', 'OUTPUT', 'DROP']]) await adb(['shell', tool, ...args]);
  await adb(['reverse', `tcp:${port}`, `tcp:${port}`]);
  await adb(['shell', 'settings', 'put', 'secure', 'user_setup_complete', '1']);
  await adb(['shell', 'settings', 'put', 'global', 'device_provisioned', '1']);
  for (const setting of ['window_animation_scale', 'transition_animation_scale', 'animator_duration_scale']) await adb(['shell', 'settings', 'put', 'global', setting, '0']);
  for (const apk of apks) await adb(['install', path.join(repository, apk.path)], { timeout: 120000 });
  const serverSha256 = crypto.createHash('sha256').update(await fs.readFile(path.join(packageDirectory, 'jellyfin.dll'))).digest('hex');
  await fs.writeFile(path.join(fixture, 'provenance.json'), JSON.stringify({ packageDirectory, serverSha256, apks, androidApi: 34, headless: true, adbPort, consolePort, avdName }, null, 2));
  ready = true; console.log('READY:', JSON.stringify({ fixture, base, adbPort, consolePort, avdName }));
  clearTimeout(watchdog);
  watchdog = setTimeout(() => { console.error('Fixture idle lifetime expired'); void finish(); }, 45 * 60 * 1000);
}
main().catch(async error => { console.error(error.stack); process.exitCode = 1; await finish(); });
