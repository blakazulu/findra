# How fast is Findra?

Measured with `findra --searchbench` and pasted without editing. A number without its machine is
marketing rather than measurement, so the machine is named.

**Under 4 ms.** Type part of a filename and the matches are back: 0.60 to 3.78 ms median round trip
for every query measured, across 1,780,595 names, from the moment the query leaves the window to the
moment the results arrive. The worst single sample across the five measured name queries was 5.36 ms.

| Name query | Round trip p50 | Round trip p95 | Index scan p50 | Worst | Hits |
|---|---|---|---|---|---|
| config | 0.60 ms | 0.78 ms | 0.15 ms | 1.13 ms | 50 |
| report | 0.70 ms | 0.89 ms | 0.26 ms | 0.96 ms | 50 |
| readme | 1.03 ms | 1.25 ms | 0.58 ms | 1.55 ms | 50 |
| invoice | 3.24 ms | 4.57 ms | 2.81 ms | 4.88 ms | 43 |
| sunset | 3.78 ms | 4.62 ms | 3.41 ms | 5.36 ms | 21 |

- 5.4 s from cold to ready, for 1,780,595 names read off the disk when the helper starts. Longer
  right after a restart, when nothing is cached.
- 200.3 MB for the name index of those 1,780,595 names.
- 0 bytes sent anywhere about your files.

| Word | p50 | p95 | Worst | Hits |
|---|---|---|---|---|
| lease | 1.90 ms | 2.59 ms | 4.07 ms | 50 |
| agreement | 3.68 ms | 4.43 ms | 4.70 ms | 50 |
| invoice | 0.79 ms | 1.08 ms | 1.80 ms | 32 |
| total | 9.41 ms | 10.89 ms | 13.53 ms | 50 |
| report | 15.36 ms | 16.67 ms | 16.87 ms | 50 |

Machine: AMD Ryzen 9 9900X3D, 47.1 GB RAM, NVMe SSD, Windows 11 Pro 10.0.26200.9445, ONNX via
DirectML, Findra 0.3.1, n=50 per query. Yours will differ.

**These numbers are from one machine, and it has an NVIDIA card. AMD and Intel graphics have not
been tested on real hardware, and neither has an arm64 machine.** The paths Findra uses are
vendor-neutral by design so that they should work there; that is a decision rather than a
measurement, and it is said here as one.

Findra tries DirectML for the vision and meaning models and Vulkan for speech, and falls back to
the processor when neither answers. CPU is a supported configuration rather than a failure state:
only the first pass through your files is slower. `findra --searchmodels` prints which provider it
chose and every one it turned down, with reasons.
