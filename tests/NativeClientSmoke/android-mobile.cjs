'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { NativeUi } = require('./ui.cjs');
const fixture = process.argv[2], ui = new NativeUi(fixture), pkg = 'org.jellyfin.mobile';
const report = { client: pkg, version: '2.7.3-libre', mode: 'Stock app hosting the bundled folder WebView player', checks: [] };
const text = value => ui.waitFor(async () => (await ui.nodes()).find(node => node.text === value), value);
const id = value => ui.waitFor(async () => (await ui.nodes()).find(node => node['resource-id'] === value), value);
async function tapVisible(value, attribute = 'resource-id', direction = 'KEYCODE_PAGE_DOWN') {
  for (let attempt = 0; attempt < 5; attempt++) {
    const node = (await ui.nodes()).find(node => node[attribute] === value);
    const bounds = node?.bounds.match(/\d+/g).map(Number);
    if (bounds && bounds[2] > bounds[0] && bounds[3] > bounds[1]) return ui.tapNode(node);
    await ui.key(direction);
  }
  throw new Error('Cannot scroll to ' + value);
}
async function folder(name) { await text('Open folder ' + name); await tapVisible('Open folder ' + name, 'text'); }
async function select(name, title) { await text('Select file ' + name); await ui.tap('Select file ' + name); await text(title); }
async function progress(type, after = 0) {
  const session = await ui.waitFor(async () => (await ui.api('Sessions')).find(session => session.Client === 'Jigglefin Web' && session.NowPlayingItem?.MediaType === type), type + ' session');
  const item = session.NowPlayingItem.Id;
  await ui.waitFor(async () => (await ui.api('Items/' + item)).UserData.PlaybackPositionTicks > after + 50_000_000, 'Real mobile ' + type + ' progress', 45);
  return item;
}
async function stop(item) {
  await ui.key('KEYCODE_BACK');
  await ui.waitFor(async () => !(await ui.api('Sessions')).some(session => session.NowPlayingItem?.Id === item), 'Hardware Back stops and saves');
  return (await ui.api('Items/' + item)).UserData;
}
(async () => {
  await ui.waitFor(async () => (await ui.command('status')).ready, 'Owned guest boot', 300);
  await ui.adb('logcat', '-b', 'crash', '-c');
  await ui.adb('shell', 'settings', 'put', 'secure', 'show_ime_with_hard_keyboard', '0');
  await ui.adb('shell', 'am', 'force-stop', pkg);
  await ui.adb('shell', 'am', 'start', '-n', pkg + '/.MainActivity');
  if (!process.argv.includes('--signed-in')) {
    await text('Host');
    const host = (await ui.nodes()).find(node => node.class === 'android.widget.EditText');
    assert.ok(host, 'Host input is visible'); await ui.tapNode(host);
    await ui.waitFor(async () => (await ui.nodes()).some(node => node.class === 'android.widget.EditText' && node.focused === 'true'), 'Host input focus');
    await ui.type('base');
    const base = (await ui.command('status')).base;
    await ui.waitFor(async () => (await ui.nodes()).some(node => node.class === 'android.widget.EditText' && node.text === base), 'Typed server address');
    await ui.tap('Connect');
    await id('username'); await ui.tap('username', 'resource-id');
    await ui.waitFor(async () => (await ui.nodes()).some(node => node['resource-id'] === 'username' && node.focused === 'true'), 'Username focus');
    await ui.type('username');
    await ui.key('KEYCODE_TAB');
    assert.ok((await ui.nodes()).some(node => node['resource-id'] === 'password' && node.focused === 'true' && node.password === 'true'), 'Password field has focus before typing');
    await ui.type('password'); await ui.key('KEYCODE_ENTER');
  }
  await folder('Native Folders'); await folder('Empty'); await text('This folder is empty.');
  await ui.key('KEYCODE_BACK'); await text('Open folder Books');
  report.checks.push('Stock app connects, signs in, browses empty folders and handles hardware Back');
  console.log('Mobile login and navigation verified');
  await folder('Books'); await folder('Novel'); await select('Chapter 01.m4b', 'Selected local audiobook');
  await tapVisible('play-button'); await ui.screen('mobile-local-nfo.png');
  const audio = await progress('Audio'); await ui.screen('mobile-audio-playing.png');
  report.audio = { id: audio, saved: await stop(audio) };
  assert.ok(report.audio.saved.PlaybackPositionTicks > 50_000_000);
  await ui.adb('shell', 'am', 'force-stop', pkg);
  await ui.adb('shell', 'am', 'start', '-n', pkg + '/.MainActivity');
  await text('Your folders'); await ui.tap('Continue listening & watching', 'content-desc');
  await select('Chapter 01.m4b', 'Selected local audiobook');
  await tapVisible('play-button'); await progress('Audio', report.audio.saved.PlaybackPositionTicks);
  await ui.screen('mobile-audio-resumed.png'); report.audio.resumed = await stop(audio);
  report.checks.push('Local audiobook details and real playback/save/resume after app restart');
  console.log('Mobile audiobook restart/resume verified');
  await tapVisible('home-link', 'resource-id', 'KEYCODE_PAGE_UP'); await folder('Native Folders'); await folder('Movies'); await folder('Action');
  await select('Film.mp4', 'Selected local film'); await tapVisible('play-button');
  const video = await progress('Video'); await ui.screen('mobile-video-playing.png');
  const videoSession = (await ui.api('Sessions')).find(session => session.Client === 'Jigglefin Web' && session.NowPlayingItem?.Id === video);
  assert.equal(videoSession?.PlayState.PlayMethod, 'DirectPlay', 'Supported video with subtitles Off does not burn in the default subtitle');
  assert.equal(videoSession.PlayState.SubtitleStreamIndex, -1, 'The client reports subtitles Off');
  report.video = { id: video, playState: videoSession.PlayState, saved: await stop(video) };
  assert.ok(report.video.saved.PlaybackPositionTicks > 50_000_000);
  report.checks.push('Native WebView direct video playback with subtitles Off and hardware-Back saved position');
  await ui.adb('shell', 'am', 'force-stop', pkg);
  report.completed = true;
})().catch(error => { report.error = String(error.stack); process.exitCode = 1; }).finally(async () => {
  report.crashes = await ui.adb('logcat', '-d', '-b', 'crash').catch(error => String(error));
  if (report.crashes.includes('FATAL EXCEPTION')) { report.completed = false; process.exitCode = 1; }
  await fs.writeFile(path.join(fixture, 'android-mobile-report.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report, null, 2));
});
