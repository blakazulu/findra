// Findra's mark, drawn once and emitted everywhere it is needed.
//
// This is run BY HAND, when the mark changes, and never by the build: `dotnet build`, the
// publish script, the installer and all three workflows read the files it produces and do not
// know it exists. It needs node and nothing else - no packages, no lockfile, no rasteriser, no
// image library. Run it from the repository root:
//
//     node build/Make-Icon.mjs
//
// It writes assets/icon/findra.ico, assets/icon/findra.svg, assets/icon/findra-flat.svg,
// assets/icon/findra-wizard.png and website/public/favicon.svg, then prints what it wrote.
//
// The geometry below is the ONLY definition of the mark. Everything else in the tree - the
// application icon compiled into findra.exe, the installer's icon, the tray glyph, the site's
// favicon and the header mark - is either produced here or drawn from these same numbers, so
// there is nowhere for two versions of the logo to drift apart. Hand-editing an output file is
// how that stops being true, which is why IconTests checks the emitted files against each other.
//
// A binary .ico in the repository is a deliberate exception to the rule that Findra draws its
// icons rather than shipping them. A Win32 application icon is a PE resource: the linker needs a
// real .ico at build time and there is no runtime hook that could draw one. The tray icon, which
// CAN be drawn at runtime, still is - see TrayIconFactory, which paints these same numbers in
// whichever palette is in force.

import { deflateSync } from 'node:zlib';
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');

// ---------------------------------------------------------------- the mark

const PLATE = '#14141A';   // Mond's ground
const ACCENT = '#FA7E00';  // Mond's accent

// A 256-unit square. The lens is a disc with the capsule's own search field cut out of it, and
// the handle is a round-capped bar. Solid mass rather than stroked line: a 21-unit stroke is
// 1.3 px once the icon is 16 px across, which is finer than a pixel grid can hold on to.
const MARK = {
  plateR: 72,                                        // 28% - the corner the shipped favicon had
  disc: { cx: 110, cy: 108, r: 64 },
  slot: { w: 68, h: 28 },                            // centred on the disc; its radius is always h/2
  hand: { x1: 158, y1: 156, x2: 200, y2: 198, w: 30 },
};

// Hand-hinting, in design units, applied before rasterising. Windows draws this at sizes where
// one design unit is a sixteenth of a pixel, and the parts that survive that are not the parts
// that look best at 256.
//
// The slot is the first casualty: at 16 px it is under two pixels tall, which renders as a grey
// smear across the lens rather than a hole, so it is dropped and the mark is honestly just a lens
// - which is what the direction sheet said would happen below about 24 px. The handle thickens as
// it shrinks for the same reason a hairline is the wrong tool on paper.
const HINTS = {
  16: { slot: false, handW: 36, discR: 66 },
  20: { slotH: 36, handW: 34, discR: 65 },
  24: { slotH: 34, handW: 34, discR: 65 },
  32: { slotH: 30, handW: 32 },
};

// The standard Windows set. 20 and 40 are not decoration: the shell picks them at 125% and 150%
// display scaling, and without them it downscales 32 and 48, which throws away the hinting above.
const SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256];

function geometry(size) {
  const h = HINTS[size] ?? {};
  return {
    plateR: MARK.plateR,
    disc: { ...MARK.disc, r: h.discR ?? MARK.disc.r },
    slot: h.slot === false ? null : { w: MARK.slot.w, h: h.slotH ?? MARK.slot.h },
    hand: { ...MARK.hand, w: h.handW ?? MARK.hand.w },
  };
}

// ---------------------------------------------------------------- rasteriser

// Signed distance fields rather than a scanline fill, because coverage falls out of the distance
// for free and the antialiasing is then the same everywhere - including inside the slot, which is
// a hole rather than a shape and which a two-pass fill would have to composite by hand.

const sdCircle = (px, py, c) => Math.hypot(px - c.cx, py - c.cy) - c.r;

function sdRoundBox(px, py, cx, cy, halfW, halfH, r) {
  const qx = Math.abs(px - cx) - (halfW - r);
  const qy = Math.abs(py - cy) - (halfH - r);
  return Math.min(Math.max(qx, qy), 0) + Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) - r;
}

function sdSegment(px, py, s) {
  const bx = s.x2 - s.x1, by = s.y2 - s.y1;
  const ax = px - s.x1, ay = py - s.y1;
  const t = Math.max(0, Math.min(1, (ax * bx + ay * by) / (bx * bx + by * by)));
  return Math.hypot(ax - bx * t, ay - by * t) - s.w / 2;
}

const hex = (s) => [1, 3, 5].map((i) => parseInt(s.slice(i, i + 2), 16));

/// One size, as straight-alpha RGBA. Distances are computed in design units and converted to
/// pixels before they become coverage, so the antialiased band is one pixel wide at every size
/// rather than one design unit wide - which at 16 px would be a sixteenth of a pixel, and would
/// alias exactly as badly as no antialiasing at all.
function raster(size) {
  const g = geometry(size);
  const k = size / 256;
  const [pr, pg, pb] = hex(PLATE);
  const [ar, ag, ab] = hex(ACCENT);
  const out = Buffer.alloc(size * size * 4);

  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const dx = (x + 0.5) / k, dy = (y + 0.5) / k;

      const plate = sdRoundBox(dx, dy, 128, 128, 128, 128, g.plateR);
      const lens = g.slot === null
        ? sdCircle(dx, dy, g.disc)
        : Math.max(sdCircle(dx, dy, g.disc),
                   -sdRoundBox(dx, dy, g.disc.cx, g.disc.cy,
                               g.slot.w / 2, g.slot.h / 2, g.slot.h / 2));
      const glyph = Math.min(lens, sdSegment(dx, dy, g.hand));

      // The glyph is clipped to the plate so the two antialiased edges never add up to more
      // than one pixel of coverage, and the colour is a ratio of the two rather than a second
      // composite - which is what keeps the plate's own rounded edge clean.
      const aP = Math.max(0, Math.min(1, 0.5 - plate * k));
      if (aP <= 0) continue;
      const aG = Math.min(Math.max(0, Math.min(1, 0.5 - glyph * k)), aP);

      const t = aG / aP;
      const i = (y * size + x) * 4;
      out[i]     = Math.round(pr + (ar - pr) * t);
      out[i + 1] = Math.round(pg + (ag - pg) * t);
      out[i + 2] = Math.round(pb + (ab - pb) * t);
      out[i + 3] = Math.round(aP * 255);
    }
  }
  return out;
}

// ---------------------------------------------------------------- PNG

const CRC = (() => {
  const t = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
    t[n] = c;
  }
  return t;
})();

function crc32(buf) {
  let c = -1;
  for (const b of buf) c = CRC[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ -1) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'latin1'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

function png(size, rgba) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8;     // bit depth
  ihdr[9] = 6;     // colour type: RGBA

  // Filter byte 0 on every scanline. The art is flat colour over long runs, so deflate does the
  // work a per-line filter heuristic would, and choosing filters would cost code to save bytes
  // in a file measured in kilobytes.
  const stride = size * 4 + 1;
  const raw = Buffer.alloc(stride * size);
  for (let y = 0; y < size; y++) {
    raw[y * stride] = 0;
    rgba.copy(raw, y * stride + 1, y * size * 4, (y + 1) * size * 4);
  }

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

// ---------------------------------------------------------------- ICO

/// PNG payloads at every size, not just at 256. Windows has read PNG-compressed icon entries
/// since Vista and Findra's floor is Windows 10, so the older BMP-with-AND-mask encoding buys
/// nothing here and costs about four times the bytes.
function ico(images) {
  const head = Buffer.alloc(6);
  head.writeUInt16LE(0, 0);              // reserved
  head.writeUInt16LE(1, 2);              // type 1: icon
  head.writeUInt16LE(images.length, 4);

  const dir = Buffer.alloc(16 * images.length);
  let offset = head.length + dir.length;
  images.forEach(({ size, data }, n) => {
    const at = n * 16;
    // The width and height fields are ONE BYTE each, so 256 is written as 0. An icon that
    // records 256 as 256 truncates to zero anyway; writing it deliberately is the difference
    // between knowing that and finding out.
    dir[at] = size === 256 ? 0 : size;
    dir[at + 1] = size === 256 ? 0 : size;
    dir[at + 2] = 0;                     // palette entries: none
    dir[at + 3] = 0;                     // reserved
    dir.writeUInt16LE(1, at + 4);        // colour planes
    dir.writeUInt16LE(32, at + 6);       // bits per pixel
    dir.writeUInt32LE(data.length, at + 8);
    dir.writeUInt32LE(offset, at + 12);
    offset += data.length;
  });

  return Buffer.concat([head, dir, ...images.map((i) => i.data)]);
}

// ---------------------------------------------------------------- SVG

/// The disc and its hole as one even-odd path rather than a mask, so the hole is a hole in every
/// renderer - including the ones that quietly ignore a mask inside a favicon.
function discPath(g) {
  const { cx, cy, r } = g.disc;
  const disc = `M${cx - r} ${cy}a${r} ${r} 0 1 0 ${r * 2} 0a${r} ${r} 0 1 0 ${-r * 2} 0z`;
  if (!g.slot) return disc;
  const hh = g.slot.h / 2;
  const straight = g.slot.w - g.slot.h;
  const hole = `M${cx - straight / 2} ${cy - hh}h${straight}` +
               `a${hh} ${hh} 0 0 1 0 ${g.slot.h}h${-straight}` +
               `a${hh} ${hh} 0 0 1 0 ${-g.slot.h}z`;
  return disc + hole;
}

function svg(plated) {
  const g = geometry(256);
  const glyph =
    `  <path fill="${ACCENT}" fill-rule="evenodd" d="${discPath(g)}"/>\n` +
    `  <line x1="${g.hand.x1}" y1="${g.hand.y1}" x2="${g.hand.x2}" y2="${g.hand.y2}"` +
    ` stroke="${ACCENT}" stroke-width="${g.hand.w}" stroke-linecap="round"/>\n`;

  // Unplated, the glyph is scaled up about the centre of the square: with no plate around it
  // there is no reason to keep the plate's breathing room, and at 26 px in a site header the
  // difference between filling the box and rattling around inside it is the whole impression.
  const body = plated
    ? `  <rect width="256" height="256" rx="${g.plateR}" fill="${PLATE}"/>\n${glyph}`
    : `  <g transform="translate(128 128) scale(1.16) translate(-128 -128)">\n  ${glyph}  </g>\n`;

  return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">\n' +
         `  <title>Findra</title>\n${body}</svg>\n`;
}

// ---------------------------------------------------------------- write

const images = SIZES.map((size) => ({ size, data: png(size, raster(size)) }));

mkdirSync(join(ROOT, 'assets', 'icon'), { recursive: true });

const wrote = [];
function put(rel, bytes) {
  writeFileSync(join(ROOT, ...rel.split('/')), bytes);
  wrote.push(`  ${rel} - ${bytes.length} bytes`);
}

put('assets/icon/findra.ico', ico(images));
put('assets/icon/findra.svg', Buffer.from(svg(true), 'utf8'));
put('assets/icon/findra-flat.svg', Buffer.from(svg(false), 'utf8'));
put('website/public/favicon.svg', Buffer.from(svg(true), 'utf8'));

// The installer wizard's small image, in the corner of every page. A separate file rather than
// the .ico because Inno wants a bitmap it can scale, and 128 is the size it reads at 250% display
// scaling - above that it is scaling up, and below it is throwing pixels away.
put('assets/icon/findra-wizard.png', images.find((i) => i.size === 128).data);

console.log(`findra.ico carries ${SIZES.join(', ')}`);
for (const line of wrote) console.log(line);
