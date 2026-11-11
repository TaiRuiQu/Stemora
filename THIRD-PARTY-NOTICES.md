# Third-party notices

Stemora uses the following third-party components. Their original licenses remain applicable. Model weights are independent of the application and engine license.

| Component | Source | License / terms |
| --- | --- | --- |
| BSRoformer.cpp v0.1.0 | https://github.com/chenmozhijin/BSRoformer.cpp | MIT; upstream release archives also contain their runtime dependencies |
| GGML and dependencies | https://github.com/ggml-org/ggml | MIT and component-specific notices in upstream distributions |
| NAudio 2.3.0 | https://github.com/naudio/NAudio | MIT |
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
