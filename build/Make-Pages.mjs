// The site's written pages, generated from Markdown.
//
// This is run BY HAND, when the prose changes, and never by the build - the site has no build
// step and `publish = "website/public"` is the whole of its configuration, so what is committed
// under website/public is exactly what is served. It needs node and nothing else. Run it from the
// repository root:
//
//     node build/Make-Pages.mjs
//
// There are two reasons this exists rather than four hand-written HTML files.
//
// The first is PRIVACY.md. That file is the privacy policy: it is what GitHub shows, what
// PolicyPageTests holds to its promises, and what Findra's own update check sends somebody who
// built from source. A second copy of it written out as HTML would be a second privacy policy,
// and the one thing worse than no privacy page is two that disagree. So the Markdown is the
// source, the page is emitted from it, and WebsiteTests strips both back to prose and asserts
// they still say the same thing.
//
// The second is the shell. The navigation and the footer are the same on every page, and four
// hand-maintained copies of a footer is four chances to link the wrong privacy policy.

import { writeFileSync, mkdirSync, readFileSync, copyFileSync, statSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
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
    reviewed: '2026-09-05',
    // The Markdown twin is published beside the page so an agent asking for text/markdown, and a
    // person who would rather read the file, both get the real thing rather than a rewrite.
    ogType: 'website',
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
    reviewed: '2026-09-05',
    ogType: 'website',
    markdown: 'about.md',
  },
  {
    // The one page here that exists because somebody else requires it. The SignPath Foundation's
    // terms say to "use the term 'Code signing policy' on your project's home page and
    // download/release pages", so the term is the link label in the footer and in the install
    // section, and this is the page they point at. Generated from docs/code-signing-policy.md
    // for the reason PRIVACY.md is generated: PolicyPageTests holds that file to its promises -
    // including the coupling that keeps its "Not yet in force" note and the release workflow's
    // empty signing step in step with each other - and a second copy written out as HTML would
    // be a second policy that no test reads.
    slug: 'code-signing',
    source: 'docs/code-signing-policy.md',
    kicker: 'Signing',
    headline: 'Code signing policy: who writes Findra, who approves a release, and who signs it.',
    title: 'Code signing policy - Findra',
    description:
      'The team roles behind a Findra release, what the program changes on the machine, and ' +
      'what it never sends anywhere. Findra is not signed yet; this is the policy for when it is.',
    reviewed: '2026-09-05',
    ogType: 'website',
    markdown: 'code-signing.md',
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
    reviewed: '2026-09-05',
    ogType: 'website',
    markdown: 'contact.md',
  },
];

// ---------------------------------------------------------------- Markdown

// A converter for the Markdown these four pages actually use, and no more than that. Headings,
// paragraphs, bullet lists, one table, inline code, bold, fenced code, block quotes and bare
// links - which is the whole vocabulary of the four source files and is checked by a test that
// would fail if a further construction appeared and came out as literal asterisks.
//
// The block quote arrived with the signing policy and is the reason to say what "no more than
// that" costs: an unsupported construction does not fail, it renders as prose with its own
// marker still in it, so `> ` would have appeared on the page as a literal character on every
// line of the one paragraph a reader has to see first.

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

    // A block quote. Paragraphs inside one are separated by a bare `>` line rather than by a
    // blank line, because a blank line would have ended the block before this ever saw it.
    if (/^>/.test(lines[0])) {
      const quoted = lines.map((l) => l.replace(/^>[ \t]?/, '')).join('\n');
      const paragraphs = quoted.split(/\n\s*\n/).map((q) => q.trim()).filter(Boolean);
      html.push('<blockquote>\n' +
        paragraphs.map((q) => `  <p>${inline(q.split('\n').join(' '))}</p>`).join('\n') +
        '\n</blockquote>');
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
      <a href="/code-signing/">Code signing policy</a>
      <a href="https://github.com/blakazulu/findra/blob/main/SECURITY.md" rel="noopener">Security</a>
      <a href="https://github.com/blakazulu/findra/blob/main/LICENSE" rel="noopener">Apache-2.0</a>
      <a href="https://github.com/blakazulu/findra/blob/main/NOTICE" rel="noopener">Notice</a>
      <a href="/llms.txt">For language models</a>
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

function structuredData(page, url) {
  // The four generated pages carried no structured data at all, so nothing said which product
  // they were about, who wrote them, or when they were last looked at. Every value below is one
  // this file already holds - the @id references reach into the front page's own graph, so the
  // five pages resolve as one entity and nothing new is claimed here.
  // There is no PrivacyPolicy class in schema.org - the WebPage subtypes are AboutPage,
  // CheckoutPage, CollectionPage, ContactPage, FAQPage, ItemPage, MedicalWebPage, ProfilePage,
  // QAPage, RealEstateListing and SearchResultsPage. The privacy signal already lives where a
  // consumer looks for it, on SoftwareApplication.privacyPolicy.
  const types = { about: 'AboutPage', contact: 'ContactPage' };
  const graph = [
    {
      '@type': types[page.slug] ?? 'WebPage',
      '@id': `${url}#page`,
      url,
      name: page.title,
      description: page.description,
      inLanguage: 'en',
      dateModified: page.reviewed,
      isPartOf: { '@id': `${SITE}/#site` },
      about: { '@id': `${SITE}/#findra` },
      author: { '@id': `${SITE}/#blakazulu` },
      publisher: { '@id': `${SITE}/#blakazulu` },
      breadcrumb: { '@id': `${url}#crumbs` },
    },
    { '@id': `${SITE}/#findra`, '@type': 'SoftwareApplication', name: 'Findra', url: `${SITE}/` },
    { '@id': `${SITE}/#blakazulu`, '@type': 'Person', name: 'Liraz Amir', alternateName: 'blakazulu' },
    { '@id': `${SITE}/#site`, '@type': 'WebSite', name: 'Findra', url: `${SITE}/` },
    {
      '@type': 'BreadcrumbList',
      '@id': `${url}#crumbs`,
      itemListElement: [
        { '@type': 'ListItem', position: 1, name: 'Findra', item: `${SITE}/` },
        { '@type': 'ListItem', position: 2, name: page.kicker, item: url },
      ],
    },
  ];
  return `<script type="application/ld+json">
${JSON.stringify({ '@context': 'https://schema.org', '@graph': graph }, null, 2)}
</script>
`;
}

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
<meta property="og:type" content="${page.ogType ?? 'article'}">
<meta property="og:site_name" content="Findra">
<meta property="og:locale" content="en_US">
<meta property="og:url" content="${url}">
<meta property="og:title" content="${escape(page.title)}">
<meta property="og:description" content="${escape(page.description)}">
<meta property="og:image" content="${SITE}/share/card.png">
<meta property="og:image:type" content="image/png">
<meta property="og:image:width" content="1200">
<meta property="og:image:height" content="630">
<meta property="og:image:alt" content="Findra. Windows Search, but it works. Filenames in 0.33 to 2.05 ms median, straight from RAM. Nothing leaves your machine.">
<meta name="twitter:card" content="summary_large_image">
<meta name="theme-color" content="#08090c">
<link rel="icon" href="/favicon.svg" type="image/svg+xml">
<link rel="icon" href="/favicon.ico" sizes="32x32">
<link rel="apple-touch-icon" href="/apple-touch-icon.png">
<link rel="alternate" type="text/markdown" href="/${page.markdown}" title="${escape(page.title)} as Markdown">
<link rel="alternate" type="text/markdown" href="/llms.txt" title="Findra for language models">
<!-- Preconnected and linked rather than @import-ed from inside styles.css: an @import cannot
     begin until the whole stylesheet has arrived, which put four serial round trips in front of
     the first painted glyph. Both origins are already the only two the CSP permits. -->
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Quicksand:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500;700&display=swap">
<link rel="stylesheet" href="/styles.css">
${structuredData(page, url)}</head>
<body>
<a class="skip" href="#top">Skip to content</a>

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

<main id="top" tabindex="-1">
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


// The sitemap is emitted here rather than hand-kept, for the reason the footer is: a date typed
// by hand is a date nobody updates. lastmod for a generated page is that page's own reviewed
// date; the front page takes the last commit that touched the file that is served, or today
// while that file is still uncommitted. changefreq and
// priority are deliberately absent - Google has said it ignores both, and two more hand-kept
// values that can only be wrong is the defect this replaces, not a feature.
const indexPath = join(ROOT, 'website', 'public', 'index.html');
const homeMod = (() => {
  try {
    // A committed date cannot see the working tree, so an edited-but-uncommitted front page
    // would be stamped with the commit before it. Dirty means it changed today.
    const dirty = execFileSync('git', ['status', '--porcelain', '--', 'website/public/index.html'],
                               { cwd: ROOT, encoding: 'utf8' }).trim().length > 0;
    if (dirty) return new Date().toISOString().slice(0, 10);
    return execFileSync('git', ['log', '-1', '--format=%cs', '--', 'website/public/index.html'],
                        { cwd: ROOT, encoding: 'utf8' }).trim()
        || statSync(indexPath).mtime.toISOString().slice(0, 10);
  } catch {
    return statSync(indexPath).mtime.toISOString().slice(0, 10);
  }
})();
const entries = [{ loc: `${SITE}/`, mod: homeMod }]
  .concat(PAGES.map((p) => ({ loc: `${SITE}/${p.slug}/`, mod: p.reviewed })));
const sitemap = `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
${entries.map((e) => `  <url>\n    <loc>${e.loc}</loc>\n    <lastmod>${e.mod}</lastmod>\n  </url>`).join('\n')}
</urlset>
`;
writeFileSync(join(ROOT, 'website', 'public', 'sitemap.xml'), sitemap);
wrote.push('  website/public/sitemap.xml');

console.log(`Findra ${VERSION}`);
for (const line of wrote) console.log(line);
