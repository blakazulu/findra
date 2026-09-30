# Starting up, the helper task, one interface, uninstall

Rules for `src/Findra/Startup/` and the startup shell in `src/Findra/App/App.axaml.cs`. The
first-run screen itself is in `src/Findra/First/CLAUDE.md`.

## Starting up

**When the first screen is needed, it owns the display until answered.** `Window.Show()` does not
block, so nothing (hotkey, capsule, tray) may be built behind it. `StartupOrder` is the seam: the
stages are a LIST, testable without a window. `Shell.Run` switches on `StartupStep` and its default
arm throws.

- **Nothing reads inside files until the LAST question is answered**, or indexing fights the 2.9 GB
  download for the disk. `App._holdReading` holds it, `FirstRun.Asks` decides whether the last act
  asks, "Start reading" clears it. "Later" keeps it for the session and changes no setting (the
  next launch reads without asking - hence **Later**, not "Not now"). Anything that explicitly
  starts reading (settings button, Content pill) clears it too.
- **The reading hold has THREE endings, two of them events.** A screen that never ASKED has nobody
  to clear it; `WhenTheWelcomeScreenIsGone` reads `FirstRunWindow.AskedAboutReading` to tell that
  from "Later".
- **The names helper is the one exception**, not in `AfterTheScreenIsAnswered()`: the answer
  registers the task and starts the helper at once; names need no models.
- **`Topmost` is set at construction and released in `Opened`**. **The gate in
  `App.TheWelcomeScreenIsInTheWay`** makes the screen the only door in.
- **`_firstRunIsUp` AND `_firstRun` are set AFTER `Show()`.** A screen that throws must leave a
  launch that carries on (`WhenTheScreenCouldNotBeShown()`); `Closed` never fires on a window that
  never opened, so an early gate would `Activate` a dead window for ever.
- **A screen CLOSED unanswered hands the launch on too** (X, Alt+F4, taskbar never raise
  `Answered`): `StartupOrder.WhenTheScreenWasClosedUnanswered()`. Nothing writes `FirstRunDone`, so
  it is asked again.

## The helper task

- **Quit stops the helper too**: asked, not killed (it closes its volume handles and logs its own
  end). `HelperTask.Stop` sends a `stop` frame (answered BEFORE the helper cancels itself) and waits
  until the pipe goes quiet, because the task is `IgnoreNew` and a launch straight after Quit would
  be ignored. The task stays registered; the next launch's `EnsureRunning` starts it. Only Quit
  sends it: `--stop`, the installer and sign-out do their own.
- **At sign-in it stays only when Findra starts too**: the task fires at every logon, and
  `HelperStart.ShouldStay` lets it stay when another findra.exe runs in this session, when
  `Autostart.StartsAtSignIn` (the Run entry, not switched off under Startup apps: an odd first byte
  in `StartupApproved\Run`), or when started from a terminal; otherwise it exits before reading the
  disk. No task change, so no UAC on upgrade.
- **`schtasks` CSV headings are localized; the XML is not.** Parse the XML. Registration can fail in
  a way Findra cannot fix: keep a non-fatal path that leaves names working on whatever reads
  unelevated. `HelperTask.RunSchtasks` drains both streams as tasks before waiting; `Register` reads
  the result of its own timeout.
- **Inside a Store package the task follows the versioned folder**: `HelperTask.NeedsRewriting`
  re-registers at every start when the task names another copy - inside a package any difference,
  outside one only a program that is gone (a source build must not take over the installed copy's
  task). `Autostart.RunKey` is then the package's `windows.startupTask` (`Autostart.PackageTaskId`)
  and `InstallSource` is `store`, known rather than recorded. All keyed on `Packaged.IsPackaged`
  (`StoreCopyTests`).

## One interface per index

`OnlyOne` holds an exclusive handle on `.running` in the index folder, taken in `RunUi` before
Avalonia starts (two interfaces mean two `--index` children fighting over the vector store). **A
file handle, not a named mutex** (mutexes are thread-owned and untestable; a hard kill frees a
handle; the index directory is the key). A second launch exits 0 naming the owner; failing for any
reason but contention lets the launch through with a log line.

## Uninstall

- **Always removes** the app files, **the `HighestAvailable` scheduled task** and any autostart
  entry, stopping helper and indexer first. An orphaned elevated task is a defect.
- **Keeps by default** `models\`, `index\` and the config; deleting is opt-in (checkbox and
  `--purge`), and the prompt states the **measured** size.
- **Always clears `FirstRunDone` and `InstallSource` in the kept config**: the welcome screen
  registers the task, and `HelperTask.EnsureRunning` only *runs* an existing one - skipping the
  screen on reinstall leaves no helper. `Uninstall.Run` takes its effects as delegates (tests assert
  the recorded sequence), on every route including `--quiet` (the installer's).
- **The installer runs it from `CurUninstallStepChanged`, never `[UninstallRun]`**, whose entries
  are fixed into `unins000.dat` at INSTALL time, so the `Purge` checkbox could never reach them.
  **A decision taken during the uninstall cannot be carried by anything the installer wrote down.**
- **`findra.exe --uninstall` is a first-class mode** because the `dotnet publish` route has no
  installer.
