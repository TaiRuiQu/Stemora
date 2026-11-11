#include "infra.hpp"
#include <algorithm>
#include <cmath>
#include <limits>
#include <stdexcept>

namespace stemora {
Stems overlap_add(const Audio &input, const ModelDescription &model, int chunk, int overlap,
                  const std::function<Stems(const Audio &)> &infer, std::atomic<bool> &cancelled,
                  const Progress &progress) {
    if (input.empty() || input.size() % 2)
        throw std::invalid_argument("Expected non-empty interleaved stereo PCM.");
    if (chunk < model.fft_size * 2 || chunk > 3528000 || overlap < 1 || overlap > 32 || chunk / overlap == 0)
        throw std::invalid_argument("Invalid chunk size or overlap.");
    const auto frames = static_cast<int64_t>(input.size() / 2);
    const int step = chunk / overlap;
    const int border = chunk - step;
    const int64_t pad = border > 0 && frames > 2LL * border ? border : 0;
    const int64_t padded_frames = frames + 2 * pad;
    Stems output(model.stems.size(), Audio(input.size(), 0));
    std::vector<float> weights(static_cast<size_t>(frames), 0);
    const int fade = overlap == 1 ? 0 : chunk / 10;
    const int64_t chunks = (padded_frames + step - 1) / step;
    Audio block(static_cast<size_t>(chunk) * 2);
    auto sample = [&](int64_t padded_index, int channel) {
        int64_t index = padded_index - pad;
        if (index < 0)
            index = std::min(-index, frames - 1);
        if (index >= frames)
            index = std::max<int64_t>(0, 2 * frames - 2 - index);
        return input[static_cast<size_t>(index) * 2 + channel];
    };
    int64_t completed = 0;
    for (int64_t offset = 0; offset < padded_frames; offset += step) {
        if (cancelled.load())
            throw std::runtime_error("cancelled");
        const int valid = static_cast<int>(std::min<int64_t>(chunk, padded_frames - offset));
        std::fill(block.begin(), block.end(), 0);
        for (int i = 0; i < valid; ++i)
            for (int ch = 0; ch < 2; ++ch)
                block[static_cast<size_t>(i) * 2 + ch] = sample(offset + i, ch);
        if (valid < chunk && valid > chunk / 2 + 1)
            for (int i = valid; i < chunk; ++i)
                for (int ch = 0; ch < 2; ++ch)
                    block[static_cast<size_t>(i) * 2 + ch] =
                        block[static_cast<size_t>(std::max(0, 2 * valid - 2 - i)) * 2 + ch];
        auto prediction = infer(block);
        if (cancelled.load())
            throw std::runtime_error("cancelled");
        if (prediction.size() != output.size())
            throw std::runtime_error("Adapter returned an incorrect number of stems.");
        for (const auto &stem : prediction)
            if (stem.size() < static_cast<size_t>(valid) * 2)
                throw std::runtime_error("Adapter returned a truncated chunk.");
        for (int i = 0; i < valid; ++i) {
            const auto destination = offset + i - pad;
            if (destination < 0 || destination >= frames)
                continue;
            float weight = 1;
            if (fade > 1 && offset != 0 && i < fade)
                weight *= static_cast<float>(i) / (fade - 1);
            if (fade > 1 && offset + step < padded_frames && i >= chunk - fade)
                weight *= static_cast<float>(chunk - 1 - i) / (fade - 1);
            weights[static_cast<size_t>(destination)] += weight;
            for (size_t s = 0; s < output.size(); ++s)
                for (int ch = 0; ch < 2; ++ch) {
                    const auto value = prediction[s][static_cast<size_t>(i) * 2 + ch];
                    if (!std::isfinite(value))
                        throw std::runtime_error("Non-finite model output.");
                    output[s][static_cast<size_t>(destination) * 2 + ch] += value * weight;
                }
        }
        ++completed;
        if (progress)
            progress(static_cast<float>(completed) / static_cast<float>(chunks), "inference");
    }
    for (int64_t frame = 0; frame < frames; ++frame) {
        const auto weight = weights[static_cast<size_t>(frame)];
        if (!(weight > 0))
            throw std::runtime_error("Uncovered sample in overlap-add reconstruction.");
        for (auto &stem : output)
            for (int ch = 0; ch < 2; ++ch)
                stem[static_cast<size_t>(frame) * 2 + ch] /= weight;
    }
    return output;
}
} // namespace stemora
