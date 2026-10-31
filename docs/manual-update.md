# Updating the backend by hand (when downloads fail)

**English** | [简体中文](manual-update.zh-CN.md)

"Update backend" in the plugin downloads the backend code and the components new features need. If that doesn't work
(GitHub unreachable, even with a download prefix), download the files in a browser or get the archives from a friend
who already has them, then unpack them as described below.

All you do is **unpack a few archives into the package folder**; there's no need to download the 4 GB package again.

"Package folder" below means the folder you got from unpacking the one-click package: it contains the `python` and
`backend` folders and `Start-Backend.bat`, e.g. `D:\Posegraph-Backend`.

---

## 0. Before you start

1. Update the plugin to the latest version (update Posegraph in the plugin installer).
2. Click the **power** button at the top right of the main window to **stop the backend** (the backend icon in the
   status bar turns gray). Files in use can't be replaced.

## 1. Files you need

| File | Size | For | Download |
|---|---|---|---|
| `posegraph-face-1.zip` | about 64 MB | Face capture (expressions, head follows face) | [GitHub](https://github.com/extrant/Posegraph/releases/download/backend-components-1/posegraph-face-1.zip) |
| `posegraph-depth-1.zip` | about 400 MB | Depth (camera focal length, floor) | [GitHub](https://github.com/extrant/Posegraph/releases/download/backend-components-1/posegraph-depth-1.zip) |
| `posegraph-audio-2.zip` | about 70 MB | Audio (music for video playback and recording, 0.7.0 and later) | [GitHub](https://github.com/extrant/Posegraph/releases/download/backend-components-2/posegraph-audio-2.zip) |
| `Posegraph-0.7.0.zip` | about 5 MB | Backend code | [GitHub](https://github.com/extrant/Posegraph/archive/refs/tags/v0.7.0.zip) |

- Face capture and depth are on the [backend-components-1](https://github.com/extrant/Posegraph/releases/tag/backend-components-1) page, audio on the [backend-components-2](https://github.com/extrant/Posegraph/releases/tag/backend-components-2) page;
  the backend code is **Source code (zip)** at the bottom of the [v0.7.0](https://github.com/extrant/Posegraph/releases/tag/v0.7.0) page.
- If GitHub won't open, put a download prefix in front of the link, e.g.
  `https://gh-proxy.org/https://github.com/extrant/Posegraph/releases/download/backend-components-1/posegraph-face-1.zip`.
- Components are installed once: later versions don't need them again unless they change.
- If the plugin can update the code but the large components won't download, install just the components by hand (step 2),
  then click "Update backend" in the plugin: it skips components that are already there and only downloads the ~5 MB code.

## 2. Unpack the components

Every component works the same way; `posegraph-face-1.zip` is the example (the audio component only has `python`). The archive contains a `python` and a
`backend` folder, which have to **merge with the folders of the same name in the package**.

**With Windows' built-in extractor (right-click → Extract All):**

1. Right-click the archive → **Extract All…**
2. Click **Browse…** and pick the package folder (e.g. `D:\Posegraph-Backend`).
3. **Important**: make sure the path does **not** end with an extra `\posegraph-face-1`. Windows appends the archive's
   name; delete it so the path is exactly `D:\Posegraph-Backend`.
4. Click **Extract**. When asked about existing files, choose **Replace the files in the destination**.

**With 7-Zip / Bandizip / WinRAR:**

1. Put the archive in the package folder (next to `python` and `backend`).
2. Right-click → **Extract Here** (not "Extract to posegraph-face-1\").
3. Choose **Yes to All** / overwrite when asked.

If it went right, these files exist (replace `D:\Posegraph-Backend` with your package folder):

- Face capture: `D:\Posegraph-Backend\python\Lib\site-packages\mediapipe\__init__.py` and `D:\Posegraph-Backend\backend\checkpoints\face\face_landmarker.task`
- Depth: `D:\Posegraph-Backend\python\Lib\site-packages\moge\__init__.py` and `D:\Posegraph-Backend\backend\checkpoints\moge\model.pt`

If you ended up with something like `D:\Posegraph-Backend\posegraph-face-1\python\…`, it went into a subfolder: move the
`python` and `backend` folders inside `posegraph-face-1` up into the package folder (merge, overwrite), then delete the empty `posegraph-face-1`.

## 3. Replace the backend code

1. Unpack `Posegraph-0.7.0.zip` anywhere (the desktop is fine).
2. In the package's `backend` folder, **rename** the existing `src` folder to `src.old` (a backup; delete it once the new version works).
3. **Copy** the `src` folder from the unpacked `Posegraph-0.7.0\backend` into the package's `backend` folder.

Afterwards `D:\Posegraph-Backend\backend\src\posegraph\version.json` says `"version": "0.7.0"`.

## 4. Check

Back in game, on the plugin's **Settings** tab:

- "Backend 0.7.0, plugin v0.7.0" and **Up to date** means everything is in place.
- "Missing components: …" means that component isn't unpacked correctly; check the files from step 2.
- Still offering an update means `src` wasn't replaced correctly; check the version in `version.json`.

Then click the **power** button at the top right of the main window to start the backend.

If the new backend doesn't start: delete the new `src` and rename `src.old` back to `src` to return to where you were,
and let us know on Discord or GitHub Issues.

## 5. Sharing files with friends: verify the checksum

The archives can be passed around (group files, cloud drives). To make sure a file is complete and unmodified, check
its SHA-256 before unpacking:

1. In the folder with the archive, type `cmd` in the address bar and press Enter.
2. Run (adjust the file name):

   ```
   certutil -hashfile posegraph-face-1.zip SHA256
   ```

3. The result must match exactly (case doesn't matter):

| File | SHA-256 |
|---|---|
| `posegraph-face-1.zip` | `ac62fb58b2a7600f73c48f08811e6bd15a47f1104fb8dd1f00b9f6fd24604721` |
| `posegraph-depth-1.zip` | `23e387d0008c7065fd5573d7b7fec8b28ca7997a899fff4efb4e1eabd80b03bf` |
| `posegraph-audio-2.zip` | `91e63c7c0d97fca103b499437d57cc93246828cc5305732a6356e3a6f3fee378` |

If it doesn't match, don't use it; download it again. The backend code `Posegraph-0.7.0.zip` is repackaged by GitHub on
each download and has no fixed checksum, so it's best to get it from GitHub yourself.

A shortcut: put the verified component archives in a `.download` folder inside the package folder (create it if needed),
rename them to `face.zip` / `depth.zip`, then click "Update backend" in the plugin. It checks that folder first and
installs matching files without downloading them (it still needs GitHub for the ~5 MB code).
