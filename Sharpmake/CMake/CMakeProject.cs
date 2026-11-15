// Copyright (c) Ubisoft. All Rights Reserved.
// Licensed under the Apache 2.0 License. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sharpmake
{
    /// <summary>
    /// A C/C++ project whose sources, include paths, defines and output type come from a target
    /// declared in a CMakeLists.txt. CMake itself is never run: the CMakeLists.txt is read by
    /// <see cref="CMakeListsReader"/>, and Sharpmake builds the library like any other project.
    /// </summary>
    /// <example>
    /// <code>
    /// [Generate]
    /// public class Fmt : CMakeProject
    /// {
    ///     public Fmt()
    ///     {
    ///         CMakeListsPath = @"[project.SharpmakeCsPath]\extern\fmt";
    ///         CMakeTargetName = "fmt";
    ///         AddTargets(new Target(Platform.win64, DevEnv.vs2022, Optimization.Debug | Optimization.Release));
    ///     }
    ///
    ///     [Configure]
    ///     public void ConfigureAll(Configuration conf, Target target)
    ///     {
    ///         conf.ProjectPath = @"[project.SharpmakeCsPath]\projects";
    ///     }
    /// }
    /// </code>
    /// Then, in a project that uses it: <c>conf.AddPublicDependency&lt;Fmt&gt;(target);</c>
    /// </example>
    /// <remarks>
    /// The CMakeLists.txt is read once per project, before the [Configure] methods run, so
    /// conditions in it cannot depend on the target being configured. Use
    /// <see cref="CMakeVariables"/> to set the variables they test.
    /// </remarks>
    public abstract class CMakeProject : Project
    {
        protected CMakeProject()
            : this(typeof(Target))
        {
        }

        protected CMakeProject(Type targetType, Type configurationType = null)
            : base(targetType, configurationType)
        {
        }

        /// <summary>
        /// Path to the CMakeLists.txt, or to the directory holding it. A relative path is relative
        /// to the folder of the .sharpmake.cs file; [project.SharpmakeCsPath] is also accepted.
        /// When <see cref="CMakeFetch"/> is set, a relative path is relative to the fetched sources
        /// instead, and it can be left null to use the CMakeLists.txt at their root.
        /// </summary>
        public string CMakeListsPath { get; set; }

        /// <summary>
        /// When set, the sources are downloaded (like CMake's FetchContent) before the
        /// CMakeLists.txt is read, and reused from the cache on later runs.
        /// See <see cref="CMakeFetchFrom"/>.
        /// </summary>
        public CMakeFetchRequest CMakeFetch { get; set; }

        /// <summary>
        /// Folder of the fetched sources, available from the [Configure] methods on when
        /// <see cref="CMakeFetch"/> is set.
        /// </summary>
        public string CMakeFetchedSourceDirectory { get; private set; }

        /// <summary>
        /// Downloads the library from a repository or archive URL, like FetchContent_Declare
        /// with GIT_REPOSITORY and GIT_TAG:
        /// <c>CMakeFetchFrom("https://github.com/fmtlib/fmt", "11.0.2");</c>
        /// Use a tag or a commit hash to pin the version. The sources go to a "_deps" folder next
        /// to the .sharpmake.cs file unless <see cref="CMakeFetchContent.CacheDirectory"/> or the
        /// returned request's CacheDirectory says otherwise.
        /// </summary>
        protected CMakeFetchRequest CMakeFetchFrom(string url, string reference = null)
        {
            CMakeFetch = new CMakeFetchRequest { Url = url, Ref = reference };
            return CMakeFetch;
        }

        /// <summary>
        /// Name of the target in the CMakeLists.txt. When null, the project <see cref="Project.Name"/> is used.
        /// </summary>
        public string CMakeTargetName { get; set; }

        /// <summary>
        /// Variables seen by the CMakeLists.txt, like -D arguments on the cmake command line.
        /// They are pre-filled with what CMake would set for MSVC on Windows; override them
        /// for other platforms.
        /// </summary>
        public Dictionary<string, string> CMakeVariables { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "WIN32", "1" },
            { "MSVC", "1" },
            { "CMAKE_SYSTEM_NAME", "Windows" },
            { "CMAKE_HOST_WIN32", "1" },
            { "CMAKE_C_COMPILER_ID", "MSVC" },
            { "CMAKE_CXX_COMPILER_ID", "MSVC" },
            { "CMAKE_SIZEOF_VOID_P", "8" },
            { "BUILD_TESTING", "OFF" },
        };

        /// <summary>
        /// When true (default), each configuration's <see cref="Project.Configuration.Output"/>
        /// is set from the CMake target type (static library, shared library, header only...).
        /// </summary>
        public bool CMakeSetsOutputType { get; set; } = true;

        /// <summary>
        /// When true (default), the warnings of the CMakeLists.txt reader are printed.
        /// </summary>
        public bool CMakeLogWarnings { get; set; } = true;

        /// <summary>
        /// The CMake target, available from the [Configure] methods on.
        /// </summary>
        public CMakeTarget CMakeTarget { get; private set; }

        /// <summary>
        /// Everything read from the CMakeLists.txt, available from the [Configure] methods on.
        /// </summary>
        public CMakeListsReadResult CMakeReadResult { get; private set; }

        /// <summary>
        /// Link libraries of the target that are other targets of the same CMake tree. Those are
        /// not added automatically: declare a <see cref="CMakeProject"/> for each and add it as
        /// a dependency.
        /// </summary>
        public IEnumerable<CMakeTarget> CMakeLinkedTargets =>
            CMakeTarget == null
                ? Enumerable.Empty<CMakeTarget>()
                : CMakeTarget.LinkLibraries.Concat(CMakeTarget.InterfaceLinkLibraries)
                    .Distinct()
                    .Select(CMakeReadResult.GetTarget)
                    .Where(t => t != null && t != CMakeTarget);

        public override void PreConfigure()
        {
            base.PreConfigure();

            string path;
            if (CMakeFetch != null)
            {
                var request = CMakeFetch;
                string cacheDirectory = request.CacheDirectory ?? CMakeFetchContent.CacheDirectory ?? Path.Combine(SharpmakeCsPath, "_deps");
                cacheDirectory = Util.PathGetAbsolute(SharpmakeCsPath, cacheDirectory.Replace("[project.SharpmakeCsPath]", SharpmakeCsPath));
                CMakeFetchedSourceDirectory = CMakeFetchContent.Fetch(new CMakeFetchRequest
                {
                    Url = request.Url,
                    Ref = request.Ref,
                    Sha256 = request.Sha256,
                    Method = request.Method,
                    Submodules = request.Submodules,
                    CacheDirectory = cacheDirectory,
                });

                path = string.IsNullOrEmpty(CMakeListsPath)
                    ? CMakeFetchedSourceDirectory
                    : Util.PathGetAbsolute(CMakeFetchedSourceDirectory, CMakeListsPath.Replace("[project.SharpmakeCsPath]", SharpmakeCsPath));
            }
            else
            {
                if (string.IsNullOrEmpty(CMakeListsPath))
                    throw new Error($"{GetType().Name}: CMakeListsPath (or CMakeFetch) must be set in the constructor of a CMakeProject.");

                path = CMakeListsPath.Replace("[project.SharpmakeCsPath]", SharpmakeCsPath);
                path = Util.PathGetAbsolute(SharpmakeCsPath, path);
            }

            string targetName = CMakeTargetName ?? Name;

            CMakeReadResult = CMakeListsReader.Read(path, CMakeVariables);

            if (CMakeLogWarnings && Builder.Instance != null)
            {
                foreach (string warning in CMakeReadResult.Warnings)
                    Builder.Instance.LogWarningLine("CMake: " + warning);
            }

            CMakeTarget = CMakeReadResult.GetTarget(targetName);
            if (CMakeTarget == null)
            {
                string known = string.Join(", ", CMakeReadResult.Targets.Keys.OrderBy(k => k, StringComparer.Ordinal));
                throw new Error($"{GetType().Name}: no target '{targetName}' in {path}. Targets found: {(known.Length > 0 ? known : "none")}.");
            }
            if (CMakeTarget.Type == CMakeTargetType.Imported)
                throw new Error($"{GetType().Name}: '{targetName}' is an IMPORTED target in {path}, there is nothing to build.");

            SourceRootPath = Util.PathMakeStandard(CMakeTarget.SourceDirectory);

            // The sources come from the CMake target, don't scan the directory.
            SourceFilesExtensions.Clear();
            NatvisFilesExtensions.Clear();
            PRIFilesExtensions.Clear();

            foreach (string source in CMakeTarget.Sources)
            {
                if (File.Exists(source))
                    SourceFiles.Add(Util.PathMakeStandard(source));
                else if (CMakeLogWarnings && Builder.Instance != null)
                    Builder.Instance.LogWarningLine($"CMake: {GetType().Name}: source file not found, skipped: {source}");
            }

            if (CMakeLogWarnings && Builder.Instance != null)
            {
                if (CMakeTarget.InterfaceCompileOptions.Count > 0)
                    Builder.Instance.LogWarningLine($"CMake: {GetType().Name}: target '{CMakeTarget.Name}' asks its users to compile with: {string.Join(" ", CMakeTarget.InterfaceCompileOptions)}. Sharpmake doesn't forward compiler options; add them to the projects that depend on it if they need them.");
                foreach (var linked in CMakeLinkedTargets)
                    Builder.Instance.LogWarningLine($"CMake: {GetType().Name}: target '{CMakeTarget.Name}' links to CMake target '{linked.Name}', which is not added automatically. Add a CMakeProject for it and a dependency to it.");
            }
        }

        public override void AfterConfigure()
        {
            foreach (Configuration conf in Configurations)
                ApplyCMakeTarget(conf);

            base.AfterConfigure();
        }

        /// <summary>
        /// Copies the CMake target settings into a configuration. Called for every configuration
        /// after the [Configure] methods.
        /// </summary>
        protected virtual void ApplyCMakeTarget(Configuration conf)
        {
            CMakeTarget target = CMakeTarget;

            if (CMakeSetsOutputType)
                conf.Output = GetOutputType(target.Type);

            // Include paths: Sharpmake forwards IncludePaths to dependents, and keeps
            // IncludePrivatePaths for the project itself.
            foreach (string directory in target.InterfaceIncludeDirectories)
                conf.IncludePaths.Add(Util.PathMakeStandard(directory));
            foreach (string directory in target.IncludeDirectories)
            {
                if (!target.InterfaceIncludeDirectories.Contains(directory))
                    conf.IncludePrivatePaths.Add(Util.PathMakeStandard(directory));
            }

            // ExportDefines are only seen by dependents, Defines only by the project itself.
            conf.Defines.AddRange(target.CompileDefinitions);
            conf.ExportDefines.AddRange(target.InterfaceCompileDefinitions);

            // Compiler options (target_compile_options). Sharpmake can't forward compiler options
            // to dependents: the INTERFACE ones are listed in a warning from PreConfigure.
            foreach (string option in target.CompileOptions)
                conf.AdditionalCompilerOptions.Add(option);

            // CMake defines <target>_EXPORTS when building a shared library.
            if (target.Type == CMakeTargetType.SharedLibrary || target.Type == CMakeTargetType.ModuleLibrary)
                conf.Defines.Add(MakeIdentifier(target.Name) + "_EXPORTS");

            // Libraries that are not targets of the CMake tree (ws2_32, pthread, a .lib path...).
            foreach (string library in target.LinkLibraries.Concat(target.InterfaceLinkLibraries).Distinct())
            {
                if (CMakeReadResult.GetTarget(library) != null)
                    continue;
                if (library.Contains("::"))
                    continue; // imported target from find_package, nothing we can resolve
                if (library.StartsWith("-", StringComparison.Ordinal))
                    continue; // linker flag
                conf.LibraryFiles.Add(library);
            }
        }

        private static Configuration.OutputType GetOutputType(CMakeTargetType type)
        {
            switch (type)
            {
                case CMakeTargetType.SharedLibrary:
                case CMakeTargetType.ModuleLibrary:
                    return Configuration.OutputType.Dll;
                case CMakeTargetType.InterfaceLibrary:
                    return Configuration.OutputType.Utility;
                case CMakeTargetType.Executable:
                    return Configuration.OutputType.Exe;
                default:
                    return Configuration.OutputType.Lib;
            }
        }

        private static string MakeIdentifier(string name)
        {
            var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
            return new string(chars);
        }
    }
}
