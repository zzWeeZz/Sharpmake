// Copyright (c) Ubisoft. All Rights Reserved.
// Licensed under the Apache 2.0 License. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Sharpmake
{
    /// <summary>
    /// Result of reading a CMakeLists.txt tree.
    /// </summary>
    public class CMakeListsReadResult
    {
        /// <summary>
        /// Targets declared in the tree, by name. Aliases (add_library(a::b ALIAS b)) map to the
        /// target they alias.
        /// </summary>
        public Dictionary<string, CMakeTarget> Targets { get; } = new Dictionary<string, CMakeTarget>(StringComparer.Ordinal);

        /// <summary>
        /// Things the reader could not understand, as "file(line): message".
        /// </summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>
        /// Commands that were met and skipped because they don't affect what Sharpmake needs.
        /// </summary>
        public SortedSet<string> IgnoredCommands { get; } = new SortedSet<string>(StringComparer.Ordinal);

        public CMakeTarget GetTarget(string name)
        {
            Targets.TryGetValue(name, out CMakeTarget target);
            return target;
        }
    }

    /// <summary>
    /// Reads a CMakeLists.txt without running CMake.
    /// </summary>
    /// <remarks>
    /// This is an interpreter for the subset of the CMake language that describes how a library
    /// is built: variables, lists, if/foreach, add_subdirectory, include, file(GLOB), and the
    /// add_library / target_* commands. Everything else is skipped. It does not evaluate generator
    /// expressions other than $&lt;BUILD_INTERFACE:...&gt; and $&lt;INSTALL_INTERFACE:...&gt;,
    /// and it does not run functions or macros declared in the scripts.
    /// </remarks>
    public static class CMakeListsReader
    {
        /// <summary>
        /// Reads <paramref name="cmakeListsPath"/> and all the files it pulls in.
        /// </summary>
        /// <param name="cmakeListsPath">Path to a CMakeLists.txt, or to the directory holding it.</param>
        /// <param name="cacheVariables">Variables set as if passed with -D on the cmake command line.</param>
        public static CMakeListsReadResult Read(string cmakeListsPath, IDictionary<string, string> cacheVariables = null)
        {
            string fullPath = Path.GetFullPath(cmakeListsPath);
            if (Directory.Exists(fullPath))
                fullPath = Path.Combine(fullPath, "CMakeLists.txt");
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("CMakeLists.txt not found", fullPath);

            var interpreter = new Interpreter(cacheVariables);
            interpreter.RunRoot(fullPath);
            return interpreter.Result;
        }

        #region Parsing

        internal enum ArgumentKind
        {
            Unquoted,
            Quoted,
            Bracket,
        }

        internal readonly struct RawArgument
        {
            public readonly string Text;
            public readonly ArgumentKind Kind;

            public RawArgument(string text, ArgumentKind kind)
            {
                Text = text;
                Kind = kind;
            }
        }

        internal class Command
        {
            public string Name;      // lower case
            public List<RawArgument> Arguments = new List<RawArgument>();
            public string File;
            public int Line;

            public string Location => $"{File}({Line})";
        }

        internal static List<Command> ParseFile(string path)
        {
            return Parse(File.ReadAllText(path), path);
        }

        internal static List<Command> Parse(string text, string fileName)
        {
            var commands = new List<Command>();
            int pos = 0;
            int line = 1;

            while (true)
            {
                SkipSpaceAndComments(text, ref pos, ref line);
                if (pos >= text.Length)
                    break;

                int nameStart = pos;
                while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_'))
                    ++pos;
                if (pos == nameStart)
                    throw new Error($"{fileName}({line}): unexpected character '{text[pos]}' in CMake script");

                var command = new Command
                {
                    Name = text.Substring(nameStart, pos - nameStart).ToLowerInvariant(),
                    File = fileName,
                    Line = line,
                };

                while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t'))
                    ++pos;
                if (pos >= text.Length || text[pos] != '(')
                    throw new Error($"{fileName}({line}): expected '(' after '{command.Name}'");
                ++pos;

                ParseArguments(text, ref pos, ref line, fileName, command.Arguments);
                commands.Add(command);
            }

            return commands;
        }

        private static void SkipSpaceAndComments(string text, ref int pos, ref int line)
        {
            while (pos < text.Length)
            {
                char c = text[pos];
                if (c == '\n')
                {
                    ++line;
                    ++pos;
                }
                else if (char.IsWhiteSpace(c))
                {
                    ++pos;
                }
                else if (c == '#')
                {
                    SkipComment(text, ref pos, ref line);
                }
                else
                {
                    break;
                }
            }
        }

        private static void SkipComment(string text, ref int pos, ref int line)
        {
            // pos is on '#'
            ++pos;
            if (pos < text.Length && text[pos] == '[' && TryReadBracket(text, ref pos, ref line, out _))
                return;

            while (pos < text.Length && text[pos] != '\n')
                ++pos;
        }

        // pos is on the opening '['. On success, pos is after the closing bracket.
        private static bool TryReadBracket(string text, ref int pos, ref int line, out string content)
        {
            content = null;
            int p = pos + 1;
            int level = 0;
            while (p < text.Length && text[p] == '=')
            {
                ++level;
                ++p;
            }
            if (p >= text.Length || text[p] != '[')
                return false;
            ++p;

            string close = "]" + new string('=', level) + "]";
            int end = text.IndexOf(close, p, StringComparison.Ordinal);
            if (end < 0)
                throw new Error("unterminated bracket argument in CMake script");

            // A newline right after the opening bracket is ignored.
            int contentStart = p;
            if (contentStart < end && text[contentStart] == '\n')
                ++contentStart;
            else if (contentStart + 1 < end && text[contentStart] == '\r' && text[contentStart + 1] == '\n')
                contentStart += 2;

            content = text.Substring(contentStart, end - contentStart);
            for (int i = pos; i < end; ++i)
                if (text[i] == '\n')
                    ++line;
            pos = end + close.Length;
            return true;
        }

        private static void ParseArguments(string text, ref int pos, ref int line, string fileName, List<RawArgument> arguments)
        {
            int depth = 0;
            int startLine = line;

            while (true)
            {
                SkipSpaceAndComments(text, ref pos, ref line);
                if (pos >= text.Length)
                    throw new Error($"{fileName}({startLine}): missing ')' in CMake script");

                char c = text[pos];
                if (c == ')')
                {
                    ++pos;
                    if (depth == 0)
                        return;
                    --depth;
                    arguments.Add(new RawArgument(")", ArgumentKind.Unquoted));
                }
                else if (c == '(')
                {
                    ++pos;
                    ++depth;
                    arguments.Add(new RawArgument("(", ArgumentKind.Unquoted));
                }
                else if (c == '"')
                {
                    arguments.Add(new RawArgument(ReadQuoted(text, ref pos, ref line, fileName), ArgumentKind.Quoted));
                }
                else if (c == '[' && TryReadBracket(text, ref pos, ref line, out string bracketContent))
                {
                    arguments.Add(new RawArgument(bracketContent, ArgumentKind.Bracket));
                }
                else
                {
                    arguments.Add(new RawArgument(ReadUnquoted(text, ref pos), ArgumentKind.Unquoted));
                }
            }
        }

        private static string ReadQuoted(string text, ref int pos, ref int line, string fileName)
        {
            int startLine = line;
            var sb = new StringBuilder();
            ++pos; // opening quote
            while (pos < text.Length)
            {
                char c = text[pos];
                if (c == '"')
                {
                    ++pos;
                    return sb.ToString();
                }
                if (c == '\\' && pos + 1 < text.Length)
                {
                    char next = text[pos + 1];
                    if (next == '\n')
                    {
                        // line continuation
                        ++line;
                        pos += 2;
                        continue;
                    }
                    AppendEscape(sb, next);
                    pos += 2;
                    continue;
                }
                if (c == '\n')
                    ++line;
                sb.Append(c);
                ++pos;
            }
            throw new Error($"{fileName}({startLine}): unterminated quoted argument in CMake script");
        }

        private static string ReadUnquoted(string text, ref int pos)
        {
            var sb = new StringBuilder();
            while (pos < text.Length)
            {
                char c = text[pos];
                if (char.IsWhiteSpace(c) || c == '(' || c == ')' || c == '#')
                    break;
                if (c == '"')
                {
                    // Legacy form like FOO="bar baz": CMake keeps the quotes in the value.
                    sb.Append(text[pos++]);
                    while (pos < text.Length && text[pos] != '"')
                        sb.Append(text[pos++]);
                    if (pos < text.Length)
                        sb.Append(text[pos++]);
                    continue;
                }
                if (c == '\\' && pos + 1 < text.Length)
                {
                    AppendEscape(sb, text[pos + 1]);
                    pos += 2;
                    continue;
                }
                sb.Append(c);
                ++pos;
            }
            return sb.ToString();
        }

        private static void AppendEscape(StringBuilder sb, char escaped)
        {
            switch (escaped)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '0': sb.Append('\0'); break;
                // Keep the escape so that the list splitting and variable expansion see it.
                case ';': sb.Append("\\;"); break;
                case '$': sb.Append("\\$"); break;
                default: sb.Append(escaped); break;
            }
        }

        #endregion

        #region Interpreting

        private class ReturnSignal : Exception
        {
        }

        private class Scope
        {
            public Scope Parent;
            public readonly Dictionary<string, string> Variables;
            public readonly List<string> IncludeDirectories;
            public readonly List<string> CompileDefinitions;
            public readonly List<CMakeTarget> TargetsDeclaredHere;
            public string SourceDirectory;

            // Scope of a directory (the root CMakeLists.txt or an add_subdirectory)
            public Scope(Scope parent, string sourceDirectory)
            {
                Parent = parent;
                SourceDirectory = sourceDirectory;
                Variables = parent != null ? new Dictionary<string, string>(parent.Variables, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal);
                IncludeDirectories = parent != null ? new List<string>(parent.IncludeDirectories) : new List<string>();
                CompileDefinitions = parent != null ? new List<string>(parent.CompileDefinitions) : new List<string>();
                TargetsDeclaredHere = new List<CMakeTarget>();
            }

            // Variable scope of a function call: directory properties stay shared with the caller.
            public Scope(Scope caller)
            {
                Parent = caller;
                SourceDirectory = caller.SourceDirectory;
                Variables = new Dictionary<string, string>(caller.Variables, StringComparer.Ordinal);
                IncludeDirectories = caller.IncludeDirectories;
                CompileDefinitions = caller.CompileDefinitions;
                TargetsDeclaredHere = caller.TargetsDeclaredHere;
            }
        }

        private class FunctionDefinition
        {
            public bool IsMacro;
            public List<string> Parameters;
            public List<Command> Body;
        }

        private class Interpreter
        {
            public readonly CMakeListsReadResult Result = new CMakeListsReadResult();

            private readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<CMakeTarget, Scope> _targetScopes = new Dictionary<CMakeTarget, Scope>();
            private readonly Dictionary<string, FunctionDefinition> _userFunctions = new Dictionary<string, FunctionDefinition>(StringComparer.Ordinal);
            private int _callDepth;
            private Scope _scope;
            private string _rootSourceDirectory;

            public Interpreter(IDictionary<string, string> cacheVariables)
            {
                if (cacheVariables != null)
                    foreach (var pair in cacheVariables)
                        _cache[pair.Key] = pair.Value;
            }

            public void RunRoot(string cmakeListsPath)
            {
                _rootSourceDirectory = Normalize(Path.GetDirectoryName(cmakeListsPath));
                _scope = new Scope(null, _rootSourceDirectory);
                SetVariable("CMAKE_SOURCE_DIR", _rootSourceDirectory);
                SetVariable("CMAKE_BINARY_DIR", _rootSourceDirectory + "/build");
                SetVariable("CMAKE_VERSION", "3.28.0");
                SetVariable("CMAKE_MAJOR_VERSION", "3");
                SetVariable("CMAKE_MINOR_VERSION", "28");

                RunDirectory(cmakeListsPath);

                // Directory level definitions (add_definitions) apply to every target of the
                // directory, whenever they were declared.
                foreach (var pair in _targetScopes)
                    foreach (string define in pair.Value.CompileDefinitions)
                        AddUnique(pair.Key.CompileDefinitions, define);
            }

            private void RunDirectory(string cmakeListsPath)
            {
                string directory = Normalize(Path.GetDirectoryName(cmakeListsPath));
                _scope.SourceDirectory = directory;
                SetVariable("CMAKE_CURRENT_SOURCE_DIR", directory);
                SetVariable("CMAKE_CURRENT_BINARY_DIR", BinaryDirFor(directory));
                RunFile(cmakeListsPath);
            }

            private string BinaryDirFor(string sourceDirectory)
            {
                string relative = Path.GetRelativePath(_rootSourceDirectory, sourceDirectory).Replace('\\', '/');
                return relative == "." ? GetVariable("CMAKE_BINARY_DIR") : GetVariable("CMAKE_BINARY_DIR") + "/" + relative;
            }

            private void RunFile(string path)
            {
                string fullPath = Normalize(path);
                string previousListDir = GetVariableOrNull("CMAKE_CURRENT_LIST_DIR");
                string previousListFile = GetVariableOrNull("CMAKE_CURRENT_LIST_FILE");
                SetVariable("CMAKE_CURRENT_LIST_DIR", Normalize(Path.GetDirectoryName(fullPath)));
                SetVariable("CMAKE_CURRENT_LIST_FILE", fullPath);

                var commands = ParseFile(path);
                try
                {
                    Execute(commands, 0, commands.Count);
                }
                catch (ReturnSignal)
                {
                }

                RestoreVariable("CMAKE_CURRENT_LIST_DIR", previousListDir);
                RestoreVariable("CMAKE_CURRENT_LIST_FILE", previousListFile);
            }

            private void Execute(List<Command> commands, int start, int end)
            {
                int i = start;
                while (i < end)
                {
                    Command command = commands[i];
                    switch (command.Name)
                    {
                        case "if":
                            i = ExecuteIf(commands, i, end);
                            continue;
                        case "foreach":
                            i = ExecuteForeach(commands, i, end);
                            continue;
                        case "while":
                            Warn(command, "while() loops are not supported, the loop body is skipped");
                            i = FindBlockEnd(commands, i, end, "while", "endwhile") + 1;
                            continue;
                        case "function":
                        case "macro":
                            {
                                int blockEnd = FindBlockEnd(commands, i, end, command.Name, "end" + command.Name);
                                var args = ExpandArguments(command);
                                if (args.Count > 0)
                                {
                                    _userFunctions[args[0].ToLowerInvariant()] = new FunctionDefinition
                                    {
                                        IsMacro = command.Name == "macro",
                                        Parameters = args.Skip(1).ToList(),
                                        Body = commands.GetRange(i + 1, blockEnd - i - 1),
                                    };
                                }
                                i = blockEnd + 1;
                                continue;
                            }
                        case "block":
                            // Scoping is not modelled, just run the body.
                            ++i;
                            continue;
                        case "endblock":
                            ++i;
                            continue;
                    }

                    ExecuteCommand(command);
                    ++i;
                }
            }

            private static int FindBlockEnd(List<Command> commands, int start, int end, string open, string close)
            {
                int depth = 0;
                for (int i = start; i < end; ++i)
                {
                    if (commands[i].Name == open)
                        ++depth;
                    else if (commands[i].Name == close && --depth == 0)
                        return i;
                }
                throw new Error($"{commands[start].Location}: missing {close}()");
            }

            private int ExecuteIf(List<Command> commands, int start, int end)
            {
                // Collect the branches at our nesting level.
                var branches = new List<int> { start };
                int depth = 0;
                int endIf = -1;
                for (int i = start; i < end; ++i)
                {
                    string name = commands[i].Name;
                    if (name == "if")
                    {
                        ++depth;
                    }
                    else if (name == "endif")
                    {
                        if (--depth == 0)
                        {
                            endIf = i;
                            break;
                        }
                    }
                    else if (depth == 1 && (name == "elseif" || name == "else"))
                    {
                        branches.Add(i);
                    }
                }
                if (endIf < 0)
                    throw new Error($"{commands[start].Location}: missing endif()");

                for (int b = 0; b < branches.Count; ++b)
                {
                    Command branch = commands[branches[b]];
                    bool taken = branch.Name == "else" || EvaluateCondition(branch);
                    if (taken)
                    {
                        int bodyEnd = b + 1 < branches.Count ? branches[b + 1] : endIf;
                        Execute(commands, branches[b] + 1, bodyEnd);
                        break;
                    }
                }

                return endIf + 1;
            }

            private int ExecuteForeach(List<Command> commands, int start, int end)
            {
                int endForeach = FindBlockEnd(commands, start, end, "foreach", "endforeach");
                var args = ExpandArguments(commands[start]);
                if (args.Count == 0)
                    return endForeach + 1;

                string loopVariable = args[0];
                var items = new List<string>();
                if (args.Count >= 2 && args[1] == "RANGE")
                {
                    var numbers = args.Skip(2).Select(a => int.Parse(a, CultureInfo.InvariantCulture)).ToList();
                    int from = numbers.Count >= 2 ? numbers[0] : 0;
                    int to = numbers.Count >= 2 ? numbers[1] : numbers[0];
                    int step = numbers.Count >= 3 ? numbers[2] : 1;
                    for (int n = from; n <= to; n += step)
                        items.Add(n.ToString(CultureInfo.InvariantCulture));
                }
                else if (args.Count >= 2 && args[1] == "IN")
                {
                    string mode = null;
                    foreach (string arg in args.Skip(2))
                    {
                        if (arg == "LISTS" || arg == "ITEMS")
                        {
                            mode = arg;
                            continue;
                        }
                        if (mode == "LISTS")
                            items.AddRange(SplitList(GetVariable(arg)));
                        else
                            items.Add(arg);
                    }
                }
                else
                {
                    items.AddRange(args.Skip(1));
                }

                string previous = GetVariableOrNull(loopVariable);
                foreach (string item in items)
                {
                    SetVariable(loopVariable, item);
                    Execute(commands, start + 1, endForeach);
                }
                RestoreVariable(loopVariable, previous);

                return endForeach + 1;
            }

            private void ExecuteCommand(Command command)
            {
                switch (command.Name)
                {
                    case "set": CommandSet(command); break;
                    case "unset": CommandUnset(command); break;
                    case "option": CommandOption(command); break;
                    case "list": CommandList(command); break;
                    case "file": CommandFile(command); break;
                    case "project": CommandProject(command); break;
                    case "include": CommandInclude(command); break;
                    case "add_subdirectory": CommandAddSubdirectory(command); break;
                    case "include_directories": CommandIncludeDirectories(command); break;
                    case "add_definitions": CommandAddDefinitions(command, true); break;
                    case "add_compile_definitions": CommandAddDefinitions(command, false); break;
                    case "add_library": CommandAddLibrary(command); break;
                    case "add_executable": CommandAddExecutable(command); break;
                    case "target_sources": CommandTargetSources(command); break;
                    case "target_include_directories": CommandTargetIncludeDirectories(command); break;
                    case "target_compile_definitions": CommandTargetCompileDefinitions(command); break;
                    case "target_link_libraries": CommandTargetLinkLibraries(command); break;
                    case "return": throw new ReturnSignal();
                    case "message": CommandMessage(command); break;
                    case "string": CommandString(command); break;
                    default:
                        if (_userFunctions.TryGetValue(command.Name, out FunctionDefinition function))
                            CallFunction(command, function);
                        else
                            Result.IgnoredCommands.Add(command.Name);
                        break;
                }
            }

            private static readonly HashSet<string> s_builtinCommands = new HashSet<string>(StringComparer.Ordinal)
            {
                "set", "unset", "option", "list", "file", "project", "include", "add_subdirectory", "include_directories",
                "add_definitions", "add_compile_definitions", "add_library", "add_executable", "target_sources",
                "target_include_directories", "target_compile_definitions", "target_link_libraries", "return",
                "message", "string", "if", "foreach", "function", "macro", "cmake_minimum_required",
                "target_compile_options", "target_compile_features", "set_target_properties", "install",
            };

            private void CallFunction(Command command, FunctionDefinition function)
            {
                if (_callDepth > 64)
                {
                    Warn(command, $"{command.Name}() recursion is too deep, call ignored");
                    return;
                }

                var args = ExpandArguments(command);
                var bindings = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ARGC"] = args.Count.ToString(CultureInfo.InvariantCulture),
                    ["ARGV"] = JoinList(args),
                    ["ARGN"] = JoinList(args.Skip(function.Parameters.Count)),
                };
                for (int i = 0; i < args.Count; ++i)
                    bindings["ARGV" + i.ToString(CultureInfo.InvariantCulture)] = args[i];
                for (int i = 0; i < function.Parameters.Count; ++i)
                    bindings[function.Parameters[i]] = i < args.Count ? args[i] : "";

                ++_callDepth;
                try
                {
                    if (function.IsMacro)
                    {
                        // A macro is a text substitution of its arguments, run in the caller scope.
                        var body = function.Body.Select(c => SubstituteMacroArguments(c, bindings)).ToList();
                        Execute(body, 0, body.Count);
                        return;
                    }

                    Scope caller = _scope;
                    _scope = new Scope(caller);
                    try
                    {
                        foreach (var pair in bindings)
                            _scope.Variables[pair.Key] = pair.Value;
                        Execute(function.Body, 0, function.Body.Count);
                    }
                    catch (ReturnSignal)
                    {
                    }
                    finally
                    {
                        _scope = caller;
                    }
                }
                finally
                {
                    --_callDepth;
                }
            }

            private static Command SubstituteMacroArguments(Command command, Dictionary<string, string> bindings)
            {
                var copy = new Command { Name = command.Name, File = command.File, Line = command.Line };
                foreach (var argument in command.Arguments)
                {
                    if (argument.Kind == ArgumentKind.Bracket || argument.Text.IndexOf("${", StringComparison.Ordinal) < 0)
                    {
                        copy.Arguments.Add(argument);
                        continue;
                    }
                    string text = argument.Text;
                    foreach (var pair in bindings)
                        text = text.Replace("${" + pair.Key + "}", pair.Value);
                    copy.Arguments.Add(new RawArgument(text, argument.Kind));
                }
                return copy;
            }

            #region Commands

            private void CommandSet(Command command)
            {
                var args = ExpandArguments(command);
                if (args.Count == 0)
                    return;

                string name = args[0];
                var values = args.Skip(1).ToList();

                int cacheIndex = values.IndexOf("CACHE");
                if (cacheIndex >= 0)
                {
                    bool force = values.Contains("FORCE");
                    string value = JoinList(values.Take(cacheIndex));
                    if (force || !_cache.ContainsKey(name))
                        _cache[name] = value;
                    // A cache set also removes the normal binding with the same name.
                    _scope.Variables.Remove(name);
                    return;
                }

                bool parentScope = values.Count > 0 && values[values.Count - 1] == "PARENT_SCOPE";
                if (parentScope)
                {
                    values.RemoveAt(values.Count - 1);
                    if (_scope.Parent != null)
                    {
                        if (values.Count == 0)
                            _scope.Parent.Variables.Remove(name);
                        else
                            _scope.Parent.Variables[name] = JoinList(values);
                    }
                    return;
                }

                if (values.Count == 0)
                    _scope.Variables.Remove(name);
                else
                    _scope.Variables[name] = JoinList(values);
            }

            private void CommandUnset(Command command)
            {
                var args = ExpandArguments(command);
                if (args.Count == 0)
                    return;
                if (args.Contains("CACHE"))
                    _cache.Remove(args[0]);
                else if (args.Contains("PARENT_SCOPE"))
                    _scope.Parent?.Variables.Remove(args[0]);
                else
                    _scope.Variables.Remove(args[0]);
            }

            private void CommandOption(Command command)
            {
                var args = ExpandArguments(command);
                if (args.Count == 0)
                    return;
                if (!_cache.ContainsKey(args[0]) && !_scope.Variables.ContainsKey(args[0]))
                    _cache[args[0]] = args.Count >= 3 && IsTrueConstant(args[2]) ? "ON" : "OFF";
            }

            private void CommandList(Command command)
            {
                var args = ExpandArguments(command);
                if (args.Count < 2)
                    return;

                string operation = args[0];
                string name = args[1];
                var list = SplitList(GetVariable(name)).ToList();
                var rest = args.Skip(2).ToList();

                switch (operation)
                {
                    case "APPEND":
                        list.AddRange(rest);
                        break;
                    case "PREPEND":
                        list.InsertRange(0, rest);
                        break;
                    case "REMOVE_ITEM":
                        list.RemoveAll(rest.Contains);
                        break;
                    case "REMOVE_DUPLICATES":
                        list = list.Distinct().ToList();
                        break;
                    case "JOIN":
                        if (rest.Count >= 2)
                            SetVariable(rest[1], string.Join(rest[0], list));
                        return;
                    case "LENGTH":
                        if (rest.Count > 0)
                            SetVariable(rest[0], list.Count.ToString(CultureInfo.InvariantCulture));
                        return;
                    case "FILTER":
                        // list(FILTER <list> INCLUDE|EXCLUDE REGEX <regex>)
                        if (rest.Count >= 3 && rest[1] == "REGEX")
                        {
                            var regex = new Regex(rest[2]);
                            bool include = rest[0] == "INCLUDE";
                            list = list.Where(item => regex.IsMatch(item) == include).ToList();
                        }
                        break;
                    default:
                        Warn(command, $"list({operation}) is not supported");
                        return;
                }

                SetVariable(name, JoinList(list));
            }

            private void CommandFile(Command command)
            {
                var args = ExpandArguments(command);
                if (args.Count < 2)
                    return;

                string operation = args[0];
                if (operation == "READ" && args.Count >= 3)
                {
                    string file = MakeAbsolute(args[1]);
                    SetVariable(args[2], File.Exists(file) ? File.ReadAllText(file) : "");
                    return;
                }
                if (operation != "GLOB" && operation != "GLOB_RECURSE")
                {
                    Result.IgnoredCommands.Add("file(" + operation + ")");
                    return;
                }

                bool recurse = operation == "GLOB_RECURSE";
                string variable = args[1];
                string relativeTo = null;
                var results = new List<string>();

                for (int i = 2; i < args.Count; ++i)
                {
                    string arg = args[i];
                    if (arg == "CONFIGURE_DEPENDS" || arg == "FOLLOW_SYMLINKS")
                        continue;
                    if (arg == "LIST_DIRECTORIES")
                    {
                        ++i;
                        continue;
                    }
                    if (arg == "RELATIVE" && i + 1 < args.Count)
                    {
                        relativeTo = MakeAbsolute(args[++i]);
                        continue;
                    }
                    results.AddRange(Glob(MakeAbsolute(arg), recurse));
                }

                results = results.Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
                if (relativeTo != null)
                    results = results.Select(p => Path.GetRelativePath(relativeTo, p).Replace('\\', '/')).ToList();

                SetVariable(variable, JoinList(results));
            }

            private void CommandProject(Command command)
            {
                var args = ExpandArguments(command);
                if (args.Count == 0)
                    return;

                string name = args[0];
                string sourceDir = _scope.SourceDirectory;
                string binaryDir = GetVariable("CMAKE_CURRENT_BINARY_DIR");

                SetVariable("PROJECT_NAME", name);
                SetVariable("PROJECT_SOURCE_DIR", sourceDir);
                SetVariable("PROJECT_BINARY_DIR", binaryDir);
                SetVariable(name + "_SOURCE_DIR", sourceDir);
                SetVariable(name + "_BINARY_DIR", binaryDir);
                SetVariable("PROJECT_IS_TOP_LEVEL", sourceDir == _rootSourceDirectory ? "ON" : "OFF");
                SetVariable(name + "_IS_TOP_LEVEL", sourceDir == _rootSourceDirectory ? "ON" : "OFF");
                if (GetVariableOrNull("CMAKE_PROJECT_NAME") == null)
                    SetVariable("CMAKE_PROJECT_NAME", name);

                int versionIndex = args.IndexOf("VERSION");
                if (versionIndex >= 0 && versionIndex + 1 < args.Count)
                {
                    string version = args[versionIndex + 1];
                    var parts = version.Split('.');
                    string[] suffixes = { "MAJOR", "MINOR", "PATCH", "TWEAK" };
                    SetVariable("PROJECT_VERSION", version);
                    SetVariable(name + "_VERSION", version);
                    for (int i = 0; i < suffixes.Length; ++i)
                    {
                        string value = i < parts.Length ? parts[i] : "";
                        SetVariable("PROJECT_VERSION_" + suffixes[i], value);
                        SetVariable(name + "_VERSION_" + suffixes[i], value);
                    }
                }
            }

            private void CommandInclude(Command command)
            {
                var args = ExpandArguments(command);
                if (args.Count == 0)
                    return;

                string file = args[0];
                bool optional = args.Contains("OPTIONAL");

                // include(SomeModule) looks up CMAKE_MODULE_PATH; include(path/file.cmake) is a file.
                var candidates = new List<string>();
                if (file.EndsWith(".cmake", StringComparison.OrdinalIgnoreCase) || file.Contains('/'))
                    candidates.Add(MakeAbsolute(file, GetVariable("CMAKE_CURRENT_LIST_DIR")));
                foreach (string modulePath in SplitList(GetVariable("CMAKE_MODULE_PATH")))
                    candidates.Add(MakeAbsolute(Path.Combine(modulePath, file + ".cmake")));

                string found = candidates.FirstOrDefault(File.Exists);
                if (found != null)
                {
                    RunFile(found);
                    return;
                }

                // Modules shipped with CMake (GNUInstallDirs, CTest...) are expected to be missing.
                if (!optional && (file.EndsWith(".cmake", StringComparison.OrdinalIgnoreCase) || file.Contains('/')))
                    Warn(command, $"include({file}) not found");
                else
                    Result.IgnoredCommands.Add("include(" + file + ")");
            }

            private void CommandAddSubdirectory(Command command)
            {
                var args = ExpandArguments(command);
                if (args.Count == 0)
                    return;

                string directory = MakeAbsolute(args[0]);
                string cmakeLists = Path.Combine(directory, "CMakeLists.txt");
                if (!File.Exists(cmakeLists))
                {
                    Warn(command, $"add_subdirectory({args[0]}): {cmakeLists} not found");
                    return;
                }

                Scope parent = _scope;
                _scope = new Scope(parent, directory);
                try
                {
                    RunDirectory(cmakeLists);
                }
                finally
                {
                    _scope = parent;
                }
            }

            private void CommandIncludeDirectories(Command command)
            {
                var args = ExpandArguments(command);
                bool before = false;
                var directories = new List<string>();
                foreach (string arg in args)
                {
                    if (arg == "AFTER" || arg == "SYSTEM")
                        continue;
                    if (arg == "BEFORE")
                    {
                        before = true;
                        continue;
                    }
                    string resolved = ResolveGeneratorExpressions(arg, command);
                    if (!string.IsNullOrEmpty(resolved))
                        directories.Add(MakeAbsolute(resolved));
                }

                if (before)
                    _scope.IncludeDirectories.InsertRange(0, directories);
                else
                    _scope.IncludeDirectories.AddRange(directories);

                // Also affects targets already declared in this directory.
                foreach (var target in _scope.TargetsDeclaredHere)
                    foreach (string directory in directories)
                        AddUnique(target.IncludeDirectories, directory);
            }

            private void CommandAddDefinitions(Command command, bool flagsSyntax)
            {
                var args = ExpandArguments(command);
                foreach (string arg in args)
                {
                    string define = arg;
                    if (flagsSyntax)
                    {
                        if (define.StartsWith("-D", StringComparison.Ordinal) || define.StartsWith("/D", StringComparison.Ordinal))
                            define = define.Substring(2);
                        else
                            continue; // add_definitions also accepts any compiler flag, ignore those
                    }
                    define = ResolveGeneratorExpressions(define, command);
                    if (!string.IsNullOrEmpty(define))
                        AddUnique(_scope.CompileDefinitions, define);
                }
            }

            private void CommandAddLibrary(Command command)
            {
                var args = ExpandArguments(command);
                if (args.Count == 0)
                    return;

                string name = args[0];

                if (args.Count >= 3 && args[1] == "ALIAS")
                {
                    if (Result.Targets.TryGetValue(args[2], out CMakeTarget aliased))
                        Result.Targets[name] = aliased;
                    else
                        Warn(command, $"ALIAS {name} refers to unknown target {args[2]}");
                    return;
                }

                CMakeTargetType type = IsTrue(GetVariable("BUILD_SHARED_LIBS")) ? CMakeTargetType.SharedLibrary : CMakeTargetType.StaticLibrary;
                int i = 1;
                for (; i < args.Count; ++i)
                {
                    switch (args[i])
                    {
                        case "STATIC": type = CMakeTargetType.StaticLibrary; continue;
                        case "SHARED": type = CMakeTargetType.SharedLibrary; continue;
                        case "MODULE": type = CMakeTargetType.ModuleLibrary; continue;
                        case "OBJECT": type = CMakeTargetType.ObjectLibrary; continue;
                        case "INTERFACE": type = CMakeTargetType.InterfaceLibrary; continue;
                        case "UNKNOWN": continue;
                        case "EXCLUDE_FROM_ALL": continue;
                        case "GLOBAL": continue;
                        case "IMPORTED": type = CMakeTargetType.Imported; continue;
                    }
                    break;
                }

                var target = DeclareTarget(command, name, type);
                if (target != null && type != CMakeTargetType.Imported)
                    AddSources(target, args.Skip(i), command);
            }

            private void CommandAddExecutable(Command command)
            {
                var args = ExpandArguments(command);
                if (args.Count == 0)
                    return;

                if (args.Count >= 3 && args[1] == "ALIAS")
                {
                    if (Result.Targets.TryGetValue(args[2], out CMakeTarget aliased))
                        Result.Targets[args[0]] = aliased;
                    return;
                }

                bool imported = args.Contains("IMPORTED");
                var target = DeclareTarget(command, args[0], imported ? CMakeTargetType.Imported : CMakeTargetType.Executable);
                if (target != null && !imported)
                    AddSources(target, args.Skip(1).Where(a => a != "WIN32" && a != "MACOSX_BUNDLE" && a != "EXCLUDE_FROM_ALL"), command);
            }

            private CMakeTarget DeclareTarget(Command command, string name, CMakeTargetType type)
            {
                if (Result.Targets.ContainsKey(name))
                {
                    Warn(command, $"target {name} is declared more than once, keeping the first one");
                    return null;
                }

                var target = new CMakeTarget
                {
                    Name = name,
                    Type = type,
                    SourceDirectory = _scope.SourceDirectory,
                };

                // The INCLUDE_DIRECTORIES of a target starts as the one of its directory.
                if (type != CMakeTargetType.InterfaceLibrary && type != CMakeTargetType.Imported)
                    target.IncludeDirectories.AddRange(_scope.IncludeDirectories);

                Result.Targets[name] = target;
                _targetScopes[target] = _scope;
                _scope.TargetsDeclaredHere.Add(target);
                return target;
            }

            private void CommandTargetSources(Command command)
            {
                var args = ExpandArguments(command);
                var target = FindTarget(command, args);
                if (target == null)
                    return;

                // target_sources(t PRIVATE a.cpp PUBLIC FILE_SET HEADERS BASE_DIRS inc FILES inc/a.h)
                string visibility = "PRIVATE";
                string fileSetPart = null;
                for (int i = 1; i < args.Count; ++i)
                {
                    string arg = args[i];
                    if (IsVisibilityKeyword(arg))
                    {
                        visibility = arg;
                        fileSetPart = null;
                        continue;
                    }
                    if (arg == "FILE_SET")
                    {
                        fileSetPart = "NAME";
                        continue;
                    }
                    if (fileSetPart != null && (arg == "TYPE" || arg == "BASE_DIRS" || arg == "FILES"))
                    {
                        fileSetPart = arg;
                        continue;
                    }

                    if (fileSetPart == "NAME" || fileSetPart == "TYPE")
                        continue;

                    if (fileSetPart == "BASE_DIRS")
                    {
                        string directory = ResolveGeneratorExpressions(arg, command);
                        if (!string.IsNullOrEmpty(directory))
                            AddWithVisibility(target.IncludeDirectories, target.InterfaceIncludeDirectories, visibility, MakeAbsolute(directory));
                        continue;
                    }

                    AddSources(target, new[] { arg }, command);
                }
            }

            private void CommandTargetIncludeDirectories(Command command)
            {
                var args = ExpandArguments(command);
                var target = FindTarget(command, args);
                if (target == null)
                    return;

                string visibility = target.Type == CMakeTargetType.InterfaceLibrary ? "INTERFACE" : "PRIVATE";
                foreach (string arg in args.Skip(1))
                {
                    if (arg == "SYSTEM" || arg == "BEFORE" || arg == "AFTER")
                        continue;
                    if (IsVisibilityKeyword(arg))
                    {
                        visibility = arg;
                        continue;
                    }

                    string directory = ResolveGeneratorExpressions(arg, command);
                    if (!string.IsNullOrEmpty(directory))
                        AddWithVisibility(target.IncludeDirectories, target.InterfaceIncludeDirectories, visibility, MakeAbsolute(directory));
                }
            }

            private void CommandTargetCompileDefinitions(Command command)
            {
                var args = ExpandArguments(command);
                var target = FindTarget(command, args);
                if (target == null)
                    return;

                string visibility = target.Type == CMakeTargetType.InterfaceLibrary ? "INTERFACE" : "PRIVATE";
                foreach (string arg in args.Skip(1))
                {
                    if (IsVisibilityKeyword(arg))
                    {
                        visibility = arg;
                        continue;
                    }

                    string define = ResolveGeneratorExpressions(arg, command);
                    if (define.StartsWith("-D", StringComparison.Ordinal))
                        define = define.Substring(2);
                    if (!string.IsNullOrEmpty(define))
                        AddWithVisibility(target.CompileDefinitions, target.InterfaceCompileDefinitions, visibility, define);
                }
            }

            private void CommandTargetLinkLibraries(Command command)
            {
                var args = ExpandArguments(command);
                var target = FindTarget(command, args);
                if (target == null)
                    return;

                // Without a keyword, the old signature makes everything transitive.
                string visibility = "PUBLIC";
                foreach (string arg in args.Skip(1))
                {
                    if (IsVisibilityKeyword(arg))
                    {
                        visibility = arg;
                        continue;
                    }
                    if (arg == "LINK_PUBLIC" || arg == "LINK_INTERFACE_LIBRARIES")
                    {
                        visibility = "PUBLIC";
                        continue;
                    }
                    if (arg == "LINK_PRIVATE")
                    {
                        visibility = "PRIVATE";
                        continue;
                    }
                    if (arg == "debug" || arg == "optimized" || arg == "general")
                        continue;

                    string library = ResolveGeneratorExpressions(arg, command);
                    if (!string.IsNullOrEmpty(library))
                        AddWithVisibility(target.LinkLibraries, target.InterfaceLinkLibraries, visibility, library);
                }
            }

            private void CommandString(Command command)
            {
                var args = ExpandArguments(command);
                if (args.Count < 2)
                    return;

                string operation = args[0];
                switch (operation)
                {
                    case "APPEND":
                        SetVariable(args[1], GetVariable(args[1]) + string.Concat(args.Skip(2)));
                        return;
                    case "PREPEND":
                        SetVariable(args[1], string.Concat(args.Skip(2)) + GetVariable(args[1]));
                        return;
                    case "CONCAT":
                        SetVariable(args[1], string.Concat(args.Skip(2)));
                        return;
                    case "JOIN":
                        if (args.Count >= 3)
                            SetVariable(args[2], string.Join(args[1], args.Skip(3)));
                        return;
                    case "TOLOWER":
                    case "TOUPPER":
                        if (args.Count >= 3)
                            SetVariable(args[2], operation == "TOLOWER" ? args[1].ToLowerInvariant() : args[1].ToUpperInvariant());
                        return;
                    case "STRIP":
                        if (args.Count >= 3)
                            SetVariable(args[2], args[1].Trim());
                        return;
                    case "LENGTH":
                        if (args.Count >= 3)
                            SetVariable(args[2], args[1].Length.ToString(CultureInfo.InvariantCulture));
                        return;
                    case "REPLACE":
                        // string(REPLACE <match> <replace> <out> <input>...)
                        if (args.Count >= 4 && args[1].Length > 0)
                            SetVariable(args[3], string.Concat(args.Skip(4)).Replace(args[1], args[2]));
                        return;
                    case "REGEX":
                        CommandStringRegex(command, args);
                        return;
                }
                Result.IgnoredCommands.Add("string(" + operation + ")");
            }

            private void CommandStringRegex(Command command, List<string> args)
            {
                if (args.Count < 5)
                    return;
                string mode = args[1];
                Regex regex;
                try
                {
                    regex = new Regex(args[2], RegexOptions.CultureInvariant);
                }
                catch (ArgumentException e)
                {
                    Warn(command, $"invalid regex '{args[2]}': {e.Message}");
                    return;
                }

                switch (mode)
                {
                    case "MATCH":
                        {
                            var match = regex.Match(string.Concat(args.Skip(4)));
                            SetVariable(args[3], match.Success ? match.Value : "");
                            SetMatchVariables(match);
                            return;
                        }
                    case "MATCHALL":
                        SetVariable(args[3], JoinList(regex.Matches(string.Concat(args.Skip(4))).Select(m => m.Value)));
                        return;
                    case "REPLACE":
                        {
                            // string(REGEX REPLACE <regex> <replace> <out> <input>...), with \1 style groups
                            if (args.Count < 6)
                                return;
                            string replacement = Regex.Replace(args[3].Replace("$", "$$"), @"\\([0-9])", "$${$1}");
                            SetVariable(args[4], regex.Replace(string.Concat(args.Skip(5)), replacement));
                            return;
                        }
                }
                Warn(command, $"string(REGEX {mode}) is not supported");
            }

            private void SetMatchVariables(Match match)
            {
                for (int i = 0; i < 10; ++i)
                    _scope.Variables.Remove("CMAKE_MATCH_" + i.ToString(CultureInfo.InvariantCulture));
                if (!match.Success)
                    return;
                _scope.Variables["CMAKE_MATCH_COUNT"] = (match.Groups.Count - 1).ToString(CultureInfo.InvariantCulture);
                for (int i = 0; i < match.Groups.Count && i < 10; ++i)
                    _scope.Variables["CMAKE_MATCH_" + i.ToString(CultureInfo.InvariantCulture)] = match.Groups[i].Value;
            }

            private void CommandMessage(Command command)
            {
                var args = ExpandArguments(command);
                if (args.Count > 0 && (args[0] == "FATAL_ERROR" || args[0] == "SEND_ERROR"))
                    Warn(command, "message(" + args[0] + "): " + string.Join(" ", args.Skip(1)));
            }

            #endregion

            #region Helpers

            private CMakeTarget FindTarget(Command command, List<string> args)
            {
                if (args.Count == 0)
                    return null;
                if (Result.Targets.TryGetValue(args[0], out CMakeTarget target))
                    return target;
                Warn(command, $"{command.Name}() on unknown target {args[0]}");
                return null;
            }

            private void AddSources(CMakeTarget target, IEnumerable<string> sources, Command command)
            {
                foreach (string source in sources)
                {
                    string resolved = ResolveGeneratorExpressions(source, command);
                    if (string.IsNullOrEmpty(resolved))
                        continue;
                    if (resolved.StartsWith("$<TARGET_OBJECTS:", StringComparison.Ordinal))
                        continue;
                    AddUnique(target.Sources, MakeAbsolute(resolved));
                }
            }

            private static bool IsVisibilityKeyword(string arg) => arg == "PUBLIC" || arg == "PRIVATE" || arg == "INTERFACE";

            private static void AddWithVisibility(List<string> build, List<string> forwarded, string visibility, string value)
            {
                if (visibility != "INTERFACE")
                    AddUnique(build, value);
                if (visibility != "PRIVATE")
                    AddUnique(forwarded, value);
            }

            private static void AddUnique(List<string> list, string value)
            {
                if (!list.Contains(value))
                    list.Add(value);
            }

            private void Warn(Command command, string message)
            {
                Result.Warnings.Add($"{command.Location}: {message}");
            }

            private string MakeAbsolute(string path) => MakeAbsolute(path, _scope.SourceDirectory);

            private static string MakeAbsolute(string path, string baseDirectory)
            {
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(baseDirectory, path);
                return Normalize(path);
            }

            private static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

            private static IEnumerable<string> Glob(string absolutePattern, bool recurse)
            {
                // Split into the longest directory prefix without wildcards, and the rest.
                var segments = absolutePattern.Split('/');
                int firstWild = Array.FindIndex(segments, s => s.IndexOfAny(new[] { '*', '?', '[' }) >= 0);
                if (firstWild < 0)
                    return File.Exists(absolutePattern) ? new[] { absolutePattern } : Array.Empty<string>();

                string baseDirectory = string.Join("/", segments.Take(firstWild));
                if (baseDirectory.Length == 0)
                    baseDirectory = "/";
                if (!Directory.Exists(baseDirectory))
                    return Array.Empty<string>();

                string relativePattern = string.Join("/", segments.Skip(firstWild));
                var regex = new Regex("^" + GlobToRegex(relativePattern, recurse) + "$", RegexOptions.CultureInvariant);

                return Directory.EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories)
                    .Select(f => f.Replace('\\', '/'))
                    .Where(f => regex.IsMatch(f.Substring(baseDirectory.TrimEnd('/').Length + 1)));
            }

            private static string GlobToRegex(string pattern, bool recurse)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < pattern.Length; ++i)
                {
                    char c = pattern[i];
                    if (c == '*')
                    {
                        bool doubleStar = i + 1 < pattern.Length && pattern[i + 1] == '*';
                        if (doubleStar)
                        {
                            ++i;
                            // "**/" also matches no directory at all
                            if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                            {
                                ++i;
                                sb.Append("(?:.*/)?");
                                continue;
                            }
                            sb.Append(".*");
                        }
                        else if (recurse && pattern.IndexOf('/', i) < 0)
                        {
                            // GLOB_RECURSE "dir/*.cpp" matches dir/a/b/c.cpp too
                            sb.Append(".*");
                        }
                        else
                        {
                            sb.Append("[^/]*");
                        }
                    }
                    else if (c == '?')
                    {
                        sb.Append("[^/]");
                    }
                    else
                    {
                        sb.Append(Regex.Escape(c.ToString()));
                    }
                }
                return sb.ToString();
            }

            #endregion

            #region Variables and expansion

            private string GetVariableOrNull(string name)
            {
                if (_scope.Variables.TryGetValue(name, out string value))
                    return value;
                if (_cache.TryGetValue(name, out value))
                    return value;
                return null;
            }

            private string GetVariable(string name) => GetVariableOrNull(name) ?? "";

            private bool IsDefined(string name) => _scope.Variables.ContainsKey(name) || _cache.ContainsKey(name);

            private void SetVariable(string name, string value) => _scope.Variables[name] = value;

            private void RestoreVariable(string name, string previous)
            {
                if (previous == null)
                    _scope.Variables.Remove(name);
                else
                    _scope.Variables[name] = previous;
            }

            private static string JoinList(IEnumerable<string> items) => string.Join(";", items);

            internal static IEnumerable<string> SplitList(string value)
            {
                if (string.IsNullOrEmpty(value))
                    yield break;

                var sb = new StringBuilder();
                int bracketDepth = 0;
                for (int i = 0; i < value.Length; ++i)
                {
                    char c = value[i];
                    if (c == '\\' && i + 1 < value.Length && value[i + 1] == ';')
                    {
                        sb.Append(';');
                        ++i;
                        continue;
                    }
                    // ';' inside generator expressions does not split the list
                    if (c == '<' && i > 0 && value[i - 1] == '$')
                        ++bracketDepth;
                    else if (c == '>' && bracketDepth > 0)
                        --bracketDepth;

                    if (c == ';' && bracketDepth == 0)
                    {
                        if (sb.Length > 0)
                            yield return sb.ToString();
                        sb.Clear();
                        continue;
                    }
                    sb.Append(c);
                }
                if (sb.Length > 0)
                    yield return sb.ToString();
            }

            private List<string> ExpandArguments(Command command)
            {
                var result = new List<string>();
                foreach (var argument in command.Arguments)
                {
                    switch (argument.Kind)
                    {
                        case ArgumentKind.Bracket:
                            result.Add(argument.Text);
                            break;
                        case ArgumentKind.Quoted:
                            result.Add(Unescape(ExpandVariables(argument.Text)));
                            break;
                        default:
                            result.AddRange(SplitList(ExpandVariables(argument.Text)).Select(Unescape));
                            break;
                    }
                }
                return result;
            }

            private static string Unescape(string value) => value.Replace("\\;", ";").Replace("\\$", "$");

            private string ExpandVariables(string text)
            {
                if (text.IndexOf('$') < 0)
                    return text;

                int pos = 0;
                return ExpandUntil(text, ref pos, null);
            }

            // Expands ${...} and $ENV{...} until 'stop' (or the end of the text) is found.
            private string ExpandUntil(string text, ref int pos, char? stop)
            {
                var sb = new StringBuilder();
                while (pos < text.Length)
                {
                    char c = text[pos];
                    if (stop.HasValue && c == stop.Value)
                        return sb.ToString();

                    if (c == '\\' && pos + 1 < text.Length && text[pos + 1] == '$')
                    {
                        sb.Append("\\$");
                        pos += 2;
                        continue;
                    }

                    if (c == '$' && pos + 1 < text.Length && text[pos + 1] == '{')
                    {
                        pos += 2;
                        string name = ExpandUntil(text, ref pos, '}');
                        ++pos; // '}'
                        sb.Append(GetVariable(name));
                        continue;
                    }

                    if (c == '$' && string.CompareOrdinal(text, pos, "$ENV{", 0, 5) == 0)
                    {
                        pos += 5;
                        string name = ExpandUntil(text, ref pos, '}');
                        ++pos;
                        sb.Append(Environment.GetEnvironmentVariable(name) ?? "");
                        continue;
                    }

                    sb.Append(c);
                    ++pos;
                }
                return sb.ToString();
            }

            /// <summary>
            /// Keeps the BUILD_INTERFACE part of generator expressions, drops INSTALL_INTERFACE,
            /// and drops (with a warning) every other generator expression, since they depend on
            /// the configuration or the compiler.
            /// </summary>
            private string ResolveGeneratorExpressions(string value, Command command)
            {
                if (value.IndexOf("$<", StringComparison.Ordinal) < 0)
                    return value;

                var sb = new StringBuilder();
                int pos = 0;
                while (pos < value.Length)
                {
                    int start = value.IndexOf("$<", pos, StringComparison.Ordinal);
                    if (start < 0)
                    {
                        sb.Append(value, pos, value.Length - pos);
                        break;
                    }
                    sb.Append(value, pos, start - pos);

                    int depth = 0;
                    int end = start;
                    for (; end < value.Length; ++end)
                    {
                        if (value[end] == '<' && end > 0 && value[end - 1] == '$')
                            ++depth;
                        else if (value[end] == '>' && --depth == 0)
                            break;
                    }
                    if (end >= value.Length)
                    {
                        Warn(command, $"unterminated generator expression in '{value}'");
                        return "";
                    }

                    string inner = value.Substring(start + 2, end - start - 2);
                    int colon = inner.IndexOf(':');
                    string name = colon >= 0 ? inner.Substring(0, colon) : inner;
                    string content = colon >= 0 ? inner.Substring(colon + 1) : "";

                    if (name == "BUILD_INTERFACE" || name == "BUILD_LOCAL_INTERFACE")
                        sb.Append(ResolveGeneratorExpressions(content, command));
                    else if (name == "INSTALL_INTERFACE")
                    {
                        // dropped
                    }
                    else
                        Warn(command, $"generator expression $<{inner}> is not supported and was ignored");

                    pos = end + 1;
                }
                return sb.ToString();
            }

            #endregion

            #region Conditions

            private struct ConditionToken
            {
                public string Text;
                public bool Quoted;
            }

            private bool EvaluateCondition(Command command)
            {
                // Like CMake, ${} references are expanded first, then the remaining words are
                // looked up as variables. Quoted arguments are never looked up.
                var tokens = new List<ConditionToken>();
                foreach (var argument in command.Arguments)
                {
                    if (argument.Kind == ArgumentKind.Unquoted)
                    {
                        foreach (string item in SplitList(ExpandVariables(argument.Text)))
                            tokens.Add(new ConditionToken { Text = Unescape(item), Quoted = false });
                    }
                    else
                    {
                        string text = argument.Kind == ArgumentKind.Quoted ? Unescape(ExpandVariables(argument.Text)) : argument.Text;
                        tokens.Add(new ConditionToken { Text = text, Quoted = true });
                    }
                }

                if (tokens.Count == 0)
                    return false;

                int pos = 0;
                try
                {
                    bool result = ParseOr(tokens, ref pos);
                    return result;
                }
                catch (Exception e) when (!(e is Error))
                {
                    Warn(command, $"could not evaluate condition: {e.Message}");
                    return false;
                }
            }

            private bool ParseOr(List<ConditionToken> tokens, ref int pos)
            {
                bool value = ParseAnd(tokens, ref pos);
                while (pos < tokens.Count && !tokens[pos].Quoted && tokens[pos].Text == "OR")
                {
                    ++pos;
                    bool right = ParseAnd(tokens, ref pos);
                    value = value || right;
                }
                return value;
            }

            private bool ParseAnd(List<ConditionToken> tokens, ref int pos)
            {
                bool value = ParseNot(tokens, ref pos);
                while (pos < tokens.Count && !tokens[pos].Quoted && tokens[pos].Text == "AND")
                {
                    ++pos;
                    bool right = ParseNot(tokens, ref pos);
                    value = value && right;
                }
                return value;
            }

            private bool ParseNot(List<ConditionToken> tokens, ref int pos)
            {
                if (pos < tokens.Count && !tokens[pos].Quoted && tokens[pos].Text == "NOT")
                {
                    ++pos;
                    return !ParseNot(tokens, ref pos);
                }
                return ParsePrimary(tokens, ref pos);
            }

            private static readonly HashSet<string> s_unaryOperators = new HashSet<string>
            {
                "DEFINED", "EXISTS", "COMMAND", "TARGET", "POLICY", "TEST", "IS_DIRECTORY", "IS_ABSOLUTE", "IS_SYMLINK",
            };

            private static readonly HashSet<string> s_binaryOperators = new HashSet<string>
            {
                "STREQUAL", "STRLESS", "STRGREATER", "STRLESS_EQUAL", "STRGREATER_EQUAL",
                "EQUAL", "LESS", "GREATER", "LESS_EQUAL", "GREATER_EQUAL",
                "VERSION_EQUAL", "VERSION_LESS", "VERSION_GREATER", "VERSION_LESS_EQUAL", "VERSION_GREATER_EQUAL",
                "MATCHES", "IN_LIST", "IS_NEWER_THAN", "PATH_EQUAL",
            };

            private bool ParsePrimary(List<ConditionToken> tokens, ref int pos)
            {
                if (pos >= tokens.Count)
                    throw new InvalidOperationException("unexpected end of condition");

                ConditionToken token = tokens[pos];

                if (!token.Quoted && token.Text == "(")
                {
                    ++pos;
                    bool inner = ParseOr(tokens, ref pos);
                    if (pos < tokens.Count && tokens[pos].Text == ")")
                        ++pos;
                    return inner;
                }

                if (!token.Quoted && s_unaryOperators.Contains(token.Text) && pos + 1 < tokens.Count)
                {
                    ++pos;
                    string operand = tokens[pos++].Text;
                    switch (token.Text)
                    {
                        case "DEFINED":
                            if (operand.StartsWith("ENV{", StringComparison.Ordinal))
                                return Environment.GetEnvironmentVariable(operand.Substring(4).TrimEnd('}')) != null;
                            if (operand.StartsWith("CACHE{", StringComparison.Ordinal))
                                return _cache.ContainsKey(operand.Substring(6).TrimEnd('}'));
                            return IsDefined(operand);
                        case "EXISTS": return File.Exists(operand) || Directory.Exists(operand);
                        case "IS_DIRECTORY": return Directory.Exists(operand);
                        case "IS_ABSOLUTE": return Path.IsPathRooted(operand);
                        case "TARGET": return Result.Targets.ContainsKey(operand);
                        case "COMMAND": return _userFunctions.ContainsKey(operand.ToLowerInvariant()) || s_builtinCommands.Contains(operand.ToLowerInvariant());
                        case "POLICY": return true;
                        default: return false;
                    }
                }

                ++pos;

                if (pos < tokens.Count && !tokens[pos].Quoted && s_binaryOperators.Contains(tokens[pos].Text) && pos + 1 < tokens.Count)
                {
                    string op = tokens[pos].Text;
                    ConditionToken rightToken = tokens[pos + 1];
                    pos += 2;
                    string left = ValueOf(token);
                    string right = ValueOf(rightToken);
                    return EvaluateBinary(op, left, right, rightToken);
                }

                return IsTruthy(token);
            }

            // Operands of binary operators are variable names when such a variable exists.
            private string ValueOf(ConditionToken token)
            {
                if (!token.Quoted && IsDefined(token.Text))
                    return GetVariable(token.Text);
                return token.Text;
            }

            private bool EvaluateBinary(string op, string left, string right, ConditionToken rightToken)
            {
                switch (op)
                {
                    case "STREQUAL": return left == right;
                    case "STRLESS": return string.CompareOrdinal(left, right) < 0;
                    case "STRGREATER": return string.CompareOrdinal(left, right) > 0;
                    case "STRLESS_EQUAL": return string.CompareOrdinal(left, right) <= 0;
                    case "STRGREATER_EQUAL": return string.CompareOrdinal(left, right) >= 0;
                    case "PATH_EQUAL": return Normalize(left) == Normalize(right);
                    case "MATCHES":
                        {
                            var match = Regex.Match(left, right, RegexOptions.CultureInvariant);
                            SetMatchVariables(match);
                            return match.Success;
                        }
                    case "IN_LIST":
                        return SplitList(GetVariable(rightToken.Text)).Contains(left);
                    case "IS_NEWER_THAN":
                        return true;
                }

                if (op.StartsWith("VERSION_", StringComparison.Ordinal))
                {
                    int cmp = CompareVersions(left, right);
                    switch (op)
                    {
                        case "VERSION_EQUAL": return cmp == 0;
                        case "VERSION_LESS": return cmp < 0;
                        case "VERSION_GREATER": return cmp > 0;
                        case "VERSION_LESS_EQUAL": return cmp <= 0;
                        case "VERSION_GREATER_EQUAL": return cmp >= 0;
                    }
                }

                if (!double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double l) ||
                    !double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out double r))
                    return false;

                switch (op)
                {
                    case "EQUAL": return l == r;
                    case "LESS": return l < r;
                    case "GREATER": return l > r;
                    case "LESS_EQUAL": return l <= r;
                    case "GREATER_EQUAL": return l >= r;
                }
                return false;
            }

            private static int CompareVersions(string left, string right)
            {
                var a = left.Split('.');
                var b = right.Split('.');
                for (int i = 0; i < Math.Max(a.Length, b.Length); ++i)
                {
                    int x = i < a.Length && int.TryParse(a[i], out int xv) ? xv : 0;
                    int y = i < b.Length && int.TryParse(b[i], out int yv) ? yv : 0;
                    if (x != y)
                        return x.CompareTo(y);
                }
                return 0;
            }

            private bool IsTruthy(ConditionToken token)
            {
                string text = token.Text;
                if (IsTrueConstant(text))
                    return true;
                if (IsFalseConstant(text))
                    return false;
                if (token.Quoted)
                    return false;
                // Not a constant: it names a variable, true if its value is not a false constant.
                string value = GetVariableOrNull(text);
                return value != null && !IsFalseConstant(value);
            }

            private static bool IsTrue(string value) => !IsFalseConstant(value);

            private static bool IsTrueConstant(string value)
            {
                switch (value.ToUpperInvariant())
                {
                    case "1":
                    case "ON":
                    case "YES":
                    case "TRUE":
                    case "Y":
                        return true;
                }
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number != 0;
            }

            private static bool IsFalseConstant(string value)
            {
                if (string.IsNullOrEmpty(value))
                    return true;
                string upper = value.ToUpperInvariant();
                switch (upper)
                {
                    case "0":
                    case "OFF":
                    case "NO":
                    case "FALSE":
                    case "N":
                    case "IGNORE":
                    case "NOTFOUND":
                        return true;
                }
                if (upper.EndsWith("-NOTFOUND", StringComparison.Ordinal))
                    return true;
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number == 0;
            }

            #endregion
        }

        #endregion
    }
}
