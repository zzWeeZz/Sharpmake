// Copyright (c) Ubisoft. All Rights Reserved.
// Licensed under the Apache 2.0 License. See LICENSE.md in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using NUnit.Framework;

namespace Sharpmake.UnitTests
{
    [TestFixture]
    public class CMakeFetchContentTest
    {
        private string _root;
        private string _cache;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "sharpmake_fetch_" + Guid.NewGuid().ToString("N"));
            _cache = Path.Combine(_root, "cache");
            Directory.CreateDirectory(_root);
            CMakeFetchContent.ClearProcessCache();
            CMakeFetchContent.UpdateMode = CMakeFetchContent.Update.Missing;
        }

        [TearDown]
        public void TearDown()
        {
            CMakeFetchContent.ClearProcessCache();
            CMakeFetchContent.UpdateMode = CMakeFetchContent.Update.Missing;
            if (Directory.Exists(_root))
            {
                foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_root, true);
            }
        }

        [Test]
        public void GitHubArchiveUrl()
        {
            Assert.AreEqual("https://github.com/fmtlib/fmt/archive/11.0.2.zip", CMakeFetchContent.GetGitHubArchiveUrl("https://github.com/fmtlib/fmt", "11.0.2"));
            Assert.AreEqual("https://github.com/fmtlib/fmt/archive/11.0.2.zip", CMakeFetchContent.GetGitHubArchiveUrl("https://github.com/fmtlib/fmt.git", "11.0.2"));
            Assert.AreEqual("https://github.com/a/b/archive/release/v1.zip", CMakeFetchContent.GetGitHubArchiveUrl("https://github.com/a/b/", "release/v1"));
            Assert.IsNull(CMakeFetchContent.GetGitHubArchiveUrl("https://gitlab.com/a/b", "v1"));
            Assert.IsNull(CMakeFetchContent.GetGitHubArchiveUrl("https://github.com/a/b", null));
        }

        [Test]
        public void CacheFolderName()
        {
            string name = CMakeFetchContent.GetCacheFolderName("https://github.com/fmtlib/fmt.git", "11.0.2");
            StringAssert.StartsWith("fmt-11.0.2-", name);
            Assert.AreNotEqual(name, CMakeFetchContent.GetCacheFolderName("https://github.com/someone/fmt", "11.0.2"));
            StringAssert.StartsWith("lib-0123456789ab-", CMakeFetchContent.GetCacheFolderName("https://x.org/lib.git", "0123456789abcdef0123456789abcdef01234567"));
            StringAssert.StartsWith("b-feature_x-", CMakeFetchContent.GetCacheFolderName("https://x.org/a/b", "feature/x"));
            Assert.IsTrue(CMakeFetchContent.IsCommitHash("0123456789abcdef0123456789abcdef01234567"));
            Assert.IsFalse(CMakeFetchContent.IsCommitHash("v1.2.3"));
        }

        private static string Git(string directory, params string[] args)
        {
            var startInfo = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string arg in new[] { "-c", "user.name=test", "-c", "user.email=test@example.com", "-c", "init.defaultBranch=main", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false" }.Concat(args))
                startInfo.ArgumentList.Add(arg);
            using (var process = Process.Start(startInfo))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    Assert.Fail($"git {string.Join(" ", args)} failed: {error}");
                return output.Trim();
            }
        }

        private string MakeRepository()
        {
            string repo = Path.Combine(_root, "repo");
            Directory.CreateDirectory(repo);
            Git(repo, "init", "-q");
            File.WriteAllText(Path.Combine(repo, "CMakeLists.txt"), "add_library(lib lib.cpp)\n");
            File.WriteAllText(Path.Combine(repo, "lib.cpp"), "// v1\n");
            Git(repo, "add", ".");
            Git(repo, "commit", "-q", "-m", "v1");
            Git(repo, "tag", "v1");
            File.WriteAllText(Path.Combine(repo, "lib.cpp"), "// v2\n");
            Git(repo, "commit", "-q", "-am", "v2");
            return repo;
        }

        [Test]
        public void GitTagIsFetchedOnceThenReusedOffline()
        {
            string repo = MakeRepository();
            var request = new CMakeFetchRequest { Url = repo, Ref = "v1", CacheDirectory = _cache };

            string fetched = CMakeFetchContent.Fetch(request);
            Assert.AreEqual("// v1\n", File.ReadAllText(Path.Combine(fetched, "lib.cpp")));
            Assert.IsTrue(File.Exists(Path.Combine(fetched, ".sharpmake-fetch")));

            // A new run, with the repository gone and fetching offline: the cache is used.
            CMakeFetchContent.ClearProcessCache();
            Directory.Move(repo, repo + "_moved");
            CMakeFetchContent.UpdateMode = CMakeFetchContent.Update.Offline;
            Assert.AreEqual(fetched, CMakeFetchContent.Fetch(request));

            // Something not in the cache can't be fetched offline.
            CMakeFetchContent.ClearProcessCache();
            Assert.Throws<Error>(() => CMakeFetchContent.Fetch(new CMakeFetchRequest { Url = repo + "_moved", Ref = "main", CacheDirectory = _cache }));
        }

        [Test]
        public void GitBranchIsRefreshedOnlyOnUpdate()
        {
            string repo = MakeRepository();
            var request = new CMakeFetchRequest { Url = repo, Ref = "main", CacheDirectory = _cache };

            string fetched = CMakeFetchContent.Fetch(request);
            Assert.AreEqual("// v2\n", File.ReadAllText(Path.Combine(fetched, "lib.cpp")));

            File.WriteAllText(Path.Combine(repo, "lib.cpp"), "// v3\n");
            Git(repo, "commit", "-q", "-am", "v3");

            CMakeFetchContent.ClearProcessCache();
            Assert.AreEqual("// v2\n", File.ReadAllText(Path.Combine(CMakeFetchContent.Fetch(request), "lib.cpp")), "cached copy is reused");

            CMakeFetchContent.ClearProcessCache();
            CMakeFetchContent.UpdateMode = CMakeFetchContent.Update.Refresh;
            Assert.AreEqual("// v3\n", File.ReadAllText(Path.Combine(CMakeFetchContent.Fetch(request), "lib.cpp")), "/fetchupdate downloads again");

            // Refreshing without the repository keeps the cached copy.
            CMakeFetchContent.ClearProcessCache();
            Directory.Move(repo, repo + "_moved");
            Assert.AreEqual("// v3\n", File.ReadAllText(Path.Combine(CMakeFetchContent.Fetch(request), "lib.cpp")));
        }

        [Test]
        public void GitCommitHashIsPinned()
        {
            string repo = MakeRepository();
            string v1 = Git(repo, "rev-parse", "v1");

            string fetched = CMakeFetchContent.Fetch(new CMakeFetchRequest { Url = repo, Ref = v1, CacheDirectory = _cache });
            Assert.AreEqual("// v1\n", File.ReadAllText(Path.Combine(fetched, "lib.cpp")));
            StringAssert.Contains("commit=" + v1, File.ReadAllText(Path.Combine(fetched, ".sharpmake-fetch")));
        }

        [Test]
        public void ArchiveIsDownloadedAndChecked()
        {
            string content = Path.Combine(_root, "content", "lib-1.0");
            Directory.CreateDirectory(content);
            File.WriteAllText(Path.Combine(content, "CMakeLists.txt"), "add_library(lib lib.cpp)\n");
            File.WriteAllText(Path.Combine(content, "lib.cpp"), "// zip\n");
            string zip = Path.Combine(_root, "lib-1.0.zip");
            ZipFile.CreateFromDirectory(Path.GetDirectoryName(content), zip);
            byte[] bytes = File.ReadAllBytes(zip);
            string sha256;
            using (var sha = SHA256.Create())
                sha256 = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));

            using (var server = new ZipServer(bytes))
            {
                string url = server.Url + "lib-1.0.zip";

                var wrongHash = new CMakeFetchRequest { Url = url, Sha256 = new string('0', 64), CacheDirectory = _cache };
                var error = Assert.Throws<Error>(() => CMakeFetchContent.Fetch(wrongHash));
                StringAssert.Contains("SHA-256", error.Message);

                string fetched = CMakeFetchContent.Fetch(new CMakeFetchRequest { Url = url, Sha256 = sha256, CacheDirectory = _cache });
                Assert.AreEqual("// zip\n", File.ReadAllText(Path.Combine(fetched, "lib.cpp")), "the single top folder of the archive is the source root");
                Assert.AreEqual(1, Directory.GetDirectories(_cache).Length, "no temporary folder left behind");

                var target = CMakeListsReader.Read(fetched).GetTarget("lib");
                Assert.AreEqual(1, target.Sources.Count);
            }
        }

        /// <summary>Serves one file over http on localhost.</summary>
        private sealed class ZipServer : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();
            private readonly Thread _thread;

            public string Url { get; }

            public ZipServer(byte[] body)
            {
                var socket = new TcpListener(IPAddress.Loopback, 0);
                socket.Start();
                int port = ((IPEndPoint)socket.LocalEndpoint).Port;
                socket.Stop();

                Url = $"http://localhost:{port}/";
                _listener.Prefixes.Add(Url);
                _listener.Start();
                _thread = new Thread(() =>
                {
                    try
                    {
                        while (_listener.IsListening)
                        {
                            var context = _listener.GetContext();
                            context.Response.ContentType = "application/zip";
                            context.Response.OutputStream.Write(body, 0, body.Length);
                            context.Response.Close();
                        }
                    }
                    catch (Exception) when (!_listener.IsListening)
                    {
                    }
                    catch (HttpListenerException)
                    {
                    }
                }) { IsBackground = true };
                _thread.Start();
            }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
            }
        }
    }
}
