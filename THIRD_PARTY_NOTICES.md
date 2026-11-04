# Third-party notices

Posegraph builds on the following work. The backend set up by the installer contains the items marked with ★
(downloaded from their original sources, or from this project's component archives); their license texts are in the
backend folder's `LICENSES` folder.

| Component | Use | License |
|---|---|---|
| ★ [SAM 3D Body](https://github.com/facebookresearch/sam-3d-body) (Meta) — code and `sam-3d-body-vith` weights | Human mesh recovery from a single image | [SAM License](https://github.com/facebookresearch/sam-3d-body/blob/main/LICENSE) — redistribution allowed with the license included |
| ★ [MHR — Momentum Human Rig](https://github.com/facebookresearch/MHR) (Meta), shipped inside the SAM 3D Body weights (`assets/mhr_model.pt`) | Body model used by SAM 3D Body | Apache-2.0 / SAM License |
| [Fast SAM 3D Body](https://github.com/yangtiming/Fast-SAM-3D-Body) (Yang et al.) | The fast recognition mode (`backend/src/posegraph/estimators/sam3d_fast.py`) ports its speed-ups: both hands in one batch and MHR without pose correctives. That file also adapts SAM 3D Body's decoder and inference code, which stays under the SAM License | MIT |
| ★ [Python](https://www.python.org/) 3.11 | Portable interpreter | PSF License |
| ★ [PyTorch](https://pytorch.org/) and CUDA runtime libraries | Inference | BSD-3-Clause; NVIDIA CUDA EULA for the bundled runtime |
| ★ [MediaPipe](https://github.com/google-ai-edge/mediapipe) (Google) — `mediapipe` package and the Face Landmarker model (`checkpoints/face/face_landmarker.task`); the Hand Landmarker model for the experimental finger refinement (off by default) is not shipped | Facial expression capture | Apache-2.0 |
| ★ [ONNX Runtime](https://github.com/microsoft/onnxruntime) (Microsoft) — `onnxruntime` package | Runs the person detection models on the CPU | MIT |
| ★ [Ultralytics YOLO11](https://github.com/ultralytics/ultralytics) — YOLO11m weights exported to ONNX (`checkpoints/detectors/yolo11m.onnx`) | Finding and tracking people in photos and videos | [AGPL-3.0](https://github.com/ultralytics/ultralytics/blob/main/LICENSE); the original weights and code are available from the Ultralytics repository |
| ★ [anime_person_detection](https://huggingface.co/deepghs/anime_person_detection) (deepghs) — `person_detect_v1.3_s` (`checkpoints/detectors/anime_person_v1.3_s.onnx`) | Finding people in illustrations | MIT |
| ★ [PyAV](https://github.com/PyAV-Org/PyAV) — `av` package with the FFmpeg libraries bundled in its wheel (`av.libs`; this FFmpeg build includes GPL parts such as x264 / x265, source: [FFmpeg](https://ffmpeg.org/download.html), [PyAV-ffmpeg build scripts](https://github.com/PyAV-Org/pyav-ffmpeg)) | Music in video playback and recording | BSD-3-Clause (PyAV); FFmpeg under GPL-2.0-or-later for this build |
| ★ [MoGe-2](https://github.com/microsoft/MoGe) (Microsoft) — `moge` package and the `moge-2-vitb-normal` model (`checkpoints/moge/model.pt`) | Depth, camera field of view and floor | MIT |
| ★ FastAPI, Uvicorn, Starlette, Pydantic, NumPy, SciPy, OpenCV, Pillow, roma, timm, … | Backend dependencies | Their respective open-source licenses (see each package's `*.dist-info` in `python/Lib/site-packages`) |
| [Brio](https://github.com/Etheirys/Brio) — `Brio.API` | Applying poses and freezing actors through IPC | See the Brio repository |
| [Dalamud](https://github.com/goatcorp/Dalamud) | Plugin framework | AGPL-3.0 |
| [ReAnimate](https://github.com/Nyotee/ReAnimate) (Nyaughty) | "Convert to idle" (`Posegraph/Idle/`) ports its approach and code: baking a pose into the game's idle `.pap` with the game's own Havok runtime, keeping the original breathing motion, grounding, the `_start` transition easing, the idle slot table and race fallbacks, and saving it as a Penumbra mod | MIT, Copyright (c) 2026 Nyaughty |
| [VFXEditor](https://github.com/0ceal0t/Dalamud-VFXEditor) (0ceal0t) | Driving the game's Havok runtime from a plugin (loading, sampling and saving animations; the interleaved-animation vtable signature), used by the face data export and "Convert to idle" | MIT |
| [Illusio Vitae Custom Skeletons (IVCS)](https://discord.gg/RvuPrCNBWP) | The FFXIV skeleton data in `backend/src/posegraph/retarget/data/ffxiv_*.json` (bone names, hierarchy, bind pose) was extracted from the IVCS 2.2 skeleton files; the racial deformer format was studied from IVCS's `human.pbd` | Credit to Illusio Vitae |
| The Body SE-X, hrBody 3 | Body sizes in `body_extents.json` were measured from these meshes (only aggregate measurements are included, no mesh data) | Credit to their authors |

## ReAnimate license

```
MIT License

Copyright (c) 2026 Nyaughty

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. All Rights Reserved. Posegraph is a fan-made tool and is not affiliated with or endorsed by Square Enix.
