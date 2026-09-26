import { mkdir, copyFile, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { createHash } from 'node:crypto';

const root = path.dirname(fileURLToPath(import.meta.url));
const output = path.join(root, 'dist');
await mkdir(output, { recursive: true });
const files = ['index.html', 'app.css', 'app.js'];
for (const name of files) await copyFile(path.join(root, 'public', name), path.join(output, name));
await copyFile(path.join(root, '..', 'branding', 'jigglefin-256.png'), path.join(output, 'logo.png'));
await copyFile(path.join(root, 'node_modules', 'hls.js', 'LICENSE'), path.join(output, 'HLS-LICENSE'));
const hls = await readFile(path.join(root, 'node_modules', 'hls.js', 'dist', 'hls.min.js'), 'utf8');
await writeFile(path.join(output, 'hls.min.js'), hls.replace(/\/\/# sourceMappingURL=.*$/m, ''));
const hashes = {};
for (const name of [...files, 'logo.png', 'hls.min.js', 'HLS-LICENSE']) {
  hashes[name] = createHash('sha256').update(await readFile(path.join(output, name))).digest('hex');
}
await writeFile(path.join(output, 'jigglefin-web.manifest.json'), JSON.stringify({ name: 'Jigglefin folder browser', offline: true, hlsVersion: '1.6.16', files: hashes }, null, 2) + '\n');
console.log(`Built offline folder UI: ${output}`);
