#include "format.h"

#include <algorithm>
#include <cctype>

namespace greeter
{
    std::string Decorate(const std::string& text)
    {
        std::string result = text;
#if defined(GREETER_SHOUT)
        std::transform(result.begin(), result.end(), result.begin(), [](unsigned char c) { return static_cast<char>(std::toupper(c)); });
#endif
        return result + "!";
    }
}
