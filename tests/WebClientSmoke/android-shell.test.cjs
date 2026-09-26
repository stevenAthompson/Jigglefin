'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '../../Jigglefin.Web');
test('folder client does not redeclare stock Android shell globals', () => {
  // NativeShell injects these global lexical declarations before the deferred
  // main bundle. A collision rejects our entire script before any UI runs.
  const shell = 'const deviceId="native", deviceName="test", appName="Android", appVersion="1", codecCaps={}, features=[], plugins=[];';
  assert.doesNotThrow(() => new vm.Script(shell + fs.readFileSync(path.join(root, 'public/app.js'), 'utf8')));
});
test('stock Android recognizes the locally served ready bundle', () => {
  const html = fs.readFileSync(path.join(root, 'public/index.html'), 'utf8');
  assert.match(html, /src="main\.[^/\s]+\.bundle\.js"/);
  assert.doesNotMatch(html, /<script[^>]+src="https?:/);
});
