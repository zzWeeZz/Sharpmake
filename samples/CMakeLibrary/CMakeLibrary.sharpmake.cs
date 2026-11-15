// Copyright (c) Ubisoft. All Rights Reserved.
// Licensed under the Apache 2.0 License. See LICENSE.md in the project root for license information.

using Sharpmake;

namespace CMakeLibrary
{
    /// <summary>
    /// A library described by a CMakeLists.txt. Sharpmake reads the CMakeLists.txt (CMake is
    /// not run) and takes the sources, include paths, defines and output type of the "greeter"
    /// target from it.
    /// </summary>
    [Sharpmake.Generate]
    public class GreeterProject : CMakeProject
    {
        public GreeterProject()
        {
            Name = "Greeter";
            CMakeListsPath = @"[project.SharpmakeCsPath]/extern/greeter";
            CMakeTargetName = "greeter";

            // Same as passing -DGREETER_SHOUT=ON to cmake.
            CMakeVariables["GREETER_SHOUT"] = "ON";

            AddTargets(new Target(Platform.win64, DevEnv.vs2022, Optimization.Debug | Optimization.Release));

            IsFileNameToLower = false;
        }

        [Configure]
        public void ConfigureAll(Configuration conf, Target target)
        {
            conf.ProjectFileName = "[project.Name]_[target.DevEnv]_[target.Platform]";
            conf.ProjectPath = @"[project.SharpmakeCsPath]\projects";
            conf.IntermediatePath = @"[conf.ProjectPath]\obj\[project.Name]\[target.Platform]_[target.Optimization]_[target.DevEnv]";
        }
    }

    [Sharpmake.Generate]
    public class HelloCMakeProject : Project
    {
        public HelloCMakeProject()
        {
            Name = "HelloCMake";
            SourceRootPath = @"[project.SharpmakeCsPath]\codebase";

            AddTargets(new Target(Platform.win64, DevEnv.vs2022, Optimization.Debug | Optimization.Release));

            IsFileNameToLower = false;
        }

        [Configure]
        public void ConfigureAll(Configuration conf, Target target)
        {
            conf.ProjectFileName = "[project.Name]_[target.DevEnv]_[target.Platform]";
            conf.ProjectPath = @"[project.SharpmakeCsPath]\projects";
            conf.IntermediatePath = @"[conf.ProjectPath]\obj\[project.Name]\[target.Platform]_[target.Optimization]_[target.DevEnv]";

            // The CMake library is used like any other Sharpmake project.
            conf.AddPrivateDependency<GreeterProject>(target);
        }
    }

    [Sharpmake.Generate]
    public class CMakeLibrarySolution : Sharpmake.Solution
    {
        public CMakeLibrarySolution()
        {
            Name = "CMakeLibrary";
            AddTargets(new Target(Platform.win64, DevEnv.vs2022, Optimization.Debug | Optimization.Release));

            IsFileNameToLower = false;
        }

        [Configure]
        public void ConfigureAll(Configuration conf, Target target)
        {
            conf.SolutionFileName = "[solution.Name]_[target.DevEnv]_[target.Platform]";
            conf.SolutionPath = @"[solution.SharpmakeCsPath]\projects";
            conf.AddProject<HelloCMakeProject>(target);
        }
    }

    public static class Main
    {
        [Sharpmake.Main]
        public static void SharpmakeMain(Sharpmake.Arguments arguments)
        {
            KitsRootPaths.SetUseKitsRootForDevEnv(DevEnv.vs2022, KitsRootEnum.KitsRoot10, Options.Vc.General.WindowsTargetPlatformVersion.v10_0_22621_0);

            arguments.Generate<CMakeLibrarySolution>();
        }
    }
}
