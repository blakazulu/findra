# Changelog

All notable changes to Findra are documented here.

The format follows [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and Findra follows [Semantic Versioning](https://semver.org/spec/v2.0.0/).

## [Unreleased]

Findra has not been released yet. Everything below is the work so far, and it moves
into a numbered section on the first release.

### Added

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

### Changed

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


### Fixed

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

[Unreleased]: https://github.com/blakazulu/findra/commits/main
