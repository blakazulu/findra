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
import { writeFileSync, mkdirSync, readFileSync } from 'node:fs';
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

// ---------------------------------------------------------------- the face

// A TrueType outline reader, because the share card has words on it and the only Quicksand in
// this tree is the file the application embeds. Rendering the card's type from that same file is
// the whole point: a card set in something else is a second typeface nobody signed off on, and a
// card set in a screenshot of type is unreadable at thumbnail size.
//
// Outlines only. No hinting, no kerning, no shaping - the card carries about forty Latin
// characters and a hinting engine would be several hundred lines answering a question this file
// never asks. What it does read is enough to place a glyph correctly: the character map, the
// advance widths, and the quadratic contours themselves.

function openFont(bytes) {
  const tables = {};
  const count = bytes.readUInt16BE(4);
  for (let i = 0; i < count; i++) {
    const at = 12 + i * 16;
    tables[bytes.toString('latin1', at, at + 4)] =
      { off: bytes.readUInt32BE(at + 8), len: bytes.readUInt32BE(at + 12) };
  }

  const head = tables.head.off;
  const upm = bytes.readUInt16BE(head + 18);
  const longLoca = bytes.readInt16BE(head + 50) === 1;
  const glyphs = bytes.readUInt16BE(tables.maxp.off + 4);
  const metrics = bytes.readUInt16BE(tables.hhea.off + 34);

  // Format 4 at (3,1) - the Windows Unicode BMP subtable, which is the one every desktop font
  // carries and the only one this file's character set needs.
  const cmapAt = tables.cmap.off;
  let sub = 0;
  for (let i = 0, n = bytes.readUInt16BE(cmapAt + 2); i < n; i++) {
    const at = cmapAt + 4 + i * 8;
    if (bytes.readUInt16BE(at) === 3 && bytes.readUInt16BE(at + 2) === 1) {
      sub = cmapAt + bytes.readUInt32BE(at + 4);
    }
  }
  if (!sub) throw new Error('the face carries no (3,1) character map');

  const segs = bytes.readUInt16BE(sub + 6) / 2;
  const ends = sub + 14, starts = ends + segs * 2 + 2;
  const deltas = starts + segs * 2, ranges = deltas + segs * 2;

  const glyphOf = (code) => {
    for (let s = 0; s < segs; s++) {
      if (bytes.readUInt16BE(ends + s * 2) < code) continue;
      const first = bytes.readUInt16BE(starts + s * 2);
      if (first > code) return 0;
      const offset = bytes.readUInt16BE(ranges + s * 2);
      if (offset === 0) return (code + bytes.readInt16BE(deltas + s * 2)) & 0xffff;
      const at = ranges + s * 2 + offset + (code - first) * 2;
      const g = bytes.readUInt16BE(at);
      return g === 0 ? 0 : (g + bytes.readInt16BE(deltas + s * 2)) & 0xffff;
    }
    return 0;
  };

  // The last entry in hmtx repeats for every glyph past it. Quicksand happens to carry a metric
  // per glyph, but a face that does not is normal and the reader must not walk off the table.
  const advanceOf = (gid) => bytes.readUInt16BE(
    tables.hmtx.off + Math.min(gid, metrics - 1) * 4);

  const locaOf = (gid) => longLoca
    ? bytes.readUInt32BE(tables.loca.off + gid * 4)
    : bytes.readUInt16BE(tables.loca.off + gid * 2) * 2;

  return { bytes, tables, upm, glyphs, glyphOf, advanceOf, locaOf };
}

// How many straight pieces a quadratic is cut into. Sixteen is comfortably past the point where
// the error is under a tenth of a pixel at the largest size on the card, and the cost of being
// generous is a few thousand extra distance sums on text that is drawn once, by hand.
const CURVE_STEPS = 16;

/// One glyph's contours in font units, as closed polylines. Composite glyphs are followed,
/// because a face is entitled to build any glyph out of others and finding out that it did by
/// getting a blank on the card is the wrong time.
function contours(font, gid, depth = 0) {
  const { bytes, tables } = font;
  const from = font.locaOf(gid), to = font.locaOf(gid + 1);
  if (to <= from) return [];                                   // a blank, and legitimately so

  const at = tables.glyf.off + from;
  const n = bytes.readInt16BE(at);

  if (n < 0) {
    if (depth > 4) throw new Error(`glyph ${gid} nests composites more than five deep`);
    const out = [];
    let p = at + 10;
    for (;;) {
      const flags = bytes.readUInt16BE(p), index = bytes.readUInt16BE(p + 2);
      p += 4;
      let dx, dy;
      if (flags & 1) { dx = bytes.readInt16BE(p); dy = bytes.readInt16BE(p + 2); p += 4; }
      else { dx = bytes.readInt8(p); dy = bytes.readInt8(p + 1); p += 2; }
      // Scales are read past rather than applied: no glyph this card sets uses one, and silently
      // dropping a scale would place a part correctly and size it wrong.
      if (flags & 8) p += 2;
      else if (flags & 0x40) p += 4;
      else if (flags & 0x80) p += 8;
      if ((flags & 8) || (flags & 0x40) || (flags & 0x80)) {
        throw new Error(`glyph ${gid} scales a component, which this reader does not apply`);
      }
      for (const c of contours(font, index, depth + 1)) {
        out.push(c.map((pt) => ({ x: pt.x + dx, y: pt.y + dy })));
      }
      if (!(flags & 0x20)) break;
    }
    return out;
  }

  const endsAt = at + 10;
  const last = bytes.readUInt16BE(endsAt + (n - 1) * 2);
  const total = last + 1;
  let p = endsAt + n * 2;
  p += 2 + bytes.readUInt16BE(p);                              // past the hinting instructions

  const flags = new Uint8Array(total);
  for (let i = 0; i < total;) {
    const f = bytes[p++];
    flags[i++] = f;
    if (f & 8) for (let r = bytes[p++]; r > 0 && i < total; r--) flags[i++] = f;
  }

  const read = (shortBit, sameBit) => {
    const out = new Int16Array(total);
    let v = 0;
    for (let i = 0; i < total; i++) {
      const f = flags[i];
      if (f & shortBit) v += (f & sameBit) ? bytes[p++] : -bytes[p++];
      else if (!(f & sameBit)) { v += bytes.readInt16BE(p); p += 2; }
      out[i] = v;
    }
    return out;
  };
  const xs = read(2, 16), ys = read(4, 32);

  const out = [];
  let start = 0;
  for (let c = 0; c < n; c++) {
    const end = bytes.readUInt16BE(endsAt + c * 2);
    const pts = [];
    for (let i = start; i <= end; i++) {
      pts.push({ x: xs[i], y: ys[i], on: (flags[i] & 1) !== 0 });
    }
    start = end + 1;
    if (pts.length) out.push(flatten(pts));
  }
  return out;
}

/// One contour's on- and off-curve points as a closed polyline.
///
/// TrueType allows two off-curve points in a row and means an on-curve point at their midpoint,
/// so the implied points are inserted first and the contour is then a plain alternation. A
/// contour may also begin off-curve, which is why the starting point is searched for rather than
/// assumed to be the first one.
function flatten(pts) {
  const full = [];
  for (let i = 0; i < pts.length; i++) {
    const a = pts[i], b = pts[(i + 1) % pts.length];
    full.push(a);
    if (!a.on && !b.on) full.push({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2, on: true });
  }

  let first = full.findIndex((p) => p.on);
  if (first < 0) return [];                                    // all off-curve: not a contour
  const ring = full.slice(first).concat(full.slice(0, first));

  const line = [{ x: ring[0].x, y: ring[0].y }];
  for (let i = 1; i <= ring.length; i++) {
    const p = ring[i % ring.length];
    if (p.on) { line.push({ x: p.x, y: p.y }); continue; }
    const from = line[line.length - 1];
    const to = ring[(i + 1) % ring.length];
    for (let s = 1; s <= CURVE_STEPS; s++) {
      const t = s / CURVE_STEPS, u = 1 - t;
      line.push({
        x: u * u * from.x + 2 * u * t * p.x + t * t * to.x,
        y: u * u * from.y + 2 * u * t * p.y + t * t * to.y,
      });
    }
    i++;
  }
  return line;
}

// ---------------------------------------------------------------- the canvas

/// A rectangular RGBA surface. The icon rasteriser above writes straight into a buffer because it
/// draws one shape on a transparent square; the card composites half a dozen things onto an
/// opaque ground, so it wants a blend rather than an assignment.
function canvas(w, h) {
  const px = Buffer.alloc(w * h * 4);
  return {
    w, h, px,
    /// Source-over, with the destination known to be opaque - which it is, because the first
    /// thing every card does is fill itself with the ground.
    blend(x, y, [r, g, b], a) {
      if (a <= 0 || x < 0 || y < 0 || x >= w || y >= h) return;
      const i = (y * w + x) * 4;
      px[i]     = Math.round(px[i]     + (r - px[i])     * a);
      px[i + 1] = Math.round(px[i + 1] + (g - px[i + 1]) * a);
      px[i + 2] = Math.round(px[i + 2] + (b - px[i + 2]) * a);
      px[i + 3] = 255;
    },
    fill(colour) {
      const [r, g, b] = colour;
      for (let i = 0; i < px.length; i += 4) {
        px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255;
      }
    },
  };
}

/// The accent glow behind the mark. Squared falloff rather than linear, because a linear ramp
/// bands visibly across six hundred pixels of nearly-black ground.
function bloom(c, cx, cy, radius, strength, colour) {
  for (let y = Math.max(0, cy - radius | 0); y < Math.min(c.h, cy + radius); y++) {
    for (let x = Math.max(0, cx - radius | 0); x < Math.min(c.w, cx + radius); x++) {
      const t = 1 - Math.hypot(x - cx, y - cy) / radius;
      if (t > 0) c.blend(x, y, colour, t * t * strength);
    }
  }
}

/// The faint grid, fading out with distance from one corner so it reads as texture rather than
/// as graph paper. Drawn under everything else and never over the type.
function grid(c, step, colour, strength, fromX, fromY, reach) {
  for (let y = 0; y < c.h; y++) {
    for (let x = 0; x < c.w; x++) {
      if (x % step !== 0 && y % step !== 0) continue;
      const t = 1 - Math.hypot(x - fromX, y - fromY) / reach;
      if (t > 0) c.blend(x, y, colour, t * t * strength);
    }
  }
}

function rect(c, x, y, w, h, colour, radius = 0) {
  for (let py = Math.floor(y); py < Math.ceil(y + h); py++) {
    for (let px = Math.floor(x); px < Math.ceil(x + w); px++) {
      const d = sdRoundBox(px + 0.5, py + 0.5, x + w / 2, y + h / 2, w / 2, h / 2, radius);
      c.blend(px, py, colour, Math.max(0, Math.min(1, 0.5 - d)));
    }
  }
}

/// The mark, at any size and anywhere, from the same numbers the icon is cut from. Unplated: on
/// a card the ground IS the plate, and a rounded square drawn on top of it is a box round a logo
/// that does not need one.
function mark(c, left, top, size, colour) {
  const g = geometry(256);
  const k = size / 256;
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      // The same 1.16 enlargement about the centre that findra-flat.svg carries, for the same
      // reason: with no plate around it there is no plate's breathing room to keep.
      const dx = (x + 0.5) / k, dy = (y + 0.5) / k;
      const ux = (dx - 128) / 1.16 + 128, uy = (dy - 128) / 1.16 + 128;
      const lens = Math.max(
        sdCircle(ux, uy, g.disc),
        -sdRoundBox(ux, uy, g.disc.cx, g.disc.cy, g.slot.w / 2, g.slot.h / 2, g.slot.h / 2));
      const d = Math.min(lens, sdSegment(ux, uy, g.hand)) * k * 1.16;
      c.blend(left + x, top + y, colour, Math.max(0, Math.min(1, 0.5 - d)));
    }
  }
}

// ---------------------------------------------------------------- setting type

const FACE = openFont(readFileSync(join(ROOT, 'assets', 'fonts', 'Quicksand-Regular.ttf')));

/// How much a glyph's outline is pushed outward to stand in for a bold weight, as a fraction of
/// the size. Only Quicksand Regular ships - `Parts.Face` resolves that one file and the
/// application's own bold is `SKFont.Embolden` on it - so the card fakes bold by the same means
/// rather than introducing a second font file the licence would then have to travel with.
const EMBOLDEN = 0.022;

/// The advance width of a string, in pixels, before it is drawn. Everything centred or
/// right-aligned on either card needs this, and measuring is the only honest way to get it:
/// guessing an average character width is how a wordmark ends up a few pixels off its own icon.
function widthOf(text, size, tracking = 0) {
  let w = 0;
  for (const ch of text) {
    w += FACE.advanceOf(FACE.glyphOf(ch.codePointAt(0))) * size / FACE.upm + tracking * size;
  }
  return w - (text.length ? tracking * size : 0);
}

/// A run of text, drawn glyph by glyph on its baseline.
///
/// Each glyph is rasterised only inside its own bounding box rather than over the whole card:
/// the distance to every segment of every letter, for every pixel of a 1200 by 630 image, is
/// billions of square roots and minutes of waiting. Confined to the glyph it belongs to it is a
/// few million and imperceptible.
function text(c, str, x, baseline, size, colour, { bold = false, tracking = 0 } = {}) {
  const scale = size / FACE.upm;
  const grow = bold ? size * EMBOLDEN : 0;
  const pad = Math.ceil(grow) + 2;
  let pen = x;

  for (const ch of str) {
    const gid = FACE.glyphOf(ch.codePointAt(0));
    const advance = FACE.advanceOf(gid) * scale;

    const segments = [];
    let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
    for (const line of contours(FACE, gid)) {
      for (let i = 0; i < line.length; i++) {
        const a = line[i], b = line[(i + 1) % line.length];
        // Font units are y-up and the canvas is y-down, which is the one transform that has to
        // happen exactly once. It happens here.
        const ax = pen + a.x * scale, ay = baseline - a.y * scale;
        const bx = pen + b.x * scale, by = baseline - b.y * scale;
        segments.push([ax, ay, bx, by]);
        x0 = Math.min(x0, ax, bx); y0 = Math.min(y0, ay, by);
        x1 = Math.max(x1, ax, bx); y1 = Math.max(y1, ay, by);
      }
    }

    if (segments.length) {
      const left = Math.floor(x0) - pad, top = Math.floor(y0) - pad;
      const right = Math.ceil(x1) + pad, bottom = Math.ceil(y1) + pad;
      for (let py = top; py <= bottom; py++) {
        for (let px = left; px <= right; px++) {
          const cx = px + 0.5, cy = py + 0.5;
          let best = Infinity, winding = 0;
          for (const [ax, ay, bx, by] of segments) {
            best = Math.min(best, distanceToSegment(cx, cy, ax, ay, bx, by));
            // Nonzero winding, which is the fill rule TrueType is defined under: a counter in a
            // letter is a contour running the other way, and an even-odd test would fill it.
            if ((ay <= cy) !== (by <= cy)) {
              const t = (cy - ay) / (by - ay);
              if (ax + t * (bx - ax) > cx) winding += by > ay ? 1 : -1;
            }
          }
          const d = (winding !== 0 ? -best : best) - grow;
          c.blend(px, py, colour, Math.max(0, Math.min(1, 0.5 - d)));
        }
      }
    }

    pen += advance + tracking * size;
  }
  return pen - x - (str.length ? tracking * size : 0);
}

function distanceToSegment(px, py, ax, ay, bx, by) {
  const dx = bx - ax, dy = by - ay;
  const len = dx * dx + dy * dy;
  const t = len === 0 ? 0 : Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / len));
  return Math.hypot(px - ax - dx * t, py - ay - dy * t);
}

// ---------------------------------------------------------------- the share card

// What WhatsApp, X, Facebook, LinkedIn, Slack and Discord show when somebody posts the link. It
// is drawn here rather than exported from a design tool for the same reason the icon is: it
// carries the mark, and a second copy of the mark is how one logo quietly becomes two.
//
// No screenshot on it. The product's own card is 820 pixels wide and full of 13px type; inside a
// WhatsApp thumbnail that is grey mush, and a share image that cannot be read at thumbnail size
// has failed at the only job it has.

const GROUND = hex('#08090c');       // Mond's ground, as the site sets it
const INK_BRIGHT = hex('#f8f1e4');
const INK = hex('#ebdbc0');
const INK_FAINT = hex('#7e786d');
const ACCENT_RGB = hex(ACCENT);

const HEADLINE = ['WINDOWS SEARCH.', 'BUT IT WORKS.'];
const SUBLINE = ['0.50 ms to a filename, straight from RAM.', 'Nothing leaves your machine.'];
const FINE = 'WINDOWS 10 AND 11   /   APACHE-2.0   /   FREE';

/// The 1200 by 630 card: the headline on the left, the lockup on the right.
///
/// 1200 by 630 is not a preference. It is the size X and Facebook both crop to, and the existing
/// og:image - a product screenshot at 820 by 626 - is 1.31:1 against their 1.91:1, so X was
/// taking a slice out of the middle of it.
function shareCard() {
  const c = canvas(1200, 630);
  c.fill(GROUND);
  grid(c, 60, INK, 0.05, 240, 190, 900);
  bloom(c, 985, 250, 420, 0.13, ACCENT_RGB);

  const left = 88;
  text(c, HEADLINE[0], left, 240, 64, INK_BRIGHT, { bold: true });
  text(c, HEADLINE[1], left, 316, 64, ACCENT_RGB, { bold: true });
  text(c, SUBLINE[0], left, 382, 25, INK);
  text(c, SUBLINE[1], left, 418, 25, INK);
  rect(c, left, 458, 96, 4, ACCENT_RGB, 2);
  text(c, FINE, left, 512, 16, INK_FAINT, { bold: true, tracking: 0.1 });

  // The lockup is centred on the icon rather than on the wordmark, because the icon is the part
  // somebody recognises at thumbnail size and a wordmark hung off-centre under it reads as a
  // mistake even when nobody can say which part moved.
  const size = 216, centre = 985;
  mark(c, Math.round(centre - size / 2), 168, size, ACCENT_RGB);
  const word = 'FINDRA', track = 0.3, wide = widthOf(word, 44, track);
  text(c, word, Math.round(centre - wide / 2), 462, 44, INK_BRIGHT, { bold: true, tracking: track });

  return c;
}

/// The 1080 square, for a feed. It exists for one reason and the reason is worth writing down:
/// Instagram reads no Open Graph at all and renders no link preview anywhere, so nothing on the
/// site will ever serve this file. It is posted by hand or not at all.
function shareSquare() {
  const c = canvas(1080, 1080);
  c.fill(GROUND);
  grid(c, 60, INK, 0.05, 300, 300, 900);
  bloom(c, 540, 300, 480, 0.13, ACCENT_RGB);

  const size = 232, centre = 540;
  mark(c, Math.round(centre - size / 2), 168, size, ACCENT_RGB);
  const word = 'FINDRA', track = 0.3;
  let wide = widthOf(word, 46, track);
  text(c, word, Math.round(centre - wide / 2), 470, 46, INK_BRIGHT, { bold: true, tracking: track });

  const middle = (str, y, px, colour, opts) =>
    text(c, str, Math.round(centre - widthOf(str, px, opts?.tracking ?? 0) / 2), y, px, colour, opts);

  middle(HEADLINE[0], 612, 68, INK_BRIGHT, { bold: true });
  middle(HEADLINE[1], 694, 68, ACCENT_RGB, { bold: true });
  middle(SUBLINE[0], 772, 26, INK);
  middle(SUBLINE[1], 812, 26, INK);
  rect(c, centre - 55, 858, 110, 4, ACCENT_RGB, 2);
  middle(FINE, 914, 17, INK_FAINT, { bold: true, tracking: 0.1 });

  return c;
}

/// A PNG from a rectangular canvas. The icon's own `png` takes one side because an icon is
/// square by definition; a share card is not, so the two share the chunk writer and nothing else.
function pngOf(c) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(c.w, 0);
  ihdr.writeUInt32BE(c.h, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;

  const stride = c.w * 4 + 1;
  const raw = Buffer.alloc(stride * c.h);
  for (let y = 0; y < c.h; y++) {
    raw[y * stride] = 0;
    c.px.copy(raw, y * stride + 1, y * c.w * 4, (y + 1) * c.w * 4);
  }

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
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

// The two share images. They are emitted from here rather than drawn somewhere else because they
// carry the mark, and the whole reason this file exists is that there is one copy of the mark.
mkdirSync(join(ROOT, 'website', 'public', 'share'), { recursive: true });
put('website/public/share/card.png', pngOf(shareCard()));
put('website/public/share/square.png', pngOf(shareSquare()));

console.log(`findra.ico carries ${SIZES.join(', ')}`);
for (const line of wrote) console.log(line);
