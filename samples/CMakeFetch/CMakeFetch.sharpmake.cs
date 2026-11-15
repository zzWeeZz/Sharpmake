// Copyright (c) Ubisoft. All Rights Reserved.
// Licensed under the Apache 2.0 License. See LICENSE.md in the project root for license information.

using Sharpmake;

namespace CMakeFetch
{
    /// <summary>
    /// fmt, downloaded from GitHub at a pinned tag (like FetchContent_Declare with GIT_REPOSITORY
    /// and GIT_TAG), then read from its CMakeLists.txt. The download goes to "_deps" next to this
    /// file and is reused on the next runs.
    /// </summary>
    [Sharpmake.Generate]
    public class FmtProject : CMakeProject
    {
        public FmtProject()
        {
            Name = "fmt";
            CMakeFetchFrom("https://github.com/fmtlib/fmt", "11.0.2");
            CMakeTargetName = "fmt";

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
    public class HelloFetchProject : Project
    {
        public HelloFetchProject()
        {
            Name = "HelloFetch";
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

            conf.AddPrivateDependency<FmtProject>(target);

            // fmt asks its users to build with /utf-8 (target_compile_options INTERFACE).
            // Sharpmake doesn't forward compiler options, so it is added here.
            conf.AdditionalCompilerOptions.Add("/utf-8");
        }
    }

    [Sharpmake.Generate]
    public class CMakeFetchSolution : Sharpmake.Solution
    {
        public CMakeFetchSolution()
        {
            Name = "CMakeFetch";
            AddTargets(new Target(Platform.win64, DevEnv.vs2022, Optimization.Debug | Optimization.Release));

            IsFileNameToLower = false;
        }

        [Configure]
        public void ConfigureAll(Configuration conf, Target target)
        {
            conf.SolutionFileName = "[solution.Name]_[target.DevEnv]_[target.Platform]";
            conf.SolutionPath = @"[solution.SharpmakeCsPath]\projects";
            conf.AddProject<HelloFetchProject>(target);
        }
    }

    public static class Main
    {
        [Sharpmake.Main]
        public static void SharpmakeMain(Sharpmake.Arguments arguments)
        {
            KitsRootPaths.SetUseKitsRootForDevEnv(DevEnv.vs2022, KitsRootEnum.KitsRoot10, Options.Vc.General.WindowsTargetPlatformVersion.v10_0_22621_0);

            arguments.Generate<CMakeFetchSolution>();
        }
    }
}
