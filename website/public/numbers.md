# How fast is Findra?

Measured with `findra --searchbench` and pasted without editing. A number without its machine is
marketing rather than measurement, so the machine is named.

**Under 4 ms.** Type part of a filename and the matches are back: 2.13 to 3.77 ms median round trip
for every query measured, across 1,339,635 names, from the moment the query leaves the window to the
moment the results arrive. The worst single sample across the five measured name queries was 4.76 ms.

| Name query | Round trip p50 | Round trip p95 | Index scan p50 | Worst | Hits |
|---|---|---|---|---|---|
| invoice | 2.13 ms | 2.45 ms | 1.77 ms | 2.77 ms | 50 |
| sunset | 2.77 ms | 3.00 ms | 2.41 ms | 3.99 ms | 23 |
| report | 3.36 ms | 3.74 ms | 2.98 ms | 4.18 ms | 50 |
| config | 3.73 ms | 4.61 ms | 3.26 ms | 4.76 ms | 50 |
| readme | 3.77 ms | 4.20 ms | 3.26 ms | 4.75 ms | 50 |

- 2.4 s from cold to ready, for 1,339,635 names read off the disk when the helper starts. Longer
  right after a restart, when nothing is cached.
- 172.0 MB for the name index of those 1,339,635 names.
- 0 bytes sent anywhere about your files.

| Word | p50 | p95 | Worst | Hits |
|---|---|---|---|---|
| lease | 1.12 ms | 1.31 ms | 2.67 ms | 50 |
| agreement | 4.18 ms | 4.66 ms | 5.23 ms | 50 |
| invoice | 0.82 ms | 0.91 ms | 1.77 ms | 50 |
| total | 8.89 ms | 11.80 ms | 12.75 ms | 50 |
| report | 14.95 ms | 15.94 ms | 16.98 ms | 50 |

Machine: AMD Ryzen 9 9900X3D, 47.1 GB RAM, NVMe SSD, Windows 11 Pro 10.0.26200.9445, no model
loaded, Findra built after 0.5.0, n=50 per query. Yours will differ.

**These numbers are from one machine, and it has an NVIDIA card. AMD and Intel graphics have not
been tested on real hardware, and neither has an arm64 machine.** The paths Findra uses are
vendor-neutral by design so that they should work there; that is a decision rather than a
measurement, and it is said here as one.

Findra tries DirectML for the vision and meaning models and Vulkan for speech, and falls back to
the processor when neither answers. CPU is a supported configuration rather than a failure state:
only the first pass through your files is slower. `findra --searchmodels` prints which provider it
chose and every one it turned down, with reasons.
