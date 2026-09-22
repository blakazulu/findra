# Publishing Findra to the Microsoft Store

> **Status: submission 1 filled, not submitted.** "Findra" is reserved on the existing
> developer account (step 3 done), all three identity values are final in
> `packaging/store/Package.appxmanifest`, and the `store` workflow can produce the bundle.
> On 22 September 2026 submission 1 (id `1152921505701952194`) was filled in Partner Center
> and saved up to the Submit button; the package upload is the only section not done.
> Nothing has been sent to Microsoft for review.

## The decision

Recorded 22 September 2026, the day the packaging was prepared.

**Findra will be published to the Microsoft Store.** The Store is the shelf Windows users
trust by default, it handles updates itself, and a listing there is the third distribution
channel beside the installer on the releases page and the winget catalogue - not a replacement
for either.

**As MSIX, not as the EXE installer.** The Store takes both. The EXE route requires an
Authenticode certificate, because the Store does not re-sign EXE/MSI installers; Findra is
unsigned today and its signing story (SignPath) is not arranged. MSIX packages are re-signed
by Microsoft after certification, so the unsigned artefact this repository honestly produces
is exactly what the MSIX route accepts. One caveat that follows: the MSIX build and the
installer build are two artefacts of one release, and the Store copy is updated through the
Store, never by the in-app update check.

**The one declared risk is accepted.** `allowElevation` is a restricted capability, and the
Store's review can refuse it. It is declared anyway, because without it the packaged app
cannot raise the single UAC prompt that registers the names-helper task, and a Findra without
name search is not Findra. The justification argues the architecture Microsoft's own guidance
recommends (unelevated UI, one small elevated helper), and if review refuses anyway, the
fallback is a Store build with name search disabled - recorded in the risk section below, not
decided in advance of an actual refusal.

**Registration is free.** Individual developer accounts carry no registration fee since
September 2025. The account is personal (an individual account can never become a company
account later), and identity verification is a government ID plus a selfie.

## Why MSIX and not the EXE

The Store takes two shapes of desktop app: an MSIX package, or a classic EXE/MSI installer.
The EXE route was considered and rejected on one fact: the Store does not re-sign EXE/MSI
installers, so that route requires an Authenticode certificate Findra does not have. MSIX
packages are re-signed by Microsoft after certification, so the unsigned artefact this
repository can honestly produce is exactly what the MSIX route wants.
(Source: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)

## What the repository now contains

- `packaging/store/Package.appxmanifest` - the package definition. Desktop bridge
  (`Windows.FullTrustApplication`, mediumIL), the Store tiles under `packaging/store/Assets`,
  a `windows.startupTask` for the sign-in autostart, and two restricted capabilities:
  `runFullTrust` and `allowElevation`.
- `packaging/store/Assets/` - the seven tile images the manifest names, drawn by
  `build/Make-Icon.mjs` from the same geometry as every other copy of the mark.
- `build/Make-Msix.ps1` - packs one architecture (`-Rid win-x64|win-arm64`) or bundles the two
  (`-Bundle`). Refuses to run while the identity placeholders survive.
- `.github/workflows/store.yml` - builds, tests, packs both architectures and bundles, on
  `workflow_dispatch` only. A person starts it, same rule as the winget catalogue.

## The steps, in order

### 1. Developer account: ALREADY EXISTS

The Microsoft account liraz1234@hotmail.com already holds an active developer account - it
publishes Scalpel PDF today. No registration, no ID verification, no fee. The account-level
identity values are already in the manifest:

- Package/Identity/Publisher: `CN=8B3919EF-5B9D-4935-A322-FC9435A969F6`
- Package/Properties/PublisherDisplayName: `Liraz Shaka Amir`

The steps below are kept for the record; the account they describe creating is this one.

### 1a. (record) Developer account (free)

Registration is free for individuals since September 2025 - no fee for either account type.
(Sources: https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/open-a-developer-account ,
https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/)

1. Go to https://storedeveloper.microsoft.com and click **Get started for free**.
2. Choose **Individual developer**. (An individual account cannot later be converted to a
   company account - that requires a new registration.)
3. Sign in with a Microsoft account, or create one.
4. Identity verification: a government-issued ID and a selfie, captured on a phone, in good
   light, from the original document.
5. Complete the profile details.
6. Continue to the Partner Center dashboard. If the Apps & Games tile does not appear
   immediately, wait about five minutes and refresh.

### 2. Reserve the name

In Partner Center: Apps and Games > New app > MSIX or PWA app, and reserve the name **Findra**.
Reservation is what mints the package identity.

### 3. Copy the identity values into the manifest

Partner Center > the app > Product management > App identity shows three values:

- **Package/Identity/Name** (e.g. `12345YourName.Findra`)
- **Package/Identity/Publisher** (a `CN=...` string)
- **Package/Properties/PublisherDisplayName**

Done 2026-09-22: "Findra" reserved; Name is `LirazShakaAmir.Findra`, PFN
`LirazShakaAmir.Findra_6wbaw9fmp3y9t`, Store ID `9P78Z9KT48PR`. All three values are final
in the manifest; `build/Make-Msix.ps1` refuses to pack while any placeholder survives, and
none do.

### 4. Build the bundle

Actions > store > Run workflow. The artefact `store-msixbundle` holds
`findra_<version>.msixbundle` (x64 + arm64, unsigned - Microsoft re-signs after certification).

### 5. The submission

Apps and Games > Findra > Start submission. What each page needs:

| Page | What to put |
|---|---|
| Pricing and availability | Free, all markets, public audience. |
| Properties | Category: **Productivity**. Privacy policy URL: `https://findra-search.netlify.app/privacy`. Website: `https://findra-search.netlify.app/`. Support contact: the GitHub issues URL. |
| Age ratings | The IARC questionnaire; nothing in Findra pushes it above the lowest band. |
| Packages | Upload the `.msixbundle` from step 4. |
| Store listing | The text below. Screenshots: at least one is required, four or more recommended, PNG at 1366x768 or larger for Desktop, up to 10. The four in `packaging/store/listing/` are exactly 1366x768. Store logos: the 1:1 app tile icon at 300x300 (`packaging/store/listing/AppTileIcon300.png`) is strongly recommended - without it the Store falls back to the package icon. (Source: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images) |
| Submission options | The **restricted capability justification** for `allowElevation` - text below. Notes for certification: the app registers one elevated scheduled task after one UAC prompt; everything else runs as the user. |

(Source for the checklist: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/create-app-submission)

### 5a. Submission 1 state (22 September 2026)

Saved in Partner Center, submission id `1152921505701952194`:

| Page | State |
|---|---|
| Pricing and availability | **Complete.** Free (USD 0), all 240 markets incl. future markets, public audience, discoverable, publish as soon as it passes certification, no trial. |
| Properties | **Complete.** Category Productivity (secondary: Utilities > tools). Privacy: "Yes, my product uses personal information" with `https://findra-search.netlify.app/privacy`. Website `https://findra-search.netlify.app/`, support contact the GitHub issues URL. |
| Age ratings | **Complete.** IARC questionnaire, every content question answered No; generated ESRB Everyone / PEGI 3 (lowest band). IARC Terms of Use acknowledged. |
| Store listing (en-US) | **Complete.** Description, short description, the six product features, copyright, license-terms URL and keywords exactly as in the text below; "What's new" left blank for the first submission. Four Desktop screenshots (1366x768 PNG from `packaging/store/listing/`) uploaded in the order firstrun, results, advanced, settings; no per-image captions (optional field). Store logos skipped: Partner Center only offers 9:16 (720x1080) and 1:1 (1080x1080) slots and the 300x300 asset fits neither, so the Store falls back to the MSIX package tiles. |
| Submission options | Publishing hold left at the default (publish when certified). Notes for certification still to fill - the rich-text editor did not render in the automation browser session; paste the allowElevation text from below plus the helper-task note. |
| Packages | **Not started.** The 289 MB `.msixbundle` exceeds the 25 MB upload cap of the automation browser bridge used on the day; upload still pending (Partner Center in a normal browser session, or the Microsoft Store submission API with a Microsoft Entra app associated to the account). The restricted-capability justification field for `allowElevation` appears in this section once a package is uploaded. |

What remains before release: upload the bundle, paste the allowElevation justification and
certification notes, review, then the owner clicks Submit. Submit was deliberately not
clicked during the fill.

### 6. Certification

Certification reads the restricted-capability justification and can take longer for it. If
`allowElevation` is refused, the submission fails with feedback - the fallback is below.

## The one real risk: allowElevation

Findra's name search needs the NTFS change journal, which needs an elevated process exactly
once, registered as a scheduled task after one UAC prompt. A packaged process may not raise
that prompt without the restricted `allowElevation` capability, and restricted capabilities are
reviewed by a person who can say no.
(Sources: https://learn.microsoft.com/en-us/windows/uwp/packaging/app-capability-declarations ,
https://stefanwick.com/2018/10/06/app-elevation-samples-part-2/)

The justification to paste into Submission options:

> Findra searches files by name by reading the NTFS master file table, which requires
> administrator rights exactly once. The interface runs unelevated; one scheduled task,
> registered after a single UAC prompt, starts a small helper at logon that holds the name
> index. allowElevation is declared so the packaged app can raise that one prompt. No other
> feature uses elevation.

The split is the architecture Microsoft's own guidance recommends - the UI at user integrity,
the admin work in a separate process - which is the strongest version of this case.

If review refuses anyway: the packaged build can ship with name search disabled and content
search (which needs no elevation) intact, and the Store listing would say so. That is a code
change, a smaller product, and the reason the attempt order is: submit as-is first.

## Known behavioural differences inside a package

These are the honest list of things a packaged Findra does differently today. None blocks a
first submission; the first is the one most worth fixing.

1. **The scheduled task points at a versioned path.** A packaged exe lives under
   `C:\Program Files\WindowsApps\<package>_<version>_...`, which moves on every update. The
   helper task keeps the old path until the app is run once after an update and re-registers
   it. The fix is a startup check that re-registers when the recorded path no longer matches
   the running exe - a code change, queued.
2. **The update check would send Store users to GitHub.** InstallSource knows winget,
   installer and source; a package has no marker file, and the check's sentence would point at
   the wrong shelf. Store apps are updated by the Store, so a packaged build should report
   "updated through the Microsoft Store" and skip the check. A code change, queued.
3. **The Run-key autostart is virtualised.** `Autostart.cs` writes HKCU Run, which a package
   redirects. The manifest declares `windows.startupTask` instead, so the Settings > Apps >
   Startup entry works; the in-app toggle should drive the StartupTask API when packaged.
   A code change, queued.

## Store listing text

**Short description** (also the manifest Description):

> Fast, private desktop search for Windows.

**Description:**

> Findra finds your files by name in milliseconds, reading the NTFS master file table directly
> and keeping it live from the change journal. A capsule sits on your desktop; a global hotkey
> brings it up from anywhere and it unfolds into a card of results.
>
> It can also search inside your files: the words in documents, what a photo shows, and what
> was said in a recording. That part is off until you turn it on, and the models it needs are
> optional downloads you choose from a screen inside the app - none of them required.
>
> Nothing about your files, your searches or your machine leaves it. There is one exception,
> disclosed on the first screen and switchable off: an anonymous check for a newer version, at
> most once a day.
>
> Free and open source (Apache-2.0).

**App features:**

- Find any file by name in milliseconds
- Searches 1.7 million names in about five seconds
- Search inside documents, photos and recordings - optional, off by default
- A capsule on the desktop, one hotkey from anywhere
- No account, no cloud, no analytics, no telemetry
- Free and open source (Apache-2.0)

**Keywords:** search, files, desktop, ntfs, offline, privacy

**Copyright:** Copyright (c) 2026 blakazulu

**Additional license terms:** https://github.com/blakazulu/findra/blob/main/LICENSE

## The images, ready in packaging/store/listing/

- `AppTileIcon300.png` - the 1:1 app tile icon at exactly 300x300, drawn by Make-Icon.mjs from
  the same geometry as the package tiles.
- `screenshot-results.png`, `screenshot-settings.png`, `screenshot-firstrun.png`,
  `screenshot-advanced.png` - 1366x768 each: the real `--searchshot` renders from `docs/shots/`,
  centred on the plate background with no scaling, because none of the committed renders is
  large enough for the Store on its own (the largest is 820x928). If screenshots captured in
  situ on a real desktop are ever preferred, replace these; the size floor is the only rule.

Suggested captions (200 characters each, optional): "Results unfold from the capsule - names in
milliseconds", "Every capability is a choice, and off means off", "The first screen says exactly
what the app does and what it never does", "Advanced search: content, photos and speech when you
ask for them".

## The standing workflow, end to end

Once the first submission is live, a new release reaches the Store like this:

1. Tag the release as usual. The `release` workflow publishes the installer to GitHub; the
   `winget` workflow updates the catalogue. Neither touches the Store.
2. Run the `store` workflow by hand (Actions > store > Run workflow). It builds, tests, packs
   both architectures and uploads `findra_<version>.msixbundle` as an artefact. The version in
   the bundle is the tag's version with the Store's fourth part appended - it can never
   disagree with Directory.Build.props, because Make-Msix.ps1 reads the number from there.
3. Partner Center > Findra > Start submission (or update the existing one): upload the new
   bundle on the Packages page, refresh "What's new in this version" from the CHANGELOG
   section, and submit for certification.
4. Certification runs and the Store publishes the update. Microsoft re-signs the package with
   its own certificate; users on the Store copy get the update from the Store, not from
   Findra's update check.

The identity values in the manifest are permanent for the app - step 3 of the initial steps
below happens once, and the workflow above never repeats it.

## Sources

- Open a developer account: https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/open-a-developer-account
- Free individual registration announcement: https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/
- MSIX package requirements (re-signing, identity): https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements
- Submission checklist: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/create-app-submission
- Capability declarations and restricted-capability review: https://learn.microsoft.com/en-us/windows/uwp/packaging/app-capability-declarations
- Store policies: https://learn.microsoft.com/en-us/windows/apps/publish/store-policies
