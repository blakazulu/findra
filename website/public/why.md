# How is Findra different from the search Windows already has?

Four differences, and every one of them is something you can check on your own machine rather
than a claim about somebody else's product.

**The names live in RAM, not in a database.** Findra reads the NTFS file table when it starts and
holds every name in memory, and answers a filename search in under 4 ms median for every query
measured. There is no overnight pass and no index to rebuild, so a file saved a minute ago is
findable now. The measurements are on https://findra-search.netlify.app/numbers/.

**Reading inside a PDF needs nothing installed.** Findra decodes documents itself, in a process of
its own that never runs elevated. There is no filter to install, no file type to register and no
index to rebuild afterwards. Turning a capability on later goes back for exactly the files that
capability covers. While another program is working the graphics card, or a game is fullscreen,
it waits and lets go of its models rather than competing for the card.

**Filename-only search cannot find what you remember.** A file-table index is fast because names
are all it holds. Findra holds the table too, and then, only if you ask, the words in a document,
what a photograph shows, and what was said in a recording - each one a download you can decline.

**Nothing is sent anywhere to make it work.** The models run on your own processor or graphics
card. A photograph is never uploaded to be understood, a query is never sent anywhere to be
answered, and no web results are mixed in among your own files.

## Why it did not find your file

- **The index had not got to it.** You saved the file. The indexer runs at 3 AM. Findra: names are
  live, always.
- **That folder was not included.** There is a settings page listing which of your own drives
  Windows is willing to look at. Findra: every NTFS volume.
- **You only remembered the sentence.** Not what you called the file. Findra: inside documents too.
- **It sent the question upstream.** You typed a private filename into a box on your own computer
  and something went over the wire about it. Findra: nothing leaves.
