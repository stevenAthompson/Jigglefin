const assert = require('node:assert/strict');
const { chromium } = require('playwright');

const baseUrl = process.env.JIGGLEFIN_TEST_BASE_URL;
const user = process.env.JIGGLEFIN_TEST_USER;
const password = process.env.JIGGLEFIN_TEST_PASSWORD;
const itemId = process.env.JIGGLEFIN_TEST_AUDIO_ID;
const navigation = JSON.parse(process.env.JIGGLEFIN_TEST_AUDIO_NAV || '[]');
const expectedTicks = Number(process.env.JIGGLEFIN_TEST_AUDIO_POSITION_TICKS || 0);
const recordSeconds = Number(process.env.JIGGLEFIN_TEST_AUDIO_RECORD_SECONDS || 5);
const phase = process.argv[2];
assert.equal(baseUrl, 'http://127.0.0.1:8096', 'Resume checks must use an isolated local server.');
assert.ok(user && password && itemId && navigation.length);
assert.ok(['record', 'resume'].includes(phase));
assert.ok(recordSeconds > 0 && recordSeconds <= 120);

async function main() {
    const browser = await chromium.launch({ headless: true });
    try {
        // Jellyfin Web hides the audio Stop button at widths of 80em or less.
        const context = await browser.newContext({ viewport: { width: 1600, height: 1000 } });
        await context.route('**/*', route => new URL(route.request().url()).hostname === '127.0.0.1' ? route.continue() : route.abort());
        const page = await context.newPage();
        page.setDefaultTimeout(15000);
        await page.goto(`${baseUrl}/web/`, { waitUntil: 'domcontentloaded' });
        await page.locator('#txtManualName').fill(user);
        await page.locator('#txtManualPassword').fill(password);
        await page.getByRole('button', { name: 'Sign In' }).click();
        await page.waitForURL(/#\/home/, { timeout: 30000 });
        if (phase === 'resume') {
            await page.getByRole('heading', { name: 'Continue Listening', exact: true }).waitFor();
            const resumeCard = page.locator(`.itemsContainer[data-monitor="audioplayback,markplayed"] .card[data-id="${itemId}"]`)
                .filter({ visible: true }).first();
            await resumeCard.waitFor();
            assert.equal(Number(await resumeCard.getAttribute('data-positionticks')), expectedTicks,
                'Continue Listening did not show the saved chapter and position.');
        }
        for (const name of navigation) {
            await page.getByText(name, { exact: true }).filter({ visible: true }).first().click();
        }

        const card = page.locator(`.card[data-id="${itemId}"]`).filter({ visible: true }).first();
        await card.waitFor();
        const cardTicks = Number(await card.getAttribute('data-positionticks') || 0);
        assert.equal(cardTicks, expectedTicks, 'The folder card did not expose the stored audiobook position.');
        const mediaResponse = page.waitForResponse(response => {
            const path = new URL(response.url()).pathname.toLowerCase().replaceAll('-', '');
            return path.includes(`/audio/${itemId.toLowerCase().replaceAll('-', '')}/`) && [200, 206].includes(response.status());
        });
        // The ordinary card Play button uses Jellyfin Web's Resume action when a bookmark exists.
        await card.locator('button[data-action="resume"][title="Play"]').click();
        await mediaResponse;
        await page.waitForFunction(() => {
            const audio = document.querySelector('audio');
            return audio && audio.currentTime > 0 && audio.readyState >= 3 && !audio.paused && !audio.seeking && !audio.error;
        });
        const firstPosition = await page.locator('audio').evaluate(audio => audio.currentTime);
        if (phase === 'resume') {
            assert.ok(expectedTicks > 0);
            const expectedSeconds = expectedTicks / 10000000;
            assert.ok(firstPosition >= expectedSeconds - 0.5 && firstPosition < expectedSeconds + 2,
                `Playback started at ${firstPosition}s instead of the saved ${expectedSeconds}s.`);
        }
        const minimumSeconds = phase === 'record' ? recordSeconds : expectedTicks / 10000000 + 1;
        await page.waitForFunction(minimum => {
            const audio = document.querySelector('audio');
            return audio && audio.currentTime >= minimum && !audio.paused && !audio.error;
        }, minimumSeconds, { timeout: Math.ceil(minimumSeconds * 1000) + 15000 });
        // The Web player reports its last native timeupdate, not the continuously
        // advancing media clock. Wait for that event before pressing Stop.
        await page.locator('audio').evaluate(audio => new Promise((resolve, reject) => {
            const onTimeUpdate = () => {
                clearTimeout(timer);
                resolve();
            };
            const timer = setTimeout(() => {
                audio.removeEventListener('timeupdate', onTimeUpdate);
                reject(new Error('Audio did not report a native timeupdate before Stop.'));
            }, 5000);
            audio.addEventListener('timeupdate', onTimeUpdate, { once: true });
        }));
        const [stopResponse] = await Promise.all([
            page.waitForResponse(response => new URL(response.url()).pathname.endsWith('/Sessions/Playing/Stopped')
                && response.request().method() === 'POST' && response.status() === 204),
            page.locator('.nowPlayingBar .stopButton').filter({ visible: true }).click()
        ]);
        const stopReport = stopResponse.request().postDataJSON();
        assert.equal(stopReport.ItemId.replaceAll('-', '').toLowerCase(), itemId.replaceAll('-', '').toLowerCase());
        assert.ok(stopReport.PositionTicks >= minimumSeconds * 10000000,
            `Stop reported ${stopReport.PositionTicks / 10000000}s before the required ${minimumSeconds}s.`);
        console.log(`Audiobook Web ${phase} passed: first position ${firstPosition.toFixed(2)}s; Stop reported ${(stopReport.PositionTicks / 10000000).toFixed(2)}s.`);
    } finally {
        await browser.close();
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
