#include <greeter/greeter.h>

#include "format.h"

#if !defined(GREETER_BUILDING)
#error "GREETER_BUILDING should be defined when building the library"
#endif

namespace greeter
{
    std::string Greet(const std::string& name)
    {
        return Decorate("Hello, " + name);
    }
}
