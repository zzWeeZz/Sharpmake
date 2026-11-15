#include <greeter/greeter.h>

#include <iostream>

#if defined(GREETER_BUILDING)
#error "GREETER_BUILDING is private to the library and should not reach its users"
#endif

int main()
{
    std::cout << greeter::Greet("Sharpmake") << " (greeter " << GREETER_VERSION << ")" << std::endl;
    return 0;
}
