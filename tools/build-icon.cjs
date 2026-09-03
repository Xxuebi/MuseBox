// Regenerate the Windows icons from the supplied artwork.
// Requires sharp: npm install --no-save sharp (or set NODE_PATH to an existing installation).
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');

async function main() {
  const assets = path.resolve(__dirname, '..', 'Assets');
  const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
  async function buildIcon(stem, extension) {
    const source = await fs.readFile(path.join(assets, stem + '.' + extension));
    const render = size => sharp(source, extension === 'svg' ? { density: 384 } : undefined)
      .resize(size, size).ensureAlpha().png().toBuffer();
    const frames = await Promise.all(sizes.map(render));
    const header = Buffer.alloc(6 + sizes.length * 16);
    header.writeUInt16LE(1, 2);
    header.writeUInt16LE(sizes.length, 4);
    let offset = header.length;
    frames.forEach((frame, index) => {
      const entry = 6 + index * 16;
      header[entry] = header[entry + 1] = sizes[index] === 256 ? 0 : sizes[index];
      header.writeUInt16LE(1, entry + 4);
      header.writeUInt16LE(32, entry + 6);
      header.writeUInt32LE(frame.length, entry + 8);
      header.writeUInt32LE(offset, entry + 12);
      offset += frame.length;
    });
    await fs.writeFile(path.join(assets, stem + '.ico'), Buffer.concat([header, ...frames]));
    await fs.writeFile(path.join(assets, stem + '-preview.png'), await render(512));
    console.log('Generated ' + stem + '.ico (' + sizes.join(', ') + 'px) and preview');
  }
  await buildIcon('app-icon', 'png');
  await buildIcon('scene-icon', 'svg');
  const immersive = await fs.readFile(path.join(assets, 'immersive-collection-icon.svg'));
  await fs.writeFile(path.join(assets, 'immersive-collection-icon.png'),
    await sharp(immersive, { density: 384 }).resize(256, 256).png().toBuffer());
  console.log('Generated immersive-collection-icon.png (256px monochrome preview)');
}
main().catch(error => { console.error(error); process.exitCode = 1; });
