'use strict';
// A standard-resolution decoder control for the owned development fixture.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const { promisify } = require('node:util');
const { execFile } = require('node:child_process');
const { NativeUi } = require('./ui.cjs');
(async () => {
  const fixture = path.resolve(process.argv[2]), ui = new NativeUi(fixture);
  assert.equal((await ui.command('status')).fixture, fixture);
  const config = JSON.parse(await fs.readFile(path.join(fixture, 'control.json'), 'utf8'));
  const directory = path.join(fixture, 'VideoControl');
  async function snapshot() {
    const result = {};
    for (const name of await fs.readdir(directory)) { const file = path.join(directory, name); result[name] = { hash: crypto.createHash('sha256').update(await fs.readFile(file)).digest('hex'), mtime: (await fs.stat(file)).mtimeMs }; }
    return result;
  }
  if (process.argv.includes('--verify')) {
    assert.deepEqual(await snapshot(), JSON.parse(await fs.readFile(path.join(fixture, 'video-control-before.json'), 'utf8')));
    await fs.writeFile(path.join(fixture, 'video-control-report.json'), JSON.stringify({ pass: true, mediaUnchanged: true }, null, 2));
    console.log('Owned decoder control media unchanged.'); return;
  }
  await fs.mkdir(directory);
  const file = path.join(directory, 'Film.mp4');
  await promisify(execFile)(path.join(config.packageDirectory, 'ffmpeg.exe'), ['-hide_banner', '-loglevel', 'error', '-nostdin', '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=24', '-f', 'lavfi', '-i', 'sine=frequency=660:sample_rate=44100', '-t', '120', '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-threads', '2', '-c:a', 'aac', '-b:a', '64000', '-movflags', '+faststart', file], { windowsHide: true, timeout: 60000 });
  await fs.writeFile(path.join(directory, 'Film.nfo'), '<movie><title>Decoder control film</title><plot>Standard 320 by 180 native playback control.</plot></movie>');
  await fs.writeFile(path.join(directory, 'Film.en.srt'), '1\n00:00:00,000 --> 00:01:00,000\nLocal native-client subtitle\n');
  await fs.writeFile(path.join(fixture, 'video-control-before.json'), JSON.stringify(await snapshot(), null, 2));
  await ui.api('Library/VirtualFolders?name=Video%20Control', 'POST', { LibraryOptions: { PathInfos: [{ Path: directory }] } });
  console.log('Owned decoder control ready.');
})().catch(error => { console.error(error); process.exitCode = 1; });
