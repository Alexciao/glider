// Minimal, dependency-light .ico writer.
//  - sizes below `pngFrom` are stored as 32-bit BMP/DIB frames (best Explorer /
//    taskbar compatibility for small sizes, straight alpha, empty AND mask)
//  - larger sizes are stored as embedded PNG (Vista+), which keeps 256 px small.

import { PNG } from 'pngjs';

function dibFrame(pngBuf) {
  const { width: w, height: h, data } = PNG.sync.read(pngBuf); // RGBA, straight alpha
  const header = Buffer.alloc(40);
  header.writeUInt32LE(40, 0); // biSize
  header.writeInt32LE(w, 4);
  header.writeInt32LE(h * 2, 8); // XOR + AND masks
  header.writeUInt16LE(1, 12); // planes
  header.writeUInt16LE(32, 14); // bit count
  const xor = Buffer.alloc(w * h * 4);
  for (let y = 0; y < h; y++) {
    const src = (h - 1 - y) * w * 4; // bottom-up
    for (let x = 0; x < w; x++) {
      const s = src + x * 4;
      const d = (y * w + x) * 4;
      xor[d] = data[s + 2]; // B
      xor[d + 1] = data[s + 1]; // G
      xor[d + 2] = data[s]; // R
      xor[d + 3] = data[s + 3]; // A
    }
  }
  const andStride = Math.ceil(w / 32) * 4;
  const and = Buffer.alloc(andStride * h); // zeros: alpha channel carries transparency
  return Buffer.concat([header, xor, and]);
}

/**
 * @param {{size:number, png:Buffer}[]} frames
 * @param {{pngFrom?: number}} [opts]
 */
export function buildIco(frames, { pngFrom = 64 } = {}) {
  const data = frames.map((f) => (f.size >= pngFrom ? f.png : dibFrame(f.png)));
  const dir = Buffer.alloc(6 + 16 * frames.length);
  dir.writeUInt16LE(0, 0); // reserved
  dir.writeUInt16LE(1, 2); // type: icon
  dir.writeUInt16LE(frames.length, 4);
  let offset = dir.length;
  frames.forEach((f, i) => {
    const o = 6 + i * 16;
    dir[o] = f.size >= 256 ? 0 : f.size;
    dir[o + 1] = f.size >= 256 ? 0 : f.size;
    dir[o + 2] = 0; // palette colours
    dir[o + 3] = 0;
    dir.writeUInt16LE(1, o + 4); // planes
    dir.writeUInt16LE(32, o + 6); // bpp
    dir.writeUInt32LE(data[i].length, o + 8);
    dir.writeUInt32LE(offset, o + 12);
    offset += data[i].length;
  });
  return Buffer.concat([dir, ...data]);
}
