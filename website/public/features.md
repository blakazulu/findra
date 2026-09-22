# What can Findra actually find?

**Names first. Then, if you ask, everything else.** Names are searchable the second Findra starts,
because a name index costs seconds to build. Everything else is opt-in, because looking inside
files walks every drive and can run for hours. You turn it on, one capability at a time, and each
one is a separate download you can decline.

| Capability | Needs | Download |
|---|---|---|
| Names | nothing | 0 |
| Words in documents | nothing, just FTS5 | 0 |
| Photos and video | SigLIP-2 vision, text and spm | 629 MB |
| Meaning in documents | e5-base and e5-spm | 1.04 GB |
| Speech | Whisper turbo, plus the e5 pair | 547 MB |
| Hebrew speech | whisper-ivrit, requires speech | 1.51 GB |

Every capability is independently installable and degrades silently when its model is absent. A
missing model is a normal state, not an error state.

Taking every capability is 3.7 GB of model files. The rows do not sum to what a mixed selection
costs, because a transcript is searched exactly as a document is, so speech brings the document
models with it and you are never charged twice for the same file.

Reading inside files is off until you turn it on, models or no models.

## Every picture is the product

Each screenshot on the page was drawn by the same painter the window uses, with the command that
drew it printed underneath. Run the command, get the image.

- The card: nine results for "sunset" - files by name, documents by their contents, and a
  photograph matched on what is actually in the frame. `findra --searchshot results.png results Mond`
- The capsule: it sits on the desktop doing nothing until you click it, or press the hotkey from
  wherever you happen to be. `findra --searchshot capsule.png capsule Mond`
- Six palettes, three dark and three light: pick one of each and Findra follows the Windows
  setting, or pin it to either. `findra --searchshot light.png results Blueprint`
- The grammar, with a form: typing has a syntax, and there is a form for the parts of it nobody
  remembers. `findra --searchshot advanced.png adv Mond`
- One question, then out of the way: take a preset, take one capability, or take nothing at all.
  `findra --searchshot firstrun.png firstrun Paper`
- Reading inside your files is a switch, and it starts off.
  `findra --searchshot settings.png settingscontent Paper`
