# Third-party notices

Stemora uses the following third-party components. Their original licenses remain applicable. Model weights are independent of the application and engine license.

| Component | Source | License / terms |
| --- | --- | --- |
| BSRoformer.cpp v0.1.0 | https://github.com/chenmozhijin/BSRoformer.cpp | MIT; upstream release archives also contain their runtime dependencies |
| GGML and dependencies | https://github.com/ggml-org/ggml | MIT and component-specific notices in upstream distributions |
| Salamander Grand Piano recordings — Alexander Holm | https://github.com/Tonejs/audio/tree/869b6f8d9cddb47d966238c012041480b1ce517a/salamander | CC BY 3.0; adapted piano samples bundled; see `licenses/Salamander-Grand-Piano.txt` |
| NAudio 2.3.0 | https://github.com/naudio/NAudio | MIT |
| MuScriptor 0.4.0 | https://github.com/muscriptor/muscriptor | MIT software; installed on demand with its Python / PyTorch dependencies and their respective licenses |
| MuScriptor model weights | https://huggingface.co/MuScriptor | CC BY-NC 4.0; access requires acceptance on each official model card; weights are not bundled with Stemora |
| madmom-infer, pinned at bffead9be61857fd44b2e0d3a2510d9d94c8f2d2 | https://github.com/openmirlab/madmom-infer | BSD-2-Clause software; installed on demand in the transcription environment |
| madmom beat/downbeat BLSTM and key CNN weights | https://github.com/CPJKU/madmom_models | CC BY-NC-SA 4.0; downloaded on demand from the official repository, revision / SHA-256 pinned by madmom-infer; not bundled with Stemora |
| uv 0.13.1 | https://github.com/astral-sh/uv | MIT or Apache-2.0; downloaded and verified on first transcription setup |
| NVIDIA CUDA runtime / cuBLAS | https://developer.nvidia.com/cuda-toolkit | NVIDIA redistribution terms and license text in `licenses/NVIDIA-*.txt` |
| Microsoft Visual C++ runtime | https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist | Microsoft runtime redistribution terms; see `licenses/MSVC-Redistributable.txt` |
| Windows App SDK / WinUI | https://github.com/microsoft/WindowsAppSDK | Microsoft/WinUI package licenses and third-party notices distributed in the packages |
| .NET runtime | https://github.com/dotnet/runtime | MIT and runtime third-party notices in the published distribution |
| GGUF models | https://huggingface.co/chenmozhijin/BSRoformer-GGUF | Model conversion repository; consult each original model author's model card and license before redistribution or commercial use |

Original model sources:

- https://huggingface.co/anvuew/BS-RoFormer
- https://huggingface.co/becruily/mel-band-roformer-deux
- https://huggingface.co/GaboxR67/MelBandRoformers
- https://huggingface.co/FractalSurfer/BS-RoFormer-SW-6stem (SW six-stem checkpoint mirror; original weight license is undeclared)

The downloaded native runtime archives and their DLLs must remain together. Preserve any LICENSE/NOTICE files that ship in those archives. Models are not treated as MIT-licensed merely because the inference engine is MIT-licensed.

## BSRoformer.cpp license

The verbatim upstream v0.1.0 license is included as `licenses/BSRoformer.cpp.txt` in the source and portable distribution.
