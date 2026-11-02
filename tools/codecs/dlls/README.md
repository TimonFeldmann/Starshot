# Bundled encoder DLLs (pre-built)

Pre-built encoder DLLs for the recording pipeline. Starshot loads these at
runtime via `LoadLibrary`/`GetProcAddress` (see
`src/Starshot.Codecs/starshot-codecs.c`); they are **not** statically
linked, so you can drop in your own builds at any time.

| File | Library | Version | License |
|---|---|---|---|
| `libx264-164.dll` | x264 (H.264/AVC) | 0.164 | LGPL 2.1+ — `LICENSE.x264` |
| `libx265.dll` | x265 (H.265/HEVC) | 4.3 | **GPL 2.0** — `LICENSE.x265` |
| `faac-2.dll` | faac / libfaac (AAC) | 2.2.0 | LGPL 2.1+ — `LICENSE.faac` |

`tools/codecs/build-codecs.ps1` copies these into `build\codecs\` and
compiles the C shim (`Starshot.Codecs.dll`) from
`src/Starshot.Codecs/starshot-codecs.c`. The shim is the only component
built from source; the three DLLs above are pre-built, so no OBS install,
CMake, or nasm is required to stage the codecs — the repo is self-contained
for the codec line.

Note: **x265 is GPL-licensed** (unlike x264/faac, which are LGPL). It is
shipped as a standalone runtime-loaded plugin; see `LICENSE.x265` for the
full text and notes.
