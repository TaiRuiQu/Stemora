#include "infra.hpp"
#include <algorithm>
#include <sstream>
#include <stdexcept>
namespace stemora {
std::string quote(const std::string &text) {
    std::string result = "\"";
    static const char hex[] = "0123456789abcdef";
    for (unsigned char c : text) {
        switch (c) {
        case '"':
            result += "\\\"";
            break;
        case '\\':
            result += "\\\\";
            break;
        case '\n':
            result += "\\n";
            break;
        case '\r':
            result += "\\r";
            break;
        case '\t':
            result += "\\t";
            break;
        default:
            if (c < 32) {
                result += "\\u00";
                result += hex[c >> 4];
                result += hex[c & 15];
            } else
                result += static_cast<char>(c);
        }
    }
    return result + '"';
}
void validate_stem_id(const std::string &id) {
    if (id.empty() || id.size() > 64 || !std::all_of(id.begin(), id.end(), [](unsigned char c) {
            return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
        }))
        throw std::invalid_argument(
            "Stem IDs must contain only lowercase ASCII letters, digits, underscore or hyphen.");
}
std::vector<std::string> split_stems(const std::string &text) {
    std::vector<std::string> result;
    if (text.empty())
        return result;
    std::istringstream stream(text);
    std::string item;
    while (std::getline(stream, item, ',')) {
        validate_stem_id(item);
        if (std::find(result.begin(), result.end(), item) != result.end())
            throw std::invalid_argument("Duplicate stem ID.");
        result.push_back(item);
    }
    return result;
}

} // namespace stemora
