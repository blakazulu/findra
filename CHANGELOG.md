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

### Changed

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

- **The privacy page no longer says that deleting Findra's two folders by hand removes
  everything.** Two things live outside them: the scheduled task that starts the name helper
  at sign-in, and the start-at-sign-in entry. The page now names both and says to run
  `findra.exe --uninstall`, or use the uninstaller, to remove them.

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
