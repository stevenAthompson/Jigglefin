'use strict';
const fs = require('node:fs/promises');
const path = require('node:path');
(async () => {
  const [fixture, operation, ...args] = process.argv.slice(2);
  const control = JSON.parse(await fs.readFile(path.join(fixture, 'control.json'), 'utf8'));
  const command = operation === 'adb' ? { args } : operation === 'api' ? { route: args[0], method: args[1] || 'GET', body: args[2] ? JSON.parse(args[2]) : undefined } : operation === 'screen' ? { name: args[0] } : operation === 'type' ? { field: args[0] } : {};
  const response = await fetch(control.base + '/' + operation, { method: 'POST', headers: { Authorization: 'Bearer ' + control.token }, body: JSON.stringify(command), signal: AbortSignal.timeout(120000) });
  const result = await response.json(); if (!response.ok) throw new Error(result.error);
  console.log(typeof result === 'string' ? result : JSON.stringify(result, null, 2));
})().catch(error => { console.error(error.message); process.exitCode = 1; });
