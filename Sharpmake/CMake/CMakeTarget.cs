// Copyright (c) Ubisoft. All Rights Reserved.
// Licensed under the Apache 2.0 License. See LICENSE.md in the project root for license information.

using System.Collections.Generic;

namespace Sharpmake
{
    /// <summary>
    /// Kind of target declared in a CMakeLists.txt with add_library / add_executable.
    /// </summary>
    public enum CMakeTargetType
    {
        StaticLibrary,
        SharedLibrary,
        ModuleLibrary,
        ObjectLibrary,
        InterfaceLibrary,
        Executable,
        Imported,
    }

    /// <summary>
    /// A target read from a CMakeLists.txt, with every path made absolute.
    /// </summary>
    /// <remarks>
    /// CMake keeps two lists for most properties: the one used to build the target itself and
    /// the "interface" one that is forwarded to whoever links to it. A PUBLIC item lands in both,
    /// a PRIVATE item only in the build list, and an INTERFACE item only in the interface list.
    /// </remarks>
    public class CMakeTarget
    {
        public string Name { get; internal set; }
        public CMakeTargetType Type { get; internal set; }

        /// <summary>
        /// The directory of the CMakeLists.txt that declared the target.
        /// </summary>
        public string SourceDirectory { get; internal set; }

        public List<string> Sources { get; } = new List<string>();

        public List<string> IncludeDirectories { get; } = new List<string>();
        public List<string> InterfaceIncludeDirectories { get; } = new List<string>();

        public List<string> CompileDefinitions { get; } = new List<string>();
        public List<string> InterfaceCompileDefinitions { get; } = new List<string>();

        public List<string> CompileOptions { get; } = new List<string>();
        public List<string> InterfaceCompileOptions { get; } = new List<string>();

        public List<string> LinkLibraries { get; } = new List<string>();
        public List<string> InterfaceLinkLibraries { get; } = new List<string>();

        public bool IsLibrary => Type != CMakeTargetType.Executable;

        public override string ToString() => $"{Name} ({Type})";
    }
}
