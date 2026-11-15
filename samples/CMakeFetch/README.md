# CMakeFetch sample

Downloads a library from GitHub and uses it from its `CMakeLists.txt`, like CMake's `FetchContent`. CMake is not run.

```csharp
[Sharpmake.Generate]
public class FmtProject : CMakeProject
{
    public FmtProject()
    {
        CMakeFetchFrom("https://github.com/fmtlib/fmt", "11.0.2"); // URL + tag, branch or commit
        CMakeTargetName = "fmt";
        AddTargets(...);
    }
}
```

Then depend on it like on any other project: `conf.AddPrivateDependency<FmtProject>(target);`

This is the Sharpmake equivalent of:

```cmake
FetchContent_Declare(fmt GIT_REPOSITORY https://github.com/fmtlib/fmt GIT_TAG 11.0.2)
FetchContent_MakeAvailable(fmt)
```

Everything else is the same as a `CMakeProject` with a local folder (see the [CMakeLibrary](../CMakeLibrary) sample). `CMakeListsPath`, when set, is relative to the root of the downloaded sources, for a library whose `CMakeLists.txt` is in a sub folder.

## Where the sources go

The first run downloads the sources to a `_deps` folder next to the `.sharpmake.cs` file, in one sub folder per URL and ref (`_deps/fmt-11.0.2-1a2b3c4d`). Later runs use that folder and don't touch the network. Add `_deps/` to your `.gitignore`.

To put the downloads somewhere else, for instance to share them between several checkouts:

- for every project: `CMakeFetchContent.CacheDirectory = @"D:\sharpmake-deps";` in your `[Sharpmake.Main]`, or the `SHARPMAKE_FETCH_DIR` environment variable;
- for one project: `CMakeFetchFrom(...).CacheDirectory = @"[project.SharpmakeCsPath]\..\extern";`.

A folder is only used once it is complete: the download goes to a temporary folder first, and a `.sharpmake-fetch` file records what is in it. Delete a folder to download it again.

## Pinning the version

| ref | what happens |
| --- | --- |
| a tag (`11.0.2`) | downloaded once, then reused. Tags rarely move, so this pins the version. |
| a commit hash (40 hex digits) | downloaded once, and checked: the strongest pin. Never downloaded again, even with `/fetchupdate`. |
| a branch (`master`) | downloaded once, then reused **until you run with `/fetchupdate`**. |

For an archive URL, `Sha256` checks the download, like `URL_HASH SHA256=...`:

```csharp
CMakeFetchFrom("https://example.com/lib-1.2.zip").Sha256 = "9f86d0...";
```

## Command line

- `/fetchupdate`: download again everything that is not pinned to a commit hash or a SHA-256. If the download fails (no network), the cached copy is kept, with a warning.
- `/fetchoffline`: never download. Generation fails if something is not in the cache yet, which is useful on build machines that must not reach the internet.

## How it downloads

| URL | method |
| --- | --- |
| `https://github.com/<owner>/<repo>` | the archive of the ref that GitHub serves (no git needed, no history). If that download fails, for instance because a firewall blocks it, `git clone` is tried. |
| an archive (`.zip`, `.tar.gz`, `.tgz`, `.tar.xz`) | downloaded and extracted. `.tar.*` archives need `tar` in the `PATH` (it ships with Windows 10 and later). |
| any other URL (GitLab, a `.git` URL, a local path) | `git clone --depth 1`. |

Set `Method = CMakeFetchMethod.Git` to always use git: it is needed for private repositories, since git uses your credentials. Set `Submodules = true` for a repository that needs its submodules (this also uses git).

```csharp
var fetch = CMakeFetchFrom("https://github.com/my-company/private-lib", "v2.0");
fetch.Method = CMakeFetchMethod.Git;
fetch.Submodules = true;
```

`CMakeFetchContent.Fetch(new CMakeFetchRequest { ... })` returns the folder of the sources. It can be used from a plain `Project` too, for a library that has no `CMakeLists.txt`.

## About this sample

fmt's `CMakeLists.txt` asks the users of the library to compile with `/utf-8`. Sharpmake doesn't forward compiler options to dependents, so `HelloFetch` adds it itself; Sharpmake prints a warning that names those options.
