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
| link libraries that are not CMake targets (`ws2_32`...) | `LibraryFiles` |

The reader understands variables, lists, `option`, `if`/`elseif`/`else`, `foreach`, `function`/`macro`, `include`, `add_subdirectory`, `file(GLOB)`, `file(READ)`, common `string()` operations, `include_directories`, `add_definitions` and the `target_*` commands. Generator expressions other than `$<BUILD_INTERFACE:...>` and `$<INSTALL_INTERFACE:...>` are ignored with a warning.

## Current limitations

- The `CMakeLists.txt` is read once per project, not once per target, so it sees the same variables for every configuration. The defaults are the ones of MSVC on Windows (`WIN32`, `MSVC`...), and `CMakeVariables` overrides them.
- `target_link_libraries` to another target of the same CMake tree is not turned into a dependency automatically: declare a `CMakeProject` for it and add the dependency. Sharpmake prints a warning listing those targets, and `CMakeLinkedTargets` gives them.
- `target_compile_options`, `target_compile_features`, `set_target_properties`, `find_package`, `configure_file` and custom commands are ignored.
