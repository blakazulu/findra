// The one decision in markdown.ts that can silently ruin the site for everybody who is not an
// agent, run as a table.
//
//     node tests/edge/markdown.test.mjs
//
// A browser sends `text/html,...,*/*;q=0.8`, and `*/*` matches text/markdown. A negotiator that
// asks "does anything here match" rather than "which did the caller ask for more" therefore hands
// raw Markdown to every human visitor, on every page, and the site looks broken to everyone while
// passing the readiness check it was written for.
//
// This is a node file rather than an xUnit fact because the code under test is TypeScript running
// on Deno at Netlify's edge, and the C# suite cannot execute it. It runs in CI as its own step;
// WorkflowTests asserts that step is still there, because a test nothing runs is not a test.
//
// It lives HERE, and not beside the function it tests, because Netlify deploys every top-level
// file in the edge functions directory AS an edge function. Put here, this file was bundled as
// one, and an edge function with no default export fails the build - so the site stopped deploying
// entirely and went on serving the previous commit. Only real edge functions go in that folder.
//
// It imports the real module rather than a copy. Node strips the type annotations, and the only
// import in markdown.ts is `import type`, which erases to nothing - so what runs here is the file
// that deploys, not a transcription of it.

import { prefersMarkdown, withoutNetlifyPromotion } from '../../netlify/edge-functions/markdown.ts';

const CASES = [
  // What a real browser sends. Every one of these must get HTML.
  ['text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8', false, 'Chrome'],
  ['text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8', false, 'Firefox'],
  ['text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8', false, 'Safari'],
  ['*/*', false, 'curl, which states no preference at all'],
  ['', false, 'an empty Accept header'],
  [null, false, 'no Accept header at all'],

  // What an agent asking for Markdown sends.
  ['text/markdown', true, 'asked for plainly'],
  ['text/markdown, text/html;q=0.5', true, 'ranked above HTML'],
  ['text/html;q=0.9, text/markdown;q=1.0', true, 'ranked above HTML by q value'],
  ['text/x-markdown', true, 'the older type name'],
  ['text/markdown;q=0.9, */*;q=0.1', true, 'with a wildcard underneath it'],

  // And the refusals, which are the cases a "does it match" test gets backwards.
  ['text/markdown;q=0.4, text/html;q=0.9', false, 'ranked BELOW HTML'],
  ['text/markdown;q=0', false, 'explicitly refused'],
  ['application/json', false, 'something else entirely'],
];

let failed = 0;
for (const [accept, want, why] of CASES) {
  const got = prefersMarkdown(accept);
  if (got !== want) {
    failed++;
    console.error(`FAIL  ${JSON.stringify(accept)} (${why})\n      wanted ${want ? 'markdown' : 'html'}, got ${got ? 'markdown' : 'html'}`);
  }
}

if (failed) {
  console.error(`\n${failed} of ${CASES.length} Accept headers negotiated wrongly.`);
  process.exit(1);
}
console.log(`${CASES.length} Accept headers negotiated correctly.`);

// ---- Netlify's advertisement, taken back out ----
//
// The head of /numbers/ exactly as Netlify served it on 22 September 2026: our own four lines with
// its comment and two meta tags written in among them. The stripping was deleted once by a commit
// titled as documentation and every page carried the link again the same day; this is what makes a
// second deletion fail before it deploys.

const OURS = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>How fast is Findra? Measured filename and full-text search times</title>
</head>`;

const SERVED = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<!-- This site is hosted on Netlify. Anyone can build and deploy a site
     like this one for free: https://netlify.new/?utm_campaign=ai-legible&utm_source=comment&utm_medium=referral&utm_id=0a463e70-ff22-4f1e-960a-9aa6c2620330
     Netlify hosting facts for this site: static/SSR served via Netlify Edge. -->
<meta name="hosting-provider" content="Netlify">
<meta name="netlify-deploy" content="https://netlify.new/?utm_campaign=ai-legible&amp;utm_source=meta&amp;utm_medium=referral&amp;utm_id=0a463e70-ff22-4f1e-960a-9aa6c2620330">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>How fast is Findra? Measured filename and full-text search times</title>
</head>`;

const cleaned = withoutNetlifyPromotion(SERVED);
const promo = [];
if (cleaned !== OURS) promo.push('the served page, cleaned, is not exactly our page:\n' + cleaned);
if (withoutNetlifyPromotion(OURS) !== OURS) promo.push('a page with nothing injected was changed');
if (/netlify\.new|hosting-provider|netlify-deploy/i.test(cleaned)) promo.push('the promotion survived');

// Our own comments are ours. Only Netlify's is taken out.
const commented = '<head>\n<!-- Every figure on this page is a row of the README. -->\n</head>';
if (withoutNetlifyPromotion(commented) !== commented) promo.push('one of our own comments was removed');

if (promo.length) {
  for (const p of promo) console.error('FAIL  ' + p);
  process.exit(1);
}
console.log("Netlify's promotion is taken out, and nothing of ours is.");
