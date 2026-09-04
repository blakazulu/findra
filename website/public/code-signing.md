# Code signing policy

> **Not yet in force.** Findra ships unsigned today, and Windows warns about an unknown
> publisher because of it. The line below states the policy in advance, and it becomes true on
> the day the application to the SignPath Foundation is accepted and not a day earlier. Until
> then nothing here, on the site, in the installer or in a release claims a signature that does
> not exist.

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org/).

## Team roles

Findra is maintained by one person, who fills all three roles.

| Role | Who | What they do |
|---|---|---|
| Author | [blakazulu](https://github.com/blakazulu) | Writes and changes the code. |
| Reviewer | [blakazulu](https://github.com/blakazulu) | Approves changes before they reach the default branch. |
| Approver | [blakazulu](https://github.com/blakazulu) | Decides which builds are signed and released. |

Multi-factor authentication is required on the source repository and on the signing account.

## Privacy

Full policy: [findra-search.netlify.app/privacy](https://findra-search.netlify.app/privacy/).

This program will not transfer any information to other networked systems unless specifically
requested.

One exception exists and it is switchable off: Findra checks whether a newer version has been
released. It is an anonymous HTTPS request to the GitHub releases API, made at most once every
24 hours, in the background, on startup. It carries no query parameters, no machine or install
identifier, and nothing about your files or your searches. It never blocks anything, and a
failure is a line in the log rather than a dialog. Turning the check off means the request is
not made at all.

The model downloads are the "specifically requested" case in the sentence above: they happen
only when you choose a capability and ask for it, they fetch model files, and they send nothing
about you. If you never choose one, Findra never makes them.

Findra never sends your files, their names, their contents, or your searches anywhere. There is
no account, no cloud and no telemetry. Everything it indexes stays on the machine that indexed
it.

## What Findra changes on the machine

- It registers a scheduled task that runs a helper process at logon with the highest privileges
  available. That helper is what reads the filesystem index, and it is the only part of Findra
  that needs administrator rights.
- It writes settings to `%APPDATA%\Findra\` and its index, models and logs to
  `%LOCALAPPDATA%\Findra\`.
- If you switch on "start when I sign in", it writes one value under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. That is the only registry value the
  application itself ever writes, and the installer does not write it - only Findra does, when
  you ask for it. The installer registers itself with Apps & features in the usual way, which is
  how it is listed there and removed from there.
- It installs its program files, including this project's `LICENSE` and `NOTICE`, into a folder
  with no version number in the path, so an upgrade never leaves the scheduled task pointing at a
  binary that has been replaced.

The uninstaller stops the helper and the indexer first, then **always** removes the scheduled
task and any autostart entry. Your settings, your index and any downloaded models are **kept**
by default, because re-downloading gigabytes and re-indexing a disk is expensive and most
people who uninstall are reinstalling. Deleting those is opt-in, through a choice in the
uninstaller and a flag on the command line. `findra.exe --uninstall` does the same for anyone
who built from source, and `--purge` also deletes the data.

## Why Findra reads the disk directly

Findra opens a read handle to each NTFS volume and reads the master file table and the change
journal. This is how a search tool learns every filename on a disk in seconds instead of walking
directories for minutes, and it is the same technique other fast desktop search tools use. It
reads filesystem metadata only. The elevated helper never opens the contents of a file; every
decoder that parses file content runs in a separate process at normal privileges, because
decoders read arbitrary files and are the most likely thing a malformed file could exploit.

## Licence

Apache-2.0. See `LICENSE` and `NOTICE`.
