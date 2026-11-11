# Model catalog metadata

`src/Stemora.Core/Data/models.json` pins each model file and records its output order, quantization, supported compute devices, recommended device and available app export formats. `WAV` and `MP3` describe Stemora's exporters, not the GGUF model format.

Output definitions were checked against these upstream model configurations and the available pinned GGUF files:

- BS Roformer SW: `bass`, `drums`, `other`, `vocals`, `guitar`, `piano`. [Model repository](https://huggingface.co/FractalSurfer/BS-RoFormer-SW-6stem).
- anvuew BS-RoFormer: one vocal output. [Configuration](https://huggingface.co/anvuew/BS-RoFormer/blob/main/config.yaml).
- GaboxR67 voc_fv6: one vocal output. [Model family](https://huggingface.co/GaboxR67/MelBandRoformers/tree/main/melbandroformers/vocals).
- becruily Deux: `vocals`, `instrumental`. [Configuration](https://huggingface.co/becruily/mel-band-roformer-deux/blob/main/config_deux_becruily.yaml).

The bundled engine supports CPU and CUDA for both architectures. CUDA is recommended for speed; no current catalog model requires a dedicated GPU. [Upstream engine documentation](https://github.com/chenmozhijin/BSRoformer.cpp).

The catalog now includes local CPU/CUDA measurements, calibrated estimates and recommended RAM/VRAM capacities. See [the measurement report](MODEL-MEMORY.md) for the hardware, default chunk sizes, counter definitions, raw results and estimation method. Every FP16/FP32 version was measured directly. Remaining quantizations use a measured reference from the same model group.

`hardware.memory.basis` distinguishes `measured` and `estimated`; `referenceModelId`, `chunkFrames`, `overlap`, `audioSeconds` and `benchmark` preserve provenance. `cpuRamMiB` and `cudaRamMiB` are host working-set peaks for separate CPU/CUDA runs; `vramMiB` is process dedicated GPU memory. `recommendedRamGb` and `recommendedVramGb` add operating headroom and do not describe model file size. The interface keeps only concise platform and capacity tags; detailed conditions belong in the report.

The English message catalog is `src/Stemora.Core/Data/en-US.json`. Chinese source messages are its keys; preserve all numbered format placeholders. GUI attached resources, queue statuses, track labels, dialogs, progress and application errors use this shared catalog. Language preference is stored alongside the other portable settings.
