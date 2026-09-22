// Content negotiation for text/markdown, per the convention at acceptmarkdown.com.
//
// An agent that asks a page for `Accept: text/markdown` gets the Markdown the page was generated
// from, at the page's own URL, rather than being told to go and find a different one. A browser
// asking for HTML is untouched.
//
// This is the ONE piece of the site that is not a static file, and it is worth saying why, because
// netlify.toml opens by boasting that `publish` is the whole configuration. Negotiation cannot be
// done with headers or redirect rules: Netlify's redirect conditions do not read Accept, and a
// static file cannot vary on a request header by definition. Either the site answers the header
// or it does not.
//
// Three rules, and the second is the one that is easy to skip:
//
//  - The response says `Vary: Accept`. Without it a CDN caches whichever variant it saw first and
//    serves that to everybody, so a browser gets Markdown or an agent gets HTML depending on who
//    knocked first. It goes on BOTH branches, not only the Markdown one, for exactly that reason.
//  - A request that does not ask for Markdown gets the file it asked for. This function must never
//    become a router: everything else on this site is a file, and it stays a file.
//  - The one change made to that file on the way out is taking Netlify's advertisement back out of
//    it (withoutNetlifyPromotion, below). Nothing of ours is ever rewritten.

import type { Config, Context } from '@netlify/edge-functions';

/// Which page is generated from which Markdown file. The values are real published files, so
/// nothing here is rendered on the fly and the two can never disagree - build/Make-Pages.mjs
/// copies the source verbatim into place and this hands back that copy.
const TWIN: Record<string, string> = {
  '/': '/index.md',
  '/features/': '/features.md',
  '/why/': '/why.md',
  '/numbers/': '/numbers.md',
  '/faq/': '/faq.md',
  '/install/': '/install.md',
  '/about/': '/about.md',
  '/contact/': '/contact.md',
  '/privacy/': '/privacy.md',
  '/code-signing/': '/code-signing.md',
  '/changelog/': '/changelog.md',
  '/windows-search-not-finding-files/': '/windows-search-not-finding-files.md',
  '/search-inside-pdfs/': '/search-inside-pdfs.md',
  '/find-photos-by-description/': '/find-photos-by-description.md',
  '/search-recordings-by-speech/': '/search-recordings-by-speech.md',
};

/// Whether the caller would rather have Markdown than HTML.
///
/// The q values matter and are not decoration. A browser sends
/// `text/html,application/xhtml+xml,...,*/*;q=0.8`, and `*/*` matches text/markdown - so a naive
/// "does any of this match" test hands Markdown to every visitor with a browser. What is compared
/// is the weight the caller put on Markdown against the weight it put on HTML.
export function prefersMarkdown(accept: string | null): boolean {
  if (!accept) return false;

  let markdown = -1;
  let html = -1;

  for (const part of accept.split(',')) {
    const [raw, ...params] = part.trim().split(';');
    const type = raw.trim().toLowerCase();
    if (!type) continue;

    let q = 1;
    for (const p of params) {
      const [k, v] = p.split('=');
      if (k?.trim().toLowerCase() === 'q') q = Number(v) || 0;
    }

    // A wildcard is a floor under everything, never a vote for Markdown in particular.
    if (type === 'text/markdown' || type === 'text/x-markdown') markdown = Math.max(markdown, q);
    else if (type === 'text/html' || type === 'application/xhtml+xml') html = Math.max(html, q);
  }

  return markdown > 0 && markdown > html;
}

// Netlify writes an advertisement into every HTML page it serves: a comment with a netlify.new
// link carrying campaign parameters, and two meta tags, one of them the same link. The site's
// settings did not stop it (built_with_badge_enabled was already false), and a page that promises
// to fetch nothing from anybody else should not ship somebody else's tracking link either. None of
// it is in our files, so it is taken out here, on the way out, and never in the sources.
//
// This was removed once by a commit titled as a documentation change, and every page went back to
// carrying it the same day. tests/edge/markdown.test.mjs runs this against the markup Netlify
// actually served, so a second removal fails there before it reaches a deploy.
const NETLIFY_PROMO = /[ \t]*<!-- This site is hosted on Netlify\.[\s\S]*?-->[ \t]*\r?\n?/g;
const NETLIFY_META = /[ \t]*<meta\s+name=["'](?:hosting-provider|netlify-deploy)["'][^>]*>[ \t]*\r?\n?/gi;

/// A page as it would be served without Netlify's promotion in it. Nothing else is touched.
export function withoutNetlifyPromotion(html: string): string {
  return html.replace(NETLIFY_PROMO, '').replace(NETLIFY_META, '');
}

/// A response passed through from the origin, less the promotion, and saying what it varied on:
/// the HTML variant is cacheable too, and under the same key as the Markdown one unless it says so.
async function passThrough(context: Context): Promise<Response> {
  const passed = await context.next();
  const headers = new Headers(passed.headers);
  headers.set('Vary', 'Accept, Accept-Encoding');
  headers.delete('netlify-hosting');

  if (!headers.get('content-type')?.toLowerCase().includes('text/html')) {
    return new Response(passed.body, { status: passed.status, statusText: passed.statusText, headers });
  }

  // The body changes length, so a length the origin declared no longer describes it.
  headers.delete('content-length');
  return new Response(withoutNetlifyPromotion(await passed.text()),
                      { status: passed.status, statusText: passed.statusText, headers });
}

export default async function handler(request: Request, context: Context): Promise<Response> {
  const path = new URL(request.url).pathname;
  const twin = TWIN[path];

  if (!twin || !prefersMarkdown(request.headers.get('accept'))) return passThrough(context);

  const source = await fetch(new URL(twin, request.url));
  if (!source.ok) {
    // The Markdown twin is missing, which is a deployment fault rather than the caller's. Hand
    // back the page instead of an error: HTML the caller did not ask for beats nothing at all.
    return passThrough(context);
  }

  // This branch builds its own response, so Netlify's [[headers]] rules for /* never reach it -
  // the two pass-through branches above mutate the origin response and keep them, and this one
  // started from a blank object and silently dropped every one. The site's whole argument is that
  // the policy holds; "except on the negotiated branch" is not a policy anyone can reason about.
  // The Content-Security-Policy is deliberately not among them: default-src constrains nothing on
  // a text/markdown body, and a second copy of it here is a second policy to keep in step.
  return new Response(await source.text(), {
    status: 200,
    headers: {
      'Content-Type': 'text/markdown; charset=utf-8',
      'Vary': 'Accept, Accept-Encoding',
      'Cache-Control': 'public, max-age=0, must-revalidate',
      'X-Content-Type-Options': 'nosniff',
      'Referrer-Policy': 'strict-origin-when-cross-origin',
      'X-Frame-Options': 'DENY',
      'Permissions-Policy': 'camera=(), microphone=(), geolocation=(), interest-cohort=()',
      'Link': `<${twin}>; rel="alternate"; type="text/markdown"`,
    },
  });
}

// Every path, and not only the pages with a twin: a mistyped URL is answered with 404.html, and
// Netlify writes the same promotion into that. The files that are never HTML are excluded so the
// fonts, pictures and Markdown twins do not pay for a function that would hand them back untouched.
export const config: Config = {
  path: '/*',
  excludedPath: ['/fonts/*', '/shots/*', '/share/*', '/.netlify/*',
                 '/*.css', '/*.js', '/*.md', '/*.txt', '/*.xml', '/*.png', '/*.ico', '/*.svg', '/*.woff2'],
  // A fault here must never cost somebody the page. Bypassed, the page is served as Netlify
  // would serve it, advertisement and all, which is the lesser failure.
  onError: 'bypass',
};
