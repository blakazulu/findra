# Full-precision e5, and indexing on the accelerator

**Status:** designed, 2026-09-05. Supersedes the "per-provider model split" that CLAUDE.md called
mandatory design work; see *What this replaces* at the end.

## The problem, as measured

Findra embeds every text segment of every document through e5, on the **processor**, deliberately -
`Decoders.cs:511` passes `wantAccelerator: false` and no comment says why. That is the slowest step
in a content pass by a wide margin: the benchmark's extraction row reports 58,501 files/min with no
model loaded, while a real first pass on a desktop ran at 21 to 133 files/min.

Moving it to DirectML is worth 3 to 5 times. The obstacle is that **the shipped e5 file is
quantised, and DirectML does not execute quantised operators the way the processor does.**

Measured on one desktop (AMD Ryzen 9 9900X3D, NVIDIA RTX 5070 Ti, Windows 11 26200), same
deterministic input, comparing raw model output:

| model | precision | CPU vs DirectML cosine | max element gap |
|---|---|---|---|
| `siglip2-vision.onnx` | fp32 | **1.000000** | 0.00044 |
| `siglip2-text-q.onnx` | quantised | 0.998180 | 0.26168 |
| `e5-base-q.onnx` | quantised | **0.969968** | 0.81596 |

The processor against itself is 1.000000, so the divergence is real and not harness noise. It is
not the graph optimiser either - it persists at `ORT_DISABLE_ALL`.

That matters because **search runs on the processor and must keep running there.** Indexing on
DirectML with the quantised file would store vectors in one dialect and search them in another,
against thresholds argued in a third. It would also be unstable over time: a driver change would
silently move an index between dialects with nothing reporting anything wrong.

## The three files, measured

| file | size | CPU vs GPU | one query on CPU | indexing on GPU |
|---|---|---|---|---|
| `model_quantized.onnx` (ships today) | 279 MB | 0.970 | **6.2 ms** | 318 seg/sec |
| `model_fp16.onnx` | 530 MB | 0.999991 | 27.7 ms | **539 seg/sec** |
| `model.onnx` (fp32) | 1,059 MB | **1.000000** | 10.9 ms | 408 seg/sec |

Today's baseline is the quantised file indexing on the processor: **134 seg/sec**.

fp32 is *faster than fp16 on the processor* - 10.9 ms against 27.7 - because processors do fp32
natively and emulate fp16. For a product whose search must stay on the processor, the larger file
is the better one.

## The design

**One full-precision file per model, universal, no variants.**

### 1. The model

`ModelStore.E5Base` changes file and URL. Nothing about the `Model` record changes; no variant
field, no provider field, no new type.

```
e5-base-q.onnx  265.7 MiB  onnx/model_quantized.onnx
e5-base.onnx   1058.6 MiB  onnx/model.onnx
```

`MinBytes` rises with it. It is a "this cannot be the file" floor, generous by design, and a floor
left at 250 MB would accept the old quantised file under the new name.

### 2. Where it runs

`Decoders.cs:511` becomes `wantAccelerator: true`. The query side - `ContentBranch.cs:52` - stays
`false`.

That pairing is safe **by construction rather than by calibration**: the two providers agree to
1.000000 on this file, so a machine with no usable accelerator produces the same vectors as one
with. No floor moves. `Onnx.Open`'s existing fall back to the processor stops being a silent change
of units and becomes merely slower.

### 3. What it invalidates

Schema step 4, invalidating `Document`, `Audio` and `Video` - every kind that carries an e5 vector.
`Photo` is untouched: photo vectors come from SigLIP-2's vision tower, which does not change.

`ReWalk` is **false**. Which files are eligible does not change; only what is stored about the ones
already known. Re-walking a finished disk for rows that are all present is the expensive mistake
spec §2a names.

The old 279 MB file is orphaned under a name nothing reads any more. Removing it is not in this
change.

### 4. What it costs

| | before | after |
|---|---|---|
| Meaning capability | 270 MB | **1.04 GB** |
| Everything preset | 2.93 GB | **~3.70 GB** |
| document indexing | 134 seg/sec | **408 seg/sec** |
| one content search | 6.2 ms | **10.9 ms** |
| indexer resident memory | ~280 MB | ~1.1 GB |

The +4.7 ms on search is **bought deliberately**, not incurred. It is the price of keeping search on
the processor, and it is the right side of the trade: indexing happens once per file in a background
child on a duty cycle, and search happens while somebody waits.

The download is the real cost. It lands on the first-run screen, where the number somebody is
weighing gets 26 percent bigger, and on the Meaning row, which nearly quadruples.

### 5. Testing

A test that opens e5 on both providers and fails below 0.9999 cosine. It would have caught the
quantised file on the day it was chosen, and it is what stops a future model swap reintroducing
this. It needs the model on disk, so it skips where the models are absent, as the other
model-backed tests do.

The size table in the README and on the site is regenerated from real files, as it already is.

## What this replaces

CLAUDE.md records that a per-provider artifact split is "MANDATORY design work rather than an
optimisation", on the grounds that SigLIP-2's fp16 export **will not load at all** on the processor.

**That fact is wrong.** The fp16 export fails only at `ORT_ENABLE_ALL`; the exception is
`SimplifiedLayerNormFusion` against an `InsertedPrecisionFreeCast`, thrown by the graph optimiser.
At `ORT_ENABLE_EXTENDED` and below the same file loads and runs. Confirmed on e5's fp16 export,
which fails with the byte-identical error and then loads once the optimiser is turned down.

With that gone, the premise for variants goes too. Every measurement points the other way: one
full-precision file agrees everywhere, loads everywhere, and needs no per-machine reasoning at all.
Variants would buy download size by reintroducing exactly the divergence this change exists to
remove.

A second recorded belief is also wrong and is corrected here: photo vectors were thought to carry an
index/query provider mismatch, because `ClipImageEncoder` indexes on DirectML while
`ClipTextEncoder` queries on the processor. The vision tower is fp32 and agrees 1.000000 across
providers, so there is no mismatch and never was.

## Limits of these numbers

One desktop, one GPU, through DirectML. Whether another vendor's DirectML driver diverges the same
way on quantised models is unmeasured, as is everything else about hardware in this project. The
design does not depend on it: full precision agrees on the processor path regardless, and a machine
whose accelerator will not take the model falls back to the processor and gets identical vectors
more slowly.
