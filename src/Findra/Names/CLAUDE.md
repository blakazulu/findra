# The name index and walked drives

Rules for `src/Findra/Names/` and `src/Findra/Pipe/`. The three-process architecture and the
elevation rule are in the root `CLAUDE.md`; the helper's task and lifetime in
`src/Findra/Startup/CLAUDE.md`.

## The pipe and ranking

- **Name search is async everywhere** - a round trip, not an in-RAM `IndexOf`.
- **Every query carries a generation counter, stamped on the reply, checked by the UI**, so a late
  answer cannot overwrite a newer result. It needs an explicit adversarial test.
- **The index keeps the best `max` hits of the whole scan, never the first `max` it meets.** Ties go
  to fewer folders down, then the shorter path: `Rank` is that order, shared by `NameIndex.Best`,
  the helper's merge across volumes and the card. **Depth only breaks ties.** `NameIndex.Depth`
  remembers answers in `_depthOf`, dropped when a folder moves or is deleted.

## The helper's memory

**It is `NameIndex.ResidentBytes`, never `BufferBytes`** (the names alone, about a third). Building
doubles every array and `Trim` copies each once more, so `RunAsync` runs one compacting collection
after enumeration (1.78M names: 502 MB working set down to 253 MB).

## Walked drives

**Drives without a readable file table are WALKED, names only** (`WalkedVolume.cs`): removable media
in any format, fixed disks not NTFS (exFAT, FAT32, ReFS Dev Drives); never network or optical
(`WalkedVolume.Walks`). `WalkedDrives` looks every five seconds, walks a new drive into the helper's
concurrent volume table, follows it with a `FileSystemWatcher`, drops it when pulled out and re-walks
a letter that now names another drive.

- **A record's number is a hash of its path relative to the root, and the root is 5** (NTFS's own),
  so `PathOf`, `Depth` and root names work unchanged. `IdOf` never yields low 48 bits of 5 or 0, nor
  all ones (the map's tombstone).
- **A file change is applied on the spot; anything done to a FOLDER re-walks the drive**, settled for
  two seconds; `Reconcile` writes in batches under the write lock, then sweeps what was not seen.
- **Walked drives are never offered to the content index** (`VolumeView.NamesOnly` /
  `VolumeStatus.NamesOnly`): Subscribe leaves them out, enumerate answers empty, and the interface
  keeps them out of `_drives` and `ChosenDrives`. A walked listing opens no file.

## The log here

**Ask what a line costs per day, not per occurrence** - the journal tail polls about once a second.
`JournalDigest` sums rather than throttles (not `Log.Repeat`); a quiet window says nothing (tail liveness is
`--searchindex`'s job, and a dying tail warns); the first activity on a volume is reported at once.
