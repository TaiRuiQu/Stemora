#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <stdexcept>
#include <vector>

namespace stemora {
struct WaveAudio {
    int sample_rate;
    std::vector<float> pcm;
};
inline uint16_t u16(const unsigned char *b) { return static_cast<uint16_t>(b[0] | b[1] << 8); }
inline uint32_t u32(const unsigned char *b) {
    return static_cast<uint32_t>(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);
}
inline WaveAudio load_wave(const std::filesystem::path &path) {
    std::ifstream file(path, std::ios::binary);
    unsigned char header[12];
    if (!file.read(reinterpret_cast<char *>(header), 12) || std::memcmp(header, "RIFF", 4) ||
        std::memcmp(header + 8, "WAVE", 4))
        throw std::runtime_error("Input must be a RIFF WAV file; use the C# CLI for compressed audio decoding.");
    uint16_t format = 0, bits = 0, channels = 0, block = 0;
    uint32_t rate = 0, data_size = 0;
    std::streampos data_position{};
    while (file.read(reinterpret_cast<char *>(header), 8)) {
        const auto size = u32(header + 4);
        const auto next = file.tellg() + static_cast<std::streamoff>(size + (size & 1));
        if (!std::memcmp(header, "fmt ", 4)) {
            if (size < 16 || size > 4096)
                throw std::runtime_error("Invalid WAV format chunk.");
            std::vector<unsigned char> bytes(size);
            if (!file.read(reinterpret_cast<char *>(bytes.data()), size))
                throw std::runtime_error("Truncated WAV format.");
            format = u16(bytes.data());
            channels = u16(bytes.data() + 2);
            rate = u32(bytes.data() + 4);
            block = u16(bytes.data() + 12);
            bits = u16(bytes.data() + 14);
            if (format == 0xfffe && size >= 40)
                format = u16(bytes.data() + 24);
        } else if (!std::memcmp(header, "data", 4)) {
            data_position = file.tellg();
            data_size = size;
        }
        file.seekg(next);
    }
    if (channels != 2 || rate != 44100 || !data_size || block != channels * bits / 8 || data_size % block)
        throw std::runtime_error("Expected non-empty 44.1 kHz stereo WAV.");
    if (!((format == 3 && bits == 32) || (format == 1 && (bits == 16 || bits == 24 || bits == 32))))
        throw std::runtime_error("Unsupported WAV encoding.");
    file.clear();
    file.seekg(data_position);
    WaveAudio result{static_cast<int>(rate), std::vector<float>(data_size / (bits / 8))};
    std::vector<unsigned char> bytes(data_size);
    if (!file.read(reinterpret_cast<char *>(bytes.data()), data_size))
        throw std::runtime_error("Truncated WAV samples.");
    for (size_t i = 0; i < result.pcm.size(); ++i) {
        const auto *value = bytes.data() + i * (bits / 8);
        float sample;
        if (format == 3)
            std::memcpy(&sample, value, 4);
        else if (bits == 16)
            sample = static_cast<int16_t>(u16(value)) / 32768.0f;
        else if (bits == 24) {
            int32_t v = value[0] | value[1] << 8 | value[2] << 16;
            if (v & 0x800000)
                v |= ~0xffffff;
            sample = v / 8388608.0f;
        } else
            sample = static_cast<int32_t>(u32(value)) / 2147483648.0f;
        if (!std::isfinite(sample))
            throw std::runtime_error("Non-finite input PCM.");
        result.pcm[i] = sample;
    }
    return result;
}
inline void save_wave(const std::filesystem::path &path, const std::vector<float> &pcm, int sample_rate = 44100) {
    if (std::filesystem::exists(path))
        throw std::runtime_error("Refusing to overwrite an existing output: " + path.u8string());
    if (pcm.empty() || pcm.size() % 2 || pcm.size() > (UINT32_MAX - 44) / 4)
        throw std::runtime_error("Invalid or oversized WAV output.");
    std::ofstream file(path, std::ios::binary);
    const auto size = static_cast<uint32_t>(pcm.size() * 4);
    const uint32_t riff_size = size + 36, fmt_size = 16, rate = static_cast<uint32_t>(sample_rate),
                   byte_rate = rate * 8;
    const uint16_t format = 3, channels = 2, align = 8, bits = 32;
    file.write("RIFF", 4);
    file.write(reinterpret_cast<const char *>(&riff_size), 4);
    file.write("WAVEfmt ", 8);
    file.write(reinterpret_cast<const char *>(&fmt_size), 4);
    file.write(reinterpret_cast<const char *>(&format), 2);
    file.write(reinterpret_cast<const char *>(&channels), 2);
    file.write(reinterpret_cast<const char *>(&rate), 4);
    file.write(reinterpret_cast<const char *>(&byte_rate), 4);
    file.write(reinterpret_cast<const char *>(&align), 2);
    file.write(reinterpret_cast<const char *>(&bits), 2);
    file.write("data", 4);
    file.write(reinterpret_cast<const char *>(&size), 4);
    file.write(reinterpret_cast<const char *>(pcm.data()), size);
    if (!file)
        throw std::runtime_error("Cannot write WAV output.");
}
} // namespace stemora
