'use strict';
// Android guest UI only: all commands pass through the owned fixture controller.
const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');
class NativeUi {
  constructor(fixture) { this.fixture = fixture; }
  async command(operation, command = {}) {
    const controller = JSON.parse(await fs.readFile(path.join(this.fixture, 'control.json'), 'utf8'));
    const response = await fetch(controller.base + '/' + operation, { method: 'POST', headers: { Authorization: 'Bearer ' + controller.token }, body: JSON.stringify(command), signal: AbortSignal.timeout(120000) });
    const result = await response.json(); assert.ok(response.ok, result?.error); return result;
  }
  adb(...args) { return this.command('adb', { args }); }
  async nodes() {
    // Android can temporarily refuse a dump while a screen is transitioning.
    // Do not run another UI driver concurrently against this fixture.
    for (let attempt = 0; ; attempt++) {
      try { await this.adb('shell', 'uiautomator', 'dump', '/sdcard/jigglefin-ui.xml'); break; }
      catch (error) { if (attempt === 2) throw error; await new Promise(resolve => setTimeout(resolve, 500)); }
    }
    const xml = await this.adb('shell', 'cat', '/sdcard/jigglefin-ui.xml');
    return [...xml.matchAll(/<node\s+([^>]+)>?/g)].map(match => Object.fromEntries([...match[1].matchAll(/([\w-]+)="([^"]*)"/g)].map(attribute => [attribute[1], attribute[2].replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&')])));
  }
  async tap(value, attribute = 'text') {
    const nodes = (await this.nodes()).filter(node => node[attribute] === value);
    assert.equal(nodes.length, 1, `Expected one ${attribute}=${value}; found ${nodes.length}`);
    return this.tapNode(nodes[0]);
  }
  async tapNode(node) {
    const bounds = node.bounds.match(/\d+/g).map(Number);
    assert.ok(bounds[2] > bounds[0] && bounds[3] > bounds[1]);
    return this.adb('shell', 'input', 'tap', String(Math.floor((bounds[0] + bounds[2]) / 2)), String(Math.floor((bounds[1] + bounds[3]) / 2)));
  }
  key(code) { return this.adb('shell', 'input', 'keyevent', code); }
  type(field) { return this.command('type', { field }); }
  screen(name) { return this.command('screen', { name }); }
  api(route, method = 'GET', body) { return this.command('api', { route, method, body }); }
  async waitFor(check, label, seconds = 30) {
    const end = Date.now() + seconds * 1000;
    while (Date.now() < end) { const result = await check(); if (result) return result; await new Promise(resolve => setTimeout(resolve, 500)); }
    throw new Error('Timed out: ' + label);
  }
}
module.exports = { NativeUi };
if (require.main === module) (async () => {
  const [fixture, operation, value, attribute] = process.argv.slice(2), ui = new NativeUi(fixture);
  let result;
  if (operation === 'show') result = (await ui.nodes()).filter(node => node.text || node['content-desc'] || node.focused === 'true').map(node => ({ text: node.password === 'true' || node.class === 'android.widget.EditText' ? '[input]' : node.text, id: node['resource-id'], description: node['content-desc'], bounds: node.bounds, focused: node.focused }));
  else if (operation === 'tap') result = await ui.tap(value, attribute);
  else if (operation === 'key') result = await ui.key(value);
  else throw new Error('Unknown UI operation');
  console.log(JSON.stringify(result, null, 2));
})().catch(error => { console.error(error); process.exitCode = 1; });
