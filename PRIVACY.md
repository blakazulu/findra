# Privacy

Findra is a search tool that runs on your computer. Your files, their names, their contents
and your searches stay on that computer. There is no account, no cloud service, no
analytics, no crash reporting and no telemetry of any kind.

This page says exactly what Findra stores, where it stores it, and the single thing it
sends anywhere.

## The one request that leaves your machine

Findra checks whether a newer version has been released. That is the only outbound request
it ever makes.

- It is an anonymous HTTPS GET to the GitHub releases API.
- It happens at most once every 24 hours, on startup, in the background.
- It carries no query parameters, no machine identifier, no install identifier, and nothing
  about your files or your searches.
- It never blocks anything. A failure is a line in the log, not a dialog.
- It is disclosed on the first-run screen and can be switched off, and off means the
  request is not made at all.

Findra never downloads or installs an update by itself. It tells you a newer version exists
and leaves the decision to you.

Downloading a capability's model files is a separate thing, and it only happens when you
choose a capability and ask for it. Those requests fetch model files and send nothing about
you.

## What Findra stores, and where

| Location | What is in it |
|---|---|
| `%APPDATA%\Findra\` | Your settings and any palettes you added. No file data. |
| `%LOCALAPPDATA%\Findra\index\` | The search index. See below, because this one matters. |
| `%LOCALAPPDATA%\Findra\models\` | Model files you chose to download. Nothing personal. |
| `%LOCALAPPDATA%\Findra\logs\` | Daily log files. See below. |

### The index deserves a straight answer

If you turn on content indexing, the index contains the **text of the documents you
indexed**, not merely their names. That is how searching inside files works. It also holds
the full path of every file it knows about. Later, if you enable those capabilities, it
holds numeric representations of what your photos look like and the transcripts of your
recordings.

That file is stored in your own user profile and is protected by the same Windows
permissions as everything else there. It is **not encrypted**. If someone can read your
user profile, they can read your index. Anyone using full-disk encryption already covers
this; anyone who shares a computer should know it before turning content indexing on.

Content indexing is off until you turn it on, for this reason among others.

### What the logs contain

The logs record what Findra did, so that a problem can be diagnosed. They include the names
of files you opened from Findra, the name of a file the indexer was working on when it was
slow or failed, and error messages. They do not contain the contents of your files, and
they do not contain your search queries.

Logs stay on your machine. Findra never uploads them. If you send one to report a problem,
you are choosing to share whatever it contains, so it is worth reading first.

## Deleting it

Uninstalling Findra removes the application, the scheduled task it registered and any
autostart entry.

It keeps your index, your downloaded models and your settings by default, because
re-downloading gigabytes and re-indexing a disk is expensive and most people who uninstall
are reinstalling. Deleting them is a checkbox in the uninstaller and a flag on the command
line, and `findra.exe --uninstall --purge` removes everything.

You can also just delete `%LOCALAPPDATA%\Findra\` and `%APPDATA%\Findra\` yourself. Nothing
else on your machine is touched.

## Children

Findra is a general-purpose tool and is not directed at children. It collects nothing about
anybody.

## Changes to this page

Findra is open source. Any change to this page is a commit in the public repository, with
its history visible to anyone.

## Contact

Questions or a problem: https://github.com/blakazulu/findra/issues
