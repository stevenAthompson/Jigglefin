'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { NativeUi } = require('./ui.cjs');
const fixture = process.argv[2], ui = new NativeUi(fixture);
const pkg = 'org.jellyfin.androidtv';
const report = { client: pkg, version: '0.19.10', checks: [], limitations: [] };
async function expectText(text) {
  return ui.waitFor(async () => (await ui.nodes()).find(node => node.text === text), `UI text: ${text}`);
}
async function openCard(text) {
  const nodes = await ui.nodes();
  const page = nodes => nodes.find(node => node['resource-id'] === pkg + ':id/statusText')?.text
    || nodes.find(node => node['resource-id'] === pkg + ':id/fdTitle')?.text || 'home';
  const node = nodes.find(node => node.text === text && node['resource-id'] === pkg + ':id/overlay_text')
    || nodes.find(node => node.text === text && node['resource-id'] === pkg + ':id/title');
  assert.ok(node, 'Visible card: ' + text); await ui.tapNode(node);
  // A first touch may only focus a TV card; an already focused card activates.
  // Observe navigation before sending Enter, so we cannot open two folders.
  if (page(await ui.nodes()) === page(nodes)) await ui.key('KEYCODE_DPAD_CENTER');
}
async function playSession(mediaType, ticks = 0) {
  return ui.waitFor(async () => (await ui.api('Sessions')).find(session => session.Client === 'Jellyfin Android TV' && session.NowPlayingItem?.MediaType === mediaType && session.PlayState.PositionTicks > ticks), `TV ${mediaType} progress > ${ticks}`, 45);
}
async function stopVideo(id) {
  // Back first dismisses the playback controls, if they are visible.
  for (let attempt = 0; attempt < 2; attempt++) {
    await ui.key('KEYCODE_BACK');
    await new Promise(resolve => setTimeout(resolve, 1000));
    if (!(await ui.api('Sessions')).some(session => session.NowPlayingItem?.Id === id)) return;
  }
  throw new Error('TV video did not stop');
}
(async () => {
  await ui.waitFor(async () => (await ui.command('status')).ready, 'Owned Android boot', 300);
  await ui.adb('logcat', '-b', 'crash', '-c'); // This is a newly owned guest only.
  await ui.adb('shell', 'am', 'force-stop', pkg);
  await ui.adb('shell', 'settings', 'put', 'secure', 'immersive_mode_confirmations', 'confirmed');
  await ui.adb('shell', 'am', 'start', '-n', pkg + '/.ui.startup.StartupActivity');
  if (!process.argv.includes('--signed-in')) {
  await expectText('Enter server address'); await ui.tap('Enter server address');
  await expectText('Connect'); await ui.type('base'); await ui.tap('Connect');
  await expectText('Add account'); await ui.tap('Add account');
  await expectText('Use a password'); await ui.tap('Use a password');
  await expectText('Username'); await ui.type('username'); await ui.tap(pkg + ':id/password', 'resource-id'); await ui.type('password'); await ui.tap('Sign in');
  }
  await expectText('Native Folders'); report.checks.push('Authenticated standard client login');
  console.log('TV login ready');
  await openCard('Native Folders'); await expectText("Showing All items from 'Native Folders' sorted by Name");
  await ui.screen('tv-live-folders.png');
  await openCard('Empty'); await expectText("Showing All items from 'Empty' sorted by Name");
  await ui.screen('tv-empty-folder.png'); await ui.key('KEYCODE_BACK');
  await expectText('Books'); await openCard('Books'); await expectText('Novel');
  await ui.key('KEYCODE_DPAD_CENTER'); await expectText('Chapter 01.m4b');
  await ui.key('KEYCODE_DPAD_CENTER');
  const audio = await playSession('Audio', 60_000_000);
  report.audio = { id: audio.NowPlayingItem.Id, state: audio.PlayState, name: audio.NowPlayingItem.Name };
  await ui.screen('tv-audiobook-playing.png');
  await ui.key('KEYCODE_MEDIA_PAUSE');
  await ui.waitFor(async () => (await ui.api('Sessions')).some(session => session.NowPlayingItem?.Id === audio.NowPlayingItem.Id && session.PlayState.IsPaused), 'TV audio pause');
  report.audio.saved = (await ui.api('Items/' + audio.NowPlayingItem.Id)).UserData;
  assert.ok(report.audio.saved.PlaybackPositionTicks > 0);
  report.checks.push('Empty/nested physical folders and real audiobook streaming/progress');
  console.log('TV folders/audio verified');
  await ui.adb('shell', 'am', 'force-stop', pkg);
  await ui.adb('shell', 'am', 'start', '-n', pkg + '/.ui.startup.StartupActivity');
  await expectText('Native Folders'); await openCard('Native Folders'); await expectText('Books');
  await openCard('Books'); await expectText('Novel'); await ui.key('KEYCODE_DPAD_CENTER');
  await expectText('Chapter 01.m4b'); await ui.key('KEYCODE_DPAD_CENTER');
  const resumed = await playSession('Audio');
  report.audio.reopened = resumed.PlayState;
  if (resumed.PlayState.PositionTicks < report.audio.saved.PlaybackPositionTicks) report.limitations.push('Stock TV folder audio reopening starts at zero instead of seeking to the saved bookmark.');
  await ui.key('KEYCODE_MEDIA_STOP');
  await ui.adb('shell', 'am', 'force-stop', pkg);
  await ui.adb('shell', 'am', 'start', '-n', pkg + '/.ui.startup.StartupActivity');
  const control = process.argv.includes('--video-control');
  const filmTitle = control ? 'Decoder control film' : 'Selected local film';
  if (control) { await expectText('Video Control'); await openCard('Video Control'); }
  else {
    await expectText('Native Folders'); await openCard('Native Folders');
    await expectText('Movies'); await openCard('Movies'); await expectText('Action'); await ui.key('KEYCODE_DPAD_CENTER');
  }
  await expectText('Film.mp4'); await openCard('Film.mp4'); await expectText(filmTitle);
  await expectText(control ? 'Standard 320 by 180 native playback control.' : 'Native-client local film sentinel.'); await ui.screen('tv-selected-local-nfo.png');
  await ui.key('KEYCODE_DPAD_CENTER');
  // Do not use UiAutomator's idle-wait during moving video. Observe player
  // reports and screenshots instead; the guest-only setup suppresses its tip.
  // Session PositionTicks is extrapolated by the server even while buffering.
  // Require a changed durable bookmark, which only client reports can save.
  const video = await playSession('Video');
  const previous = (await ui.api('Items/' + video.NowPlayingItem.Id)).UserData.PlaybackPositionTicks;
  await ui.waitFor(async () => {
    const ticks = (await ui.api('Items/' + video.NowPlayingItem.Id)).UserData.PlaybackPositionTicks;
    return ticks > 100_000_000 && ticks !== previous;
  }, 'TV real reported video progress', 45);
  report.video = { id: video.NowPlayingItem.Id, state: video.PlayState };
  await ui.key('KEYCODE_MEDIA_PAUSE');
  await ui.waitFor(async () => (await ui.api('Sessions')).some(session => session.NowPlayingItem?.Id === video.NowPlayingItem.Id && session.PlayState.IsPaused), 'TV video pause');
  await ui.screen('tv-video-playing.png'); await stopVideo(video.NowPlayingItem.Id);
  report.video.saved = (await ui.api('Items/' + video.NowPlayingItem.Id)).UserData;
  assert.ok(report.video.saved.PlaybackPositionTicks > 100_000_000);
  console.log('TV video stop/bookmark verified');
  const resume = await ui.waitFor(async () => (await ui.nodes()).find(node => node['content-desc']?.startsWith('Resume from ')), 'TV video Resume action');
  report.video.resumeLabel = resume['content-desc'];
  await ui.screen('tv-video-resume.png'); await ui.tapNode(resume);
  await ui.waitFor(async () => (await ui.api('Items/' + video.NowPlayingItem.Id)).UserData.PlaybackPositionTicks > report.video.saved.PlaybackPositionTicks + 20_000_000, 'TV real resumed video progress', 45);
  await ui.key('KEYCODE_MEDIA_PAUSE');
  await ui.waitFor(async () => (await ui.api('Sessions')).some(session => session.NowPlayingItem?.Id === video.NowPlayingItem.Id && session.PlayState.IsPaused), 'TV resumed video pause');
  await ui.screen('tv-video-resumed.png'); await stopVideo(video.NowPlayingItem.Id);
  report.video.resumed = (await ui.api('Items/' + video.NowPlayingItem.Id)).UserData;
  report.checks.push('Selection-time NFO and native video playback/pause/stop/resume with client-reported bookmarks');
  await ui.adb('shell', 'am', 'force-stop', pkg);
  report.completed = true;
})().catch(error => { report.error = String(error.stack); process.exitCode = 1; }).finally(async () => {
  report.crashes = await ui.adb('logcat', '-d', '-b', 'crash').catch(error => String(error));
  if (report.crashes.includes('FATAL EXCEPTION')) { report.completed = false; process.exitCode = 1; }
  await fs.writeFile(path.join(fixture, 'android-tv-report.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report, null, 2));
});
