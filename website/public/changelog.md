# Changelog

All notable changes to Findra are documented here.

The format follows [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and Findra follows [Semantic Versioning](https://semver.org/spec/v2.0.0/).

## [0.9.0 - 30 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.9.0)

### Added

- **Scanned PDFs are searched by what they say.** A PDF whose pages are pictures of pages - a scan,
  a fax, a phone photo saved as a PDF - used to be passed over as having no text. Findra now reads
  the words on those pages with the text recognition built into Windows, in English and Hebrew
  wherever those are installed, and keeps them in page order beside any pages that have text of
  their own. Up to the first 100 scanned pages of each file are read. There is nothing to
  download: this is part of reading documents.
- **RTF and OpenDocument files are read inside.** Rich Text files (.rtf), Hebrew ones included,
  and OpenDocument text, spreadsheets and presentations (.odt, .ods and .odp) are now found by
  the words in them, not only by their names. The older Word, Excel and PowerPoint formats (.doc,
  .xls and .ppt) are still found by name only.
- **The Advanced search panel works with a screen reader and the keyboard.** Narrator now reads
  each of its fields with what is typed in it and an example of what it takes, both checks, the
  six kinds and the three buttons. Tab moves through all of them in the order they are drawn,
  Space ticks a check, picks a kind or presses a button, and Enter presses a button or applies the
  panel from anywhere else, with a ring showing where you are. Clicking in the search box while
  the panel is open now puts the panel away and places the cursor there.
- **More like this.** Highlight a photo, video, document or recording and press More like this
  above Open (or Ctrl+L): Findra lists the files that look or read like it, judged by what it read
  inside them rather than by their names, and a video or recording says at which moment. It works
  by writing `like:` and the file's path into the search box, so the kind chips, sorting and any
  filter typed beside it, such as `type:video` or `in:Crete`, all apply, and Escape clears it like
  any other search. It needs Photos or Meaning, and the button waits,
  greyed out, until Findra has read that file. How alike two files must be to count was measured
  on one machine's real pictures and documents.

### Changed

- **Documents that were passed over are read again, once.** After this update, the documents
  Findra had skipped as having no text or as a format it could not read are read again, so the
  scans, RTF and OpenDocument files already on your disk become searchable. Nothing that was
  already read is read a second time.
- Asking `findra --searchindex` a `q:` or `why:` question, with no file to queue, now opens the
  index read-only, and `q:like:` followed by a path asks it the More like this question.

### Fixed

- **Findra no longer says the words inside pictures come free.** They are read as a picture is
  looked at, and Findra looks at pictures only with the Photos and video add-on. The first screen,
  Settings, `findra --models`, `findra --searchmodels` and the README now say that the words in a
  picture come with that add-on.
- **A document built to decompress to an enormous size can no longer hold up reading.** Word,
  Excel, PowerPoint, EPUB and OpenDocument files are zip archives, and a small one can unpack to
  gigabytes. Findra now stops reading such a file after 256 MB of unpacked content and keeps the
  words it had already read.

## [0.8.0 - 30 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.8.0)

### Added

- **USB sticks, memory cards and drives that are not NTFS are searched by name.** Before, only
  fixed NTFS disks were, and a file on a stick simply did not come up. A stick plugged in while
  Findra runs is listed within a few seconds, kept up to date while it stays in, and dropped from
  results when it is pulled out. Disks formatted exFAT, FAT32 or ReFS (a Dev Drive) are included
  too. Findra finds these files by name only and does not read inside them.
- **What you open comes first.** Findra remembers the files you open or reveal from its search
  window, and next time puts them ahead of results that match about as well. The empty search
  window lists the ones you opened most recently, ready to open again. The list stays on this
  computer and holds paths only, never what you typed. Settings > Opening it > "Put what I open
  first" turns it off, and turning it off forgets the list.
- **Findra works with a screen reader and the keyboard.** Every window Findra draws itself - the
  search window, the capsule, Settings, the welcome screen and the update window - now tells
  Narrator and other screen readers what is on it: each button, switch, choice and result, with
  its name and state, and pressing one there does what a click does. Tab moves through Settings,
  the welcome screen and the update window, with a ring showing where you are, and Enter or Space
  presses what the ring is on. The search window says how many results arrived and names each
  one as you move through them. The Advanced search panel is not described yet; its fields can be
  typed into the search box as words, as always.
- **Settings > About > Details for a bug report.** Copy puts a few lines on the clipboard: the
  Findra version, Windows, the processor, memory, every graphics adapter with the one Findra uses
  marked, and which add-ons are installed. It holds nothing about your files or searches and
  sends nothing; paste it into an issue if you want to.

### Fixed

- **A copy from the Microsoft Store keeps finding file names after the Store updates it.** Each
  Store update puts Findra in a new folder, and the task that starts the name helper still pointed
  at the old one. Findra now notices at the first start after an update and registers the task
  again, which asks for permission once.
- **A copy from the Microsoft Store is updated by the Store.** It no longer asks GitHub for a newer
  version; Check for updates says the Store keeps it up to date and opens Findra's page in the
  Store, and Settings drops the switch for a check that never runs.
- **"Start Findra when I sign in" works in a copy from the Microsoft Store.** It now turns on
  Findra's entry under Settings > Apps > Startup instead of writing somewhere Windows never looks.

## [0.7.2 - 29 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.7.2)

### Fixed

- **The full look at every drive that an update can ask for is no longer skipped.** If an older
  copy of Findra was still running against the same index when the update opened it, the older
  copy could cancel the look for a drive, and files deleted or replaced while Findra was not
  watching stayed in the index. A drive now stays marked until a look at it has finished.
- **Running Findra's tests from source no longer writes into the installed Findra's log folder.**
  The test run's lines were mixed into the logs used for bug reports, and the log's weekly
  clean-up ran over that folder from inside the tests.

## [0.7.1 - 29 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.7.1)

### Fixed

- **With a short list of results, the details beside it no longer run into the buttons.** The
  last line about the highlighted file ("score") was drawn on top of the Open button whenever six
  results or fewer were shown. The preview picture is now a little shorter in that case, so every
  line fits above the buttons.

## [0.7.0 - 29 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.7.0)

### Added

- **The capsule can be hidden from its own right-click menu.** The item says which shortcut
  still opens search (or the tray icon, when no shortcut could be registered). Bring the capsule
  back from the tray menu or Settings > Opening it.

### Changed

- **Searching inside files by meaning and by picture is much faster on a large index.** Each
  search used to read the whole file of pictures and passages twice, one row at a time. It now
  reads it once, skips what was deleted and what the search is not about, and shares the work
  across several processor cores.
- **That file gives disk space back.** Deleting or re-reading a file used to leave its old entries
  in place for good. When a quarter or more of the file is left over, Findra copies what is still
  in use into a fresh file while it has nothing else to read, and deletes the old one.
- **Removing many files from the index is quicker.** Files that are gone are taken out up to 200
  at a time instead of one at a time.
- **The log says where a slow content search spent its time.** Any search inside files that takes
  more than half a second leaves one line with the breakdown.
- **The search window says when a search is still running.** While you type, the results on
  screen keep the words they are the answer to, and "searching..." shows on the right until the
  new answer arrives. Before, the old results were labelled with the new words and looked finished.
- **Searching inside files right after Findra starts says it is not the whole answer yet.** For
  the first few seconds, while meaning and picture search are still loading, the window says so
  and shows the word matches it has; once they are ready it searches again by itself.

### Fixed

- **Files deleted while Findra could not see them are taken out of the index.** When Windows'
  record of changes no longer reached back to where Findra stopped, or changes were missed, Findra
  looked at the whole drive again but only ever added to the index. It now also removes what the
  drive no longer has. The first start after this update looks at each drive once to clear what
  earlier versions left behind; nothing already read is read again.
- **A file replaced while it was waiting to be read no longer ends up in the index twice.** Saving
  by writing a new copy over the old one, as many editors and build tools do, left an extra entry
  for the old copy that nothing could remove.
- **Content search no longer shows files that are no longer on the disk.** They had nothing to
  preview and could not be opened.
- **Pictures and passages deleted after a search window was opened no longer come back in it.**
- **The search window could crash when moving quickly between results.** Going back to a result
  whose preview was still being prepared could draw a picture that had already been thrown away.

## [0.6.3 - 29 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.6.3)

### Changed

- **The log says why reading stopped at a file**: what the last attempt did, the error it hit,
  and how many attempts the file has had.

### Fixed

- **A file saved again straight after Findra read it no longer counts as stuck.** Findra took the
  new save for the old one refusing to leave the queue, paused reading for two seconds, could
  show "a problem" on the capsule for that moment, and logged a warning about a failure that
  never happened. The next file saved after the queue had emptied could trip it too.

## [0.6.2 - 29 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.6.2)

### Changed

- **Quit closes all of Findra.** The name search helper used to keep running after Quit, so Task
  Manager still listed Findra. It now stops too, and starts again the next time you open Findra.
  Names take a few seconds to answer after that, while the disk is read again.
- **Signing in to Windows starts the helper only if it starts Findra too.** With "Start Findra
  when I sign in" off, or Findra switched off under Startup apps, nothing of Findra's runs after
  a restart until you open it.
- **After an update, Check for updates stays where it always is in the tray menu**, with a greyed
  line above it naming the version now running. The item itself used to read "Updated to 0.6.1",
  which looked like an offer to update again.
- **The log records each step of Update now**: the download, its checksum, the installer or winget
  starting, and whatever stopped it. A log sent with a report now shows what happened.

## [0.6.1 - 28 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.6.1)

### Changed

- **Settings' About line matches the update window.** When a newer version exists it says that
  Check now installs it, or updates it with winget, instead of sending you to download it
  yourself.

### Fixed

- **Refusing Windows' permission prompt during an update is said plainly**, instead of as the
  installer's exit code.
- **Closing the update window while it checks no longer tells Settings the check failed.**
- **A download larger than the release says is reported as too large**, not as incomplete.
- **An installer that did not run is deleted straight away**, so an uninstall before Findra's
  next start no longer leaves about 80 MB behind.
- **The update window stays where you put it during a download.** Moved partly off the screen,
  it was pulled back at every step; the bar now also repaints once per percent rather than at
  every chunk.
- **A silent install no longer starts Findra when it could not tell whether Findra had been
  running.**

## [0.6.0 - 28 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.6.0)

### Added

- **Update now installs the new version for you.** Check for updates in the tray, or Check now in
  Settings, opens a small window that checks straight away. When there is a newer version, Update
  now downloads the installer from the release page, checks it against the checksum GitHub
  publishes for it, and runs it; a copy installed with winget runs the winget upgrade instead.
  Windows asks for permission, Findra closes while the new version installs, and it starts again
  when the installer is done. If Windows is not given permission, or winget cannot run the
  upgrade, the window says so and nothing has changed. Cancel stops a download part-way and
  deletes what arrived. After an update, the tray menu says what Findra was updated to until it
  is next closed.

### Changed

- **Check now checks, even with the daily check switched off.** Turning the switch off stops the
  once-a-day check in the background; pressing Check now still asks, once, because you asked.
- **Settings no longer puts an update panel over its page.** Check now opens the update window,
  the same one the tray opens.

### Fixed

- **Findra starts again after an update.** A winget upgrade, or any installer run without its
  wizard, closed Findra and left it closed until the next sign-in. It now starts again when the
  installer is done, if it had been running. This takes effect from the update after this one,
  because it is the version being replaced that reports whether it was running.
- **An upgrade started from inside Findra can replace it.** Findra stopped itself for an installer
  by stopping its whole process tree, and an installer Findra had started is inside that tree, so
  Windows refused and Findra kept running with its files in use. It now stops only itself.

## [0.5.1 - 28 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.5.1)

### Changed

- **Among names that match equally well, the one nearest the top of the drive comes first.**
  Searching ".claude" puts the folder in your profile ahead of the ones inside every project,
  because it sits fewer folders down. A better match still wins wherever it is: a folder named
  exactly ".claude" deep in a project comes before ".claude.json" at the top of the drive.

### Fixed

- **A search that matched many names could miss the best ones.** Findra ranked only the first
  matches it came across, in the order the drive happens to list them, so an exact name could be
  left off the card behind dozens of weaker ones. It now looks at every match and keeps the best.
  Searches that match a great many names take a few milliseconds longer as a result.

## [0.5.0 - 26 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.5.0)

### Added

- **The search field edits like any other text box.** Ctrl+A selects everything, Shift with the
  arrows, Home and End selects (Ctrl+Shift by whole words), and the mouse selects by dragging
  across the text, Shift+click extends a selection, a double-click takes a word and a triple-click
  takes the lot. Typing, pasting, Backspace and Delete replace what is selected, Ctrl+X cuts it,
  and Ctrl+C copies it - with nothing selected, Ctrl+C still copies the highlighted result's path.
  Hebrew and mixed text select correctly.
- **Start and stop reading from the search card.** A fourth pill under Settings says "Start now",
  "Starting..." while reading gets going, and "Stop" while Findra is reading inside files. It does
  what the button in Settings > Content does.
- **A close button on the search card's top-right corner.**

### Changed

- **Clicking somewhere else no longer closes the search card**, so a search and its results stay
  up while you work in another window. Close it with the button on its corner, with Esc (the first
  press clears the search, the second closes the card), with the shortcut, or by clicking the
  capsule. The screen behind the card goes back to normal brightness once you click elsewhere.

## [0.4.3 - 25 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.4.3)

### Fixed

- **"Stop" in Settings turns back into "Start now" once reading has stopped.** Pressing Stop did
  stop reading, but the button kept saying "Stop" with the count beside it, and pressing it again
  did nothing. It now offers to start again, and pressing it starts reading.
- **Opening the search card no longer pauses reading.** Windows reported the card's dimmed
  backdrop as a full-screen app, so every search made Findra stop reading and let go of its
  models for as long as the card was open. Findra's own card no longer counts; a real full-screen
  game, video or presentation still does.

## [0.4.2 - 25 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.4.2)

### Fixed

- **Findra no longer keeps about 1.5 GB of graphics memory after transcribing.** Once speech had
  been read, the part of Findra that reads inside files let go of its models but the graphics card
  kept roughly 1.5 GB set aside for it until Findra was closed, even while it sat idle. It now
  checks what it still holds on the card after letting its models go, and if memory is left over it
  quietly restarts itself to give it back. Reading pictures and documents never needed this and
  still does not.

## [0.4.1 - 25 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.4.1)

### Fixed

- **Findra now fits the screen at 125% scaling and above.** On a 1920x1080 laptop set to the
  scaling Windows recommends, the welcome screen was taller than the display, and its two buttons
  sat below the bottom edge where they could not be pressed, so Findra could not be set up at all.
  The welcome screen, Settings and the search card now shrink just enough to fit whatever screen
  they open on, and stay at their normal size wherever they already fit.

## [0.4.0 - 23 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.4.0)

### Added

- **A Models toggle page in Settings.** Photos and video, meaning, speech and Hebrew each have a row
  saying what they do for you. An installed one can be **turned off** (it stops reading new files,
  and everything it already found stays searchable) or **removed** (its files are deleted and the
  space comes back). Removing asks first, says how much it frees and what goes with it (removing
  Speech removes Hebrew too), and keeps what was already found unless you untick that, so adding
  it back later is quick. Turning one back on, or adding it again, reads exactly the files it
  missed while it was away.
- **The welcome screen shows what it is doing after you press a button.** "Get these" or "Not now"
  used to leave the screen frozen for a moment and then jump to the next page. A card now lists the
  steps (saving your choices, starting name search, getting the hotkey and capsule ready, starting
  the download) and ticks each one as it finishes, and says when Windows may ask for permission.
  The second screen does the same before it closes.
- **`findra --models remove <add-on>`** removes one model from the command line, on the same
  rules as Settings. `--dry-run` prints what would be deleted and freed, and `--forget` also drops
  what it found. `findra --models install` now also reads the files a model missed while it was
  removed.
- **Settings says what reading is doing, in plain words**, under "Reading now": how many files are
  read and left out, or that it is paused while another app is busy, paused for a full-screen app,
  reading more slowly because the graphics card is nearly full, waiting for you to finish setting
  up, restarting after a problem, or all done. A dot beside it shows the same at a glance: filled
  while reading, a ring while waiting, red for a problem.

### Fixed

- **Files that have not changed no longer wait for the graphics card.** Moving a folder of photos
  or recordings queues each file again. While another program was using the card, Findra held
  every one of them back, and the count said they were waiting, even though checking that the
  file had not changed needs no card at all. Findra now clears those at once and waits only for
  files it really has to read. A fullscreen game still holds everything back.
- **A busy graphics card no longer stops Findra reading your documents.** Files are read in the
  order they were found, so one photo waiting for the card used to hold back every document
  behind it, even though reading words never uses the card. Photos, video and recordings now wait
  while documents carry on.
- **On a new or freshly upgraded PC, Findra no longer waits an hour before reading.** Windows
  marks the first hour after your first sign-in as quiet time, and Findra took it for something
  running fullscreen. It now waits only for real fullscreen apps, games and presentations.
- **On a graphics card with little memory, photos are read instead of waiting.** On a small card,
  such as one with 3 GB, Windows and your open apps alone use most of it, and Findra waited for
  room that never came, sometimes all afternoon. It now reads photos and documents on the
  processor instead: slower, but it keeps going. It still waits while a game or another heavy
  program is really using the card, and recordings still wait for room on the card.
- **The progress count moves for every file Findra deals with**, including the ones it leaves
  out (such as recordings without the Speech add-on) and the ones it cannot read. It used to count
  only files read, so a pass through files it was leaving out looked frozen at "1 of 278,067".
- **The words under the search card and in the tray are plainer**: "reading photos", "paused while
  another app is busy", "all done · 1,204 files ready to search".
- **When the reader stops unexpectedly, Findra says it is trying again and when**, instead of
  saying it was closed. A reader that had been working for a while restarts within seconds
  rather than waiting up to five minutes because of earlier problems.
- **A problem Findra cannot get past, such as a full disk, is now said on every screen** instead
  of the progress bar simply stopping.
- **Reading a large disk for the first time is faster.** Picking the next file used to sort the
  whole queue each time; it is now a direct lookup.
- **The website no longer carries Netlify's advertisement.** Netlify writes a comment and two tags
  into every page it serves, one of them a tracking link to its own sign-up page. They are taken
  back out before the page reaches you, on every page including "not found".
- **A link to the website shared on WhatsApp, Facebook and the like now says what Findra does**
  (search by name, documents, photos and speech; free, private, open source) instead of quoting
  benchmark figures.

## [0.3.1 - 22 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.3.1)

### Added

- **`findra --searchbench` shows the name helper's whole memory**, not only its index, and prints
  how fast a filename comes back first, since that is the number most people run it for.

### Changed

- **The website's front page is short.** It says what Findra is, how fast a filename search is,
  and how to install it. What it finds, why it differs, every measured table, the questions and
  the full install instructions each have a page of their own.

- **Search by meaning and by picture loads only when there is something to find.** Findra used to
  load both search models at startup whenever they were on disk - about 1.7 GB of memory, even
  for "Just names" after a reinstall that kept the models. It now loads each one only once the
  index holds something it can match, in the background, and a model installed while Findra is
  running is searchable without restarting it. A search in the few seconds a model takes to load
  answers by names and words.

### Fixed

- **A copy installed with winget is told about the versions winget can install.** The update
  check used to ask the releases page, which gets each version days before the winget catalogue
  does, so "Update now" could run `winget upgrade` and find nothing newer. A winget copy now asks
  the winget catalogue's listing on GitHub instead - still one anonymous request, at most once a
  day - and when it is up to date it says so in winget's terms.
- **The name helper gives back the memory it used while building.** Building the name index for
  a large disk left about 300 MB of spare memory held by the background helper for the rest of
  the session; it now returns it once the build is done. On 1.78 million files the helper drops
  from about 560 MB to about 210 MB.
- **The name index's memory is reported whole.** `findra --searchbench`, `findra --searchprobe`
  and the log counted only the stored names, about a third of what the index holds. They now
  count everything it keeps in memory.

## [0.3.0 - 22 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.3.0)

### Added

- **A welcome page after the first screen.** Once you choose what to download, the window shows
  where Findra lives - the capsule on your desktop, the shortcut that opens search (the one that
  actually registered on your machine), and the tray icon, including that Windows may tuck it under
  the ^ arrow - then what happens now, with the downloads as they arrive, and a few words about who
  makes Findra with links to the website and the source. "Open settings" takes you straight to
  Settings. It appears for "Just names" too, which used to close the window without saying where
  Findra had gone.

### Changed

- **The first screen's download progress can be closed from a button.** "Done" closes the page and
  the downloads carry on in the tray, as they always did when the window was closed.

## [0.2.1 - 21 September 2026](https://github.com/blakazulu/findra/releases/tag/v0.2.1)

### Added

- **The site has a changelog page.** Every release, newest first, at `/changelog/` - the same
  sections GitHub shows as the release notes, without the entries about Findra's own
  documentation. The footer's Changelog link goes there now rather than to the raw file.

- **The front page says which version is current**, under the Get Findra button, and links to
  what it changed. The number and the date come from the build and the changelog, so a release
  cannot leave the page announcing the one before it.

- **The site says Findra steps aside for other work on the graphics card.** While another program
  is working the card hard, or something is fullscreen, Findra stops reading and lets go of its
  models, and the page and its answers to common questions now say so.

- **Four guides on the site**, each answering a question people actually search for: why Windows
  Search is not finding a file and what does, how to search inside PDFs and documents, how to find
  a photo by describing it, and how to search recordings by what was said. Every page links them
  from its footer, and each has a Markdown copy for tools that read Markdown.

### Changed

- **The installers have the same name in every release**: `findra-setup-x64.exe` and
  `findra-setup-arm64.exe`, with no version number in them. The site's download button fetches the
  newest one directly - x64, with a link beside it for Arm64 - instead of sending you to the
  releases page to find it.

- **winget is the install.** `winget install blakazulu.Findra` now works, and the README, the site
  and its answers say so. The releases page still has each new release first, since the Windows
  Package Manager catalogue can take a few days to pick one up.

- **The site loads nothing from anybody else.** Its two typefaces are served from the site itself
  rather than from Google Fonts, with their licences beside them, so the page that says nothing
  leaves your machine no longer makes a request to a third party.

- **The site's page titles say what Findra is** - fast, private desktop search for Windows - and
  the pages behind the footer say what each one is about rather than only its name.

### Fixed

- **You can choose which drives Findra reads inside.** Settings could open with "All" as the only
  choice, so no drive could be picked. It now lists every fixed drive on the machine from the
  start.

- **Reading can be stopped from the button that shows it.** While Findra is reading, the button
  under "Look inside my files" says Stop with the count beside it; pressing it turns reading off
  and keeps everything read so far. Start now turns it back on.

- **The settings section for drives and skipped folders is called "Where it searches"**, which is
  what it decides.

- **The command offered when an uninstall cannot remove the scheduled task now names the real
  task.** It named one that does not exist, so running it removed nothing.

---

Older releases (0.2.0 and earlier) have been moved to [CHANGELOG-ARCHIVE.md](CHANGELOG-ARCHIVE.md).
