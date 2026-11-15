// Copyright (c) Ubisoft. All Rights Reserved.
// Licensed under the Apache 2.0 License. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Sharpmake.UnitTests
{
    [TestFixture]
    public class CMakeListsReaderTest
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "sharpmake_cmake_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        private void WriteFile(string relativePath, string content = "")
        {
            string path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }

        private string P(string relativePath) => Path.GetFullPath(Path.Combine(_root, relativePath)).Replace('\\', '/').TrimEnd('/');

        private CMakeListsReadResult Read(IDictionary<string, string> variables = null) => CMakeListsReader.Read(_root, variables);

        [Test]
        public void StaticLibraryWithVisibility()
        {
            WriteFile("CMakeLists.txt", @"
cmake_minimum_required(VERSION 3.14)
project(MyLib VERSION 1.2.3 LANGUAGES CXX)

add_library(mylib STATIC src/a.cpp src/b.cpp)  # trailing comment
target_include_directories(mylib
    PUBLIC include
    PRIVATE src)
target_compile_definitions(mylib PUBLIC MYLIB_PUBLIC=1 PRIVATE -DMYLIB_INTERNAL INTERFACE MYLIB_USER)
target_link_libraries(mylib PRIVATE ws2_32)
");
            var result = Read();
            var lib = result.GetTarget("mylib");

            Assert.IsNotNull(lib);
            Assert.AreEqual(CMakeTargetType.StaticLibrary, lib.Type);
            CollectionAssert.AreEqual(new[] { P("src/a.cpp"), P("src/b.cpp") }, lib.Sources);
            CollectionAssert.AreEqual(new[] { P("include"), P("src") }, lib.IncludeDirectories);
            CollectionAssert.AreEqual(new[] { P("include") }, lib.InterfaceIncludeDirectories);
            CollectionAssert.AreEqual(new[] { "MYLIB_PUBLIC=1", "MYLIB_INTERNAL" }, lib.CompileDefinitions);
            CollectionAssert.AreEqual(new[] { "MYLIB_PUBLIC=1", "MYLIB_USER" }, lib.InterfaceCompileDefinitions);
            CollectionAssert.AreEqual(new[] { "ws2_32" }, lib.LinkLibraries);
            CollectionAssert.IsEmpty(lib.InterfaceLinkLibraries);
            CollectionAssert.Contains(result.IgnoredCommands, "cmake_minimum_required");
            CollectionAssert.IsEmpty(result.Warnings);
        }

        [Test]
        public void VariablesListsAndConditions()
        {
            WriteFile("CMakeLists.txt", @"
project(Vars)
set(SOURCES a.cpp b.cpp)
list(APPEND SOURCES ""c.cpp"")
option(WITH_EXTRA ""Extra sources"" OFF)
if(WITH_EXTRA)
  list(APPEND SOURCES extra.cpp)
elseif(WIN32 AND NOT UNIX)
  list(APPEND SOURCES win.cpp)
else()
  list(APPEND SOURCES other.cpp)
endif()
if(""${PROJECT_NAME}"" STREQUAL ""Vars"")
  set(NAME_OK yes)
endif()
foreach(f IN ITEMS d e)
  list(APPEND SOURCES ${f}.cpp)
endforeach()
add_library(${PROJECT_NAME} ${SOURCES})
if(NAME_OK)
  target_compile_definitions(Vars PRIVATE NAME_OK)
endif()
");
            var noExtra = Read(new Dictionary<string, string> { { "WIN32", "1" } }).GetTarget("Vars");
            CollectionAssert.AreEqual(new[] { "a.cpp", "b.cpp", "c.cpp", "win.cpp", "d.cpp", "e.cpp" }.Select(P), noExtra.Sources);
            CollectionAssert.AreEqual(new[] { "NAME_OK" }, noExtra.CompileDefinitions);

            // Cache variables passed by the caller win over option() defaults.
            var extra = Read(new Dictionary<string, string> { { "WITH_EXTRA", "ON" } }).GetTarget("Vars");
            CollectionAssert.Contains(extra.Sources, P("extra.cpp"));

            var other = Read().GetTarget("Vars");
            CollectionAssert.Contains(other.Sources, P("other.cpp"));
        }

        [Test]
        public void GlobSubdirectoryAliasAndGeneratorExpressions()
        {
            WriteFile("src/one.cpp");
            WriteFile("src/two.c");
            WriteFile("src/deep/three.cpp");
            WriteFile("src/notes.txt");
            WriteFile("CMakeLists.txt", @"
project(Top)
include_directories(common)
add_definitions(-DFROM_TOP /W4)
add_subdirectory(lib)
");
            WriteFile("lib/CMakeLists.txt", @"
file(GLOB TOP_SOURCES CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/../src/*.cpp ""${CMAKE_CURRENT_SOURCE_DIR}/../src/*.c"")
file(GLOB_RECURSE ALL_CPP ../src/*.cpp)
add_library(core ${TOP_SOURCES})
add_library(top::core ALIAS core)
add_library(all ${ALL_CPP})
target_include_directories(core PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
    $<INSTALL_INTERFACE:include>)
target_compile_definitions(core PRIVATE $<$<CONFIG:Debug>:CORE_DEBUG>)
target_link_libraries(all PUBLIC top::core)
");
            var result = Read();
            var core = result.GetTarget("core");

            Assert.AreSame(core, result.GetTarget("top::core"));
            Assert.AreEqual(P("lib"), core.SourceDirectory);
            CollectionAssert.AreEqual(new[] { P("src/one.cpp"), P("src/two.c") }, core.Sources);
            CollectionAssert.AreEqual(new[] { P("common"), P("lib/include") }, core.IncludeDirectories);
            CollectionAssert.AreEqual(new[] { P("lib/include") }, core.InterfaceIncludeDirectories);
            CollectionAssert.AreEqual(new[] { "FROM_TOP" }, core.CompileDefinitions);
            Assert.AreEqual(1, result.Warnings.Count(w => w.Contains("$<CONFIG:Debug>")));

            var all = result.GetTarget("all");
            CollectionAssert.AreEqual(new[] { P("src/deep/three.cpp"), P("src/one.cpp") }, all.Sources);
            CollectionAssert.AreEqual(new[] { "top::core" }, all.InterfaceLinkLibraries);
        }

        [Test]
        public void InterfaceAndSharedLibraries()
        {
            WriteFile("CMakeLists.txt", @"
add_library(headers INTERFACE)
target_include_directories(headers INTERFACE include)
target_compile_definitions(headers INTERFACE HEADERS_ONLY)

set(BUILD_SHARED_LIBS ON)
add_library(dyn dyn.cpp)

function(my_helper target)
  add_library(${target}_from_function STATIC x.cpp)
endfunction()
my_helper(dyn)

add_executable(app WIN32 main.cpp)
target_link_libraries(app headers dyn)
");
            var result = Read();

            var headers = result.GetTarget("headers");
            Assert.AreEqual(CMakeTargetType.InterfaceLibrary, headers.Type);
            CollectionAssert.IsEmpty(headers.Sources);
            CollectionAssert.IsEmpty(headers.IncludeDirectories);
            CollectionAssert.AreEqual(new[] { P("include") }, headers.InterfaceIncludeDirectories);
            CollectionAssert.AreEqual(new[] { "HEADERS_ONLY" }, headers.InterfaceCompileDefinitions);

            Assert.AreEqual(CMakeTargetType.SharedLibrary, result.GetTarget("dyn").Type);
            Assert.AreEqual(CMakeTargetType.StaticLibrary, result.GetTarget("dyn_from_function").Type);

            var app = result.GetTarget("app");
            Assert.AreEqual(CMakeTargetType.Executable, app.Type);
            CollectionAssert.AreEqual(new[] { P("main.cpp") }, app.Sources);
            CollectionAssert.AreEqual(new[] { "headers", "dyn" }, app.LinkLibraries);
        }

        [Test]
        public void BracketArgumentsCommentsAndReturn()
        {
            WriteFile("CMakeLists.txt", @"
#[[ a bracket comment
add_library(commented_out a.cpp)
]]
set(WEIRD [=[has ;semicolons; and ${NOT_EXPANDED}]=])
add_library(lib a.cpp)
target_compile_definitions(lib PRIVATE ""WEIRD_LEN=${WEIRD}"")
return()
add_library(after_return b.cpp)
");
            var result = Read();
            Assert.IsNull(result.GetTarget("commented_out"));
            Assert.IsNull(result.GetTarget("after_return"));
            CollectionAssert.AreEqual(new[] { "WEIRD_LEN=has ;semicolons; and ${NOT_EXPANDED}" }, result.GetTarget("lib").CompileDefinitions);
        }

        [Test]
        public void FunctionsMacrosAndStrings()
        {
            WriteFile("version.h", "#define LIB_VERSION 10203\n");
            WriteFile("CMakeLists.txt", @"
project(Funcs)

function(setup_target target kind)
  target_include_directories(${target} ${kind} $<BUILD_INTERFACE:${PROJECT_SOURCE_DIR}/include>)
  set(LAST_SETUP ${target} PARENT_SCOPE)
  set(LOCAL_ONLY yes)
endfunction()

macro(add_prefixed out)
  set(${out} """")
  foreach(item ${ARGN})
    list(APPEND ${out} src/${item}.cpp)
  endforeach()
endmacro()

file(READ version.h version_h)
if(version_h MATCHES ""LIB_VERSION ([0-9]+)([0-9][0-9])([0-9][0-9])"")
  set(VERSION_MAJOR ${CMAKE_MATCH_1})
endif()
string(TOUPPER ""lib"" upper_name)
string(REGEX REPLACE ""^0+"" """" minor ""${CMAKE_MATCH_2}"")

add_prefixed(SOURCES a b)
add_library(funcs ${SOURCES})
setup_target(funcs PUBLIC)
target_compile_definitions(funcs PRIVATE ${upper_name}_MAJOR=${VERSION_MAJOR} MINOR=${minor} LAST=${LAST_SETUP} LOCAL=${LOCAL_ONLY}x QUOTED=""a b"")
");
            var result = Read();
            var funcs = result.GetTarget("funcs");

            CollectionAssert.AreEqual(new[] { P("src/a.cpp"), P("src/b.cpp") }, funcs.Sources);
            CollectionAssert.AreEqual(new[] { P("include") }, funcs.InterfaceIncludeDirectories);
            CollectionAssert.AreEqual(new[] { "LIB_MAJOR=1", "MINOR=2", "LAST=funcs", "LOCAL=x", "QUOTED=\"a b\"" }, funcs.CompileDefinitions);
            CollectionAssert.IsEmpty(result.Warnings);
        }

        [Test]
        public void ParseArgumentsAndListOperations()
        {
            WriteFile("src/a.cpp");
            WriteFile("src/b.cpp");
            WriteFile("src/module.cpp");
            // Shaped like fmt's add_module_library.
            WriteFile("CMakeLists.txt", @"
function(add_module_library name)
  cmake_parse_arguments(AML ""SHARED"" ""IF"" ""FALLBACK"" ${ARGN})
  add_library(${name})
  if (NOT ${${AML_IF}})
    target_sources(${name} PRIVATE ${AML_FALLBACK})
  else()
    target_sources(${name} PRIVATE ${AML_UNPARSED_ARGUMENTS})
  endif()
  target_compile_definitions(${name} PRIVATE SHARED_${AML_SHARED})
endfunction()

option(USE_MODULES ""modules"" OFF)
add_module_library(lib src/module.cpp FALLBACK src/a.cpp src/b.cpp IF USE_MODULES)

set(items x y z w)
list(GET items 0 -1 ends)
list(REMOVE_AT items 0 -1)
list(FIND items z z_index)
list(INSERT items 0 first)
list(POP_BACK items popped)
list(SUBLIST items 1 1 middle)
target_compile_definitions(lib PRIVATE ""ENDS=${ends}"" ""ITEMS=${items}"" Z=${z_index} POPPED=${popped} MIDDLE=${middle})
");
            var result = Read();
            var lib = result.GetTarget("lib");

            CollectionAssert.AreEqual(new[] { P("src/a.cpp"), P("src/b.cpp") }, lib.Sources);
            CollectionAssert.AreEqual(new[] { "SHARED_FALSE", "ENDS=x;w", "ITEMS=first;y", "Z=1", "POPPED=z", "MIDDLE=y" }, lib.CompileDefinitions);
            CollectionAssert.IsEmpty(result.Warnings);
        }

        [Test]
        public void CompileOptionsAndGeneratorExpressions()
        {
            WriteFile("lib.cpp");
            WriteFile("CMakeLists.txt", @"
add_library(lib lib.cpp)
target_compile_options(lib PUBLIC $<$<COMPILE_LANGUAGE:CXX>:/utf-8> PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/W4> $<$<CXX_COMPILER_ID:GNU,Clang>:-Wall>)
target_compile_definitions(lib PRIVATE $<$<PLATFORM_ID:Windows>:ON_WINDOWS> $<$<NOT:$<BOOL:${UNSET_VAR}>>:NOT_SET> $<IF:$<AND:1,0>,BOTH,NOT_BOTH>)
");
            var lib = Read(new Dictionary<string, string> { { "CMAKE_CXX_COMPILER_ID", "MSVC" }, { "CMAKE_SYSTEM_NAME", "Windows" } }).GetTarget("lib");

            CollectionAssert.AreEqual(new[] { "/utf-8", "/W4" }, lib.CompileOptions);
            CollectionAssert.AreEqual(new[] { "/utf-8" }, lib.InterfaceCompileOptions);
            CollectionAssert.AreEqual(new[] { "ON_WINDOWS", "NOT_SET", "NOT_BOTH" }, lib.CompileDefinitions);

            var gcc = CMakeListsReader.Read(_root, new Dictionary<string, string> { { "CMAKE_CXX_COMPILER_ID", "GNU" }, { "CMAKE_SYSTEM_NAME", "Linux" } }).GetTarget("lib");
            CollectionAssert.AreEqual(new[] { "/utf-8", "-Wall" }, gcc.CompileOptions);
            CollectionAssert.AreEqual(new[] { "NOT_SET", "NOT_BOTH" }, gcc.CompileDefinitions);
        }

        [Test]
        public void MissingFileThrows()
        {
            Assert.Throws<FileNotFoundException>(() => CMakeListsReader.Read(Path.Combine(_root, "nope")));
        }
    }
}
