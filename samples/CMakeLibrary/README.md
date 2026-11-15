# CMakeLibrary sample

Shows how to use a library that is described by a `CMakeLists.txt` from Sharpmake, without running CMake.

```csharp
[Sharpmake.Generate]
public class GreeterProject : CMakeProject
{
    public GreeterProject()
    {
        CMakeListsPath = @"[project.SharpmakeCsPath]/extern/greeter"; // folder or CMakeLists.txt
        CMakeTargetName = "greeter";                                  // target in the CMakeLists.txt
        CMakeVariables["GREETER_SHOUT"] = "ON";                       // like -DGREETER_SHOUT=ON
        AddTargets(...);
    }
}
```

Then depend on it like on any other project: `conf.AddPrivateDependency<GreeterProject>(target);`

## What is taken from the CMakeLists.txt

`CMakeProject` reads the CMake target with `CMakeListsReader` before the `[Configure]` methods run, and then fills each configuration:

| CMake | Sharpmake |
| --- | --- |
| sources of `add_library` / `target_sources` | `SourceFiles` (the folder is not scanned) |
| `STATIC` / `OBJECT`, `SHARED` / `MODULE`, `INTERFACE` | `Output` = `Lib`, `Dll`, `Utility` |
| `PUBLIC` / `INTERFACE` include directories | `IncludePaths` (forwarded to dependents) |
| `PRIVATE` include directories | `IncludePrivatePaths` |
| `PRIVATE` / `PUBLIC` compile definitions | `Defines` |
| `PUBLIC` / `INTERFACE` compile definitions | `ExportDefines` |
| `PRIVATE` / `PUBLIC` compile options | `AdditionalCompilerOptions` |
| link libraries that are not CMake targets (`ws2_32`...) | `LibraryFiles` |

The reader understands variables, lists, `option`, `if`/`elseif`/`else`, `foreach`, `function`/`macro`, `include`, `add_subdirectory`, `file(GLOB)`, `file(READ)`, common `string()` operations, `include_directories`, `add_definitions`, `cmake_parse_arguments` and the `target_*` commands. Generator expressions that don't depend on the configuration are evaluated (`BUILD_INTERFACE`, `INSTALL_INTERFACE`, `$<0:...>`/`$<1:...>`, `BOOL`, `NOT`, `AND`, `OR`, `IF`, `STREQUAL`, `COMPILE_LANGUAGE`, `CXX_COMPILER_ID`, `C_COMPILER_ID`, `COMPILE_LANG_AND_ID`, `PLATFORM_ID`); the others, like `$<CONFIG:...>`, are ignored with a warning.

To download the library instead of keeping a copy in your repository, see the [CMakeFetch](../CMakeFetch) sample.

## Current limitations

- The `CMakeLists.txt` is read once per project, not once per target, so it sees the same variables for every configuration. The defaults are the ones of MSVC on Windows (`WIN32`, `MSVC`...), and `CMakeVariables` overrides them.
- `target_link_libraries` to another target of the same CMake tree is not turned into a dependency automatically: declare a `CMakeProject` for it and add the dependency. Sharpmake prints a warning listing those targets, and `CMakeLinkedTargets` gives them.
- `INTERFACE` / `PUBLIC` compile options are not forwarded to dependents, since Sharpmake has no way to export compiler options. A warning lists them.
- `target_compile_features`, `set_target_properties`, `find_package`, `configure_file` and custom commands are ignored.
