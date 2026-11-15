#include <fmt/format.h>

int main()
{
    fmt::print("Hello from {} {}.{}.{}, fetched by Sharpmake\n", "fmt", FMT_VERSION / 10000, FMT_VERSION / 100 % 100, FMT_VERSION % 100);
    return 0;
}
