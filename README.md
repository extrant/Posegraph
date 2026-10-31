# Posegraph

**English** | [简体中文](README.zh-CN.md)

Turn a photo or an illustration into a pose for your FINAL FANTASY XIV character.

**New here? Start with the [step-by-step tutorial](docs/tutorial/README.md)** (with screenshots).

<p>
  <img src="docs/tutorial/images/ingame-wall.jpg" alt="Leaning on a wall" width="49%">
  <img src="docs/tutorial/images/ingame-kalaripayattu.jpg" alt="Martial arts leap" width="49%">
</p>

Posegraph is a Dalamud plugin with a local AI backend. Pick an image in game, and the backend recognizes the
3D body pose with Meta's [SAM 3D Body](https://github.com/facebookresearch/sam-3d-body), retargets it onto the
FFXIV skeleton of your race, and the plugin applies it to your GPose actor through [Brio](https://github.com/Etheirys/Brio).

```
Posegraph plugin (in game, ImGui)  ── HTTP (127.0.0.1) ──►  Posegraph backend (local Python)
  status, image picker, overlays,                            SAM 3D Body recognition (once per image)
  3D preview, tuning sliders,                                MHR → FFXIV retargeting (~50 ms per change)
  apply / camera / export          ◄── Brio pose JSON ───   race skeletons, plausibility checks
        │
        └── Brio IPC: freeze actor, load pose
```

## Features

- **Photo and illustration support**, full or half body, fingers included. An *Illustration* preset tones down
  the exaggerated head/neck proportions of drawn art.
- **All playable races, male and female.** The race is read from the character's appearance (or chosen manually).
  Midlander-type and Elezen males are verified in game; Lalafell and Roegadyn/Hrothgar had a body-proportion bug after changing race, fixed in 0.3.2.
  Female skeletons were checked offline against the vanilla bodies with the IVCS skeleton and behave like the male ones; awaiting a retest in game.
- **Live tuning without re-running the AI**: per-region amplitude (spine, neck, head, clavicle, wrist), hand contact
  compensation, gravity correction, ground snapping, helper-bone strength, and manual per-joint offsets in a 3D stick figure.
  Dragging a slider updates the preview; releasing it applies the pose (Brio's undo history stays clean).
- **Image library**: import, organize with folders / tags / favorites, batch recognition with a cache, save to the Brio pose library.
- **Several characters at once** (character list): bind one image to each GPose character and apply them all.
- **Extra views**: attach other angles of the same pose to fix the front/back depth a single photo can't tell.
- **Plausibility hints** with one-click fixes, based on ranges sampled from in-game animations.
- **Hand contact compensation**: hands touching the head / chest / hips stay in contact even when your race's
  proportions differ from the person in the photo; elbows stay pure hinges.
- **Camera matching**: reproduces the photo's camera angle, distance and field of view in GPose; depth (MoGe-2) helps estimate the focal length and the floor.
- **Facial expressions (beta)**: the photo's expression becomes the game's face bones, at the photo's actual amount and within the game's limits;
  eyelids, brows, mouth, jaw and gaze can be tuned separately, and face capture also corrects the head direction.
- **Keep Brio edits in the library**: bones you change in Brio after applying become this image's correction in one click.
- **Lighting**: 11 one-click lighting setups plus "Match the photo" (estimates the key light and colors from the photo), placed around the character and camera with full-body coverage, saved with gallery images, and a separate 3D window for adjusting each light and adding effects.
- **Framing and screenshots**: framing guide, guides, equivalent full-frame focal length, photo overlay, one-click screenshots of the game's own render.
- **Export** Brio `.pose` files.
- **Low-VRAM friendly**: Auto / GPU / CPU modes, the model is unloaded right after each recognition by default,
  and CPU mode uses no VRAM at all.
- **English and Simplified Chinese UI**, following the game client language by default.

## Requirements

- FINAL FANTASY XIV with Dalamud (API 15) and **Brio** installed and enabled
- Windows 10/11 64-bit
- For GPU inference: an NVIDIA GPU (CUDA is bundled in the backend package) or a ROCm-capable AMD GPU (via source setup below), with about **3 GB free VRAM** (about 2 GB used while recognizing).
  Without it, Posegraph runs on the CPU: about 10–20 s per image and about 4 GB RAM.
- About 7 GB of disk space for the backend package

## Installation

### 1. Backend package

1. Download **[Posegraph-Backend-win64.zip](https://drive.google.com/file/d/1PnbqdyH0hOTMSPhBaSXQ-Dpjp3fil6k2/view?usp=sharing)** (Google Drive, about 4 GB) and extract it somewhere with
   enough space. An ASCII-only path is recommended.
2. The folder contains `python`, `backend`, `LICENSES`, `Start-Backend.bat` and `README.txt`. Nothing needs to be installed.

### 2. Plugin

Posegraph is not in the official Dalamud repository; install it from the custom repository:

1. In game: `/xlsettings` → *Experimental* → *Custom Plugin Repositories*, add
   `https://raw.githubusercontent.com/extrant/DalamudPlugins/main/pluginmaster.json`, tick it and save.
2. Search for **Posegraph** in the plugin installer and install it.

Alternatively, download `Posegraph.zip` from the [Releases page](https://github.com/extrant/Posegraph/releases), extract it
and add `Posegraph.dll` under *Dev Plugin Locations*.

### 3. First start

1. `/posegraph` opens the main window.
2. *Settings* → **Choose backend package folder…** → select the extracted folder.
3. Click the **plug** icon in the status bar to start the backend. Optionally enable *Start the backend when the plugin loads*.

### Updating

- **Plugin**: updates automatically through the plugin repository.
- **Backend (package users)**: after the plugin updates, *Settings* shows "Backend …, plugin …". When the backend is older,
  click the download button next to it. The plugin downloads the matching source from this repository's tag
  (`archive/refs/tags/vX.Y.Z.zip`) and replaces `backend\src`. Python packages and models that new features need are
  **components** (listed in `backend/src/posegraph/components.json`, files in the
  [backend-components-1](https://github.com/extrant/Posegraph/releases/tag/backend-components-1) release): missing ones are
  downloaded, checked against SHA-256 and unpacked into the package, so older packages get face capture (about 64 MB) and
  depth (about 400 MB) with one click instead of a new full download.
  The previous version is kept as `backend\src.bak` and restored automatically if the new one fails to start.
  If GitHub is unreachable, set a mirror prefix such as `https://gh-proxy.org/`.
  If downloads fail in the plugin, download and unpack the files by hand: see [Updating the backend by hand](docs/manual-update.md)
  (component archives can be shared between users; checksums included).
- A full package download is only needed when Python / PyTorch itself changes; the plugin tells you
  (the package's `runtime.json` is older than what the new backend requires).
- Source installs: update with `git pull` / checkout of the same tag.

## Usage

1. Enter GPose and select the actor (or pick one in the actor list, which shows players, actors created in Brio
   and humanoid NPCs such as retainers).
2. Open the library (pictures icon) and double-click an image, or drag an image onto the *Image* tab
   (jpg, png, webp, bmp, gif first frame, tiff, avif). Dragged images are not added to the library; after recognition,
   the folder + button saves the image to the library together with your 3D / tuning corrections.
3. The first recognition loads the model (about 10–20 s); after that a recognition takes a few seconds.
   Results are cached by file content, so opening a recognized image again is instant.
4. Check the overlay (*Image* tab: green = detected skeleton, orange = FFXIV skeleton) and the *3D* tab.
5. Tune on the *Tuning* tab, read the *Hints* tab, then **Apply** (▶). Use the camera button to match the photo camera,
   ↶ to restore the pose from before the first apply, and the disk button to export a `.pose` file or save it to the
   Brio pose library.

### Library

- Images live in `Documents\Posegraph\Images` (imports, folder imports up to 500 images, and clipboard pastes are copied
  there; images already in the library are detected by content and not copied again). Existing folders can be added as
  sources without copying.
- Organize with subfolders, tags (search with `#tag`) and favorites; rename, move (also by dragging onto a folder) and
  delete to the Recycle Bin.
- Batch-recognize a selection; recognized images show a green check. Recognition results are cached in
  `Documents\Posegraph\Cache`.
- *Save to the Brio pose library* writes `.pose` files with a preview image and tags to `Documents\Brio\Poses\Posegraph`,
  where they show up in Brio's library.

Library batches are recognized one image at a time by default. With *Settings → Recognition backend → Batch recognition concurrency (experimental)*
set to 2–8, several images in a batch share one model and run together (about 35% less time per batch on an RTX 4060 laptop, ~300 MB more VRAM);
single images and auxiliary views are always one at a time. The backend lowers the count to fit free VRAM / RAM, and falls back to one at a time
(with a single notice) if the shared model is unavailable. See [parallel recognition notes](docs/parallel-recognition.md).

Status bar icons, left to right: Brio, GPose, IVCS (shown once an actor is selected in GPose), backend, VRAM, RAM;
hover for details.

### Settings

| Setting | Default | Notes |
|---|---|---|
| Language | Auto | Chinese if the game client is Chinese, otherwise the Dalamud UI language decides; English if neither is Chinese |
| Device | Auto | GPU when ~3 GB VRAM is free, otherwise CPU. *GPU* forces the GPU, *CPU* never touches VRAM |
| Batch recognition concurrency (experimental) | 1 | Batch recognition only; 2–8 share one model and run together. Effective next batch; 1 = one at a time |
| Unload model | Right after recognition | Frees VRAM/RAM for the game; the next recognition reloads the model (~5–10 s) |
| Keep MHR on the CPU | On | Saves ~750 MB VRAM in GPU mode, ~0.5 s slower |
| Apply when a slider is released | On | Turn off to apply manually |

### Races

| Skeleton group | Races | Status |
|---|---|---|
| Midlander-type ♂ | Midlander, Highlander, Miqo'te, Au Ra, Viera | verified in game |
| Elezen ♂ | Elezen | verified in game |
| Roegadyn-type ♂ | Roegadyn, Hrothgar | fixed in 0.3.2, awaiting a retest in game |
| Lalafell | Lalafell ♂ / ♀ | fixed in 0.3.2, awaiting a retest in game |
| Female skeletons | all | checked offline, awaiting a retest in game |

If the detected race is wrong (race-changing mods, Customize+ scaling), choose it manually in the race list next to the actor list.
The choice is remembered per actor for the GPose session; switching between actors does not clear their poses in Brio.

The skeleton data comes from IVCS. Actors without the IVCS skeleton work too: the bend of each finger's third joint is
merged into the second, and the IVCS icon in the status bar turns yellow.

## Known limitations

- Single-image depth is ambiguous (e.g. a hand in front of or behind the head).
- No collision avoidance yet: Lalafell's raised arms can pass through the head, and Roegadyn/Hrothgar thighs can
  sink into the belly in high-knee poses.
- Very strong elbow flexion creases more than in-game animations (~145° vs ~130°).
- Jumping / airborne images are pulled to the ground unless *Snap to ground* is turned off.
- Several people in one image (merge mode) is still being polished and is off in this version; video is reserved in the UI but not implemented. The whole image is treated as one person.
- Facial expressions: sticking out the tongue isn't possible; a one-eyed wink and a squinting smile are sometimes confused; faces in illustrations are rarely found.
- The CPU mode speed depends heavily on the CPU (measured ~10 s per image on a 20-thread laptop CPU).

## Building from source

### Plugin

```powershell
cd Posegraph
dotnet build -c Release      # output: Posegraph\bin\Release\Posegraph\latest.zip
```

The project uses `Dalamud.NET.SDK` 15 and `Brio.API`. For XIVLauncherCN the Dalamud path is picked up automatically.

### Backend (development install)

```powershell
cd backend
powershell -ExecutionPolicy Bypass -File scripts\setup_sam3d.ps1     # venv, CUDA/ROCm PyTorch, upstream source, weights (SHA256-checked)
.venv\Scripts\python -m posegraph.app                               # http://127.0.0.1:42430
```

Weights come from ModelScope by default (no login); `-Source hf` uses the official gated Hugging Face repository.
Environment variables (prefix `POSEGRAPH_`): `DEVICE` (auto|cuda|cpu), `PRECISION` (auto|bf16|fp16|fp32), `MHR_ON_CPU`,
`MIN_FREE_VRAM_MB`, `RELEASE_AFTER` (seconds, 0 = right away, -1 = keep), `PRELOAD`, `SAVE_SESSIONS` (save each recognition as an npz under `outputs/ui`), `PORT`, `BRIDGE_URL`, `SAM3D_REPO`, `SAM3D_CKPT`.
`RECOGNITION_PARALLELISM`: batch recognition concurrency (1–8, default 1; the plugin sets it before each batch). `DISABLE_PARALLEL=1` turns parallel recognition off entirely.

### Backend package

```powershell
powershell -ExecutionPolicy Bypass -File packaging\build_backend_package.ps1 `
    -Python C:\Path\To\Python311 -Venv backend\.venv `
    -Sam3d backend\third_party\sam-3d-body -Weights backend\checkpoints\sam-3d-body-vith `
    -Out D:\dist -Zip
```

It copies a regular CPython 3.11 install as a portable interpreter, the venv's site-packages, the backend source,
the SAM 3D Body source and weights, and the license files.

### Developer tools (`backend/scripts`, Chinese output)

| Script | Purpose |
|---|---|
| `photo_to_game.py`, `image_to_pose.py`, `push_pose.py`, `preview_overlay.py` | Command-line pipeline without the plugin UI |
| `verify_devices.py`, `bench_sam3d.py` | Compare CPU/GPU outputs, measure VRAM and speed (see `docs/benchmark.md`) |
| `build_retarget_data.py` | Regenerate skeleton data from IVCS `.sklb` files |
| `sample_frames.py` | Sample in-game animation frames (used to fit helper-bone rules) |
| `export_race_compare.py`, `blender_race_compare.py`, `build_body_extents.py` | Multi-race comparison scenes in Blender and body-size tables |

## HTTP interfaces

The plugin also runs a small local server (127.0.0.1:42429) used by the backend for camera matching and by the
command-line tools: `GET /status`, `GET /actors`, `GET|POST /pose?actor=N`, `GET|POST /camera`.
The backend (127.0.0.1:42430) exposes `GET /health`, `POST /sessions`, `GET /sessions/{id}/image`,
`POST /sessions/{id}/retarget`, `POST /sessions/{id}/camera`, `POST /model/load|unload|config`, `GET /races`, `GET /presets`,
`POST /shutdown`. Both listen on localhost only.

## License

Posegraph's own code (plugin, backend, scripts) is licensed under the [MIT License](LICENSE)
(versions up to 0.3.8 were released under the PolyForm Noncommercial License 1.0.0).
Third-party components keep their own licenses; in particular, the SAM 3D Body code and weights shipped in the
backend package are under Meta's SAM License.

## Credits

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). SAM 3D Body (code and weights) is © Meta and released under the SAM License;
the FFXIV skeleton data is extracted from Illusio Vitae Custom Skeletons.

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. Posegraph is a fan-made tool, not affiliated with or endorsed by Square Enix.
