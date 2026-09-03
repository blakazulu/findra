// The site's written pages, generated from Markdown.
//
// This is run BY HAND, when the prose changes, and never by the build - the site has no build
// step and `publish = "website/public"` is the whole of its configuration, so what is committed
// under website/public is exactly what is served. It needs node and nothing else. Run it from the
// repository root:
//
//     node build/Make-Pages.mjs
//
// There are two reasons this exists rather than three hand-written HTML files.
//
// The first is PRIVACY.md. That file is the privacy policy: it is what GitHub shows, what
// PolicyPageTests holds to its promises, and what Findra's own update check sends somebody who
// built from source. A second copy of it written out as HTML would be a second privacy policy,
// and the one thing worse than no privacy page is two that disagree. So the Markdown is the
// source, the page is emitted from it, and WebsiteTests strips both back to prose and asserts
// they still say the same thing.
//
// The second is the shell. The navigation and the footer are the same on every page, and three
// hand-maintained copies of a footer is three chances to link the wrong privacy policy.

import { writeFileSync, mkdirSync, readFileSync, copyFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const SITE = 'https://findra-search.netlify.app';

// ---------------------------------------------------------------- the pages

// The headline is here rather than in the Markdown because the two are answering different
// questions. `# Privacy` is the right first line of a file somebody opens on GitHub looking for
// the privacy policy; "Nothing leaves your machine, except one request" is the right first line
// of a page somebody landed on wondering whether to trust the product. The generator drops the
// Markdown's own H1 and uses this.
const PAGES = [
  {
    slug: 'privacy',
    source: 'PRIVACY.md',
    kicker: 'Privacy',
    headline: 'Nothing leaves your machine, except one request.',
    title: 'Privacy - Findra',
    description:
      'What Findra stores, where it stores it, and the single anonymous request it makes on ' +
      'its own. No account, no cloud, no analytics, no telemetry.',
    reviewed: '2026-09-03',
    // The Markdown twin is published beside the page so an agent asking for text/markdown, and a
    // person who would rather read the file, both get the real thing rather than a rewrite.
    markdown: 'privacy.md',
  },
  {
    slug: 'about',
    source: 'website/content/about.md',
    kicker: 'About',
    headline: 'One person, one repository, no company behind it.',
    title: 'About - Findra',
    description:
      'Findra is a desktop search widget for Windows built on .NET 10, Avalonia, SkiaSharp and ' +
      'SQLite, by one person, under Apache-2.0.',
    reviewed: '2026-09-03',
    markdown: 'about.md',
  },
  {
    slug: 'contact',
    source: 'website/content/contact.md',
    kicker: 'Contact',
    headline: 'Four ways in, and none of them is a form.',
    title: 'Contact - Findra',
    description:
      'How to report a bug, report a security problem, or ask something else about Findra. ' +
      'No contact form, because Findra collects nothing and a form would.',
    reviewed: '2026-09-03',
    markdown: 'contact.md',
  },
];

// ---------------------------------------------------------------- Markdown

// A converter for the Markdown these three pages actually use, and no more than that. Headings,
// paragraphs, bullet lists, one table, inline code, bold, fenced code and bare links - which is
// the whole vocabulary of the three source files and is checked by a test that would fail if a
// fourth construction appeared and came out as literal asterisks.

const escape = (s) => s
  .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

/// Inline markup, applied AFTER escaping so that a `<` in a path cannot open a tag.
///
/// Code spans are lifted out first and put back last. Otherwise a path like
/// `%LOCALAPPDATA%\Findra\logs\` inside backticks would have its own underscores and asterisks
/// read as emphasis, which is how a filename ends up italic on a page nobody proofread.
function inline(text) {
  // The placeholder is delimited because the prose is full of bare numbers - "once every 24
  // hours" - and a bare " 24 " would be restored as code span 24, which does not exist.
  const spans = [];
  let out = escape(text).replace(/`([^`]+)`/g, (_, code) => {
    spans.push(code);
    return `@@${spans.length - 1}@@`;
  });

  out = out
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2">$1</a>')
    // Bare URLs, which the source files use deliberately: a reader of the Markdown should see
    // where a link goes, and a reader of the page should be able to click it.
    .replace(/(^|[\s(])(https?:\/\/[^\s<)]+[^\s<).,])/g,
             (_, lead, url) => `${lead}<a href="${url}" rel="noopener">${url}</a>`);

  return out.replace(/@@(\d+)@@/g, (_, n) => `<code>${spans[Number(n)]}</code>`);
}

/// One Markdown document as the body of a page. The document's own H1 is dropped: the page
/// carries the headline from PAGES above, and two first-level headings on one page is a
/// structure error every heading-order check in the world will find.
function body(markdown) {
  const blocks = markdown.replace(/\r\n?/g, '\n').split(/\n{2,}/).map((b) => b.trim()).filter(Boolean);
  const html = [];
  let droppedH1 = false;

  for (const block of blocks) {
    const lines = block.split('\n');

    if (/^# /.test(block) && !droppedH1) { droppedH1 = true; continue; }

    if (/^### /.test(block)) { html.push(`<h3>${inline(block.slice(4))}</h3>`); continue; }
    if (/^## /.test(block)) { html.push(`<h2>${inline(block.slice(3))}</h2>`); continue; }

    if (/^```/.test(lines[0])) {
      const code = lines.slice(1, lines[lines.length - 1].startsWith('```') ? -1 : undefined);
      html.push(`<pre><code>${escape(code.join('\n'))}</code></pre>`);
      continue;
    }

    if (/^- /.test(lines[0])) {
      // A bullet may be hard wrapped onto the next line, which is not a new bullet.
      const items = [];
      for (const line of lines) {
        if (/^- /.test(line)) items.push(line.slice(2));
        else items[items.length - 1] += ' ' + line.trim();
      }
      html.push(`<ul>\n${items.map((i) => `  <li>${inline(i)}</li>`).join('\n')}\n</ul>`);
      continue;
    }

    if (/^\|/.test(lines[0]) && lines.length > 2) {
      const cells = (row) => row.split('|').slice(1, -1).map((c) => c.trim());
      const head = cells(lines[0]);
      const rows = lines.slice(2).map(cells);
      html.push(
        '<div class="doc-table">\n<table>\n<thead>\n<tr>' +
        head.map((c) => `<th scope="col">${inline(c)}</th>`).join('') +
        '</tr>\n</thead>\n<tbody>\n' +
        rows.map((r) => '<tr>' + r.map((c) => `<td>${inline(c)}</td>`).join('') + '</tr>').join('\n') +
        '\n</tbody>\n</table>\n</div>');
      continue;
    }

    html.push(`<p>${inline(lines.join(' '))}</p>`);
  }

  return html.join('\n\n');
}

// ---------------------------------------------------------------- the shell

// Absolute paths throughout. These pages live one directory down, and `styles.css` from
// /privacy/ is a request for /privacy/styles.css, which is a page with no stylesheet at all.

const NAV_LINKS = [
  ['/#finds', 'What it finds'],
  ['/#numbers', 'The numbers'],
  ['/#install', 'Install'],
];

const FOOTER = `
<footer class="wrap">
  <div class="foot">
    <div class="col">
      <span class="brand" style="margin-bottom:6px">
        <span class="brand-mark" aria-hidden="true"></span>
        <span class="brand-name">FINDRA</span>
      </span>
      <span>Desktop search for Windows.</span>
      <span>Built with .NET 10, Avalonia, SkiaSharp and SQLite.</span>
    </div>
    <div class="col">
      <strong>PROJECT</strong>
      <a href="https://github.com/blakazulu/findra" rel="noopener">Source</a>
      <a href="https://github.com/blakazulu/findra/blob/main/CHANGELOG.md" rel="noopener">Changelog</a>
      <a href="https://github.com/blakazulu/findra/issues" rel="noopener">Issues</a>
    </div>
    <div class="col">
      <strong>THE SMALL PRINT</strong>
      <a href="/about/">About</a>
      <a href="/contact/">Contact</a>
      <a href="/privacy/">Privacy</a>
      <a href="https://github.com/blakazulu/findra/blob/main/SECURITY.md" rel="noopener">Security</a>
      <a href="https://github.com/blakazulu/findra/blob/main/LICENSE" rel="noopener">Apache-2.0</a>
    </div>
    <div class="col">
      <strong>ATTRIBUTION</strong>
      <span>Findra by blakazulu.</span>
      <span>Quicksand under the SIL Open Font License 1.1.</span>
      <span>&copy; <span id="year">2026</span></span>
    </div>
  </div>
</footer>`.trim();

const MARK = `<span class="brand-mark" aria-hidden="true"></span>`;

function shell(page, content) {
  const url = `${SITE}/${page.slug}/`;
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${page.title}</title>
<meta name="description" content="${escape(page.description)}">
<link rel="canonical" href="${url}">
<meta property="og:type" content="article">
<meta property="og:site_name" content="Findra">
<meta property="og:locale" content="en">
<meta property="og:url" content="${url}">
<meta property="og:title" content="${escape(page.title)}">
<meta property="og:description" content="${escape(page.description)}">
<meta property="og:image" content="${SITE}/share/card.png">
<meta property="og:image:type" content="image/png">
<meta property="og:image:width" content="1200">
<meta property="og:image:height" content="630">
<meta property="og:image:alt" content="Findra. Windows Search, but it works. 0.50 ms to a filename, straight from RAM.">
<meta name="twitter:card" content="summary_large_image">
<meta name="theme-color" content="#08090c">
<link rel="icon" href="/favicon.svg" type="image/svg+xml">
<link rel="alternate" type="text/markdown" href="/${page.markdown}" title="${escape(page.title)} as Markdown">
<link rel="stylesheet" href="/styles.css">
</head>
<body>

<div class="bg" aria-hidden="true">
  <div class="bg-blob a"></div>
  <div class="bg-grid"></div>
  <div class="bg-grain"></div>
</div>

<header class="nav">
  <a class="brand" href="/">
    ${MARK}
    <span class="brand-name">FINDRA</span>
  </a>
  <nav class="nav-links">
${NAV_LINKS.map(([href, label]) => `    <a href="${href}">${label}</a>`).join('\n')}
  </nav>
  <div class="nav-right">
    <a class="btn btn-ghost btn-sm" href="/">Back to the front</a>
  </div>
</header>

<main id="top">
  <div class="wrap">
    <section class="doc-head">
      <span class="kicker">${escape(page.kicker.toUpperCase())}</span>
      <h1>${escape(page.headline)}</h1>
      <div class="doc-meta">
        <span class="d">Findra ${VERSION} &middot; last reviewed ${page.reviewed}</span>
        <a class="doc-chip on" href="/${page.markdown}">Read as Markdown</a>
        <a class="doc-chip" href="https://github.com/blakazulu/findra/blob/main/${page.source}" rel="noopener">History on GitHub</a>
      </div>
    </section>

    <article class="doc">
${content.split('\n').map((l) => (l ? '      ' + l : l)).join('\n')}
    </article>
  </div>
</main>

${FOOTER}

<script src="/app.js" defer></script>
</body>
</html>
`;
}

// ---------------------------------------------------------------- write

/// The version, read from the one place that holds it. Directory.Build.props is the single source
/// for the whole repository and a number typed again here would be a second one.
const VERSION = (() => {
  const props = readFileSync(join(ROOT, 'Directory.Build.props'), 'utf8');
  const m = props.match(/<Version>([^<]+)<\/Version>/);
  if (!m) throw new Error('no <Version> in Directory.Build.props');
  return m[1].trim();
})();

const wrote = [];
for (const page of PAGES) {
  const source = join(ROOT, ...page.source.split('/'));
  const markdown = readFileSync(source, 'utf8');

  const dir = join(ROOT, 'website', 'public', page.slug);
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, 'index.html'), shell(page, body(markdown)));
  wrote.push(`  website/public/${page.slug}/index.html`);

  // Verbatim, not re-rendered. The point of publishing the Markdown is that it is the same file.
  copyFileSync(source, join(ROOT, 'website', 'public', page.markdown));
  wrote.push(`  website/public/${page.markdown} - copied from ${page.source}`);
}

copyFileSync(join(ROOT, 'website', 'content', 'home.md'),
             join(ROOT, 'website', 'public', 'index.md'));
wrote.push('  website/public/index.md - copied from website/content/home.md');

console.log(`Findra ${VERSION}`);
for (const line of wrote) console.log(line);
