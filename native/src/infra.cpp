#include "infra.hpp"
#include "stemora_infra.h"
#include <cstring>
#include <ggml-backend.h>
#include <mutex>
#include <stdexcept>

struct stemora_session {
    stemora::ModelDescription description;
    stemora::RunOptions options;
    std::unique_ptr<stemora::ModelAdapter> adapter;
    stemora::Stems result;
    std::string metadata;
    std::atomic<bool> cancelled{false};
    std::mutex operation;
};
namespace {
thread_local std::string error;
stemora::RunOptions parse(const stemora_options *options) {
    stemora::RunOptions result;
    if (!options)
        return result;
    if (options->struct_size != sizeof(stemora_options) || options->abi_version != 1)
        throw std::invalid_argument("Unsupported Stemora Infra ABI options.");
    if (options->device < 0 || options->device > 2 || options->threads < 0 || options->threads > 512)
        throw std::invalid_argument("Invalid device or thread count.");
    result.device = options->device;
    result.threads = options->threads;
    result.chunk_frames = options->chunk_frames;
    result.overlap = options->overlap;
    if (options->profile)
        result.profile = options->profile;
    if (options->stems)
        result.stems = options->stems;
    return result;
}
} // namespace
uint32_t stemora_abi_version() { return 1; }
const char *stemora_version() { return "Stemora Infra 0.3.0"; }
const char *stemora_last_error() { return error.c_str(); }
size_t stemora_probe(const char *model, const stemora_options *options, char *json, size_t capacity) {
    try {
        error.clear();
        if (!model || !*model)
            throw std::invalid_argument("Missing model path.");
        const auto result = stemora::ModelRegistry::probe(model, parse(options)).json();
        if (json && capacity >= result.size() + 1)
            std::memcpy(json, result.c_str(), result.size() + 1);
        return result.size() + 1;
    } catch (const std::exception &ex) {
        error = ex.what();
        return 0;
    }
}
int stemora_create(const char *model, const stemora_options *options, stemora_session **output) {
    try {
        error.clear();
        if (!output || !model || !*model)
            throw std::invalid_argument("Invalid session arguments.");
        *output = nullptr;
        auto session = std::make_unique<stemora_session>();
        session->options = parse(options);
        session->description = stemora::ModelRegistry::probe(model, session->options);
        static std::once_flag init;
        std::call_once(init, [] { ggml_backend_load_all(); });
        session->adapter = stemora::ModelRegistry::create(session->description, model, session->options);
        session->metadata = session->description.json();
        session->metadata.pop_back();
        session->metadata += ",\"backend\":" + stemora::quote(session->adapter->backend_name()) + "}";
        *output = session.release();
        return STEMORA_OK;
    } catch (const std::exception &ex) {
        error = ex.what();
        return STEMORA_ERROR;
    }
}
const char *stemora_model_json(const stemora_session *session) { return session ? session->metadata.c_str() : nullptr; }
int stemora_process(stemora_session *session, const float *input, uint64_t frames, stemora_progress_callback callback,
                    void *user) {
    try {
        error.clear();
        if (!session || !input || !frames || frames > (1ULL << 30))
            throw std::invalid_argument("Invalid PCM buffer.");
        std::unique_lock<std::mutex> guard(session->operation, std::try_to_lock);
        if (!guard.owns_lock())
            throw std::invalid_argument("Session is already processing.");
        if (session->cancelled.load())
            return STEMORA_CANCELLED;
        session->result.clear();
        stemora::Audio audio(input, input + frames * 2);
        const auto &model = session->description;
        session->result = stemora::overlap_add(
            audio, model, session->options.chunk_frames > 0 ? session->options.chunk_frames : model.chunk_frames,
            session->options.overlap > 0 ? session->options.overlap : model.overlap,
            [&](const stemora::Audio &block) { return session->adapter->infer_chunk(block); }, session->cancelled,
            [&](float fraction, const char *stage) {
                if (callback)
                    callback(fraction, stage, user);
            });
        return STEMORA_OK;
    } catch (const std::exception &ex) {
        error = ex.what();
        return session && session->cancelled.load() ? STEMORA_CANCELLED : STEMORA_ERROR;
    }
}
void stemora_cancel(stemora_session *session) {
    if (session)
        session->cancelled.store(true);
}
uint32_t stemora_stem_count(const stemora_session *session) {
    return session ? static_cast<uint32_t>(session->description.stems.size()) : 0;
}
const char *stemora_stem_name(const stemora_session *session, uint32_t index) {
    return session && index < session->description.stems.size() ? session->description.stems[index].c_str() : nullptr;
}
uint64_t stemora_frame_count(const stemora_session *session) {
    return session && !session->result.empty() ? session->result.front().size() / 2 : 0;
}
int stemora_copy_stem(const stemora_session *session, uint32_t index, float *output, uint64_t capacity) {
    if (!session || index >= session->result.size() || !output || capacity < session->result[index].size()) {
        error = "Invalid output stem buffer.";
        return STEMORA_INVALID_ARGUMENT;
    }
    std::memcpy(output, session->result[index].data(), session->result[index].size() * sizeof(float));
    return STEMORA_OK;
}
void stemora_destroy(stemora_session *session) { delete session; }
