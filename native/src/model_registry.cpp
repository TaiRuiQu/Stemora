#include "bs_roformer/inference.h"
#include "infra.hpp"
#include <algorithm>
#include <cctype>
#include <cstring>
#include <filesystem>
#include <gguf.h>
#include <map>
#include <mutex>
#include <sstream>
#include <stdexcept>

namespace stemora {
std::string ModelDescription::json() const {
    std::ostringstream out;
    out << "{\"architecture\":" << quote(architecture) << ",\"family\":" << quote(family) << ",\"name\":" << quote(name)
        << ",\"sampleRate\":" << sample_rate << ",\"channels\":" << channels << ",\"chunkFrames\":" << chunk_frames
        << ",\"overlap\":" << overlap << ",\"stems\":[";
    for (size_t i = 0; i < stems.size(); ++i) {
        if (i)
            out << ',';
        out << quote(stems[i]);
    }
    return out.str() + "]}";
}
namespace {
std::map<std::string, ModelRegistry::Factory> &factories() {
    static std::map<std::string, ModelRegistry::Factory> value;
    return value;
}
std::mutex &registry_mutex() {
    static std::mutex value;
    return value;
}
uint32_t integer(gguf_context *context, const std::string &key, uint32_t fallback) {
    const auto index = gguf_find_key(context, key.c_str());
    if (index < 0)
        return fallback;
    switch (gguf_get_kv_type(context, index)) {
    case GGUF_TYPE_UINT32:
        return gguf_get_val_u32(context, index);
    case GGUF_TYPE_INT32: {
        const auto value = gguf_get_val_i32(context, index);
        if (value < 0)
            throw std::runtime_error("Negative GGUF integer: " + key);
        return static_cast<uint32_t>(value);
    }
    default:
        throw std::runtime_error("Invalid GGUF integer metadata: " + key);
    }
}
std::string string_value(gguf_context *context, const std::string &key, std::string fallback = {}) {
    const auto index = gguf_find_key(context, key.c_str());
    if (index < 0)
        return fallback;
    if (gguf_get_kv_type(context, index) != GGUF_TYPE_STRING)
        throw std::runtime_error("Invalid GGUF string: " + key);
    return gguf_get_val_str(context, index);
}
class RoformerAdapter final : public ModelAdapter {
    Inference graph_;

  public:
    RoformerAdapter(const std::string &path, const RunOptions &options)
        : graph_(path, options.device, options.threads) {}
    Stems infer_chunk(const Audio &pcm) override { return graph_.ProcessChunk(pcm); }
    std::string backend_name() const override { return graph_.GetBackendName(); }
};
void register_builtins() {
    static std::once_flag once;
    std::call_once(once, [] {
        auto factory = [](const std::string &path, const RunOptions &options) {
            return std::make_unique<RoformerAdapter>(path, options);
        };
        ModelRegistry::register_family("bs_roformer", factory);
        ModelRegistry::register_family("mel_band_roformer", factory);
    });
}
} // namespace
void ModelRegistry::register_family(const std::string &architecture, Factory factory) {
    std::lock_guard<std::mutex> guard(registry_mutex());
    if (!factory || !factories().emplace(architecture, std::move(factory)).second)
        throw std::invalid_argument("Invalid or duplicate model-family registration: " + architecture);
}
std::unique_ptr<ModelAdapter> ModelRegistry::create(const ModelDescription &model, const std::string &path,
                                                    const RunOptions &options) {
    register_builtins();
    Factory factory;
    {
        std::lock_guard<std::mutex> guard(registry_mutex());
        const auto match = factories().find(model.architecture);
        if (match == factories().end())
            throw std::runtime_error("Unsupported model architecture: " + model.architecture);
        factory = match->second;
    }
    return factory(path, options);
}
ModelDescription ModelRegistry::probe(const std::string &path, const RunOptions &options) {
    gguf_init_params parameters{true, nullptr};
    auto *raw = gguf_init_from_file(path.c_str(), parameters);
    if (!raw)
        throw std::runtime_error("Cannot read GGUF model: " + path);
    std::unique_ptr<gguf_context, decltype(&gguf_free)> context(raw, gguf_free);
    ModelDescription result;
    result.architecture = string_value(raw, "general.architecture");
    if (result.architecture == "bs")
        result.architecture = "bs_roformer";
    if (result.architecture == "mel_band")
        result.architecture = "mel_band_roformer";
    if (result.architecture != "bs_roformer" && result.architecture != "mel_band_roformer")
        throw std::runtime_error("Unsupported model family: " + result.architecture);
    const auto prefix = result.architecture + ".";
    result.family = result.architecture;
    result.name = string_value(raw, "general.name", std::filesystem::u8path(path).stem().u8string());
    const auto count = integer(raw, prefix + "num_stems", 1);
    if (!count || count > 64)
        throw std::runtime_error("Invalid model stem count.");
    result.sample_rate = static_cast<int>(integer(raw, prefix + "sample_rate", 44100));
    result.chunk_frames = static_cast<int>(integer(raw, prefix + "default_chunk_size", 352800));
    result.overlap = static_cast<int>(integer(raw, prefix + "default_num_overlap", 2));
    result.fft_size = static_cast<int>(integer(raw, prefix + "stft_n_fft", 2048));
    if (integer(raw, prefix + "linear_transformer_depth", 0) != 0)
        throw std::runtime_error("This adapter does not implement linear-transformer attention.");
    result.stems = split_stems(options.stems);
    if (result.stems.empty()) {
        for (const auto &key : {std::string("stemora.stems"), prefix + "stems", prefix + "instruments",
                                std::string("training.instruments")}) {
            const auto index = gguf_find_key(raw, key.c_str());
            if (index < 0)
                continue;
            if (gguf_get_kv_type(raw, index) != GGUF_TYPE_ARRAY || gguf_get_arr_type(raw, index) != GGUF_TYPE_STRING)
                throw std::runtime_error("Stem metadata must be a string array.");
            for (size_t i = 0; i < gguf_get_arr_n(raw, index); ++i) {
                auto id = std::string(gguf_get_arr_str(raw, index, i));
                validate_stem_id(id);
                result.stems.push_back(id);
            }
            break;
        }
    }
    if (options.profile == "bs_roformer_sw") {
        if (result.architecture != "bs_roformer" || count != 6)
            throw std::runtime_error("BS Roformer SW profile requires a six-stem BS Roformer model.");
        const std::vector<std::string> sw{"bass", "drums", "other", "vocals", "guitar", "piano"};
        if (!result.stems.empty() && result.stems != sw)
            throw std::runtime_error("SW profile contradicts supplied stem order.");
        result.stems = sw;
        result.family = "bs_roformer_sw";
        result.name = "BS Roformer SW";
    } else if (!options.profile.empty() && options.profile != result.architecture)
        throw std::runtime_error("Unknown or mismatching model profile.");
    if (result.stems.empty())
        for (uint32_t i = 0; i < count; ++i)
            result.stems.push_back(count == 1 ? "primary" : "stem_" + std::to_string(i + 1));
    if (result.stems.size() != count)
        throw std::runtime_error("Stem labels do not match model outputs.");
    auto sorted = result.stems;
    std::sort(sorted.begin(), sorted.end());
    if (std::adjacent_find(sorted.begin(), sorted.end()) != sorted.end())
        throw std::runtime_error("Duplicate stem labels.");
    if (result.sample_rate != 44100 || result.fft_size < 256 || result.fft_size > 16384 ||
        (result.fft_size & (result.fft_size - 1)))
        throw std::runtime_error("Unsupported Roformer audio/STFT format.");
    return result;
}
} // namespace stemora
