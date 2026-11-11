#include "infra.hpp"
#include "stemora_infra.h"
#include "wave_io.hpp"
#include <charconv>
#include <csignal>
#include <cstdio>
#include <filesystem>
#include <iostream>
#include <map>
#include <memory>
#include <sstream>
#ifdef _WIN32
#include <windows.h>
#endif

namespace {
stemora_session *active = nullptr;
void interrupt(int) {
    if (active)
        stemora_cancel(active);
}
void progress(float value, const char *stage, void *) {
    std::printf("{\"type\":\"progress\",\"stage\":\"%s\",\"fraction\":%.6f}\n", stage, static_cast<double>(value));
    std::fflush(stdout);
}
int run(const std::vector<std::string> &args) {
    try {
        if (args.size() < 2 || args[1] == "--help" || args[1] == "help") {
            std::cout
                << "Stemora Infra 0.2.0\n"
                   "  stemora-infra probe --model model.gguf [--profile bs_roformer_sw] [--stems bass,drums,...]\n"
                   "  stemora-infra separate --model model.gguf --input input.wav --output-dir ./stems\n"
                   "    [--device auto|cpu|cuda] [--chunk-size 588800] [--overlap 2] [--threads 8]\n";
            return 0;
        }
        if (args[1] == "--version") {
            std::cout << stemora_version() << '\n';
            return 0;
        }
        std::map<std::string, std::string> values;
        for (size_t i = 2; i < args.size(); i += 2) {
            if (i + 1 >= args.size() || args[i].rfind("--", 0) != 0 || !values.emplace(args[i], args[i + 1]).second)
                throw std::invalid_argument("Invalid or duplicate command option.");
        }
        const std::vector<std::string> allowed{"--model",  "--input",      "--output-dir", "--profile", "--stems",
                                               "--device", "--chunk-size", "--overlap",    "--threads"};
        for (const auto &entry : values)
            if (std::find(allowed.begin(), allowed.end(), entry.first) == allowed.end())
                throw std::invalid_argument("Unknown option: " + entry.first);
        auto required = [&](const std::string &key) {
            auto found = values.find(key);
            if (found == values.end())
                throw std::invalid_argument("Missing " + key);
            return found->second;
        };
        auto integer = [&](const std::string &key) {
            if (!values.count(key))
                return 0;
            int number = 0;
            const auto &value = values.at(key);
            const auto parsed = std::from_chars(value.data(), value.data() + value.size(), number);
            if (parsed.ec != std::errc{} || parsed.ptr != value.data() + value.size() || number < 1)
                throw std::invalid_argument("Invalid integer: " + key);
            return number;
        };
        const auto model = required("--model");
        const auto profile = values.count("--profile") ? values.at("--profile") : "";
        const auto stems = values.count("--stems") ? values.at("--stems") : "";
        const auto device = values.count("--device") ? values.at("--device") : "auto";
        if (device != "auto" && device != "cpu" && device != "cuda")
            throw std::invalid_argument("Device must be auto, cpu or cuda.");
        stemora_options options{sizeof(stemora_options),
                                1,
                                device == "cpu"    ? 1
                                : device == "cuda" ? 2
                                                   : 0,
                                integer("--threads"),
                                integer("--chunk-size"),
                                integer("--overlap"),
                                profile.c_str(),
                                stems.c_str()};
        if (args[1] == "probe") {
            const auto length = stemora_probe(model.c_str(), &options, nullptr, 0);
            if (!length)
                throw std::runtime_error(stemora_last_error());
            std::vector<char> json(length);
            if (!stemora_probe(model.c_str(), &options, json.data(), json.size()))
                throw std::runtime_error(stemora_last_error());
            std::cout << "{\"type\":\"model\",\"data\":" << json.data() << "}\n";
            return 0;
        }
        if (args[1] != "separate")
            throw std::invalid_argument("Unknown native command.");
        const auto input = required("--input");
        const auto directory = std::filesystem::u8path(required("--output-dir"));
        const auto audio = stemora::load_wave(std::filesystem::u8path(input));
        stemora_session *raw = nullptr;
        if (stemora_create(model.c_str(), &options, &raw) != STEMORA_OK)
            throw std::runtime_error(stemora_last_error());
        std::unique_ptr<stemora_session, decltype(&stemora_destroy)> session(raw, stemora_destroy);
        active = raw;
        std::signal(SIGINT, interrupt);
        std::signal(SIGTERM, interrupt);
        std::cout << "{\"type\":\"model\",\"data\":" << stemora_model_json(raw) << "}\n" << std::flush;
        const auto status = stemora_process(raw, audio.pcm.data(), audio.pcm.size() / 2, progress, nullptr);
        if (status == STEMORA_CANCELLED) {
            active = nullptr;
            return 130;
        }
        if (status != STEMORA_OK)
            throw std::runtime_error(stemora_last_error());
        std::filesystem::create_directories(directory);
        std::ostringstream manifest;
        manifest << "{\"model\":" << stemora_model_json(raw) << ",\"stems\":[";
        for (uint32_t i = 0; i < stemora_stem_count(raw); ++i) {
            const auto id = std::string(stemora_stem_name(raw, i));
            const auto filename = "raw_" + id + ".wav";
            std::vector<float> pcm(static_cast<size_t>(stemora_frame_count(raw)) * 2);
            if (stemora_copy_stem(raw, i, pcm.data(), pcm.size()) != STEMORA_OK)
                throw std::runtime_error(stemora_last_error());
            stemora::save_wave(directory / filename, pcm);
            if (i)
                manifest << ',';
            manifest << "{\"id\":" << stemora::quote(id) << ",\"file\":" << stemora::quote(filename) << '}';
        }
        manifest << "]}";
        std::ofstream file(directory / "native-result.json", std::ios::binary);
        file << manifest.str();
        file.close();
        if (!file)
            throw std::runtime_error("Cannot save native result manifest.");
        active = nullptr;
        std::cout << "{\"type\":\"completed\",\"data\":" << manifest.str() << "}\n";
        return 0;
    } catch (const std::exception &error) {
        active = nullptr;
        std::cout << "{\"type\":\"error\",\"message\":" << stemora::quote(error.what()) << "}\n";
        return 1;
    }
}
} // namespace
#ifdef _WIN32
int wmain(int argc, wchar_t **argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::vector<std::string> args;
    for (int i = 0; i < argc; ++i) {
        const auto length =
            WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, argv[i], -1, nullptr, 0, nullptr, nullptr);
        if (!length)
            return 2;
        std::string value(static_cast<size_t>(length), '\0');
        WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, argv[i], -1, value.data(), length, nullptr, nullptr);
        value.pop_back();
        args.push_back(std::move(value));
    }
    return run(args);
}
#else
int main(int argc, char **argv) { return run(std::vector<std::string>(argv, argv + argc)); }
#endif
