#pragma once
#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace stemora {
struct ModelDescription {
    std::string architecture;
    std::string family;
    std::string name;
    std::vector<std::string> stems;
    int sample_rate = 44100;
    int channels = 2;
    int chunk_frames = 352800;
    int overlap = 2;
    int fft_size = 2048;
    std::string json() const;
};
struct RunOptions {
    int device = 0;
    int threads = 0;
    int chunk_frames = 0;
    int overlap = 0;
    std::string profile;
    std::string stems;
};
using Progress = std::function<void(float, const char *)>;
using Audio = std::vector<float>;
using Stems = std::vector<Audio>;

/* New model families implement this boundary. Chunking, cancellation, ABI,
   output validation, host protocol and stem routing are shared infrastructure. */
class ModelAdapter {
  public:
    virtual ~ModelAdapter() = default;
    virtual Stems infer_chunk(const Audio &pcm) = 0;
    virtual std::string backend_name() const = 0;
};
class ModelRegistry {
  public:
    using Factory = std::function<std::unique_ptr<ModelAdapter>(const std::string &, const RunOptions &)>;
    static ModelDescription probe(const std::string &path, const RunOptions &options);
    static std::unique_ptr<ModelAdapter> create(const ModelDescription &, const std::string &, const RunOptions &);
    static void register_family(const std::string &architecture, Factory factory);
};

/* Sample-aligned weighted overlap-add, independent of any model adapter. */
Stems overlap_add(const Audio &input, const ModelDescription &description, int chunk_frames, int overlap,
                  const std::function<Stems(const Audio &)> &infer, std::atomic<bool> &cancelled,
                  const Progress &progress);
std::string quote(const std::string &text);
std::vector<std::string> split_stems(const std::string &value);
void validate_stem_id(const std::string &id);
} // namespace stemora
