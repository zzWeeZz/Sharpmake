// Copyright (c) Ubisoft. All Rights Reserved.
// Licensed under the Apache 2.0 License. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sharpmake
{
    /// <summary>
    /// How <see cref="CMakeFetchContent"/> gets the sources.
    /// </summary>
    public enum CMakeFetchMethod
    {
        /// <summary>
        /// GitHub repositories and .zip/.tar.gz URLs are downloaded as an archive (no git needed,
        /// no history). Any other URL is cloned with git.
        /// </summary>
        Auto,

        /// <summary>
        /// Always download an archive. For a GitHub repository URL, the archive of the ref.
        /// </summary>
        Archive,

        /// <summary>
        /// Always clone with the git command line (needed for submodules and private repositories,
        /// as it uses your git credentials).
        /// </summary>
        Git,
    }

    /// <summary>
    /// What to download: the equivalent of FetchContent_Declare in CMake.
    /// </summary>
    public class CMakeFetchRequest
    {
        /// <summary>
        /// A repository URL (https://github.com/fmtlib/fmt, any git URL) or an archive URL (.zip, .tar.gz).
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        /// Tag, branch or commit to fetch (like GIT_TAG). Ignored for an archive URL.
        /// A tag or a full commit hash pins the version; a branch is fetched once and then reused
        /// from the cache until <see cref="CMakeFetchContent.UpdateMode"/> asks for an update.
        /// </summary>
        public string Ref { get; set; }

        /// <summary>
        /// Optional SHA-256 of the downloaded archive (like URL_HASH SHA256=...). A mismatch is an error.
        /// </summary>
        public string Sha256 { get; set; }

        public CMakeFetchMethod Method { get; set; } = CMakeFetchMethod.Auto;

        /// <summary>
        /// Also fetch git submodules. Implies <see cref="CMakeFetchMethod.Git"/>, since archives don't hold them.
        /// </summary>
        public bool Submodules { get; set; }

        /// <summary>
        /// Folder where the sources go, one sub folder per URL and ref. Defaults to
        /// <see cref="CMakeFetchContent.CacheDirectory"/>.
        /// </summary>
        public string CacheDirectory { get; set; }
    }

    /// <summary>
    /// Downloads library sources, like CMake's FetchContent, and keeps them in a cache folder so
    /// that later runs (and offline runs) reuse them.
    /// </summary>
    public static class CMakeFetchContent
    {
        public enum Update
        {
            /// <summary>Use what is in the cache, download only what is missing (default).</summary>
            Missing,

            /// <summary>Download again everything that is not pinned to a commit hash (branches, tags, archives without hash).</summary>
            Refresh,

            /// <summary>Never download; fail if something is not in the cache.</summary>
            Offline,
        }

        /// <summary>
        /// Default folder for downloaded sources when a request has none. When null, it is a
        /// "_deps" folder next to the .sharpmake.cs file of the project asking.
        /// Can also be set with the SHARPMAKE_FETCH_DIR environment variable.
        /// </summary>
        public static string CacheDirectory { get; set; } = Environment.GetEnvironmentVariable("SHARPMAKE_FETCH_DIR");

        /// <summary>
        /// Set from the command line with /fetchupdate or /fetchoffline.
        /// </summary>
        public static Update UpdateMode { get; set; } = Update.Missing;

        /// <summary>Timeout of one download.</summary>
        public static TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(10);

        private const string StampFileName = ".sharpmake-fetch";
        private static readonly ConcurrentDictionary<string, Lazy<string>> s_fetched = new ConcurrentDictionary<string, Lazy<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex s_commitHash = new Regex("^[0-9a-fA-F]{40}([0-9a-fA-F]{24})?$", RegexOptions.Compiled);
        private static readonly Regex s_gitHubRepo = new Regex(@"^https?://github\.com/(?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(\.git)?/?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Downloads the sources if they are not in the cache yet, and returns the folder that holds them.
        /// Safe to call from several projects for the same URL and ref: it is fetched once.
        /// </summary>
        public static string Fetch(CMakeFetchRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Url))
                throw new Error("CMake fetch: Url must be set.");

            string cacheRoot = request.CacheDirectory ?? CacheDirectory;
            if (string.IsNullOrEmpty(cacheRoot))
                throw new Error("CMake fetch: no cache directory. Set CMakeFetchContent.CacheDirectory or CMakeFetchRequest.CacheDirectory.");
            cacheRoot = Path.GetFullPath(cacheRoot);

            string key = string.Join("|", cacheRoot, request.Url.Trim(), request.Ref ?? "", request.Sha256 ?? "", request.Method, request.Submodules);
            var fetch = s_fetched.GetOrAdd(key, _ => new Lazy<string>(() => FetchNow(request, cacheRoot)));
            try
            {
                return fetch.Value;
            }
            catch
            {
                // Don't remember failures: a later call may succeed.
                s_fetched.TryRemove(key, out _);
                throw;
            }
        }

        /// <summary>Forgets what was fetched by this process, so the next Fetch looks at the cache again.</summary>
        internal static void ClearProcessCache() => s_fetched.Clear();

        internal static bool IsCommitHash(string reference) => reference != null && s_commitHash.IsMatch(reference);

        internal static bool IsArchiveUrl(string url)
        {
            string path = url.Split('?', '#')[0].ToLowerInvariant();
            return path.EndsWith(".zip") || path.EndsWith(".tar.gz") || path.EndsWith(".tgz") || path.EndsWith(".tar.xz") || path.EndsWith(".tar.bz2") || path.EndsWith(".tar");
        }

        /// <summary>
        /// The archive URL GitHub serves for a ref of a repository, or null when url is not a GitHub repository.
        /// </summary>
        internal static string GetGitHubArchiveUrl(string url, string reference)
        {
            var match = s_gitHubRepo.Match(url.Trim());
            if (!match.Success || string.IsNullOrEmpty(reference))
                return null;
            string escapedRef = string.Join("/", reference.Split('/').Select(Uri.EscapeDataString));
            return $"https://github.com/{match.Groups["owner"].Value}/{match.Groups["repo"].Value}/archive/{escapedRef}.zip";
        }

        /// <summary>
        /// Name of the cache sub folder: repository name and ref, plus a hash of the URL so that
        /// two forks with the same name and tag don't collide.
        /// </summary>
        internal static string GetCacheFolderName(string url, string reference)
        {
            string path = url.Trim().Split('?', '#')[0].TrimEnd('/');
            string name = path.Substring(path.LastIndexOfAny(new[] { '/', '\\', ':' }) + 1);
            foreach (string suffix in new[] { ".git", ".zip", ".tar.gz", ".tgz", ".tar.xz", ".tar.bz2", ".tar" })
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - suffix.Length);
                    break;
                }
            }
            if (!string.IsNullOrEmpty(reference))
                name += "-" + (IsCommitHash(reference) ? reference.Substring(0, 12) : reference);
            name = Sanitize(name);

            string hash;
            using (var sha = SHA256.Create())
                hash = ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(url.Trim().ToLowerInvariant() + "|" + reference))).Substring(0, 8);
            return name + "-" + hash;
        }

        private static string Sanitize(string name)
        {
            var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_').ToArray();
            string result = new string(chars).Trim('.');
            if (result.Length > 60)
                result = result.Substring(0, 60);
            return result.Length > 0 ? result : "src";
        }

        private static string FetchNow(CMakeFetchRequest request, string cacheRoot)
        {
            string url = request.Url.Trim();
            string reference = string.IsNullOrWhiteSpace(request.Ref) ? null : request.Ref.Trim();
            string destination = Path.Combine(cacheRoot, GetCacheFolderName(url, reference));
            string description = reference == null ? url : $"{url} @ {reference}";

            bool pinned = IsCommitHash(reference) || !string.IsNullOrEmpty(request.Sha256);
            Stamp stamp = Stamp.Read(destination);
            bool cached = stamp != null && stamp.Url == url && stamp.Ref == (reference ?? "");

            if (cached && (UpdateMode != Update.Refresh || pinned))
                return destination;

            if (UpdateMode == Update.Offline)
            {
                if (cached)
                    return destination;
                throw new Error($"CMake fetch: {description} is not in the cache ({destination}) and fetching is offline (/fetchoffline).");
            }

            Log($"CMake fetch: downloading {description}");

            Directory.CreateDirectory(cacheRoot);
            string temp = Path.Combine(cacheRoot, ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            try
            {
                var newStamp = new Stamp { Url = url, Ref = reference ?? "" };

                CMakeFetchMethod method = request.Method;
                if (request.Submodules)
                    method = CMakeFetchMethod.Git;
                bool gitFallback = false;
                if (method == CMakeFetchMethod.Auto)
                {
                    bool isGitHub = !IsArchiveUrl(url) && GetGitHubArchiveUrl(url, reference) != null;
                    method = IsArchiveUrl(url) || isGitHub ? CMakeFetchMethod.Archive : CMakeFetchMethod.Git;
                    gitFallback = isGitHub && string.IsNullOrEmpty(request.Sha256);
                }

                if (method == CMakeFetchMethod.Archive)
                {
                    string archiveUrl = IsArchiveUrl(url) ? url : GetGitHubArchiveUrl(url, reference);
                    if (archiveUrl == null)
                        throw new Error($"CMake fetch: {description}: no archive URL known for this repository. Use an archive URL, or CMakeFetchMethod.Git.");
                    try
                    {
                        newStamp.Sha256 = DownloadAndExtract(archiveUrl, request.Sha256, temp);
                        newStamp.Method = "archive";
                    }
                    catch (Exception e) when (gitFallback)
                    {
                        // Some networks block archive downloads but allow git: try git before giving up.
                        Log($"CMake fetch: archive download failed ({e.Message}), trying git clone.");
                        if (Directory.Exists(temp))
                            DeleteDirectory(temp);
                        newStamp.Commit = GitClone(url, reference, false, temp);
                        newStamp.Method = "git";
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(request.Sha256))
                        throw new Error($"CMake fetch: {description}: Sha256 only applies to archives. Pin a git checkout with a commit hash instead.");
                    newStamp.Commit = GitClone(url, reference, request.Submodules, temp);
                    newStamp.Method = "git";
                }

                string sourceRoot = SingleSubDirectoryOrSelf(temp);
                newStamp.Write(sourceRoot);

                // Swap in the new folder. Another Sharpmake may have done it meanwhile: then keep its copy.
                if (Directory.Exists(destination))
                    DeleteDirectory(destination);
                try
                {
                    Directory.Move(sourceRoot, destination);
                }
                catch (IOException) when (Stamp.Read(destination) is Stamp other && other.Url == url && other.Ref == (reference ?? ""))
                {
                }

                Log($"CMake fetch: {description} -> {destination}" + (newStamp.Commit != null ? $" (commit {newStamp.Commit})" : ""));
                return destination;
            }
            catch (Exception e) when (cached && !(e is Error && e.Message.Contains("SHA-256")))
            {
                // Refreshing failed, for instance because there is no network: keep what we have.
                Warn($"CMake fetch: could not update {description} ({e.Message}), using the cached copy.");
                return destination;
            }
            catch (Exception e) when (!(e is Error))
            {
                throw new Error($"CMake fetch: could not download {description}: {e.Message}");
            }
            finally
            {
                if (Directory.Exists(temp))
                    DeleteDirectory(temp);
            }
        }

        private static string DownloadAndExtract(string archiveUrl, string expectedSha256, string temp)
        {
            Directory.CreateDirectory(temp);
            string fileName = Path.GetFileName(new Uri(archiveUrl).AbsolutePath);
            if (string.IsNullOrEmpty(fileName))
                fileName = "archive.zip";
            string archive = Path.Combine(temp, "_download_" + fileName);

            using (var client = new HttpClient { Timeout = DownloadTimeout })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Sharpmake-FetchContent");
                using (var response = client.GetAsync(archiveUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string hint = response.StatusCode == System.Net.HttpStatusCode.NotFound
                            ? " Check the URL and that the tag, branch or commit exists. For a private repository use CMakeFetchMethod.Git."
                            : "";
                        throw new Error($"CMake fetch: {archiveUrl} answered {(int)response.StatusCode} {response.ReasonPhrase}.{hint}");
                    }
                    using (var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var output = File.Create(archive))
                        input.CopyTo(output);
                }
            }

            string sha256;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(archive))
                sha256 = ToHex(sha.ComputeHash(stream));
            if (!string.IsNullOrEmpty(expectedSha256) && !string.Equals(sha256, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new Error($"CMake fetch: SHA-256 mismatch for {archiveUrl}: expected {expectedSha256.Trim()}, got {sha256}.");

            string extractTo = Path.Combine(temp, "src");
            Directory.CreateDirectory(extractTo);
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(archive, extractTo); // refuses entries that escape extractTo
            else
                Run("tar", new[] { "-xf", archive, "-C", extractTo }, temp);

            File.Delete(archive);
            return sha256;
        }

        private static string GitClone(string url, string reference, bool submodules, string temp)
        {
            string checkout = Path.Combine(temp, "src");
            Directory.CreateDirectory(temp);

            bool cloned = false;
            if (!IsCommitHash(reference))
            {
                var args = new List<string> { "clone", "--depth", "1", "-c", "advice.detachedHead=false" };
                if (reference != null)
                    args.AddRange(new[] { "--branch", reference });
                if (submodules)
                    args.AddRange(new[] { "--recurse-submodules", "--shallow-submodules" });
                args.AddRange(new[] { url, checkout });
                cloned = TryRun("git", args, temp, out _);
                if (!cloned && reference == null)
                    throw new Error($"CMake fetch: git clone {url} failed.");
            }

            if (!cloned)
            {
                // A commit hash (or a short one): fetch just that commit, or fall back to a full clone.
                if (Directory.Exists(checkout))
                    DeleteDirectory(checkout);
                Directory.CreateDirectory(checkout);
                Run("git", new[] { "init", "-q" }, checkout);
                Run("git", new[] { "remote", "add", "origin", url }, checkout);
                if (!TryRun("git", new[] { "fetch", "-q", "--depth", "1", "origin", reference }, checkout, out _))
                    Run("git", new[] { "fetch", "-q", "--tags", "origin" }, checkout);
                if (!TryRun("git", new[] { "checkout", "-q", "FETCH_HEAD" }, checkout, out _) || !IsAt(checkout, reference))
                    Run("git", new[] { "checkout", "-q", reference }, checkout);
                if (submodules)
                    Run("git", new[] { "submodule", "update", "-q", "--init", "--recursive", "--depth", "1" }, checkout);
            }

            string commit = Run("git", new[] { "rev-parse", "HEAD" }, checkout).Trim();
            if (IsCommitHash(reference) && !commit.Equals(reference, StringComparison.OrdinalIgnoreCase))
                throw new Error($"CMake fetch: {url}: checked out {commit}, expected {reference}.");
            return commit;
        }

        private static bool IsAt(string checkout, string reference)
        {
            if (!TryRun("git", new[] { "rev-parse", "HEAD" }, checkout, out string head))
                return false;
            return head.Trim().StartsWith(reference, StringComparison.OrdinalIgnoreCase);
        }

        private static string Run(string fileName, IEnumerable<string> args, string workingDirectory)
        {
            if (!TryRun(fileName, args, workingDirectory, out string output))
                throw new Error($"CMake fetch: '{fileName} {string.Join(" ", args)}' failed:{Environment.NewLine}{output}");
            return output;
        }

        private static bool TryRun(string fileName, IEnumerable<string> args, string workingDirectory, out string output)
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

            Process process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                throw new Error($"CMake fetch: could not run '{fileName}' ({e.Message}). Is it installed and in the PATH?");
            }

            using (process)
            {
                var stderr = process.StandardError.ReadToEndAsync();
                string stdout = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                output = stdout + stderr.Result;
                return process.ExitCode == 0;
            }
        }

        /// <summary>
        /// Archives usually hold one top folder (fmt-11.0.2/...): use it as the source root.
        /// </summary>
        private static string SingleSubDirectoryOrSelf(string temp)
        {
            string src = Path.Combine(temp, "src");
            var entries = Directory.GetFileSystemEntries(src);
            if (entries.Length == 1 && Directory.Exists(entries[0]) && !Path.GetFileName(entries[0]).Equals(".git", StringComparison.OrdinalIgnoreCase))
                return entries[0];
            return src;
        }

        private static void DeleteDirectory(string path)
        {
            // git marks its object files read only, which Directory.Delete refuses on Windows.
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, true);
        }

        private static string ToHex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        private static void Log(string message) => Builder.Instance?.LogWriteLine(message);

        private static void Warn(string message) => Builder.Instance?.LogWarningLine(message);

        /// <summary>
        /// Written in the fetched folder once it is complete. A folder without it is not trusted.
        /// </summary>
        private class Stamp
        {
            public string Url;
            public string Ref;
            public string Method;
            public string Commit;
            public string Sha256;

            public static Stamp Read(string directory)
            {
                string path = Path.Combine(directory, StampFileName);
                if (!File.Exists(path))
                    return null;
                var values = File.ReadAllLines(path)
                    .Select(l => l.Split(new[] { '=' }, 2))
                    .Where(p => p.Length == 2)
                    .GroupBy(p => p[0].Trim())
                    .ToDictionary(g => g.Key, g => g.First()[1].Trim());
                string Get(string k) => values.TryGetValue(k, out string v) ? v : null;
                return new Stamp { Url = Get("url"), Ref = Get("ref") ?? "", Method = Get("method"), Commit = Get("commit"), Sha256 = Get("sha256") };
            }

            public void Write(string directory)
            {
                var lines = new List<string>
                {
                    "# Written by Sharpmake when this folder was fetched. Delete the folder to fetch it again.",
                    "url=" + Url,
                    "ref=" + Ref,
                    "method=" + Method,
                };
                if (Commit != null)
                    lines.Add("commit=" + Commit);
                if (Sha256 != null)
                    lines.Add("sha256=" + Sha256);
                File.WriteAllLines(Path.Combine(directory, StampFileName), lines);
            }
        }
    }
}
