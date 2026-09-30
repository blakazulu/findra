# The Microsoft Store package

Rules for `packaging/store/`, `build/Make-Msix.ps1` and `.github/workflows/store.yml`.
**`docs/store.md` is the record**: the decision, submission pages, listing text and the per-release
routine. Behaviour inside a package (keyed on `Packaged.IsPackaged`) is in
`src/Findra/Startup/CLAUDE.md`.

**Published** at https://apps.microsoft.com/detail/9p78z9kt48pr (Store ID `9P78Z9KT48PR`, listed 30
September 2026 after the first submission failed on scaling, 10.1.2.10, fixed in 0.4.1). The Store is
a fourth route beside the releases page, the installer and winget, not a replacement; a release does
not touch it.

- **MSIX, not the EXE installer.** The Store does not re-sign EXE/MSI; MSIX is re-signed by Microsoft
  after certification, so the bundle is **unsigned on purpose** - never add a signing step to it.
- **`Package.appxmanifest`** is a desktop-bridge package (`Windows.FullTrustApplication`) with
  `runFullTrust`, **`allowElevation`** (restricted, human-reviewed: the one UAC prompt that registers
  the names-helper task; accepted at certification) and a `windows.startupTask`. `Version` and
  `ProcessorArchitecture` are filled by the script, never committed. If a later review refuses
  `allowElevation`, the fallback is a build with name search disabled, said in the listing - decided
  only after an actual refusal.
- **The identity is final and permanent**: `Name` `LirazShakaAmir.Findra`, the account's `CN=...`
  `Publisher`, `PublisherDisplayName` "Liraz Shaka Amir", copied from Partner Center
  (case-sensitive). **Never edit or invent them.** `Make-Msix.ps1` refuses the old placeholders
  (`__PACKAGE_IDENTITY_NAME__` and friends); `StorePackagingTests` validates each field.
- **`Make-Msix.ps1`** calls `Publish.ps1`, lays out `store/layout/<rid>`, packs
  `store/msix/findra_<ver>_<arch>.msix`, and `-Bundle` makes `store/bundle/findra_<ver>.msixbundle`.
  The version is `Directory.Build.props` plus `.0`; `makeappx` is the newest Windows Kit found.
- **`store.yml` is `workflow_dispatch` only** (`WorkflowTests`): build, test, pack both RIDs, bundle,
  upload the `store-msixbundle` artefact. Workflow files are committed through the web editor (the
  fine-grained token cannot carry the `workflow` scope).
- **The tiles in `Assets/` and `listing/AppTileIcon300.png` come from `build/Make-Icon.mjs`** - never
  hand-edit; `StorePackagingTests` checks sizes. **The four listing screenshots are `docs/shots`
  renders centred on a 1366x768 plate of Mond's ground (20,20,26), unscaled** (a taller render is
  cropped top and bottom); redo them when their `docs/shots` source changes.
