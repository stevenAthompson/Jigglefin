'use strict';

const assert = require('node:assert/strict');
const { chromium } = require('playwright');

const base = process.env.JIGGLEFIN_TEST_BASE_URL;
const user = process.env.JIGGLEFIN_TEST_USER;
const password = process.env.JIGGLEFIN_TEST_PASSWORD;
const isoName = process.env.JIGGLEFIN_TEST_ISO_NAME;
assert.equal(base, 'http://127.0.0.1:8096');
assert.ok(user && password && isoName);

async function main() {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext();
    const external = [], pageErrors = [], dvdResponses = [];
    await context.route('**/*', route => {
      const url = route.request().url();
      if (!url.startsWith(base + '/')) { external.push(url); return route.abort(); }
      return route.continue();
    });
    const page = await context.newPage();
    page.setDefaultTimeout(30000);
    page.on('pageerror', error => pageErrors.push(error.message));
    page.on('response', response => {
      if (response.url().includes('/Jigglefin/Dvd/')) dvdResponses.push({ path: new URL(response.url()).pathname, status: response.status() });
    });
    await page.goto(base + '/web/');
    await page.getByLabel('Username', { exact: true }).fill(user);
    await page.getByLabel('Password', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.getByRole('heading', { name: 'Your folders', exact: true }).waitFor();
    await page.getByRole('link', { name: 'DVD CLI Test' }).click();
    await page.getByText(isoName, { exact: true }).first().click();
    await page.locator('#dvd-menu-play').waitFor({ state: 'visible' });
    await page.locator('#dvd-menu-play').click();
    await page.locator('#dvd-controls').waitFor({ state: 'visible', timeout: 90000 });
    await page.waitForFunction(() => { const video = document.querySelector('#player'); return video && video.currentTime > 1 && video.readyState >= 2 && !video.error; }, null, { timeout: 90000 });
    for (const command of ['down', 'select', 'menu']) {
      await page.locator(`[data-dvd-command="${command}"]`).click();
    }
    await page.locator('#stop-button').click();
    await page.locator('#player-panel').waitFor({ state: 'hidden' });
    await page.waitForFunction(() => document.querySelector('#stop-button').getAttribute('aria-busy') !== 'true');
    assert.ok(dvdResponses.some(response => response.path.endsWith('/Sessions') && response.status === 200));
    assert.ok(dvdResponses.some(response => response.path.endsWith('/stream.m3u8') && response.status === 200));
    assert.ok(dvdResponses.some(response => response.path.endsWith('.ts') && response.status === 200));
    assert.ok(dvdResponses.some(response => response.path.endsWith('/Commands/down') && response.status === 204));
    assert.ok(dvdResponses.some(response => /\/Jigglefin\/Dvd\/[0-9a-f-]{32,36}$/.test(response.path) && response.status === 204));
    assert.deepEqual(external, []);
    assert.deepEqual(pageErrors, []);
    console.log('PASS: headless DVD menu web playback, navigation, stop, and no external browser requests.');
  } finally {
    await browser.close();
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
