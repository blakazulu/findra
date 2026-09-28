# The update window, and updating in one click

**Status:** designed, 2026-09-28. Replaces the rule in spec §9b that Findra never updates itself,
and the rule that a switched-off update check makes no request even when a person asks for one.
See *What this replaces* at the end.

## The problem

A person who clicks **Check for updates** in the tray sees nothing happen. The check runs, and its
answer lands in three places they have to go looking for: the menu item's own label (visible only
if they open the menu again), a tooltip line (the first line dropped when the tooltip is too long),
and a panel in Settings (raised only if the Settings window happens to be open). From the tray,
"Update now" is never offered at all.

When they do find an update, doing something about it is a chore. An installed copy is sent to
the releases page to download and run the installer by hand. A winget copy gets a console window
running `winget upgrade`. And either way, when the upgrade finishes, **Findra does not come back**:
the installer's "Start Findra" step is marked `skipifsilent`, and every winget upgrade is silent.

And a person who switched the daily check off and then pressed **Check now** was told the switch
is off and nothing was asked, which answers a question they did not ask.

## What changes, in one paragraph

Pressing **Check for updates** in the tray or **Check now** in Settings opens one small update
window. It checks straight away, whatever the daily limit or the switch says, because the person
asked. If there is a newer version it says so and offers **Update now**. For a copy installed from
the releases page that downloads the installer, checks it, and runs it; for a winget copy it runs
the winget upgrade. Findra closes while the new version installs and starts again afterwards. The
once-a-day background check does not change at all.

## Behaviour

### What opens the window

- The tray's **Check for updates**, and Settings' **Check now**. Both open the same window; if it is
  already open it comes to the front rather than opening a second one.
- Nothing else. The daily background check never opens it: a person who asked no question is not
  waiting for an answer (spec §3).
- Settings no longer draws an update panel over its pane. Its **Check now** button opens the window
  and does nothing else; the button no longer shows an "Asking..." state of its own.

### Which checks run

| | Manual (the window) | Background (startup) |
|---|---|---|
| Daily limit | ignored | kept: at most once every 24 hours |
| Switch off | ignored: the person asked | no request at all |
| Opens anything | the window | nothing; the tooltip line only |

The request itself is unchanged: one anonymous HTTPS GET, to the winget catalogue's listing for a
winget copy and to GitHub's latest release for every other copy.

### The window's states

| State | Title and body | Buttons |
|---|---|---|
| Checking | "Checking for updates" | none |
| Up to date | "You have the latest version"; a winget copy says "the newest version winget has" | Close |
| Unreachable | "Could not reach GitHub"; nothing is wrong with this copy | Close, Try again |
| Available | "Findra 0.5.2 is available", and what Update now will do for this copy (below) | Not now, Update now |
| Downloading | "Downloading Findra 0.5.2", a bar, "41 of 86 MB" | Cancel |
| Download failed | Says whether it did not arrive or did not match its checksum; nothing was installed | Close, Try again |
| Installing | "Findra will close now and open again when the installer is done" | none |
| Installer did not run | Permission was refused, or the installer exited before installing | Close, Try again |
| Updating through winget | "Updating through winget" | none |
| winget did not update | winget's own last line of output, or "winget found nothing newer to install" | Close |

**Try again** from Unreachable runs the check again; from Download failed or Installer did not run
it starts the download again. Closing the window during Downloading is the same as Cancel. During
Installing and Updating through winget the window has no buttons and no close cross, because the
hand-off is under way and cannot be called back.

### What Update now does, by how this copy was installed

| Install source | Update now |
|---|---|
| `installer` | download, check, run the installer |
| unknown (an older copy that never recorded one) | the same: the installer works however Findra arrived |
| `winget` | run the winget upgrade |
| `source` | the button says **Open releases** and opens the release page, as today: running the installer would turn a source build into an installed copy |

If the latest release has no installer for this machine's architecture (see below), an installed
copy falls back to **Open releases** too, and the body says why.

### After an update

- Findra starts again by itself (see *Starting again*).
- On the first start on a new version, a greyed line that cannot be clicked, **Now on 0.5.2**,
  sits above **Check for updates** for that session. The item keeps its words and place. Nothing
  pops up. (It first read **Updated to 0.5.2** in the item's place, and a tester read that as an
  offer to update.)
- The tray item no longer changes to "Checked: ..." after a manual check: the window is the answer.
  The tooltip's update line is unchanged.

## Download, checks and hand-off

### What the check keeps

GitHub's latest-release record already lists every installer with its size and SHA-256 digest, so
the check keeps this machine's entry from the same response and makes no second request:

- The asset is `findra-setup-x64.exe` or `findra-setup-arm64.exe`, chosen from the running
  process's architecture (`RuntimeInformation.ProcessArchitecture`), never assumed. Any other
  architecture has no asset.
- Kept: the name, the download URL, the declared size and the digest.

A winget copy asks the catalogue, which lists versions and no files, so it keeps only the version.

### The download

- Only when **Update now** is pressed.
- Into `%LOCALAPPDATA%\Findra\updates\` (bulk data never roams), as the asset's own name.
- HTTPS only. The URL and every redirect must stay on `github.com` or a host ending in
  `.githubusercontent.com`; anything else is refused before a byte is written.
- The bar reads the declared size. The download stops at that size; a declared size over 512 MB is
  refused outright.
- **Cancel** stops it and deletes the partial file.

### Nothing runs until it checks out

- The file's length must equal the declared size, and its SHA-256 must equal the release record's
  digest. **A release record with no digest is refused**, never run unchecked.
- What this protects against: a broken, truncated or swapped download. What it does not protect
  against: somebody in control of the GitHub account, who could publish a bad installer and its
  matching digest together. Only code signing closes that, and the docs say so plainly. (The file
  is fetched by Findra, not a browser, so it carries no mark of the web and SmartScreen does not
  look at it; the digest is the gate.)

### Handing off to the installer

- The installer is started with `/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-`: a progress window and
  no wizard pages. It needs administrator rights, so Windows asks.
- The window shows Installing. The installer then stops Findra through `--stop`, as it already does
  before replacing any file.
- If the installer exits while Findra is still running, nothing was installed (permission refused,
  or an early failure), and the window shows **Installer did not run**.
- A failure after the installer has stopped Findra is the installer's to report; Inno rolls the
  files back, and Findra is started by hand. Rare, and not worth a watcher process that would hold
  the executable the installer needs to replace.

### Handing off to winget

- `winget upgrade --id blakazulu.Findra --exact --accept-source-agreements
  --accept-package-agreements --disable-interactivity`, started with no console window. Both of its
  output streams are read as they arrive, never waited on in turn. No `--silent`: winget's default
  runs the installer with its progress window, which is what the person watches once Findra closes.
- The window shows Updating through winget. winget runs the installer, which stops Findra.
- If winget exits while Findra is still running, the window shows **winget did not update** with
  winget's last line of output. A zero exit with nothing installed (the catalogue has nothing newer)
  reads "winget found nothing newer to install".
- If winget is not on the machine, the same state says so.
- A generous limit (20 minutes) stops the wait, so a hung winget cannot hold the window for ever.

### Starting again

- `--stop` reports what it found in its exit code: **2** when it stopped a running interface, **0**
  otherwise (nothing running, or only the helper and indexer).
- The installer keeps that answer. After a **silent** install, and only if the interface had been
  running, it starts Findra again through the Windows shell (`explorer.exe` with the path to
  `findra.exe`), so Findra runs as the signed-in user and never elevated, however the installer
  itself was started. Findra must never run elevated: that is what breaks dragging a result into
  Explorer.
- A silent first install on a machine where Findra was not running does not start it, as today.
- An interactive install keeps its **Start Findra** checkbox on the last page.

### Clean-up

- The first start on a new version deletes whatever is in `updates\`.
- `--uninstall --purge` removes it with the rest of `%LOCALAPPDATA%\Findra`.

## What the privacy policy says

`PRIVACY.md` (and the `/privacy/` page made from it, which the tests compare in both directions):

- The daily check is unchanged: anonymous, at most once a day, on startup, nothing about you.
- **Off stops the daily check.** Pressing Check now still asks, once, because you asked.
- **Findra downloads and installs an update only when you press Update now.** The download is the
  installer from the release page, checked against the digest GitHub publishes for it.

## The code

`App.axaml.cs` is 2,439 lines; the new work goes in small units of its own.

| Unit | Purpose |
|---|---|
| `UpdateCheck` (changed) | `CheckAsync` takes `manual`, which skips the daily gate and the off switch; the background call never sets it. The fetch returns a `LatestRelease` (tag, plus the installer entry for this architecture when the source is not winget) instead of a bare tag. Parsing the release record stays pure. |
| `UpdateDownload` (new) | Download to `updates\` with progress and cancellation; the host rule, the size cap, and the size and digest checks. Takes an `HttpClient`, so the tests drive it through a fake handler. |
| `UpdateHandoff` (new) | Builds the installer and winget start information, starts them, waits, and maps the outcome to a state. Its effects are passed in as delegates, as `Uninstall.Run`'s are, so the tests record what it would start. |
| `UpdateFlow` (new, pure) | The states and transitions above, as a list a test walks without a window. |
| `UpdateWindow` (new) | A small Skia-painted window like Settings and the welcome page. It reuses `UpdatePrompt`'s wording and layout, extended with the new states. It fits the screen at every scaling through `ScreenFit`; its hit test takes the state, never the bounds; every new target gets a written-out arm in `Pointers`. |
| `SettingsWindow` (changed) | Check now opens the window. The in-pane panel, its hit-test branch, `SettingsState.Prompt` and `UpdatePromptState.Off` go. `RemovePrompt` keeps the layout constants it borrows from `UpdatePrompt`. |
| Tray (changed) | Check for updates opens the window. `Config.LastRunVersion` (new) decides the greyed **Now on** line. `UpdateMemory.CheckedHeader` goes. |
| `--stop` (changed) | Exit code 2 when it stopped a running interface. |
| `installer/findra.iss` (changed) | Keeps `--stop`'s answer; a `[Run]` entry for `explorer.exe` with a `Check` that is true only for a silent install that stopped a running interface. |
| `--searchshot` (changed) | `settingsuptodate`, `settingsupdate` and `settingsasking` become `update*` states, one per window state, including an installer-copy and a winget-copy Available, because the painter branches on the install source. |

## The docs that change

- Spec §9b: *What it does with the answer* and the table under it; the self-updater paragraph
  becomes the reasoning for handing off to the installer and to winget rather than replacing files.
- `PRIVACY.md` and the generated `/privacy/` page.
- README: *What leaves your machine*, and anything saying Findra never installs an update.
- The website and `llms.txt`, wherever they say Findra never updates itself.
- The welcome page's update-check wording, if it says off means no request.
- CLAUDE.md: *The update panel* (now the update window) and *Versions and updates*.
- `docs/end-to-end-checklist.md` and `docs/e2e-run-sheet.md`: the real end-to-end run below.
- `CHANGELOG.md`, under Added and Changed.

## Testing

Written first, each failing before its code exists:

- A manual check requests with the switch off and inside the 24 hours; a background check does not.
- Asset choice by architecture; no asset for another architecture; a record with no digest offers
  no download.
- The download: a short file, a wrong digest, a missing digest, a redirect to another host, a
  declared size over the cap, and a cancel (the partial file is gone). All against a fake handler,
  with no network.
- The exact installer and winget command lines. Exit handling: installer gone while Findra runs,
  winget failing, winget with nothing newer, winget missing, the time limit.
- Every `UpdateFlow` transition, including Try again from each state that offers it.
- `--stop` returns 2 when an interface was running and 0 when not (through the `Discover` seam).
- **Now on** appears only when `LastRunVersion` is older than the running version, and not on a
  first run.
- Every new `--searchshot` state renders; the window fits 1080p at 100 to 175%; every `Pointers` arm.
- The installer script's new `[Run]` entry and its `Check`, as text (`InstallerScriptTests`).

One real end-to-end run, recorded on the checklist: a local build stamped 0.5.0, with this code,
finds the real 0.5.1 release, downloads the installer, checks it, and hands off; 0.5.1 installs.
That proves the download, the check and the hand-off against GitHub. **It cannot prove the restart**:
0.5.1's own installer predates the fix, so the restart is first proven when the release after this
one updates it.

## Version

New behaviour, so it ships as **0.6.0**.

## Out of scope

- Installing without a click. A check that finds an update asks first.
- Release notes inside the window.
- A Microsoft Store build. When one exists it must never take the installer path; `InstallSource`
  has no Store value yet (docs/store.md, known gap 2).
- Code signing, which stays a placeholder until SignPath is arranged.

## Risks

- **Starting Findra through `explorer.exe`** is a common way to leave an elevated process without
  carrying its rights along, but it relies on the shell being up as the signed-in user. The
  end-to-end run proves it; if it fails, Inno's `ExecAsOriginalUser` is the fallback, which is right
  whenever the installer was started unelevated (always, from Findra).
- **An unsigned installer** is what Windows' permission prompt will show as an unknown publisher.
  That is true today for anyone who downloads it by hand, and signing is the fix.

## What this replaces

- Spec §9b: "Findra never updates itself. It has no self-updater, no elevation for updates, no
  background installer." Findra still never replaces its own files: the installer and winget do,
  as before. What changes is that Findra fetches and starts them when the person presses Update now.
- Spec §9b and `PRIVACY.md`: "off means off: no request is made". Off now stops the daily check; a
  press of Check now asks once.
- CLAUDE.md, *The update panel*: "Only the button raises it", "`Disabled` has its own arm" and
  "Findra installs nothing itself".
