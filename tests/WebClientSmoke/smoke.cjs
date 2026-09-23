const assert = require('node:assert/strict');
const { chromium } = require('playwright');

const baseUrl = process.env.JIGGLEFIN_TEST_BASE_URL;
const user = process.env.JIGGLEFIN_TEST_USER;
const password = process.env.JIGGLEFIN_TEST_PASSWORD;
assert.equal(baseUrl, 'http://127.0.0.1:8096', 'The Web smoke test must use the isolated local server.');
assert.ok(user && password, 'The Web smoke test requires temporary local credentials.');

async function main() {
    const browser = await chromium.launch({ headless: true });
    try {
        const context = await browser.newContext();
        await context.route('**/*', (route) => {
            const hostname = new URL(route.request().url()).hostname;
            return hostname === '127.0.0.1' ? route.continue() : route.abort();
        });
        const page = await context.newPage();
        page.setDefaultTimeout(15000);

        const mediaResponses = [];
        page.on('response', (response) => {
            const path = new URL(response.url()).pathname;
            if (/PlaybackInfo|\/Videos\/|\/Audio\//.test(path)) {
                mediaResponses.push({ path, status: response.status() });
            }
        });

        await page.goto(`${baseUrl}/web/`, { waitUntil: 'domcontentloaded' });
        await page.locator('#txtManualName').fill(user);
        await page.locator('#txtManualPassword').fill(password);
        await page.getByRole('button', { name: 'Sign In' }).click();
        await page.waitForURL(/#\/home/, { timeout: 30000 });

        await page.getByText('Jigglefin Package Smoke Movies', { exact: true }).filter({ visible: true }).first().click();
        await page.getByText('Action', { exact: true }).filter({ visible: true }).first().click();
        await page.getByText('Jigglefin Local Smoke Film', { exact: true }).filter({ visible: true }).first().click();
        await page.getByText('Smoke-test local metadata.', { exact: true }).waitFor();
        assert.match(await page.locator('body').innerText(), /English - SUBRIP - External/);

        await page.locator('button.btnPlay[title="Play"]').click();
        await page.locator('video').waitFor({ state: 'attached' });
        await page.waitForFunction(() => {
            const video = document.querySelector('video');
            return video && video.currentTime >= 2 && !video.paused && video.readyState >= 3 && !video.error;
        }, null, { timeout: 12000 });
        const movie = await page.locator('video').evaluate((video) => ({
            currentTime: video.currentTime,
            duration: video.duration,
            paused: video.paused,
            error: video.error?.message
        }));
        assert.ok(movie.duration >= 18 && movie.duration <= 22, 'The movie duration did not match the synthetic sample.');
        assert.ok(mediaResponses.some((response) => response.path.endsWith('/PlaybackInfo') && response.status === 200));
        assert.ok(mediaResponses.some((response) => /\/Videos\/.*\/stream\.mp4/.test(response.path) && response.status === 206));
        assert.ok(mediaResponses.some((response) => /\/Subtitles\/.*\/Stream\.js/.test(response.path) && response.status === 200));

        await page.goto(`${baseUrl}/web/#/home`, { waitUntil: 'domcontentloaded' });
        await page.getByText('Jigglefin Package Smoke Books', { exact: true }).filter({ visible: true }).first().click();
        await page.getByText('Fantasy', { exact: true }).filter({ visible: true }).first().click();
        await page.getByText('Smoke Audio Book', { exact: true }).filter({ visible: true }).first().waitFor();
        await page.locator('.cardOverlayContainer[data-action="link"]').first().click();
        await page.locator('audio').waitFor({ state: 'attached' });
        await page.waitForFunction(() => {
            const audio = document.querySelector('audio');
            return audio && audio.currentTime >= 2 && !audio.paused && audio.readyState >= 3 && !audio.error;
        }, null, { timeout: 12000 });
        const audiobook = await page.locator('audio').evaluate((audio) => ({
            currentTime: audio.currentTime,
            duration: audio.duration,
            paused: audio.paused,
            error: audio.error?.message
        }));
        assert.ok(audiobook.duration >= 18 && audiobook.duration <= 22, 'The audiobook duration did not match the synthetic sample.');
        assert.ok(mediaResponses.some((response) => /\/Audio\/.*\/universal/.test(response.path) && response.status === 206));

        console.log(`Jellyfin Web headless smoke passed: movie ${movie.currentTime.toFixed(1)}s and audiobook ${audiobook.currentTime.toFixed(1)}s played through physical folders.`);
    } finally {
        await browser.close();
    }
}

main().catch((error) => {
    console.error(error);
    process.exitCode = 1;
});
