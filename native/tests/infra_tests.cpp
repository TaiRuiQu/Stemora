#include "infra.hpp"
#include "stemora_infra.h"
#include <cmath>
#include <iostream>
#include <stdexcept>

int main() {
    int checks = 0;
    auto check = [&](bool condition, const char *label) {
        if (!condition)
            throw std::runtime_error(label);
        ++checks;
        std::cout << "PASS " << label << '\n';
    };
    try {
        check(stemora_abi_version() == 1, "C ABI version");
        stemora_options options{sizeof(stemora_options), 99, 0, 0, 0, 0, nullptr, nullptr};
        check(stemora_probe("missing.gguf", &options, nullptr, 0) == 0, "reject mismatching ABI");
        options.abi_version = 1;
        options.device = 3;
        check(stemora_probe("missing.gguf", &options, nullptr, 0) == 0, "reject unknown compute device");
        check(stemora::quote("a\nb\"c") == "\"a\\nb\\\"c\"", "JSON escaping");
        check(stemora::split_stems("bass,drums,other,vocals,guitar,piano").size() == 6, "six-stem order parsing");
        try {
            stemora::split_stems("../escape");
            throw std::runtime_error("unsafe stem accepted");
        } catch (const std::invalid_argument &) {
            check(true, "reject stem path traversal");
        }
        try {
            stemora::split_stems("bass,bass");
            throw std::runtime_error("duplicate stem accepted");
        } catch (const std::invalid_argument &) {
            check(true, "reject duplicate stem IDs");
        }
        for (int frames : {1, 400, 1024, 3001, 8192})
            for (int overlaps : {1, 2, 4}) {
                stemora::ModelDescription model;
                model.fft_size = 256;
                model.stems = {"a", "b"};
                stemora::Audio audio(static_cast<size_t>(frames) * 2);
                for (size_t i = 0; i < audio.size(); ++i)
                    audio[i] = static_cast<float>(std::sin(i * 0.13));
                std::atomic<bool> cancel{false};
                auto result = stemora::overlap_add(
                    audio, model, 1024, overlaps,
                    [](const stemora::Audio &block) {
                        auto half = block;
                        for (auto &value : half)
                            value *= 0.5f;
                        return stemora::Stems{block, half};
                    },
                    cancel, nullptr);
                bool valid = result.size() == 2 && result[0].size() == audio.size();
                for (size_t i = 0; i < audio.size(); ++i)
                    valid &=
                        std::abs(audio[i] - result[0][i]) < 1e-6f && std::abs(audio[i] * 0.5f - result[1][i]) < 1e-6f;
                check(valid, "sample-aligned overlap-add reconstruction");
            }
        stemora::ModelDescription model;
        model.fft_size = 256;
        model.stems = {"a"};
        std::atomic<bool> cancel{true};
        try {
            stemora::overlap_add(
                stemora::Audio(1000), model, 1024, 2, [](const auto &block) { return stemora::Stems{block}; }, cancel,
                nullptr);
            throw std::runtime_error("cancellation ignored");
        } catch (const std::runtime_error &error) {
            check(std::string(error.what()) == "cancelled", "cancel before inference");
        }
        std::cout << checks << " native checks passed.\n";
        return 0;
    } catch (const std::exception &error) {
        std::cerr << "FAIL " << error.what() << '\n';
        return 1;
    }
}
