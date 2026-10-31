# Third-party notices

Posegraph builds on the following work. The backend package redistributes the items marked with ★;
their license texts are in the package's `LICENSES` folder.

| Component | Use | License |
|---|---|---|
| ★ [SAM 3D Body](https://github.com/facebookresearch/sam-3d-body) (Meta) — code and `sam-3d-body-vith` weights | Human mesh recovery from a single image | [SAM License](https://github.com/facebookresearch/sam-3d-body/blob/main/LICENSE) — redistribution allowed with the license included |
| ★ [MHR — Momentum Human Rig](https://github.com/facebookresearch/MHR) (Meta), shipped inside the SAM 3D Body weights (`assets/mhr_model.pt`) | Body model used by SAM 3D Body | Apache-2.0 / SAM License |
| [Fast SAM 3D Body](https://github.com/yangtiming/Fast-SAM-3D-Body) (Yang et al.) | The fast recognition mode (`backend/src/posegraph/estimators/sam3d_fast.py`) ports its speed-ups: both hands in one batch and MHR without pose correctives. That file also adapts SAM 3D Body's decoder and inference code, which stays under the SAM License | MIT |
| ★ [Python](https://www.python.org/) 3.11 | Portable interpreter | PSF License |
| ★ [PyTorch](https://pytorch.org/) and CUDA runtime libraries | Inference | BSD-3-Clause; NVIDIA CUDA EULA for the bundled runtime |
| ★ [MediaPipe](https://github.com/google-ai-edge/mediapipe) (Google) — `mediapipe` package and the Face Landmarker model (`checkpoints/face/face_landmarker.task`); the Hand Landmarker model for the experimental finger refinement (off by default) is not shipped | Facial expression capture | Apache-2.0 |
| ★ [PyAV](https://github.com/PyAV-Org/PyAV) — `av` package with the FFmpeg libraries bundled in its wheel (`av.libs`; this FFmpeg build includes GPL parts such as x264 / x265, source: [FFmpeg](https://ffmpeg.org/download.html), [PyAV-ffmpeg build scripts](https://github.com/PyAV-Org/pyav-ffmpeg)) | Music in video playback and recording | BSD-3-Clause (PyAV); FFmpeg under GPL-2.0-or-later for this build |
| ★ [MoGe-2](https://github.com/microsoft/MoGe) (Microsoft) — `moge` package and the `moge-2-vitb-normal` model (`checkpoints/moge/model.pt`) | Depth, camera field of view and floor | MIT |
| ★ FastAPI, Uvicorn, Starlette, Pydantic, NumPy, SciPy, OpenCV, Pillow, roma, timm, … | Backend dependencies | Their respective open-source licenses (see each package's `*.dist-info` in `python/Lib/site-packages`) |
| [Brio](https://github.com/Etheirys/Brio) — `Brio.API` | Applying poses and freezing actors through IPC | See the Brio repository |
| [Dalamud](https://github.com/goatcorp/Dalamud) | Plugin framework | AGPL-3.0 |
| [Illusio Vitae Custom Skeletons (IVCS)](https://discord.gg/RvuPrCNBWP) | The FFXIV skeleton data in `backend/src/posegraph/retarget/data/ffxiv_*.json` (bone names, hierarchy, bind pose) was extracted from the IVCS 2.2 skeleton files; the racial deformer format was studied from IVCS's `human.pbd` | Credit to Illusio Vitae |
| The Body SE-X, hrBody 3 | Body sizes in `body_extents.json` were measured from these meshes (only aggregate measurements are included, no mesh data) | Credit to their authors |

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. All Rights Reserved. Posegraph is a fan-made tool and is not affiliated with or endorsed by Square Enix.
