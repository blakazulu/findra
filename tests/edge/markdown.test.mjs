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

import { prefersMarkdown } from '../../netlify/edge-functions/markdown.ts';

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
