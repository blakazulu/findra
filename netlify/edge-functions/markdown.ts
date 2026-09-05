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
// Two rules, and the second is the one that is easy to skip:
//
//  - The response says `Vary: Accept`. Without it a CDN caches whichever variant it saw first and
//    serves that to everybody, so a browser gets Markdown or an agent gets HTML depending on who
//    knocked first. It goes on BOTH branches, not only the Markdown one, for exactly that reason.
//  - A request that does not ask for Markdown falls through untouched. This function must never
//    become a router: everything else on this site is a file, and it stays a file.

import type { Config, Context } from '@netlify/edge-functions';

/// Which page is generated from which Markdown file. The values are real published files, so
/// nothing here is rendered on the fly and the two can never disagree - build/Make-Pages.mjs
/// copies the source verbatim into place and this hands back that copy.
const TWIN: Record<string, string> = {
  '/': '/index.md',
  '/about/': '/about.md',
  '/contact/': '/contact.md',
  '/privacy/': '/privacy.md',
  '/code-signing/': '/code-signing.md',
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

export default async function handler(request: Request, context: Context): Promise<Response> {
  const path = new URL(request.url).pathname;
  const twin = TWIN[path];

  if (!twin || !prefersMarkdown(request.headers.get('accept'))) {
    const passed = await context.next();
    // The HTML variant is cacheable too, and it is cacheable under the same key as the Markdown
    // one unless it says what it varied on.
    const response = new Response(passed.body, passed);
    response.headers.set('Vary', 'Accept, Accept-Encoding');
    return response;
  }

  const source = await fetch(new URL(twin, request.url));
  if (!source.ok) {
    // The Markdown twin is missing, which is a deployment fault rather than the caller's. Hand
    // back the page instead of an error: HTML the caller did not ask for beats nothing at all.
    const passed = await context.next();
    const response = new Response(passed.body, passed);
    response.headers.set('Vary', 'Accept, Accept-Encoding');
    return response;
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

export const config: Config = {
  path: ['/', '/about/', '/contact/', '/privacy/', '/code-signing/'],
};
