# Security policy

Findra runs an elevated helper on your machine and reads files somebody else put on your disk.
That is two good reasons to take a report seriously, and this page says where to send one.

## Reporting a vulnerability

**Use GitHub's private advisory form:**
https://github.com/blakazulu/findra/security/advisories/new

Please do not open a public issue for a security problem, and please do not put details in one.
An ordinary bug goes to https://github.com/blakazulu/findra/issues; a vulnerability does not,
because the report itself is the exploit until there is a fix.

Findra is maintained by one person in their own time. Expect an acknowledgement within a week.
If a week passes with no reply, open an issue that says only that you are waiting on a security
report and gives no detail, and it will be picked up from there.

What helps, roughly in order of how much:

- The output of `findra --version`, which names the build and where its logs are.
- Which of the three processes is involved, if you know: the elevated name helper
  (`findra --names`), the interface, or the content indexer (`findra --index`).
- A file that triggers it, if a file triggers it. Every decoder in Findra reads files it did not
  create, so "this PDF crashes the indexer" is a complete report on its own.
- Whether it needs administrator rights to reach, and whether it survives a restart.

## What is in scope

- **The elevated helper.** It runs at the highest privileges available, from a logon scheduled
  task. Anything that makes it do more than read filesystem metadata is the most serious class of
  report this project can receive.
- **The named pipe between the helper and the interface.** It is restricted to the current user
  and the interface checks the owner before trusting a connection. A way past either is in scope.
- **The decoders.** PDF, image, audio, video, the two neural-network runtimes and the text
  recognisers all parse files found on the disk. They run in the unelevated indexer child
  precisely because they are the most likely thing a malformed file could exploit, so a crash
  there is a bug rather than a privilege problem. It is still worth reporting.
- **The installer and the uninstaller**, including the scheduled task they register and remove.
- **The update check and the model downloads**, which are the only two things that use the
  network.

## What is not

- **Findra ships unsigned**, and Windows warns about an unknown publisher. That is known and is
  not a vulnerability. Where it stands is in [docs/code-signing-policy.md](docs/code-signing-policy.md).
- **The index is not encrypted.** It holds the text of the documents it indexed and sits in your
  own user profile under the same Windows permissions as everything else there. This is written
  down deliberately in [PRIVACY.md](PRIVACY.md) rather than being a defect to report.
- **Anything that requires administrator rights to set up in the first place.** A person who can
  already write to `C:\Program Files\Findra` or register scheduled tasks does not need a bug in
  Findra.
- **Reports from an automated scanner with no working exploit path.** A dependency advisory that
  names a package Findra ships is welcome; a report that a scanner flagged something is not one
  until somebody has looked at whether Findra reaches the affected code.

## What Findra does about the network

Nothing leaves your machine except one anonymous HTTPS GET to the GitHub releases API, at most
once every 24 hours, to learn whether a newer version exists, and it can be switched off. Model
files are downloaded only when you choose a capability and ask for it. Your files, their names,
their contents and your searches are never sent anywhere. The full statement is in
[PRIVACY.md](PRIVACY.md).

Findra never installs an update by itself.

## Supported versions

There has been no release yet. Until there is one, the supported version is the current `main`
branch, and a fix ships in the next release rather than being backported.
