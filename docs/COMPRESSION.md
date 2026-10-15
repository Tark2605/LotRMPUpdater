# Compression and delta study for the LotR MP updater

Status: research and benchmark, 2026-10-15. No product code yet.
Scope: how to pack the LotR MP Event module (~380 MB, changes weekly) and LotRMPCore (~10.6 GB, changes rarely) for the
C# updater (.NET Framework 4.8 app, netstandard2.0 core library). The updater downloads from the owner's Caddy server
(3 Gbit/s). This study fills the **OPEN** transport-encoding point of `docs/CONTRACTS.md` §1–2: objects are
content-addressed by the SHA-256 of the uncompressed file, and the transport extension is still open.

---

## 1. Summary

### 1.1 Decision in one line

Use **zstd level 19, window ≤ 128 MiB (`--long=27`)** for every object that shrinks by at least 3 %. Store everything
else **raw**. Decode with **ZstdSharp.Port** (MIT, pure managed, netstandard2.0). Keep the file-level delta the
contract already gives. For the Core, add **content-defined chunks** (FastCDC, average 256 KiB), so that a Core update
does not re-download whole 1–2 GB `.tpac` files. Do not use xz/LZMA or Brotli: they are a few percent smaller but
decode 15–25 times slower in C#.

### 1.2 What each scenario costs (measured on this PC, extrapolated where marked)

| Scenario | Raw data | Recommended scheme | Download | Player-side decode (C#, ZstdSharp) |
|---|---|---|---|---|
| (1) Event, first install | 383 MB, 863 files | per-file objects, `.zst` (level 19) or `.raw` | **≈ 319 MB** (solid best case: 297 MB) | < 1 s CPU; ~1.5 s on a slower PC |
| (2) Event, weekly update (real 9 Oct → 13 Oct: 15 changed + 54 new files) | 162 MB changed or new | changed and new objects only (file-level delta) | **138 MB** (binary patches would save only 0.3 MB of it) | < 0.5 s |
| (2b) Event, small update (real 12 Oct → 13 Oct: 5 XML/xscene files) | 6.2 MB | file-level delta | **128 KB** (binary patches: 1–5 KB) | instant |
| (3) Core, first install | 10.62 GB, 403 files | 256 KiB CDC chunks, each `.zst` or `.raw` | **≈ 8.0 GB** (solid zstd ≈ 7.9 GB, xz 512 MiB dict ≈ 7.6 GB) *(extrapolated)* | ≈ 17 s CPU here (640 MB/s); 45–60 s on a normal PC; disk write is the real limit |
| (4) Core update (real v1.4.7.5 → v1.4.8.0, assets reshuffled between packs) | 10.62 GB new version | download only the chunks missing locally | **≈ 2.6 GB** (whole changed files: ≈ 7.3 GB; 1 MiB chunks: 3.6 GB; 64 KiB chunks: 2.2 GB; HDiffPatch whole-folder patch: 1.85 GB) | rebuilding changed packs ≈ disk speed |

At 100 Mbit/s the player's own connection is the bottleneck. The Core takes about 11 minutes, an event update about
11 seconds. Every codec in this table decodes faster than such a line, except LZMA.

### 1.3 Codec comparison (event module, solid 383 MB tar; Core sample, 1.16 GB of `.tpac`)

Decode speeds below are native CLI speeds, single thread, written to NUL, on a Ryzen 9 7900X. The C# speeds are in §3.4.

| Codec / setting | Event size | Event ratio | Core-sample ratio | Compress time (24 threads) | Decode speed | Decode memory |
|---|---|---|---|---|---|---|
| zstd -3 | 323 MB | 0.843 | 0.871 | < 1 s | 1.6–2.3 GB/s | 9 MB |
| zstd -19 | 314 MB | 0.819 | 0.801 | 20 s / 41 s | 0.9–1.1 GB/s | 15 MB |
| **zstd -19 --long=27** | **304 MB** | **0.793** | **0.745** | 45 s / 72 s | 1.0–1.2 GB/s | 134 MB |
| zstd -19 --long=29 | – | – | 0.736 | – / 57 s | 1.07 GB/s | 518 MB |
| zstd -19 --long=31 | 304 MB | 0.793 | 0.736 | 47 s / 75 s | 0.84–0.98 GB/s | 372 MB / 1.1 GB (= min(2 GiB, input)) |
| zstd --ultra -22 (--long=27) | 304 MB | 0.792 | 0.739 | 173 s / 233 s | 1.05 GB/s | 134 MB |
| zstd --ultra -22 --long=31 | 304 MB | 0.792 | 0.731 | 174 s / 268 s | 0.75–1.0 GB/s | up to 2 GiB |
| xz -6 (multi-thread) | 308 MB | 0.803 | 0.786 | 15 s / 28 s | 70–110 MB/s | 12 MB |
| xz -9e | 297 MB | 0.776 | 0.736 | 110 s / 111 s | 70–100 MB/s | 68 MB |
| xz -9e, dict 512 MiB (1 thread) | 298 MB | 0.776 | **0.712** | 186 s / 686 s | 74–98 MB/s | 370–516 MB |
| Brotli 11, window 16 MiB | 307 MB | 0.799 | not run | 724 s (1 thread) | 312 MB/s (Python) | – |
| Per-file zstd -19 (event) | 319 MB | 0.832 | – | 88 s (1 thread) | – | – |
| Per-file xz -9e (event) | 311 MB | 0.811 | – | 100 s | – | – |
| Per-file Brotli 11 (event) | 309 MB | 0.808 | – | 727 s | – | – |

Takeaways:

- The data is already mostly compressed, so no codec gets past about 0.71–0.80.
- The long window is the only setting that clearly pays on the Core. `.tpac` content repeats across long distances.
- xz with a 512 MiB dictionary is 4.4 % smaller on the Core sample, but decodes 14× slower natively and 15–18× slower
  in C#.
- Levels above 19 gain less than 1 %.

---

## 2. What is in the data

### 2.1 Event module (`LotRMPEvent`, commit 690d428, 863 files, 382.9 MB)

| Type | Files | Raw | zstd-19 | xz-9e | Brotli-11 | Comment |
|---|---|---|---|---|---|---|
| `.bin` (SceneObj terrain/flora/navmesh, shader headers) | 42 | 191.7 MB | 0.934 | 0.930 | 0.931 | `terrain.bin` is engine-compressed (`ZGR6RTRN` chunks); `navmesh.bin`/`terrain_ed.bin` are LZ4 blocks |
| `.wav` | 322 | 110.6 MB | 0.738 | 0.674 | 0.663 | PCM 16/24-bit; xz delta filter → 0.625 (saves 5 MB, not worth a special path) |
| `.png` | 370 | 39.8 MB | 0.972 | 0.974 | 0.971 | already deflate-compressed → store raw |
| `.mp3` / `.ogg` | 48 | 18.2 MB | 0.981 | 0.983 | 0.973 | store raw |
| `.xml` (ModuleData) | 47 | 8.4 MB | 0.026 | 0.025 | 0.024 | highly compressible |
| `.xscene` | 12 | 5.7 MB | 0.063 | 0.058 | 0.057 | |
| `.dds` | 4 | 4.2 MB | ≈0 | ≈0 | ≈0 | blank textures |
| `.dll` (.NET IL) | 3 | 2.3 MB | 0.294 | 0.258 | 0.274 | x86 BCJ filter makes it **worse** (599,976 vs 596,912 B): IL is not x86 code |
| `.sack` | 1 | 1.8 MB | 0.012 | 0.010 | 0.011 | |

About 60 % of the bytes (terrain binaries, PNG, MP3) are already compressed. That puts a ceiling of about 0.78 on
the whole module. A zstd dictionary for the 632 files under 128 KB saves 0.55 MB of 28.7 MB, which is 0.15 % of the
module. That is not worth the extra moving part.

### 2.2 Core (`LotRMPCore`, Workshop 3772431545 = v1.4.8.0, 403 files, 10.62 GB)

- 11 `.tpac` asset packages make up 10.58 GB. The rest is 370 PNG banner icons (39 MB, incompressible), 21 XML files
  and `SubModule.xml`.
- `.tpac` is TaleWorlds' asset package (magic `TPAC`). It holds textures in BCn/DXT block formats, meshes,
  materials, skeletons and so on. In TpacTool's reverse engineering, each data segment carries a storage-format byte:
  `Uncompressed = 0` or `LZ4HC = 1`
  ([TpacTool, MIT](https://github.com/szszss/TpacTool),
  [AbstractExternalLoader.cs](https://github.com/szszss/TpacTool/blob/master/TpacTool.Lib/Data/AbstractExternalLoader.cs),
  [TextureFormat.cs](https://github.com/szszss/TpacTool/blob/master/TpacTool.Lib/Texture/TextureFormat.cs)).
- Measured on the sample (lotr_ui, pack0, warg, pack7, map; 1.16 GB):
  - Byte entropy is 7.3–7.6 bits/byte. zstd-3 per 1 MiB block gives 0.80–0.93, and 5–20 % of blocks are
    incompressible. So the content is a mix of LZ4HC segments and raw BCn blocks.
  - General-purpose compression reaches 0.71–0.80. Re-encoding LZ4HC segments (precomp-style) would need a
    bit-exact LZ4HC re-encoder on the client, which is rejected.
- The sample is a little less compressible than the whole Core. Per-chunk zstd-19 over the whole Core gives 0.73 with
  1 MiB chunks, against 0.80 on the sample without a long window. The Core-wide numbers in §1.2 come from the
  whole-Core chunk measurements, not from the sample.
- Duplicates inside one Core version, at chunk level: 1.5 % of bytes at 1 MiB, 5.6 % at 256 KiB, 14 % at 64 KiB.
  A chunk store removes these for free. Within a single file, the long-window zstd captures the same thing.

---

## 3. Benchmarks in detail

### 3.1 Method

- **Machine:** AMD Ryzen 9 7900X (12 cores / 24 threads), 63 GB RAM, NVMe SSD, Windows 11.
- **Tools:**
  - zstd 1.5.7 (Meta.Zstandard via winget)
  - xz 5.8.3 (Git for Windows)
  - HDiffPatch 5.1.3 (sisong.HDiffPatch via winget)
  - Python 3.14 with `zstandard` 0.25 / libzstd 1.5.7, `brotli`, stdlib `lzma`, and `pyfastcdc` 0.3.0 (FastCDC 2020)
  - 7-Zip was not installed: the user cancelled the installer. 7z LZMA2 solid behaves like `xz -9e` with the same
    dictionary, so the xz rows stand in for it.
- **Data:**
  - Event: `git archive` of `LotRMPEvent` at 690d428, 4aa8e79 and f39480d (LFS content resolved).
  - Core: copies of 5 of the 11 packs plus all small files.
  - Core update: the two local Core copies, `LotRMPCore_clientversion` (v1.4.7.5, 13.1 GB) and Workshop 3772431545
    (v1.4.8.0, 10.6 GB), both read in place, read-only.
- **Timing:** decode times are single-thread, output to NUL, so disk writes are excluded. Peak memory is the process
  peak working set. Compression ran with `-T0` (24 threads), so its times are indicative only: the server compresses
  once per release, so its time hardly matters.
- **Scripts** were in the session scratchpad (`bench_solid.py`, `bench_perfile.py`, `bench_delta.py`, `cdc_core.py`,
  `cdc_compress.py`, C# `csbench`). They were not kept in the repo.

### 3.2 Event module, weekly deltas (two real pairs from the team repo)

| | 12 Oct (4aa8e79) → 13 Oct (690d428) | 9 Oct (f39480d) → 13 Oct (690d428) |
|---|---|---|
| Files changed / added / removed / unchanged | 5 / 0 / 0 / 858 | 15 / 54 / 0 / 794 |
| (a) Raw bytes of changed and added files | 6.23 MB | 9.2 MB changed + 153.2 MB added (8 new scenes) |
| (b) Same files, per-file zstd-19 | **0.128 MB** | **138.4 MB** (changed part: 0.32 MB) |
| (b') Same files, solid tar, zstd-19 --long=31 / xz-9e | 0.127 / 0.115 MB | 129.3 / 129.1 MB |
| (c) zstd `--patch-from` -19 (changed files) | 1.2 KB | 36 KB (+138.1 MB added files) |
| (c) zstd `--patch-from` --ultra -22 | 1.2 KB | 34 KB |
| (c) HDiffPatch `-SD -c-zstd` | 0.7 KB | 42 KB |
| (c) HDiffPatch `-c-lzma2` | 0.7 KB | 39 KB |
| (c) bsdiff format (`hdiffz -BSD`, bzip2) | 1.3 KB | 52 KB |
| (c) VCDIFF / xdelta3-compatible (`hdiffz -VCD`, xz) | 1.0 KB | 45 KB |
| (d) HDiffPatch whole-folder patch (zstd / lzma2) | 4.8 / 4.4 KB | 138.2 / 128.9 MB |
| Patch apply time (all changed files) | 0.12 s | 0.4 s |

Largest single file, `ModuleData/action_sets.xml` (4.26 MB): full zstd-19 is 55 KB, `--patch-from` 828 B,
HDiffPatch 961–986 B. `mpitems.xml` (1.57 MB): full 44 KB, `--patch-from` 14.7 KB, VCDIFF 12.7 KB.

Reading:

- A weekly update is dominated by **new files** (new scenes, new sounds). File-level delta plus per-file zstd already
  captures 99.8 % of the possible gain.
- Binary patches only matter for edited XML and xscene files. There they are 30–100× smaller, but on files that are
  already small once compressed (tens of KB).
- All patch tools land within 2× of each other. zstd `--patch-from` is the best or close, and it decodes with the
  same library as everything else.

### 3.3 Core update (v1.4.7.5 → v1.4.8.0, real)

| Method | Download | Notes |
|---|---|---|
| Whole changed files (file-level delta) | ≈ 9.96 GB raw → **≈ 7.3 GB** zstd *(estimate, ratio 0.73)* | Only 3 of 11 packs (lotr_ui, map, warg = 0.66 GB) are byte-identical; the other 8 were re-packed. |
| FastCDC 1 MiB avg | 4.71 GB raw → **3.63 GB** (per-chunk zstd-19, ratio 0.770) | 55.6 % of the new bytes already present locally; 8,454 chunks |
| **FastCDC 256 KiB avg** | 3.19 GB raw → **2.63 GB** (ratio 0.823) | 69.2 % reused; 33,210 chunks (31,017 unique) |
| FastCDC 64 KiB avg | 2.58 GB raw → **2.21 GB** (ratio 0.858) | 74.1 % reused; 132,712 chunks (112,011 unique) |
| HDiffPatch whole-folder patch (`-s-4k -SD -c-zstd-19-24`) | **1.85 GB** | 114 s to create (16 threads); native `hpatchz -s-8m` applies it in 12 s with 29 MB of RAM; needs the exact previous version on disk; one patch per (from, to) pair |

Chunk-store first install, with each chunk compressed on its own using zstd-19:

- 1 MiB: 10.54 GB unique → ≈ 7.70 GB
- 256 KiB: 10.03 GB unique → ≈ 7.98 GB
- 64 KiB: 9.11 GB unique → ≈ 7.62 GB

These are within ±4 % of solid per-file zstd (≈ 7.9 GB) and xz-512M (≈ 7.6 GB). Chunking therefore costs almost
nothing on the first install and saves 4.7 GB on a re-pack update.

The compressed sizes are measured on every 4th–6th chunk and scaled up; the raw dedup figures cover every byte.

### 3.4 Decoding in C# (.NET Framework 4.8, x64, measured with a throw-away console app)

| Library (NuGet, version, licence) | Input | Throughput | Peak working set | Notes |
|---|---|---|---|---|
| **ZstdSharp.Port 0.8.8, MIT** — `DecompressionStream` | Core sample, zstd-19 --long=27 (1.16 GB out) | **640 MB/s** (470–495 MB/s including SHA-256 of the output) | 153 MB | 0.64× native zstd (≈1.0 GB/s). Output verified bit-exact. |
| ZstdSharp.Port — low-level `ZSTD_DCtx_refPrefix` (patch-from) | `mpitems.xml` patch (14.7 KB → 1.57 MB) | 0.04 s | 26 MB | Output verified; the API works on net48 |
| Lzma.Net 5.8.4.3, 0BSD — `XzDecompressStream` | event, xz-9e (383 MB out) | 45 MB/s | 1,035 MB (!) | 2.2× slower than native xz; buffers a lot |
| SharpCompress 0.50.5, MIT — `XZStream` | event, xz-9e | 36 MB/s | 96 MB | |

The same Core decoded on this PC would take:

- ZstdSharp: ≈ 17 s, plus ≈ 6 s for SHA-256.
- Lzma.Net: ≈ 4 min.

A typical player PC is 2–3× slower. That puts zstd at under a minute for 10 GB, spreadable over a few threads by
decoding different objects in parallel, and LZMA at 8–12 minutes. zstd meets the "10 GB in a few minutes" target with
a wide margin. In practice the disk write rate (and an HDD at 100–150 MB/s) will be the limit.

---

## 4. Recommendation

### 4.1 Transport encoding of objects (fills CONTRACTS §1 "ext OPEN")

Publish-tool rule, per object (a whole file, or a Core chunk, see §4.4):

1. `sha256` = SHA-256 of the **uncompressed** bytes. This is the object's name and its integrity check after decode,
   unchanged from the contract.
2. Compress with zstd: level 19, `windowLog ≤ 27` (CLI `-19 --long=27`; library `ZSTD_c_windowLog = 27`,
   `ZSTD_c_enableLongDistanceMatching = 1`). Write the content size in the frame header (the default) and turn the
   frame checksum on (`--check`, the default for the CLI).
   - Optional: `--ultra -22` with `windowLog` still capped at 27. It is 0–0.8 % smaller and about 4× slower to
     compress. Acceptable on the server, but not needed.
3. If `compressedSize > 0.97 × size` (PNG, MP3/OGG, terrain `.bin`, incompressible chunks), store the object as
   `<sha256>.raw` instead.
   - A 3 % threshold means that, for any object, decompression cost is only paid when it saves real bytes.
4. Otherwise store `<sha256>.zst`. The manifest already carries `object` (file name with extension) and `objectSize`
   (bytes on the wire). The extension is the codec.

Expected split for the event module: about 53 % of bytes as `.zst` (XML, xscene, WAV, DLL, sack) and 47 % as `.raw`.

Reserve these extensions: `.raw`, `.zst`, and for later `.zpatch` (§4.3). No other codec (no `.xz`, no `.br`).

### 4.2 Decoder settings in the updater (netstandard2.0 core library)

- **Library: ZstdSharp.Port** ([NuGet](https://www.nuget.org/packages/ZstdSharp.Port),
  [repo](https://github.com/oleg-st/ZstdSharp)). Version 0.8.8 or later, MIT. Pure managed port of zstd 1.5.7.
  - Targets netstandard2.0 and net462+. Dependencies: System.Memory, System.Runtime.CompilerServices.Unsafe and
    System.Threading.Tasks.Extensions, all MIT.
  - No native DLL, so nothing to unpack, no x86/x64 split, and no antivirus noise.
- `.zst`:
  ```csharp
  using (var src = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
  using (var z = new ZstdSharp.DecompressionStream(src))
  {
      z.SetParameter(ZstdSharp.Unsafe.ZSTD_dParameter.ZSTD_d_windowLogMax, 27); // refuse > 128 MiB windows
      // copy to the target file with a 1 MiB buffer, hashing the decoded bytes with SHA-256 as they go
  }
  ```
  `windowLogMax = 27` caps decoder memory at about 130–150 MB per object being decoded. A frame asking for more
  fails fast, as a publish error. Decoding 2–4 objects in parallel is safe on 8 GB machines.
- `.raw`: stream copy plus SHA-256.
- Verification: the SHA-256 of the **decoded** bytes must equal the manifest `sha256`. On mismatch, delete and
  re-download. `objectSize` is only for progress bars and for checking the download length.
  - On net48, `SHA256.Create()` ran at about 1.8 GB/s on this PC, so hashing is never the bottleneck.
- Resume: objects are static files, so use HTTP `Range` with `If-Range: <ETag>`. Caddy's `file_server` supports this
  (Go `http.ServeContent`).
  - Do **not** use Caddy `precompressed` or `encode` for objects. The updater decodes `.zst` itself, and
    Content-Encoding would break Range resume.
  - `encode zstd gzip` is fine for `manifest.json` and `channels.json` only (signature over the decoded bytes).
  - Objects are immutable: serve them with `Cache-Control: public, max-age=31536000, immutable`.
- Do not use these:
  - xz/LZMA: Lzma.Net 0BSD, SharpCompress MIT, LZMA SDK public domain. All are fine licence-wise but 15–18× slower.
  - Brotli: net48 has no `BrotliStream`. BrotliSharpLib and Brotli.NET are stale (2019/2021) and give no gain over
    zstd here.
  - ZstdNet: native libzstd, BSD-3. It would be 1.5× faster, but it needs native DLL deployment per architecture.

### 4.3 (1) and (2): event module, first install and weekly updates

- **First install:** download every object listed for the module. Per-file objects cost ≈ 319 MB, against 297–304 MB
  for a solid archive. That 5 % is the price of a single storage format that also serves updates, resumes per file,
  and parallelises. No separate "full bundle" is needed.
- **Weekly update:** file-level delta, already in the contract. Compare the installed files (by `sha256`) with the new
  manifest, download only the missing objects, and delete files no longer listed.
  - Measured: 128 KB for a typical XML/xscene fix, 138 MB for a week that adds 8 scenes.
- **Binary patches: phase 2, optional.** Only if XML edits become frequent and large. The proposed contract
  addition is below; it is the coordinator's call and is marked OPEN.
  - Proposed contract addition: a file entry may list `"patches": [{"from": "<old sha256>", "object":
    "<newsha>.<oldsha[0:16]>.zpatch", "objectSize": n}]`.
  - Build with `zstd -19 --long=31 --patch-from=<old> <new>`, for files between 64 KB and 64 MB, against the previous
    1–2 releases only.
  - Decode with ZstdSharp's low-level API: `ZSTD_DCtx_refPrefix(dctx, oldBytes)` plus
    `ZSTD_d_windowLogMax = ceil(log2(oldSize + newSize))`, which is at most 27 under the 64 MB cap. Then the same
    SHA-256 check. Fall back to the full object on any failure.
  - Expected gain: 30–100× on edited XML, which is a few hundred KB per week at most. Low priority.
  - HDiffPatch is equivalent but would add SharpHPatchZ (MIT, patch-only), which does not support the HDIFFSF20 and
    HDIFFW26 formats yet. One codec family (zstd) is simpler.

### 4.4 (3) and (4): Core, first install and updates

Plain file-level objects are wrong for the Core. Any re-pack changes 1–2.6 GB `.tpac` files as a whole: the measured
v1.4.7.5 → v1.4.8.0 update would cost about 7.3 GB, close to a full reinstall.

Use **content-defined chunking** for large files:

- **Chunker:** FastCDC (2020, normalised level 1) with **average 256 KiB, min 64 KiB, max 1 MiB**, gear table and
  seed fixed in the spec.
  - Use it for files ≥ 16 MiB, in practice the `.tpac` packs. Smaller files stay whole objects.
  - No .NET FastCDC package was found. The Gear loop is about 100 lines to port. Pin it with test vectors produced by
    the Python `pyfastcdc` / Rust `fastcdc` reference, so the publish tool and the client cut identically.
- **Chunk objects:** content-addressed exactly like file objects. Each is named by the SHA-256 of its uncompressed
  bytes and stored in the same `objects/` tree as `.zst` (zstd-19; window 1 MiB suffices) or `.raw` under the same
  3 % rule.
  - The long window is irrelevant inside a 256 KiB chunk.
  - Duplicate chunks inside and across packs are stored and downloaded once.
- **Manifest:** keep `manifest.json` small. A chunked file entry carries `"chunked": true` and
  `"chunkIndex": "<sha256 of index>.zst"`, plus the usual whole-file `size` and `sha256`.
  - The index object is a compact list of `(chunk sha256, size, objectSize, ext)` in file order. That is about
    33,000 rows for the Core, ≈ 1.5 MB raw.
  - Being content-addressed, the index is covered by the signed manifest.
- **First install:** fetch the chunk index, then download all distinct chunks, 4–8 parallel HTTP/2 requests. Decode
  and append each chunk to `<file>.partial`, then check the whole-file SHA-256 and rename.
  - Expected ≈ 8.0 GB for v1.4.8.0, against ≈ 7.9 GB for solid zstd: the overhead is negligible.
- **Update:** for each changed chunked file, read the chunk index of the installed version. Keep it locally from the
  last install; otherwise re-chunk the local file, which runs at ≈ 1–2 GB/s.
  - Copy reused chunks from the old local file and download the missing ones into `<file>.partial`, then verify and
    swap.
  - Measured: 2.6 GB for the real re-pack update, against 7.3 GB for whole files.
  - Disk space needed during the update: one extra copy of the largest changed file (≤ 2.7 GB).
- **Why 256 KiB:**
  - 1 MiB: 8.4k objects per Core version, but updates cost 3.6 GB.
  - 64 KiB: 2.2 GB updates, but 112k objects and 4× the request count.
  - 256 KiB is the middle ground at 31k objects. SteamPipe uses about 1 MB chunks
    ([Valve docs](https://partner.steamgames.com/doc/sdk/uploading)). Our packs get re-packed rather than appended,
    which favours smaller chunks.
  - If the request count ever becomes a problem, chunk objects can later be grouped into larger "chunk pack" files
    read with HTTP Range, without changing the hashes.
- **Not chosen, for now:**
  - A precomputed HDiffPatch folder patch (1.85 GB, 30 % smaller than chunks). It needs the exact previous Core
    version on the player's disk, one patch per (from, to) pair, and a C# patcher for the streaming formats, which
    SharpHPatchZ does not support yet.
  - casync/desync (LGPL / Go): the format would have to be reimplemented anyway.
  - zchunk (C).

### 4.5 Manifest implications (for the coordinator)

| Item | Proposal |
|---|---|
| `object` extension | `.zst` or `.raw`, chosen per object by the 3 % rule; `.zpatch` reserved (OPEN, phase 2) |
| Hash | SHA-256 of the uncompressed bytes, for files, chunks and chunk indexes alike. The decoder verifies after decompression. |
| zstd frame constraints | single frame, content size present, checksum flag on, windowLog ≤ 27 (publish tool must enforce; client sets `windowLogMax = 27`) |
| Large files | `chunked: true` + `chunkIndex` object for files ≥ 16 MiB (Core `.tpac`); FastCDC parameters fixed in the contract (avg 256 KiB / min 64 KiB / max 1 MiB) |
| Server | objects served as plain static files (no Content-Encoding), Range + ETag, `immutable` cache headers; manifest may use `encode zstd gzip` |
| Publish cost | event: ≈ 1.5 min of CPU per release at level 19 (only new objects are compressed); Core: ≈ 10–20 min of CPU for a full re-chunk at level 19 with all cores |

---

## 5. Research notes and sources

- **zstd:**
  - [Man page](https://github.com/facebook/zstd/blob/dev/programs/zstd.1.md):
    - `--ultra` unlocks levels 20–22.
    - `--long` defaults to windowLog 27, maximum 31. The decoder limit is 27 unless `--long=N` or `--memory`.
    - `--patch-from` turns `--long` on automatically and limits the reference via `--memory`.
  - Release [v1.4.5](https://github.com/facebook/zstd/releases/tag/v1.4.5): patch-from references up to 2 GB; at
    level 19 the patches are comparable with bsdiff and about 7× faster to make.
  - [v1.5.7](https://github.com/facebook/zstd/releases): patch-from at level 18+ is much faster.
  - Dictionaries help mostly files of a few KB ([README](https://github.com/facebook/zstd)), consistent with the
    0.15 % measured here.
- **xz / LZMA:**
  - BCJ filters convert branch addresses in *machine code*
    ([xz man](https://tukaani.org/xz/man/xz.1.html), [7-Zip history](https://www.7-zip.org/history.txt)).
  - .NET IL is not machine code, and the measurement confirms no gain.
  - C# options:
    - [LZMA SDK](https://www.7-zip.org/sdk.html): public domain, LZMA only in C#.
    - [Lzma.Net](https://www.nuget.org/packages/Lzma.Net): 0BSD, managed xz.
    - [SharpCompress](https://github.com/adamhathcock/sharpcompress): MIT, xz read-only.
    - [Joveler.Compression.XZ](https://www.nuget.org/packages/Joveler.Compression.XZ): MIT, native.
- **Brotli:** `BrotliStream` does not exist on .NET Framework
  ([docs](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.brotlistream)). The alternatives,
  [BrotliSharpLib](https://www.nuget.org/packages/BrotliSharpLib) and
  [Brotli.NET](https://www.nuget.org/packages/Brotli.NET), are stale.
- **ZstdSharp:**
  - [Repo](https://github.com/oleg-st/ZstdSharp): MIT, supports `windowLogMax`, dictionaries and `RefPrefix`.
  - The README benchmark is ≈ 1.1× native on .NET 9. On net48 (no intrinsics) we measured 0.64× native.
- **ZstdNet:** [repo](https://github.com/skbkontur/ZstdNet), BSD-3, native.
- **Delta tools:**
  - [HDiffPatch](https://github.com/sisong/HDiffPatch): MIT. C# patcher
    [SharpHPatchZ](https://www.nuget.org/packages/SharpHPatchZ/), MIT.
  - [xdelta3](https://github.com/jmacd/xdelta): Apache-2.0. .NET VCDIFF decoders:
    [VCDiff](https://github.com/SnowflakePowered/vcdiff) (Apache-2.0) and
    [deniszykov.VCDiff](https://www.nuget.org/packages/deniszykov.VCDiff).
  - [bsdiff](https://www.daemonology.net/bsdiff/): C# port [BsDiff](https://github.com/LogosBible/bsdiff.net), MIT,
    archived.
  - [Courgette](https://www.chromium.org/developers/design-documents/software-updates-courgette/) only helps native
    code, so it does not apply to IL DLLs.
- **Chunking:**
  - [FastCDC paper (USENIX ATC '16)](https://www.usenix.org/conference/atc16/technical-sessions/presentation/xia)
  - [casync](https://github.com/systemd/casync) (LGPL-2.1)
  - [desync](https://github.com/folbricht/desync) (BSD-3, Go)
  - [zchunk](https://github.com/zchunk/zchunk) (BSD-2)
  - [zsync](http://zsync.moria.org.uk/)
  - SteamPipe 1 MB chunks and its advice not to pre-compress or reorder pack contents
    ([Valve](https://partner.steamgames.com/doc/sdk/uploading))
- **Caddy:**
  - [`file_server`](https://caddyserver.com/docs/caddyfile/directives/file_server): `precompressed` serves
    `.br/.zst/.gz` sidecars to browsers.
  - [`encode`](https://caddyserver.com/docs/caddyfile/directives/encode): on-the-fly compression, gzip/zstd. Not for
    our binaries.
  - Range and If-Range come from Go [`http.ServeContent`](https://pkg.go.dev/net/http#ServeContent).
- **Precomp / preflate** ([precomp](https://github.com/schnaader/precomp-cpp), Apache-2.0): bit-exact re-inflation of
  deflate streams (PNG). It would need a native step on the client, and PNG is only 0.4 % of the payload, so it is
  rejected.
- **Team guidance:** Valve's SteamPipe page advises against custom compression inside pack files and against
  reordering assets between builds. Re-packing `.tpac` (as in v1.4.7.5 → v1.4.8.0) is what makes Core updates
  expensive.
  - Worth passing to the Core authors: append new assets to a pack, or add a new pack, rather than rebuilding all
    packs.
  - With 256 KiB chunks, even a full re-pack only re-downloads about 25–30 % of the Core.
