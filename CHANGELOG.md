# Changelog

All notable changes to Findra are documented here.

The format follows [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and Findra follows [Semantic Versioning](https://semver.org/spec/v2.0.0/).

## [Unreleased]

### Added

- **The site answers the questions people actually ask before installing.** A Questions section on
  the front page, and the same answers in the Markdown the site serves to agents: what Findra is,
  whether it reads inside files, how fast it is, what leaves your machine, how to install it,
  whether it can find a photo by description or search what was said in a recording, and how much
  disk the optional models need. Every answer stands on its own and every number in one is a
  measured number with its machine named.

- **A section saying how Findra differs from the search Windows already has**, in four points that
  can each be checked on your own machine. No number is quoted against any other named product.

- **The four written pages carry structured data.** About, Contact, Privacy and the code signing
  policy now say, in a form a machine reads, which product they belong to, who wrote them and when
  they were last looked at. They had none, so each was an orphan next to a front page that
  described the whole product.

- **Check now answers you.** Pressing it in Settings brings up a panel that says either "You have
  the latest version" with a Close button, or names the new version with Not now and Update now.
  Update now runs the upgrade the way you would have run it yourself - `winget upgrade
  blakazulu.Findra` in a window you can watch, or the releases page if you installed some other
  way. Findra still never replaces itself. The answer used to appear in a line of text three rows
  above the button, which almost nobody noticed.

### Changed

- **The website quotes the current benchmark run.** The front page, the Markdown it serves to
  agents and the share image were still showing a measurement taken against a debug build with ten
  documents in the index: a filename came back in 0.33 ms and the page said 0.50, the slowest query
  median was 2.05 ms and the page said 3.99. The site was understating the product while promising that
  no number on it was one you could not reproduce.

- **The About page no longer says the first release has not been tagged.** It has been tagged since
  4 September. That page is the one a search engine reads for whether a product exists yet, and it
  was telling everybody to build from source.

- **The download sizes agree with each other everywhere.** The summary the site serves to agents
  still totalled the models at 2.9 GB and priced the meaning model at 270 MB, both of which stopped
  being true when that model went to full precision. It is 3.7 GB and 1.04 GB, and a test now holds
  every surface to the same figures.

- **The first-run screen's pricing is described the right way round.** The page said the sizes
  shown there are counted against what you have already picked. They are not, deliberately: each
  row is priced at its own files so the number does not move when you tick it, and the line along
  the bottom is what the selection costs. Settings is the surface that prices the other way.

- **The type loads sooner.** The two typefaces were fetched by an instruction inside the stylesheet,
  which cannot start until the whole stylesheet has arrived - four round trips before the first
  letter was drawn in the right font. They are requested from the page itself now, with the
  connections opened early.

- **The screenshots below the fold load when you reach them**, rather than all six at once with the
  one at the top, and the first-run picture is declared at the size it actually is. It had grown
  taller without its measurement following, so the page jumped when it arrived.

- **The install section is ordered by what works**: the installer first, winget second as
  pending, and building from source last.

- **The 404 page's footer lists everything the other footers list.** It had quietly fallen two
  links behind.

- **The install command says it is not in the catalogue yet.** `winget install blakazulu.Findra` is
  printed on every surface as the whole install, and the manifest is still waiting on a moderator
  at Microsoft, so the command does not work yet. Each place that prints it now says so and points
  at the installer on the releases page, which does. This comes back out when the manifest merges.

- **The hardware caveat mentions arm64.** Findra ships an arm64 installer and recommends it in the
  same breath as x64, and no arm64 machine has ever started it. The caveat said so about AMD and
  Intel graphics and stopped there.

- **Two questions that existed only in the version for machines now appear on the page**: whether
  Findra needs a graphics card, and whether it needs administrator rights. So does the sentence
  saying Findra is not made by Microsoft and is not a version of Windows Search, which had been
  everywhere except the page a person reads.

- **The comparison with Windows Search is in the Markdown the site serves to agents**, not only in
  the page. It was the answer to one of the highest-intent questions this product can be asked and
  it was missing from all three machine-readable surfaces.

- **The update check works at all.** It has never once succeeded: it capped the reply from GitHub
  at 64 KB, and a Findra release announcement is its whole changelog section, which is larger than
  that. Every launch failed quietly and no copy could ever have told you a new version existed.

- **Indexing takes three quarters of its duty cycle by default, up from half.** The first pass is
  the one time the work is genuinely urgent and it only happens once, so holding the machine back
  by half was doubling the wait for somebody's own files to become searchable. Settings, under
  Content, still offers 25, 50, 75 and 100 for anyone who would rather it stayed out of the way.

- **Findra reads inside documents about three times faster.** The model that works out what a
  document means now runs on your graphics card instead of your processor, where it had always run
  for no recorded reason. Measured on one desktop, 134 text segments a second became 408. Searching
  still runs on the processor, and one content search costs about 4 ms more than it did.

- **The meaning model is a bigger, more accurate download: 270 MB becomes 1.04 GB.** The small
  version was a compressed one, and compressed models do not give the same answer on a graphics
  card as they do on a processor - far enough apart that a document stored by one and searched by
  the other would score wrongly, and would start scoring differently again after a driver update.
  The full-size model gives an identical answer on both. Taking everything Findra offers is now
  3.7 GB rather than 2.93 GB, and `findra --searchmodels` prints the agreement it measured.

- **The README's numbers were measured again, on a machine that has really used Findra.** The old
  ones came from a debug build against a content index holding ten documents, so the full-text
  table measured the query path rather than a corpus. This run is the released build, with every
  model installed and 6,258 files read off a real disk, and both accelerators loaded. A filename
  now comes back in 0.33 to 2.05 ms median across five measured name queries, where the old run
  said 0.50 to 3.99.

### Fixed

- **The keyboard can reach the copy buttons.** The two install commands copy on click and were not
  reachable by keyboard at all. Everything focusable now shows where the focus is, which nothing on
  the site did, and two pieces of small print were too faint to pass a contrast check.

- **Markdown responses carry the same security headers as the pages.** Asking a page for its
  Markdown got an answer with the referrer, framing and permissions policies missing, because that
  reply was built from scratch rather than from the one the rest of the site gets.

- **The sitemap's dates are generated rather than typed.** All five said the same hand-written day
  and nothing updated them, which is the state in which a search engine stops believing them.

- **The install command is drawn in the right typeface again.** Making it reachable by keyboard
  reset its font to the body face at the wrong size, and focusing any button squared its corners.
  Both were introduced in this same pass.

- **The second copy button is a button too.** One of the two was converted and the other was left
  as it was, so the command in the closing section still could not be reached without a mouse.

- **The headline figure was still the old one in six places**, including the description every
  shared link shows, the ticker, and two lines of prose that called it measured.

- **The headline speed is a range now, not the best case.** "0.33 ms median" replaced "under a
  millisecond" and was worse: 0.33 ms is the fastest of five measured queries, where the sentence
  it replaced was at least true of three. Every surface that prints it without naming the query
  now says 0.33 to 2.05 ms across five measured queries, and the share image says the same.

- **The install command is qualified on every surface, not four of nine.** The About page, both
  Install sections, the README and the answer an agent reads first still called winget the whole
  install, and two files contradicted themselves sixty lines apart.

- **The hardware caveat sits beside the numbers**, not only inside an answer far below them, on
  every surface that prints them.

- **The copy confirmation can be announced.** It was a live region nested inside a button, which
  ARIA prunes, so the copy succeeded silently for a screen reader. The visible chip stays; the
  announcement moved beside it. The chip also failed contrast by four hundredths, and the second
  button's visible text was not part of its accessible name, so speech input could not activate it.

- **The skip link works on the 404 page too**, which was the one page the previous pass missed.

- **The install instruction leads with the route that works.** Every place Findra says how to
  install it opened with `winget install blakazulu.Findra`, which prints "No package found" until
  the submitted manifest clears moderation, and put the correction in the sentence after. Anything
  that quotes a paragraph keeps the opening and drops the caveat, so the most-quoted answer the
  product has was an instruction to run something that fails. The installer on the releases page
  is named first now, with its address, and winget is named second as pending.

- **The speed claim says it is a median.** "0.33 to 2.05 ms" is the range of five medians and the
  worst single sample is above it, so the range stood unqualified on eleven surfaces while the
  table below it disagreed.

- **The stat card no longer credits one query with another query's worst case.** It named "config"
  and then gave 2.48 ms, which is sunset's; config's worst was 0.44.

- **The mock window shows measured numbers.** Three invented latencies sat in the page text under
  a sentence promising every number on the page is reproducible.

- **The benchmark figures are held by tests wherever they appear.** The upper end of the range and
  the worst sample were in fifteen places, including a baked share image and the ticker, with
  nothing tying any of them to the README. Re-running the benchmark would have updated the tables
  and left the rest stale, silently.

- **The speed answer gives the range, not the best case.** "How fast is Findra?" opened with the
  fastest of five queries and then quoted the worst single sample, so the two read as one
  measurement's low and high. It gives both ends of the range now and names the query at each end.

- **The mock window stops timing a photo search.** Two of its three rows carried name-query
  timings, one of them on a match found by what a photograph shows, which is a speed Findra has
  never measured. One query returns its rows in one round trip, so there is one time on the row
  that earned it.

- **The front page renders without JavaScript.** Most of it was invisible until a script ran,
  while the file that tells agents how to read the site said every page is readable without one.

- **The share image cannot go stale unnoticed.** The line drawn into it is written out beside it
  in the same pass, so a test can tell whether the picture was really redrawn rather than only
  the constant edited.

- **The site ships the icons a browser actually asks for.** Only an SVG was published, so anything
  that will not use one requested `/favicon.ico` by path and got the 404 page, and an iPhone
  saving Findra to its home screen had no icon at all. Both come out of the same drawing as every
  other copy of the mark, and a test holds them to it.

- **The three footers list the small print in the same order.** They carried the same eight links
  in three different sequences, so the licence moved as you walked between pages. The guard that
  existed compared three link texts on one page; it compares the whole ordered list on all six now.

- **The screenshots go out as AVIF and WebP, at the size they are shown.** They were 820-pixel PNGs
  sent whole to a phone that renders them about a third that wide, which is most of what the page
  weighs. The files in the repository are untouched and the original PNG is still the fallback, so
  the pictures are the same renders and the printed command still reproduces them.

- **Findra tells the search engines when the site changes.** A new page on a four-day-old address
  with nothing linking to it waits a long time to be found. `node build/Ping-IndexNow.mjs` says
  what it would submit and sends nothing; `--send` submits. It is run by hand, like the other
  generators, because a submission is a claim that the pages really changed.

- **The contributor guide lists the script that submits the site, and what the mark generator
  actually writes.** Both were missing from the command list, which is the one place somebody
  looks before running anything.

- **The website stopped telling machines that Findra cannot be downloaded yet.** The structured
  data the page carries for search engines and assistants still said the release was a pre-order
  and pointed its download link at the repository root rather than at the releases page. Neither
  is visible to a reader, which is why both survived the release.

- **The code signing page no longer says Findra is unreleased.** It opened by explaining that the
  application to the SignPath Foundation could not be made until a release existed, which stopped
  being true the moment 0.1.0 shipped. It still says plainly that nothing is signed yet, because
  nothing is.

## [0.1.0] - 2026-09-04

The first release. Everything below is the whole of Findra as it stands: the three
processes and the pipe between them, name search, content search and the capabilities
behind it, the four painted surfaces, the installer, and the diagnostics that are how
all of it gets verified without a screen.

### Added

- **`findra --searchindex why:<path>` explains one file.** Every other diagnostic describes the
  whole index; none of them could say anything about a single file, which is the only thing anybody
  asks about. It says whether the file is on the disk, whether anything about its kind or your
  skipped folders stops it being read, whether the index has it, what state it is in, what came out
  of it, and whether it has been edited since. Put a `q:` beside it and it scores that file's own
  vectors against the query and says whether each cleared the threshold the search actually uses.
  It reads and changes nothing.

- **Search by name across NTFS volumes.** An elevated helper reads the Master File
  Table and the change journal and holds the name index in memory; the interface runs
  unelevated and asks over a local named pipe. Exactly one call needs administrator
  rights, and it is the one that opens the volume.

- **A query grammar** covering words, phrases, globs, negation, `ext:`, `type:`,
  `in:`, `size:`, `modified:`, `created:`, `accessed:`, `case:`, whole-word, regular
  expressions, and alternatives.

- **The results card and the desktop capsule**, drawn directly with Skia. The card
  unfolds from the capsule, takes the keyboard, opens a result, and drags a row into
  Explorer.

- **Six palettes**, three dark and three light, derived from four colours each. The
  pair you choose follows the Windows light and dark setting, or pins to either.
  `%APPDATA%\Findra\palettes.json` accepts your own.

- **A global hotkey with a fallback chain**, because the first combination is already
  taken on some machines. Findra says which one it registered, and says so plainly if
  none could be.

- **A tray icon** carrying the version, the hotkey, and the update state.

- **An application icon.** Findra's mark - a lens with the capsule's own search field cut
  out of it - on the taskbar, in Alt-Tab, on the Start-menu and desktop shortcuts, in
  Explorer, on the installer, and in the Add and Remove Programs entry. It ships at every
  size Windows asks for, including the two that only appear at 125% and 150% display
  scaling, and the smallest sizes are drawn differently rather than shrunk. The tray paints
  the same mark in whichever palette is in force, and the website carries it as its favicon
  and in its header. One set of numbers produces all of them.

- **A version check**: one anonymous request to the GitHub releases API, at most once
  a day, in the background. It never installs anything, and switching it off means no
  request is made.

- **Settings** in `%APPDATA%\Findra\config.json`, which cannot stop the application
  starting however badly they are edited.

- **Six diagnostic modes** so the product can be verified without a screen:
  `--searchprobe`, `--searchtest`, `--searchshot`, `--searchindex`, `--searchmodels`,
  `--searchbench`, plus `--version`. All six run, and every one of them reports on
  something that exists, and every one of them prints Hebrew and the separator the
  card uses without mangling them, whatever code page the console was left on.
  `--searchprobe` reports the state of the pipe whether it answered or not, so the
  line that says which way it went is there to read either way.

- **The content store**: a SQLite full-text index with a recorded schema version, a
  resumable work queue, and a consumed journal position per volume, so an interrupted
  index continues rather than starting over.

- **A memory-mapped vector store** for meaning-based search: every embedded photo,
  document chunk or transcript segment is a half-precision row tagged with its kind,
  appended by the indexer and ranked by a normalised dot product over the whole file -
  brute force on purpose, since a single pass beats an index below a million rows. A
  deleted or replaced file's row is zeroed rather than removed, so it can never answer
  a query again, and a reader only ever sees rows the writer has flushed.

- **The groundwork for searching inside photos, recordings and meaning**: the model store
  that knows which model files are present and what they cost, the accelerator selection that
  tries the graphics card first and falls back to the processor while recording every provider
  it rejected and why, and the vector store the embeddings live in.

- **The encoders that turn a picture or a passage into a vector**: SigLIP-2's image and text
  towers, which put a photo and the words you would use to look for it in the same space, and
  multilingual e5 for documents and transcripts. Each one opens on whichever execution provider
  will have it, so there is a single answer to what this machine chose and why. Indexing asks
  for the graphics card, where the work is measured in hours; typing a query runs on the
  processor, where it is measured in milliseconds.

- **A privacy policy** at `PRIVACY.md`, saying what Findra stores, where, and the single
  request that leaves the machine. It states plainly that the index holds the text of the
  documents it indexed and is not encrypted, which is one reason content indexing is off
  until you ask for it.

- **A security policy** at `SECURITY.md`, so a vulnerability has somewhere to go that is not
  the public issue tracker. It names the private reporting form, says plainly what is in scope -
  the elevated helper, the pipe between it and the interface, the decoders that read files
  nobody vetted, the installer, and the two things that use the network - and what is not,
  including the unsigned downloads and the unencrypted index, both of which are written down
  elsewhere rather than being defects to report.

- **Resumable model downloads, fetched by the interface and never by the indexer.** A model
  file already on disk at its full size is never re-requested; a partial one resumes from
  the byte already fetched rather than starting over; a connection that closes early leaves
  a short file on disk to resume from instead of promoting it under the final name; and a
  `.part` from a file that has since been republished smaller is discarded and re-fetched
  from the start rather than mistaken for a finished download. A download that was killed
  between its last byte and being renamed into place is recognised as the finished file it
  is and kept, which on the Hebrew model is 1.5 GB nobody has to fetch twice.

- **Capabilities you install one at a time, priced by what they actually add.** Searching
  photos and video, reading meaning out of documents, and transcribing speech are separate
  downloads, and speech in Hebrew is a refinement of speech rather than a substitute for it.
  They are not independent of each other: a transcript is searched exactly like a document, so
  taking speech takes the document models with it, and Hebrew needs the general speech model
  that decides which recordings are Hebrew in the first place. Every size Findra shows you is
  what that row would add to what you have already chosen, so speech costs 818 MB on its own
  and 547 MB once documents are in, and the total is the files themselves rather than the sum
  of the rows. What is installed is read from the files on disk rather than from a setting, and
  a capability missing half its files counts as absent.

- **Sound, video frames, and the words inside pictures.** A recording's sound track is decoded
  with the codecs Windows already has and transcribed, and the lines are gathered into windows
  a whole sentence fits inside, so a phrase is still findable when the speaker paused in the
  middle of it; a recording in Hebrew is transcribed a second time by a model that reads it
  properly. A video is sampled across its whole length rather than by a fixed step, so a
  ten-hour film costs the same handful of frames as a ten-minute one. And the words inside a
  screenshot are read by the recognisers Windows already ships - no download, nothing to
  install, and nothing said when a language is not on the machine.

- **A picture on the card.** Selecting a result now shows the real thing: a photo decoded at
  preview size with its orientation honoured, the thumbnail Explorer would show for the
  formats Skia cannot read, and - for a video matched at a moment in it - that frame rather
  than the poster.

- **The indexer reads each kind of file only if the capability for it is installed**, and one
  missing capability never stops the rest. With photos installed and speech not, a picture is
  read and a sound file waits - both in the same pass, neither reported as a failure. A file
  waiting for a model says so, so the capability that arrives later picks up exactly those
  files and nothing else. Video counts as worth opening for either its frames or its sound
  track, so a machine that took only speech still gets its videos transcribed; a long video
  whose frames were read is recorded as read, with a note about the sound track that was not.
  Words in documents keep working with no model on the machine at all.

- **Content search now answers by meaning, not only by exact words.** A document that never
  says "lease" but sets out what a tenant pays is found by a search for "lease", and a photo is
  found by what is in it. Each half runs only if its models are installed, and one that is not
  simply contributes nothing: a machine that took no model searches the words in its files
  exactly as before, and a search that would have been answered by a capability you have not
  got says so and offers it, rather than reporting a failure. An index with nothing in it yet
  is still explained by the index and never by a missing model. A file found both by its words
  and by its meaning outranks one found by meaning alone, a transcript answer carries the
  moment it was said so the file opens there, and `ext:`, `size:` and the rest of the query
  language apply to everything found, whichever half found it.

- **A replaced, deleted or newly unreadable file gives its vectors back.** The rows an edited
  document's old text was embedded at are retired when the new ones are written, so the old
  version stops answering queries beside the new one, and a deleted photo stops answering them
  at all.

- **A capability installed later reads exactly the files it covers, and nothing else.** Adding
  meaning in documents opens the documents again and leaves the photos alone; adding photos
  opens the pictures and the video. Files nothing could help are left where they are - a
  document too large to read whole, or one with no text in it. Findra records which
  capabilities it has already caught up on, so the catch-up happens once instead of on every
  launch, and a capability taken after another that shares the same models still clears its own
  backlog rather than inheriting the other's. A file that genuinely could not be read is never
  retried on a loop.

- **Raising the transcription limit picks up exactly the recordings it newly covers**, including
  a long video that was indexed for its frames while its sound track went unheard. Nothing
  already transcribed is transcribed again, and lowering the limit deletes nothing - it applies
  to what has not been read yet.

- **The card and the capsule say which state indexing is in** rather than looking idle. An index
  nobody has asked for reads as off, with an invitation to turn it on; one turned off after
  reading something says how much it already holds; and a backlog left behind by a closed
  Findra still says that instead.

- **`--searchmodels` reports which model files are on disk against what the table declares**,
  what each capability would cost to turn on and how many of its files you already have, and -
  the point of the mode - which execution provider each runtime chose and every one it rejected,
  with the reason, so "it's slow on my laptop" becomes "DirectML failed to initialise, fell back
  to the processor". A machine with nothing installed still gets a complete report and exits
  cleanly, because a missing model is a normal state and not a failure. The size on disk is
  printed beside the size the table declares and read against it to within the table's own
  rounding, so a file that arrived whole reads as whole and only a file that really is short
  is called out.

- **`--models` and `--content`: take a capability, and ask Findra to start reading, without a
  screen.** `findra --models` lists what is on this machine, what each capability would add to
  it, and what the whole set costs, with the two things that need no model at all - the words in
  your documents and the words inside your pictures - named first and marked free, so taking
  nothing still reads as a working search. `findra --models install recommended` says what it
  will fetch and what that comes to before fetching a byte, skips anything already on disk,
  shows one line of progress, resumes from an interrupted download rather than starting over,
  and then queues exactly the files the new capability can now read. `findra --content on` is
  the switch that starts reading inside files at all, `--content off` stops it and throws
  nothing already read away, and `findra --content` says which of those states you are in
  rather than letting an index nobody asked for look finished. `findra --content limit` sets
  how long a recording is worth transcribing, in the same words the settings screen will use,
  and refuses anything it cannot read instead of quietly turning transcription off.

- **`--searchindex` says whether anybody has asked for any of this**, above the counts, because
  an index nobody has turned on and a finished index have identical numbers. Below them it
  lists every capability with how many files are sitting waiting for exactly it, so "eight
  thousand skipped" becomes "eight thousand photos waiting for the photo models" and it is
  clear which download would clear them. It also says how long a recording is worth
  transcribing, in words rather than as a bare number, and counts the recordings passed over
  for their length apart from the ones waiting for a model - one is cleared by a download and
  the other by a setting, and a single total says nothing about which.

- **The benchmark names the silicon that answered, and it is two answers.** Documents and
  pictures go through DirectX 12 where there is a device for it, speech goes through Vulkan,
  and either falls back to the processor on its own - so a machine can run one accelerated and
  the other not, and the published numbers now say which. A runtime with no model on the
  machine says so rather than claiming a processor fallback for work that never ran.

- **`--searchtest` checks the capability graph itself**: that closing a selection twice changes
  nothing, that no capability claims a kind of file with nothing to read inside it, and that
  the whole model set still measures the 2.93 GB quoted everywhere else. It runs on a machine
  that has downloaded nothing, which is the point.

- **One version number, and a release that cannot ship without its notes.** The version Findra
  reports in the tray, in the log header and in every diagnostic mode now comes from a single
  place, so the number in a bug report is the number that was built. The update check reads it
  as a number rather than as text, and a build whose version could not be read says so instead
  of quietly telling you that you are up to date. Tagging a release checks that the tag, the
  built version and this file all agree before anything is published, and the notes for a
  release are this file's section for that version - so a tag nobody wrote anything about does
  not become a release.

- **One shape behind the settings and first-run screens.** Both are laid out by the same
  geometry and drawn from the same set of parts, so they read as one object seen twice rather
  than two screens that drifted apart. An explanatory sentence under a setting pushes the
  settings below it down by exactly the room it needs instead of being drawn over them, the
  pane never scrolls, and a click lands on the control under the pointer or on nothing at all.
  `--searchtest` now measures readable contrast on the raised surface those screens are built
  from as well, so a hand-written `palettes.json` that would be illegible there is caught
  before it reaches the screen.

- **Findra ships its own typeface.** Every surface - the capsule, the card, the settings and
  first-run screens, and the images `--searchshot` writes - is now drawn in Quicksand, which
  travels inside the application rather than being borrowed from whatever the machine happens
  to have. The screenshots in the documentation are therefore the product on any machine that
  regenerates them, instead of the product as one particular computer renders it. Quicksand is
  bundled whole and unmodified under the SIL Open Font License 1.1, and that licence sits
  beside the application in every copy. If the font is ever missing, Findra falls back to the
  system face and says so in the log rather than refusing to start.

- **`findra --uninstall` takes Findra off a machine properly, and keeps your work.** It stops
  the interface, the indexer and the name helper, then removes the scheduled task that starts
  the helper when you sign in and the start-with-Windows entry, so nothing elevated is left
  pointing at a program that is no longer there. Your models, your index and your settings are
  kept. Adding `--purge` deletes those as well, after saying in measured megabytes how much
  that would free and naming any `palettes.json` you wrote yourself as something that goes with
  them. `--dry-run` prints the whole plan and changes nothing. Nothing outside
  `%LOCALAPPDATA%\Findra\` and `%APPDATA%\Findra\` is ever deleted, and one folder that will
  not go is reported instead of stopping the rest. `findra --stop` closes the three processes
  on their own.

- **Findra records how it was installed, once.** Whether this copy came from winget, from the
  installer or from a source build is read at first run and remembered, so when a newer version
  exists the advice matches the way you actually got this one instead of being guessed afresh
  at every launch.

- **Every setting Findra has, in five sections.** Look holds the light and dark palettes, the
  mode that switches between them, and a way to open the file where you write your own.
  Opening it holds the hotkey, which you rebind by clicking the row and pressing the
  combination you want, whether the capsule shows on the desktop, a way to bring it back when
  it was left on a monitor that is gone, whether Findra starts when you sign in, and a way to
  register the name helper if that never happened. What it searches holds the drives and the
  folders Findra will not open. Content holds the one switch for reading inside files, how
  long a recording is worth transcribing, and what each capability would add to what is
  already installed. About reports the version, what the last check found, and the action
  that matches how this copy was installed.

- **The settings screen is drawn, and can be looked at without opening a window.** Every
  section is painted from the same parts the rest of Findra is built from, inside the same
  accent-lit outline the results card already has, so the two read as one object seen twice.
  The section you are in is marked in the accent colour rather than by a fill a shade away
  from the one under your pointer. `--searchshot` learned five new pictures - `settings`,
  `settingsopening`, `settingssearches`, `settingscontent` and `settingsabout` - so every
  section can be reviewed in any of the six palettes on a machine that has never run Findra.

- **A copy installed from the downloaded installer is told to download the new one**, rather
  than to run a winget upgrade for a package winget has never heard of on that machine.

- **The settings window opens, and every control in it does something.** Settings is on the
  tray menu. Changing a palette repaints the window, the capsule and the tray icon with no
  restart; "Open the file" opens `palettes.json`, writing it first if it was never there;
  clicking the hotkey row listens for the next combination you press and rebinds it there and
  then, keeping the old one if the new one is already taken and saying so; the sign-in switch
  writes and removes the registry entry; the name helper's row registers the scheduled task and
  starts it in the same session; "Add a folder" opens the Windows folder picker; a capability's
  size button downloads it, resuming anything half-fetched; "Check now" asks GitHub and the
  About line changes to what it found; and "Bring the capsule back" moves the capsule now
  rather than at the next launch.

- **The capsule has a right-click menu**, so most people never open settings at all: the
  palettes of the side actually on screen with a tick beside the one in use, a switch for
  reading inside files that says plainly when it is on but nothing is running, a way into
  settings and a way out.

- **A first screen that asks once, and means it.** The first time Findra runs it shows what it
  can do and what each part would cost, as three presets over a list you can tick yourself.
  Every size is what that row would add to what you have already chosen, so the total adds up.
  Hebrew speech appears only on a machine with Hebrew on it, and only underneath ordinary
  speech, because it is a second pass rather than an alternative. Taking nothing is a complete
  answer - searching by name is always on and costs nothing - and the screen does not come back.

- **Findra registers the name helper's scheduled task itself, and starts it in the same
  session.** Until now nothing created it: the application asked the scheduler to run a task
  that had never existed, and searching by name was empty with one line in the log to say so.
  The first screen asks for the one administrator prompt Findra needs, whatever else you chose
  on it, and name search works immediately rather than after the next sign-in.

- **The update check is disclosed where you decide about it.** The first screen says in full
  what the request is, how often it is made, that it carries nothing about your files or your
  searches, and that Findra never installs anything by itself - beside the switch that turns it
  off, where off means the request is not made.

- **Downloads survive a dropped connection and say so.** Each capability gets its own progress
  bar, a file already on disk counts as done rather than as nothing, and a fetch that fails
  puts what went wrong on the screen and keeps every byte that arrived, so pressing the row
  again carries on from there. One bad file no longer stops the ones after it, and you can
  close the window and let it run in the tray.

- **An installer, for people who would rather not use a command line.** It carries the
  application and nothing else - the models are still downloaded from inside Findra, when and
  if you ask for them - and it installs into a folder with no version number in it, so an
  upgrade never leaves the name helper pointing at a program that has been replaced. Installing
  over a running copy closes the interface, the indexer and the name helper first, including
  the two that have no window to close. Removing Findra from Apps & features does exactly what
  `findra --uninstall` does: it always takes the scheduled task and the start-with-Windows
  entry with it, and it keeps your models, your index and your settings unless you tick the box
  that says otherwise - a box that starts unticked, above the measured megabytes it would free
  on that machine rather than a warning that it might be a lot.

- **Every change is built, tested and put through every diagnostic mode before it can become a
  release.** A push builds the whole tree as a warning-free release, runs the test suite, and
  then publishes the self-contained application and runs each of its headless modes against
  that published copy - the shape a stranger actually gets, with no development tools
  underneath it. A mode that quietly stopped working used to be found by the next person who
  needed it to explain something else.

- **A release comes from a tag, and a tag cannot ship without its own notes.** Tagging a version
  builds Findra for both 64-bit Intel and 64-bit ARM, runs the whole test suite and every
  headless diagnostic against the published application, compiles an installer for each, and
  publishes them on a release page whose text is this file's section for that version, word for
  word. Nothing is published until the tag has been checked against the version the application
  reports and against the section here: a tag that disagrees with either, or that carries a
  pre-release suffix, stops the release before anything is built. Nothing in it reaches the
  winget catalogue, which stays something only a person can start.

- **The downloads are not signed, and the pipeline says so where it would sign them.** The step
  is there and it prints one line explaining that the arrangement has not been made yet, because
  the alternative - a step called sign that quietly does nothing - is worse than no step at
  all. Nothing on the release page, in the installer or in this repository claims otherwise.

- **Findra can be installed with `winget install blakazulu.Findra`, and it reaches the
  catalogue only when a person sends it there.** The listing is kept here rather than in the
  catalogue: one version covering both 64-bit Intel and 64-bit ARM, the licence and the
  attribution that goes with it, and the sentence that says the 2.93 GB of models are optional
  downloads you choose from inside the app rather than the size of the package. Publishing has
  exactly one trigger, and it is somebody opening the Actions tab and starting it - no push, no
  tag and no release can reach it - and it can be run once to check the listing without
  submitting anything. A copy installed this way knows it, so a newer version is one
  `winget upgrade` rather than a file to go and find.

- **A front page made out of real renders and real measurements.** Every screenshot on the
  README is the actual card, the actual first-run screen and the actual settings window, drawn
  by the painter the application uses, with the command that produced it printed underneath so
  anybody can draw it again. Every number is a measurement from `findra --searchbench`, pasted
  whole rather than picked from, with the machine that produced it named in the same block: a
  million and a half file names enumerated in under three seconds, name queries answered in
  half a millisecond to four, and document extraction measured over ten thousand generated
  files rather than the smaller default, because a rate from a run of a second or two does not
  reproduce. Nothing on the page is a claim you cannot check with a command from the page
  itself, no other product is named or measured against, and the page says plainly that there
  is no published release yet and that the downloads are not signed.

- **Three written pages on the website: About, Contact and Privacy.** The privacy policy is
  published at https://findra-search.netlify.app/privacy/ rather than only as a file in the
  repository, because somebody deciding whether to install Findra should not have to go to GitHub
  to read what it stores. `PRIVACY.md` is still the source: `build/Make-Pages.mjs` generates the
  page from it and publishes the Markdown verbatim beside it, and the suite strips both back to
  prose and fails if a sentence exists in one and not the other. The contact page has no form and
  says why - Findra collects nothing about the people who use it, and a form would be the first
  thing on the site that did. It also says out loud that there is no telephone number and no
  postal address, and a test refuses to let either be invented.

- **A share card, drawn from the same numbers as the application icon.** The link preview on
  WhatsApp, X, Facebook, LinkedIn, Slack and Discord used a product screenshot at 820x626, which
  is 1.31:1 against the 1.91:1 those platforms crop to, so a slice was being taken out of the
  middle of it. `build/Make-Icon.mjs` now draws a 1200x630 card and a 1080x1080 square, in Mond's
  own colours and set in the application's own Quicksand, and every page declares the image's
  width, height and alt text - WhatsApp will not fetch an image to measure it and shows no
  preview at all rather than guess. The card carries the mark, so `IconTests` holds it to the icon
  the way it already holds the two SVGs, the favicon and the tray glyph. Instagram reads no Open
  Graph at all and never will; the square exists to be posted by hand and nothing serves it.

- **The site answers `Accept: text/markdown`.** Each of the four pages hands back the Markdown it
  was generated from, at its own URL, with `Vary: Accept` on both variants - without which a cache
  serves whichever one it saw first to everybody after. This is the one part of the site that is
  not a static file, because neither a header rule nor a redirect can read an Accept header. Its
  table of real Accept strings is a node test that runs in CI: a browser sends `*/*;q=0.8`, which
  matches text/markdown, so a negotiator that asks "does anything match" rather than "which was
  asked for more" hands raw Markdown to every human visitor.

- **A 404 page of the site's own, structured data, and `llms.txt`.** A missing path used to serve
  Netlify's default page, which says nothing about Findra and offers no way onward. Ours lists
  every page and every machine-readable file instead, because whoever is reading it either mistyped
  or guessed a URL and both need the real list. It does not remove Netlify's own injection: the
  platform adds a hosting-provider meta tag and a `netlify.new` referral comment to every HTML
  response on the current plan, after the file leaves the repository, and no file here can prevent
  that. The homepage carries JSON-LD naming the application, its author and its
  licence, with the price and the version checked against `Directory.Build.props` by a test.
  `llms.txt` says when to reach for Findra and, more usefully, when not to: it is a Windows desktop
  application with no API, no accounts and no MCP server, and a readiness scan of this domain had
  credited the hosting platform's own MCP server, CLI and SDKs to Findra.

- **A website, deployed to https://findra-search.netlify.app on every push to `main`.** It is
  plain static files under `website/public` with no build step, and it holds itself to the same
  rule the README does: every screenshot is a real `--searchshot` render with its command printed
  underneath, and every number is a measurement from `--searchbench` beside the machine that
  produced it. It says which install route works today and which one waits for the first tag,
  rather than offering a download that does not exist yet. A page that sells "nothing leaves your
  machine" should not itself be reaching out, so a content security policy permits the two font
  hosts and nothing else - no analytics, no beacons, and no way for one to arrive by accident.
  The page opens on the mark, the name and the results card, with the card running off the right
  of the screen rather than stopping at a gutter; the ticket that used to open it now follows it,
  and the page title and the link preview say the same thing the page now opens on. Three of its
  screenshots had drifted from the ones in `docs/shots` while still printing the command that
  produces the current renders - the Settings picture was missing two controls the product had
  gained - so all six were regenerated and the two directories now agree. They cannot drift
  again quietly: `build/Make-Shots.ps1` redraws the README's own list into both places in one
  command, and the suite fails on a picture or a printed command where the two pages disagree.

- **The results card previews the photograph it found.** The stage's picture - the centre-crop
  that shows you the file you are standing on - had never been rendered by any `--searchshot`
  state: every one of them left the image unset and took the no-picture tile instead, so the one
  surface whose whole job is showing you the file was only ever reviewed in its fallback. The
  `results` and `opening` states now carry a picture and `many` keeps the tile, which puts both
  branches on screen. The picture is drawn rather than photographed, like every other thing about
  that state: the file names, the drive letters, the scores and the Hebrew tenancy agreement are
  all invented, and what a shot has always promised is that the PAINTER is real.

- **Settings is on the card.** A Settings button sits under Advanced, in the same column as
  the Content pill, so the capability list, the transcription limit, the indexing power and the
  switch that starts reading inside files can be reached from the thing you are looking at.
  Until now Settings opened from the tray icon's menu or a right-click on the capsule, and
  nothing on any surface said so.

- **"Start now", in Settings under Content.** A toggle states a preference; this says begin, and
  the sentence above it changes to say that reading has started. It works whether the switch was
  off or already on, because Findra only reads while it is open.

- **"Indexing power", in Settings under Content**, at 25, 50, 75 or 100 per cent. It is how much
  of the machine reading may take, the indexer has honoured it since it was written, and until
  now the only way to change it was to edit `config.json` by hand.

- **The pointer says what each surface is.** The capsule takes the four-way move cursor, which is
  the first time anything has said out loud that it can be dragged; the search field takes the
  I-beam; every button, pill, chip and row on the card, in Settings and on the welcome screen
  takes the hand. Nothing that answers a click offers to move anything.

- **`--searchshot firstrunfinished`**, an eighteenth surface. The end of a first-run download
  had its own title, its own sentence and both bars full, and no command had ever drawn it.

- **A code signing policy page on the website**, at `/code-signing/`, saying who writes Findra, who
  approves a release and who signs it, and what the program changes on the machine. It is generated
  from the same `docs/code-signing-policy.md` the repository already carried, so there is one
  document and not two. Nothing is signed yet and the page opens by saying so.

### Changed

- **The README and the site now say which hardware Findra has actually been tested on.** Every
  measurement published so far comes from one desktop with a discrete NVIDIA card, plus the
  processor-only path on that same machine. AMD and Intel graphics have not been tested on real
  hardware at all. Findra's provider chains are vendor-neutral by design so that those machines
  should work, but that is a design decision rather than a measurement, and the two are no longer
  written as if they were the same thing.

- **The first screen becomes a shorter window while the files come down.** With the choices
  settled there is nothing to draw where they were, so the window closes up around what is
  left instead of leaving a hand's width of nothing under the progress.

- **While the models download, the first screen is only a status screen.** Nothing on it
  takes a click, including the button: the choice has been made and there is nothing left
  to answer. Closing the window still works, and the download carries on in the tray. A
  button appears when the last file has landed.

- **The transcription limit on the welcome screen says it covers video.** "Transcribe up to"
  under a row called Speech gave no hint that the same number decides what happens to every
  video on the disk, and the row above it is called "Photos and video", which makes video look
  like somebody else's business. The Speech row now says it transcribes recordings and videos,
  and the limit carries a note: one number for audio and video, and a video longer than the
  limit is still found by its frames if you took Photos - only its words are skipped.

- **"Look inside my files" now explains itself.** It was a bare switch label while the update
  check under it carried a paragraph, which put the longest explanation on the smallest choice
  and none at all on the largest. It now says that names are searchable either way, that Findra
  walks your drives and reads them and that this can take hours, that it happens only while
  Findra is open, and that the text it reads is kept in an index in your user profile which is
  not encrypted. The same row in Settings says where the text goes too, in every state.

- **The welcome screen stops being a form once the download starts.** The tiles, the rows, the
  transcription limit and the three switches were all still live while files were arriving,
  acting on a selection that had already been handed over. The second act is now a download and
  a way out of it: nothing but the Close button answers a click, the settings are no longer
  drawn as controls, and the sentence that reports progress follows the list it is about.

- **The welcome screen says it is downloading.** It reported "2 of 4 done", which could have
  been anything - and content indexing, a different and far longer job, may be starting on the
  same machine. It now names what it is doing, and a finished run says it has finished and
  points at the way out.

- **The README links to the project's own page** at https://findra-search.netlify.app, whose
  screenshots are the same renders from the same commands.

- **The size beside a capability on the first screen is that capability's download, and it
  stays put.** It used to be the marginal figure - what the row would add to what was already
  ticked - so ticking a row turned its number into "0 MB" and the size you were weighing
  disappeared at the moment you decided on it. Each row is now priced at its own files, which
  is also the only pricing where the four of them add up to the 2.93 GB quoted above them. The
  line along the bottom is still what the whole selection costs, and it is where a shared file
  is counted once.

- **That line is now the headline of the first screen**, set larger and in bold rather than in
  the same small grey as the notes above it. Since the rows stopped moving, it is the only place
  the download as a whole is stated.

- **The first screen asks how long a recording is worth transcribing.** Ticking Speech is what
  signs you up for transcription, and the limit defaults to five minutes - enough for a voice
  memo and not for a lecture. The control now appears under the Speech row the moment Speech is
  ticked, offering the same five choices as Settings, and goes again if Speech is unticked.

- **A capability you install is read in the same session you installed it.** Downloading one
  from settings used to fetch the files and then wait for the next launch before anything they
  cover was looked at again, which reads as a download that did not work. Both the first screen
  and the settings row now queue exactly the files the new capability covers as soon as they
  land.

- **Content indexing is off until you ask for it**, including the free full-text search of
  documents. Searching by name is always on and costs nothing. Reading inside files walks
  every drive and opens every document, so Findra no longer starts that uninvited.

- **One setting decides how long a recording is worth transcribing**, covering audio and
  video together. It defaults to short clips of about five minutes, offers longer presets
  and no limit at all, and accepts a number of minutes typed in. A recording over the limit
  is skipped rather than failed, so raising the limit later picks up exactly those files.

- **Findra now needs Windows 10 version 2004 (build 19041) or newer.** Reading words out of
  pictures, transcribing speech and pulling frames out of video all use decoders Windows only
  publishes from that build onwards, so the whole application is built against it.

- **The progress bar under the results card appears only while Findra is actually reading files.**
  It used to stay there once a pass had finished, resting at 100% and saying "up to date", along
  with three other resting states - and a progress bar that never moves is not information, it is
  a widget looking busy. The Content pill in the card's own header already says whether Findra is
  reading and whether it has read anything. The desktop capsule has always worked this way; the
  card now does too.

### Fixed

- **The release pipeline builds the installer again.** It asked the build machine to install a
  pinned version of Inno Setup, which is older than the one the machine already had, and that was
  refused as a downgrade - so the first attempt at tagging 0.1.0 stopped at a step whose job is to
  install a tool that was already there. It now takes what is on the machine when it is new enough
  and installs one only when it is not.

- **The welcome screen's last question can be answered on a machine that took speech.** Turning on
  speech search adds a "Transcribe up to" row to the list, and that row is only there while the
  list is still a question - it goes when the screen is answered. The window and the drawing knew
  that; the part that works out what is under the pointer did not, so it measured a screen one row
  taller than the one on the display. On any machine offered Hebrew, anybody who chose speech
  reached the final "Shall Findra start reading inside your files now?" and found that neither
  "Later" nor "Start reading" would light up or respond - the two buttons were being looked for
  below the bottom edge of the window they were painted in.

- **The accelerated speech runtime has to transcribe something before Findra will use it.** It was
  accepted as soon as it loaded, and the known failure on AMD integrated graphics happens after a
  clean load: everything initialises and then the transcript comes back as garbage. Findra would
  have stored that as a finished transcript and never looked at the file again. It now runs a
  one-second sample through it first and falls back to the processor if what comes back is
  malformed.

- **`--searchmodels` checks that the picture model loads on the processor too**, not only on
  whatever accelerator the machine happens to have. Running well on a graphics card says nothing
  about the machines that have none, and those are the ones that cannot afford a surprise.

- **Searching photos stops returning things that merely are not black.** The threshold below which
  a picture counted as unrelated was set at less than half the value of a real match, so it sat
  inside the noise rather than above it. Measured over a 3,097-picture library: pictures that
  genuinely matched scored 0.130 and above, unrelated screenshots scored up to 0.066, and the
  threshold was 0.05. It is now 0.09, and the scale the scores are spread across moved with it so
  a real match still lands in the upper half rather than the bottom third.

- **`--searchindex` says which files were passed over and why.** The reason each skip carries was
  recorded from the beginning and read by nothing, so "waiting for a model", "too small to be a
  picture" and "no decoder for this format" were one undifferentiated count. It is the first thing
  anybody asks when a file they can see is not findable.

- **The query diagnostic asks the same question the card does.** It requested twenty results where
  the card requests sixty-four and then lets a filter chip narrow them, so the rows somebody had
  actually complained about were the rows it could not see.

- **Only one Findra runs against one index.** Nothing stopped a second, and the two do not simply
  coexist: each starts its own indexer, and the second cannot open the store the first is holding,
  so it is restarted and fails again every five minutes for as long as both are open. The second
  hotkey also lands on a fallback combination, and a download in either writes into the other's
  half-finished files. A second launch now says which process is already running and how to reach
  it.

- **A file that crashes the indexer no longer stops everything behind it.** A file that merely
  fails to read was always recorded and skipped, but one that takes the whole indexer down never
  got that far: it was handed back to the restarted indexer, which died on it again, and the queue
  stopped at that file for good. Each attempt is now counted before the file is opened, so the
  count survives the crash, and after three the file is written off with a reason and the queue
  moves on.

- **The tray tooltip always fits what Windows can carry.** Its field holds 127 characters and the
  longest tooltip Findra can compose is 145: a long version, no hotkey, the index line at a
  seven-figure count, and an update line. Nothing measured it. It now drops from the end, so a
  tooltip that cannot hold everything loses the update state, which is the same all day, rather
  than the indexing line, which is why somebody hovered it.

- **The progress bar under the search card is visible again, on the card it exists for.** It hangs
  below the card, so whether it is drawn decides how tall the window is - and nothing asked the
  window to re-measure when it appeared. It turned on about a tenth of a second after the card
  opened and was then drawn outside a window sized without it, so it was clipped away entirely.
  Typing a character resized the card for another reason and it appeared, which is what made it
  look intermittent.

- **Upgrading actually re-reads your pictures.** The step that says images are read differently
  queued every photo on the machine and re-read none of them: it handed the indexer the sentence
  meant for the log instead of the word that means "read this again", and the indexer drops a
  queued file untouched unless it is told that. The count went down, the log said "re-queued", and
  the index was exactly as it had been. There is a new step that does it properly, because a
  machine that already ran the broken one will never run it again.

- **The indexer no longer reports itself dead while it is working.** Its heartbeat was written
  after each file finished, so one long file - a thirty-minute recording, a large document, frames
  out of a video - went fifteen seconds without one. The capsule's bar vanished, the card said
  paused, and both diagnostics said nothing was running, during exactly the operation people ask
  about most.

- **Deleting a skipped folder now does something.** Adding one was honoured and removing one was
  not: those files have no record to bring back, and the change journal only reports what changes,
  so a folder of finished work is never looked at again. Removing an entry now asks for a fresh
  pass over the drive.

- **The card, the capsule and the tray agree about whether Findra is reading.** Two of them read
  the setting and one read the switch the setting is actually written to, so during the first-run
  download the card said reading was off while the tray said it was paused because Findra was
  closed - with the welcome screen on the display.

- **A paused index stops claiming to be paused once it is not.** The note the indexer leaves about
  what it was doing is never cleared when it stops, so a session that paused and quit left it
  behind for the next one.

- **The welcome screen keeps its rounded corners when you answer it.** The window shrinks for the
  second act and the card was still being drawn to the first act's height, so both bottom corners
  fell off the bottom of the window.

- **No empty band on the download screen.** The transcription-limit row is only drawn while the
  question is on screen, but the space for it was reserved in every act, leaving a gap between two
  rows for the whole download.

- **Pressing "Everything" lights the "Everything" tile.** On machines not offered Hebrew the tile
  selects three capabilities rather than four, and the highlight was still comparing against four -
  so the tile somebody had just clicked was the only thing that did not respond.

- **The pointer stops offering to click things that cannot be clicked.** Nine rows in Settings and
  the free row on the welcome screen showed a pointing hand over controls that refuse every press.

- **`--uninstall` says what it is doing again.** It relaunches itself with administrator rights,
  and that copy has no console to write to, so the list of what is kept, the line telling you how
  to finish, and every failure went nowhere. The mode that changes nothing talked and the mode
  that does the work was silent.

- **An uninstall that cannot remove the scheduled task now says so, in both places.** The installer
  discarded the result and reported a clean removal, leaving a task that starts an elevated helper
  at every sign-in pointing at a program that is gone.

- **`--uninstall --purge` really empties the log folder.** Every log line recreated the folder, and
  the uninstall logged its own progress, so it put back the directory it had just removed and
  reported it deleted.

- **The self-check stops calling an out-of-date index a broken build.** An older index is a
  migration owed and runs the next time Findra opens; only a newer one, written by a later build,
  is a fault.

- **A hung `schtasks` no longer reports an unrelated error and leaks the process.** One of the four
  places that runs it read the exit code without checking that the process had exited.

- **A loose file that will not delete is reported.** The sweep's answer was dropped, so a purge
  left the folder standing and still exited cleanly.

- **`--searchindex` writes the drive letter the way everything else does, and honours your skipped
  folders.** A lowercase path created a second entry for a file already indexed; pointing it at a
  skipped folder indexed files the next launch immediately deleted.

- **The indexer's fallback power setting matches the setting's own default** rather than running
  flat out when the value is missing.

- **The end-to-end check no longer stops half way through on a real machine.** It looked up
  Findra's uninstall entry by reading a property that some registry keys simply do not have - a
  patch entry, a half-written key, another vendor's bookkeeping - which is a terminating error, so
  every check below that point went unrun rather than unanswered.

- **Closing the welcome screen without answering no longer leaves Findra running invisibly.** The
  X, Alt+F4 and the taskbar are never disabled, and none of them count as an answer - so
  everything the screen was holding back stayed held back: no tray icon, no hotkey, no capsule,
  no window, and no way to end it but the task manager. The next launch would then have put a
  second one behind another welcome screen. Findra now starts the rest of itself and asks again
  next time, which is the right outcome for a question nobody answered.

- **A welcome screen that fails to appear no longer locks Findra out of its own surfaces.** The
  gate that keeps the card and Settings behind the screen was taken before the screen was shown,
  and a window that throws on its way up never raises its own close - so the recovery path built
  a complete Findra whose hotkey, capsule, tray and Settings all silently refused to open
  anything, for the life of the process.

- **The switch that starts reading works in your first session.** Reading is held from the moment
  the welcome screen appears and released by "Start reading" - but a screen that never got as far
  as asking, because reading was off in the first act, had nothing to release it. The hold then
  outlived the screen for the whole session, and the Content switch in the capsule's menu and the
  one in Settings both read as on while reading nothing.

- **"Everything" no longer downloads a 1.5 GB Hebrew model on machines that are not offered it.**
  The Hebrew row is hidden where the system reads no Hebrew, and the row list, the ticks and the
  summary all knew that; the three preset tiles did not. Pressing "Everything" there selected a
  capability with nothing on screen to name it: the visible rows added to 1.45 GB, the tile said
  2.93 GB, and the download drew a progress bar for a row that did not exist.

- **Searching by a capability you just installed works without restarting, on the run that
  installed it.** The query-side encoders are opened once at startup, which on a first run happens
  before the download it just agreed to - so they were opened against an empty folder, answered
  nothing, and were never reopened. The first content search anybody ever ran came back empty on a
  machine whose welcome screen had just said everything they chose had arrived, while the indexer
  read those same files perfectly well.

- **A paused index says it is paused, not that Findra is closed.** A paused index has no indexer
  running by design, so the line checked "is anything running" first and blamed the one cause that
  was not true. It is the state the whole first-run download sits in, so it was often the first
  thing this line ever said.

- **A broken Hebrew speech model no longer takes speech search away from every other language.**
  It is a second pass over what the general model called Hebrew, but it was opened inside the
  general model's own attempt: one corrupt file threw for every recording on the disk, each was
  recorded as failed, and nothing re-queues a failure.

- **A download that stops short is refused even when the server never said how long the file was.**
  The floor is deliberately generous, so between it and the real size there was a window up to
  124 MB wide where a truncated file passed, was promoted under its real name, and then read as
  installed while failing everything that needed it. Where there is no length, the declared size
  decides.

- **A dropped connection or a full disk during a download is reported rather than thrown.** Only
  the final rename was guarded, so both left `--models install` as a stack trace instead of the
  documented "what arrived is kept", and left the welcome screen's progress bar simply stopping.

- **Every surface quotes the same download size.** Settings, the card's offer, `--models` and
  `--searchmodels` priced a capability from the capabilities already installed, and a capability
  is all-or-nothing - so a folder holding the Whisper model with no e5 pair beside it was quoted
  818 MB for a 270 MB download, while `--models install` said 270. One failed leg of a download
  leaves exactly that folder.

- **A query encoder that will not load no longer takes the other one down with it.** Both were
  opened inside one attempt, so a corrupt e5 file stopped photo search from loading at all -
  something entirely unrelated to photos.

- **A build whose own version cannot be read is reported unknown, not up to date.** Only the
  release tag was checked for this; the running version took the same route into "up to date",
  which is a claim made on no information.

- **Uninstalling forgets how this copy was installed.** It is recorded once because how a copy
  arrived cannot change - but an uninstall ends that copy. Kept, it told somebody who built from
  source once and then installed from winget to read the release notes for ever, instead of
  running `winget upgrade`.

- **Reinstalling gives you back the welcome screen, and a working Findra with it.** Removing
  Findra takes away the scheduled task that starts the name helper, and the welcome screen is
  the only thing that registers it - but the settings file, which uninstalling keeps on
  purpose, still recorded that the screen had been answered. So a reinstall skipped it and
  came up with no name search, nothing feeding the list of files to read, and a "Start now"
  that started reading and stopped again a moment later because there was nothing in the
  queue. Three unrelated-looking faults, one missing task. Uninstalling now marks the welcome
  screen unanswered, whether or not you keep the rest.

- **Settings says how far it has got, and stops offering to start what is already running.** The
  Content section reported "On. Findra reads inside files while it is running." whether the queue
  held 1,773 files or nothing at all, and "Start now" stayed pressable throughout - so on a machine
  that was already reading, pressing it changed nothing on screen and read as a dead button. The
  button now reports "Indexing 640/1,973" and refuses the press until there is something to start.

- **Findra asks before it starts reading your files, and waits until you answer.** The welcome
  screen's last page now asks whether to begin, says plainly that a first pass walks every drive
  and can take a few hours, and offers Later beside Start reading. Both close the window; Later
  changes no setting, so the next launch begins without asking again. Nothing reads while the
  question is on the screen - reading used to start ten seconds after the first page was answered,
  while gigabytes of models were still downloading over the same disk, and the indexing rate fell
  from 57 files a minute to 9 while the two competed.

- **The welcome screen stops offering to download models you already have.** It never asked the
  disk: every row printed its capability's full size and the summary priced the whole selection,
  while the download itself skipped what was there - so a reinstall over kept models offered
  2.93 GB and then filled every bar at once. Rows for what is present now read **installed**, the
  preset tiles cost what is actually still missing, the summary says so in words rather than
  "0 MB", and the button says Continue instead of Get these. A half-present capability is priced
  at the half that is missing.

- **What is already installed opens ticked.** The screen used to start with every row empty over a
  folder that already held all 2.9 GB, asking you to choose again from a list where every answer
  was already yes - and leaving a row unticked took nothing away, because what Findra can read
  comes from the files on disk and never from that selection. A machine that has the speech model
  but not the document models it needs opens with both ticked and prices only the missing half.

- **The progress pill names documents again.** Its label matched on `"Doc"` - the column heading
  the `--searchindex` report prints - where the value it is given is `"Document"`. Documents are
  most of what a first pass finds, so for nearly every file the pill read "indexing" with no noun
  after it and looked terse rather than broken. It matches on the kind itself now, not on a
  spelling borrowed from another screen.

- **`--searchprobe` reports what the capsule's progress pill would draw.** A surface with no
  diagnostic is one nobody can be asked a question about: "I cannot see the progress pill" had no
  answer that did not involve reading source and guessing. It now prints the label, the percentage
  and the count, composed the way the running product composes them.

- **The capsule window and its own drawing agree about how big it is.** The window sized itself
  with the zoom it was given and the canvas drew with a clamped copy, so outside the clamp the two
  disagreed - and since the window is what Windows crops to, the larger drawing lost its bottom
  edge, where the progress pill sits. They matched only because the zoom is 1.0 today.

- **The card's content-mode placeholder is not cut short any more.** With the Content pill down
  the field read "Describe a photo, words in a docum..." - the sentence measured 606.6px against a
  field that holds 580.6px, and the field silently ellipsises, so it stopped mid-word on every
  empty card in that mode. It is shorter now, and measured.

- **Searching pictures by what is in them stops returning screenshots that merely mention
  something.** The words recognised inside a picture were embedded as though they were prose, and
  recognised text is not prose - it is menu chrome, timestamps, phone numbers and whatever the
  recogniser made of a language it was unsure of. Searching for "headphones" returned eleven
  screenshots saying they "said something like it", above the one picture that actually looked
  like it. The words inside a picture are still searchable as words; they are no longer searchable
  as meaning.

- **Findra reads inside your code projects.** Any folder containing a `.git` had its contents
  skipped automatically - a guess that a checkout is mostly other people's files. On a machine
  where the work lives in repositories that was wrong about all 21 of them, and it was invisible:
  nothing named the rule and the only folder control adds more skipping, so it could not be seen
  or turned off. Skipping is something you ask for now. What a checkout actually buries an index
  with - `node_modules`, `.git`, `bin`, `obj`, `packages` - is already in the list of folders
  Findra will not open, where you can read it and remove any line you disagree with.

- **Small images are indexed.** The floor was 10 KB, on the reasoning that anything smaller is a
  user-interface icon - which threw away 890 of one machine's 1,086 images, including an 8.6 KB
  picture of the thing being searched for. It is 2 KB now, and means only "too small to be a
  picture of anything". `.ico` and `.avif` are read too.

- **`--searchindex q:` tests the whole of content search.** It ran the word index alone, so photos,
  video frames and transcripts - every part that needs a model - could not be tested without a
  screen at all. It now runs the same path the card runs and prints what each hit scored and why.

- **The card shows how far the index has got, in a pill hanging under the card.** A dial, what
  is being read, the count and the percentage - and the pill itself is the bar, filling left to
  right underneath the words. It sits below the card the way the capsule's sits below the bar,
  rather than squeezed between the field and the hints inside it. It is always there when the card is open: reading, paused, off, or
  "up to date, 12,480 files" once a pass has finished. The desktop capsule carries the same pill
  from the same painter but stays quiet when there is no work, because it sits on the desktop all
  day and a widget that says "up to date" for eight hours is one that looks busy doing nothing.

- **The capsule has its own progress pill.** What is being read, a track, and how far it has got -
  "indexing photos, 6,680 of 10,800" - in a smaller pill under the search bar. It used to be a bare
  track and a line of text floating under the capsule with nothing around them, the only thing in
  the product drawn without a container. The bar is wider too, and the placeholder it draws is now
  the one the product actually uses: the window said "Search files, photos, words..." and every
  screenshot ever taken said "Search 1.5M files", so no render had shown the real one.

- **The Content pill stops offering while there is nothing behind it.** With reading on and the
  first file not yet finished, pressing it used to open the settings window over the card you had
  just opened, to say that reading was already on. There is nothing to search, nothing to turn on
  and nothing settings could add, so the pill is drawn faded, takes the plain arrow instead of the
  hand, and refuses the press. It comes back on the FIRST file read, not the last, so it is a
  minute or two on a fresh install rather than the hours the whole first pass takes.

- **The tray icon's tooltip says what the index is doing**, in the same words the capsule shows
  under its bar.

- **Findra does not open behind its own welcome screen.** The hotkey, the tray icon, the tray's
  Search and Settings items and the capsule all opened a card or a settings window while the first
  screen was still downloading, so a half-configured Findra appeared in front of the one still
  setting itself up. All of them now bring the welcome screen back to the front instead, until it
  is closed. Setting up continues behind it: names are searchable within seconds of answering, so
  nothing waits on the download.

- **The welcome screen no longer sits over every other window.** It was pinned to the front for
  as long as it was open, so a screen you read, think about, and leave running while gigabytes
  arrive stood in front of everything else on the machine with no way to put it behind anything.
  It is now pinned only long enough to arrive - Windows will not reliably let a starting process
  take the foreground, and a first screen that opened behind the desktop would read as an install
  that did nothing - and released the moment it is on screen. Nothing else opens while it is up
  regardless: the hotkey, the tray, the capsule and Settings all bring it forward instead.

- **`--purge` no longer leaves the folder it said it had deleted.** `ui.json`, which records the
  running interface's process id and hotkey, sits directly in `%LOCALAPPDATA%\Findra` rather than
  in `models`, `index` or `logs`, so none of the four things the prompt prices covered it - and
  the only code that removes it is the interface's own shutdown, which an uninstall never reaches
  because it stops the process outright. A purge that had just offered to free 2.99 GB left the
  folder standing with a stale process id in it.

- **The uninstaller's "also delete my index, my settings and the models" checkbox now does
  it.** The tick was read correctly and then had nowhere to go: the two commands it chose
  between were written into the uninstall log during the *install*, months before anybody was
  asked, so the deleting one was never recorded and could not run. Ticking the box removed
  nothing, kept 2.9 GB of models, and reported success. The uninstall is now run from code
  that executes while the answer is still in hand.

- **You can see that Findra is reading your files.** Asking it to start said nothing at all
  until the first file had been read, on the card and in settings alike, so it looked as
  though the request had been ignored. The settings window now follows the indexer while it
  is open, and the line under the search field says it has started before it has anything
  to count.

- **Every slow thing in settings says it is working.** Checking for a newer version,
  registering the name helper, downloading a capability and starting to read your files all
  looked untouched while they ran, so a second click did the work twice: two stacked
  administrator prompts, or a second download that collided with the first. Each row now
  says what it is doing and stops answering until it is done.

- **The tray icon holds still and is drawn large enough to see.** It was painted in whichever
  palette you had chosen, which put a pale mark on a light Windows taskbar, and it was drawn
  at a size the shell then had to enlarge. It is now always the dark Mond mark, rendered at a
  size the shell scales down from rather than up.

- **Findra's own icon appears in the taskbar.** The settings window and the first screen were
  showing Windows' placeholder icon instead.

- **Uninstalling from Windows Settings asks about your data again.** Inno Setup registers a
  silent uninstall command of its own beside the ordinary one, and Settings > Apps prefers it,
  so removing Findra there ran the uninstaller with no window: the checkbox that offers to
  delete the models, the index and the settings never appeared, and 2.93 GB was kept on the
  disk of somebody who had just asked for Findra to be gone. The installer now removes that
  command, so the question is asked on the route nearly everybody takes. Anything that really
  needs a removal with no questions still has `findra.exe --uninstall --purge --quiet`.

- **The log now says which way an uninstall went.** A run that kept your index, your models and
  your settings and a run that deleted them left the same lines behind - the scheduled task, the
  stopped processes, and nothing at all about the choice. One line goes in before anything is
  stopped, naming the choice and the size that was measured: "keeping models, index, logs and
  settings (2.98 GB)".

- **An uninstall no longer reports that the name helper did not answer.** It asked the helper for
  its process id over the name pipe, and from an uninstall that can never be answered: the pipe is
  owned by your account so that Findra's own window can reach it, an uninstall runs as an
  administrator, and the check that keeps other accounts out compares the two. The ask is gone
  along with the two seconds it spent failing and the warning it left in the log of every
  successful removal. Every Findra process is stopped exactly as before.

- **Clicking a palette applies it.** Choosing a light palette while the dark side was on
  screen wrote the setting and changed nothing you could see, so you had to find the mode
  row and switch that too. Picking a palette from the side you are not looking at now moves
  to that side as well.

- **Name search survives the first person who uses it.** The elevated helper read the disk,
  answered one connection and could then never listen again: every pipe instance after the
  first is access-checked against the descriptor the first one carries, and that descriptor
  allowed reading and writing but not the creation of another instance. The helper spent its
  five attempts, gave up, and the card reported that the helper was not running while a
  perfectly healthy one sat there holding 1.5 million names. The pipe is still reachable by
  one account and no other.

- **A first pass over a busy disk finishes instead of stopping silently.** When the drive is
  being written to faster than it can be read, the walk restarts, and after a few restarts it
  reads the whole volume under one lock so that it always terminates. It used to send its
  results from inside that lock - and a lock belongs to the thread that took it, so releasing
  it after a network-style wait failed on whatever thread the reply came back on. The walk
  ended with no completion frame, and everything waiting on it waited for ever: the queue, the
  progress line under the capsule, and the reading of files itself. The rows are now read under
  the lock and sent after it.

- **A fault while answering is no longer reported as a badly formed message.** Every failure
  inside the helper's request handling was logged as an undecodable body, which sent whoever
  read the log looking at the wire format for a fault that was nowhere near it. A message that
  cannot be read still says so and the connection carries on; a failure while answering one now
  says what actually broke and ends that connection, so the caller stops waiting and tries
  again rather than hanging until Findra is closed.

- **"Start reading now" works during the first pass over a disk.** The first walk of a fresh
  install takes minutes, and everything the same flow owes - the button in Settings, the line
  under the capsule, starting the process that reads inside files - could only happen between
  walks. Pressing the button during one did nothing, said nothing and left no trace. The walk
  now yields to that work as it goes, and the button reports whether the reader actually
  started.

- **A completed download no longer reports itself unfinished for ever.** Choosing Everything
  and waiting for all of it left the welcome screen saying "2 of 4 done" with every bar
  visually full, and nothing would ever move again. Two of the seven model files are a few
  kilobytes smaller than the size the table quotes for them - the table is a figure in
  megabytes to one decimal place, so it was never going to be a byte count - and the screen
  credited each file only the bytes that arrived, leaving Photos and Hebrew permanently short
  by twelve thousandths of one per cent. A file is now counted as finished when its length is
  the length that file should be, which is the same question the downloader already asks of a
  part it finds on disk.

- **Uninstalling leaves nothing behind.** A file the installer wrote itself survived removal,
  and kept the program folder alive with it.

- **The uninstall prompt was written for a terminal.** It wrapped the measured sizes out of
  their columns and ended with a command-line instruction sitting above the checkbox that
  does the same thing. It now says what it will keep, and offers to free the measured total.

- **The installer builds.** Its uninstall prompt called a Windows function with the wrong
  arguments, which no test could see and which would have failed the first release after
  the tag was already public. Both the Intel and Arm installers now compile.

- **The privacy page says exactly what it means about the network.** It claimed the update
  check was the only request Findra ever makes, and then described model downloads three
  paragraphs later. Findra makes one request on its own; the other time it uses the network
  is a download you asked for. The README and the code signing policy say the same.

- **Installing a capability while Findra is open no longer loses everything it was going
  to read.** The files a new model covers were queued straight away, but the part of
  Findra that reads inside files had looked at what was installed only once, when it
  started, so it passed over every one of them and Findra recorded the work as done.
  Nothing queued them again: photos, recordings or documents stayed unread until each
  file was edited. Reading inside files now begins within seconds of a model arriving,
  with no restart, and anything an earlier version wrote off is picked up the next time
  Findra starts.

- **Raising the transcription limit from the command line now reaches a running Findra.**
  `findra --content limit 30` queued the recordings the longer limit newly covers and
  then passed over every one of them at the old length, once, permanently. The length is
  now read before each recording, so the recordings it queues are the recordings it
  hears.

- **No black console window comes with Findra any more.** Starting it from the installer,
  the Start menu, a double-click, or the start-at-sign-in entry opened an empty console
  window that stayed for as long as Findra ran and took Findra with it when closed, and
  the elevated name helper opened a second one at every sign-in. The diagnostic commands
  still print into the terminal you type them at, and still write to a file when you
  redirect them there.

- **The installer script's architecture and its uninstall prompt are checked by tests**,
  so neither can quietly go back to a form that fails to build.

- **The front page now says which half of a new capability waits for a restart.** It said a
  capability taken later re-reads the files it covers, which is true, and left the impression
  that searching by it worked straight away, which is not: reading starts within seconds,
  searching the new way needs one restart. It also says that Findra has no console window of
  its own and that a shell therefore does not wait for the diagnostic commands, so the prompt
  coming back early reads as expected rather than as a failure.

- **The privacy page accounts for the one file outside its two folders.** The installer records
  a single word beside the executable saying how Findra got onto the machine, which the page
  did not mention while promising to say exactly what Findra stores. The code signing policy
  likewise now names the one registry value Findra writes, the start-at-sign-in entry, rather
  than only mentioning that the uninstaller removes it.

- **A download cut short is never mistaken for a finished one**, even when the server
  does not say how long the file should have been. Before, a model that arrived
  incomplete could be filed under its real name, and every capability that needed it
  would fail quietly while Findra believed it was installed.

- **The licence and the attribution notice are installed with Findra**, not just kept in
  the repository. Apache-2.0 requires the notice to travel with every copy, and it is the
  reason Findra is under Apache rather than MIT.

- **The installer builds for both architectures.** The Arm64 build named an architecture
  Inno Setup does not have, which would have failed the release for Intel and Arm alike.

- **The scheduled task no longer tells you Findra does not work without it.** Its description,
  which is what Windows shows in Task Scheduler, said exactly that; without the task Findra runs
  perfectly well and has no file names to search, which is what it says now.

- **An uninstall that cannot remove a folder now names the folder.** It reported which of the four
  it was and what went wrong, and left you to work out where it is.

- **The privacy page no longer says that deleting Findra's two folders by hand removes
  everything.** Two things live outside them: the scheduled task that starts the name helper
  at sign-in, and the start-at-sign-in entry. The page now names both and says to run
  `findra.exe --uninstall`, or use the uninstaller, to remove them.

- **The uninstaller can no longer hang while removing the scheduled task.** It read the two
  streams `schtasks` writes one after the other, which stops dead the moment either fills up -
  inside an elevated uninstall, with no timeout that can rescue it and no way out but the task
  manager.

- **A registration prompt nobody answers is now reported as what it is.** If the elevation
  prompt for the scheduled task was left on screen, Findra logged an unrelated error, called it
  a registration failure, and left a temporary file behind in `%TEMP%`.

- **The welcome screen is the only thing on the display until it is answered.** It was shown
  and not waited for, so the hotkey, the capsule and the tray icon were all built behind it
  while the first sentence was still being read - and pressing "Get these" landed in a product
  that was already running, which made the download just asked for read as a window in the way
  of it. Nothing else is built now until the screen is answered, and it all arrives from the
  answer. The one exception is the name helper, which is registered and started the moment the
  screen is answered: searching by name is what works with nobody's models, and nobody should
  wait on a 1.5 GB download for their filenames. Closing the screen mid-download still leaves
  the download running and Findra in the tray, exactly as the screen says.

- **The welcome screen's dead band reads as a parting rather than a hole.** Sixty pixels of
  nothing sat between the last capability row and the switches below it. The gap cannot close -
  a click one row past the end of the list has to land on nothing rather than on the content
  toggle - so it is now exactly one row tall, with a rule drawn through it, and the screen is
  eight pixels shorter for it.

- **The Content pill goes somewhere when there is nothing to search.** It used to flip a flag
  and re-run the query whatever the index held, so with reading turned off, or turned on and
  nothing read yet, it emptied the card and offered nothing to press next - which is the state a
  fresh install is in. Now: where files have already been read and reading is merely off, it
  turns reading back on and answers the query; where nothing has been read at all, it opens
  Settings at Content, which is where the switch, the power, the limit and the capabilities are.

- **`findra --searchmodels` prints its title before its own measurements.** The probe wrote its
  vector norms and image similarities straight to the terminal while the models were still open,
  so they arrived above the heading that explains what they are: a headerless block of numbers,
  then the title, then the report. The numbers were right and the reading order was wrong. The
  probe is now a section of the report, under a heading of its own, after everything else - and
  a run with no models on disk prints no probe heading at all rather than an empty one.

### Security

- The elevated helper parses no file content. It reads names, paths, sizes and
  modification times - metadata the filesystem hands over without opening anything -
  and never a byte of a file. Every decoder runs in the unelevated indexer, because
  decoders read arbitrary files found on disk and are the most likely thing a
  malformed file could exploit.

- Every decoder added for photos, speech and meaning runs in the unelevated indexer child:
  the two neural-network runtimes, the speech model, the media pipeline that decodes sound
  and video, the picture codecs, and the text recognisers. All five read files somebody else
  put on the disk, which is precisely why none of them is reachable from the elevated helper.

- The named pipe is restricted to the current user, and the interface verifies the
  owner before trusting a connection.

[Unreleased]: https://github.com/blakazulu/findra/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/blakazulu/findra/releases/tag/v0.1.0
