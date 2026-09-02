# Code signing policy

> **Not yet in force.** Findra has not been released, and the application to the SignPath
> Foundation cannot be made until a release exists. This page is written in advance so that
> the requirements are met on the day it is submitted, and the line below becomes true when
> the application is accepted. Until then Findra ships unsigned and says so.

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

Full policy: [PRIVACY.md](../PRIVACY.md).

This program will not transfer any information to other networked systems unless specifically
requested.

One exception exists and it is switchable off: Findra checks whether a newer version has been
released. It is an anonymous HTTPS request to the GitHub releases API, made at most once every
24 hours, in the background, on startup. It carries no query parameters, no machine or install
identifier, and nothing about your files or your searches. It never blocks anything, and a
failure is a line in the log rather than a dialog. Turning the check off means the request is
not made at all.

Findra never sends your files, their names, their contents, or your searches anywhere. There is
no account, no cloud and no telemetry. Everything it indexes stays on the machine that indexed
it.

## What Findra changes on the machine

- It registers a scheduled task that runs a helper process at logon with the highest privileges
  available. That helper is what reads the filesystem index, and it is the only part of Findra
  that needs administrator rights.
- It writes settings to `%APPDATA%\Findra\` and its index, models and logs to
  `%LOCALAPPDATA%\Findra\`.

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
