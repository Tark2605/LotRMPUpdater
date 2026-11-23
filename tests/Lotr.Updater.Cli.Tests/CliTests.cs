using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Core.Game;
using Lotr.Updater.Core.Game.Linux;
using Lotr.Updater.Core.Net;
using Lotr.Updater.Testing;
using Xunit;

namespace Lotr.Updater.Cli.Tests
{
    /// <summary>Temp sandbox for one CLI run series: a fake release served on localhost, a fake game, an updater home.</summary>
    public sealed class CliSandbox : IDisposable
    {
        public static readonly byte[] CorePack = Random(300_000, 7);
        public static readonly byte[] EventDll = Random(40_000, 8);

        public CliSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "lotr-cli-tests", Guid.NewGuid().ToString("N").Substring(0, 10));
            ServerFiles = Path.Combine(Root, "server", "files");
            Releases = Path.Combine(ServerFiles, "releases");
            Directory.CreateDirectory(Releases);
            GameRoot = FakeGame.Create(Path.Combine(Root, "game", "steamapps", "common", "Mount & Blade II Bannerlord"));
            Home = Path.Combine(Root, "home");
            Key = ReleaseBuilder.NewTestKey();
            KeyFile = Path.Combine(Root, "test-key.pub.pem");
            File.WriteAllText(KeyFile, ReleaseBuilder.PublicPem(Key));
            Server = new TestHttpServer(ServerFiles);
        }

        public string Root { get; }
        public string ServerFiles { get; }
        public string Releases { get; }
        public string GameRoot { get; }
        public string Home { get; }
        public ECDsa Key { get; }
        public string KeyFile { get; }
        public TestHttpServer Server { get; }
        public FakeProbe Processes { get; } = new FakeProbe();

        public static byte[] Random(int size, int seed)
        {
            var b = new byte[size];
            new Random(seed).NextBytes(b);
            return b;
        }

        /// <summary>Event module + Core module (the Core one goes through the download queue).</summary>
        public ReleaseBuilder Publish(string label = "r1", string eventXml = "<Items/>")
        {
            var r = new ReleaseBuilder(label) { SigningKey = Key };
            r.AddModule("LotRMPEvent", "LotR MP Event", "v1.0.2.7", "LotRMPEvent").SubModule()
                .File("bin/Win64_Shipping_Client/LotRMPEvent.dll", EventDll)
                .File("ModuleData/items.xml", eventXml);
            r.AddModule("LotRMPCore", "LotRMPCore", "v1.4.7.9", "LotRMPCore", "core").SubModule()
                .File("AssetPackages/big.tpac", CorePack);
            r.WriteTo(Releases);
            return r;
        }

        /// <summary>Runs the CLI to completion (the tests stay synchronous: it has its own async work inside).</summary>
        public static int Exec(string[] args, CliHost host) => CliApp.RunAsync(args, host).GetAwaiter().GetResult();

        public string ModuleFile(string folder, string rel) => Path.Combine(GameRoot, "Modules", folder, rel.Replace('/', Path.DirectorySeparatorChar));

        public string[] Dev(params string[] args) =>
            args.Concat(new[] { "--dev", "--server", Server.BaseUrl, "--home", Home, "--game", GameRoot, "--trust", KeyFile }).ToArray();

        public CliRun Run(string[] args, string input = "", Action<CliHost> configure = null)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var host = new CliHost
            {
                In = new StringReader(input),
                Out = o,
                Err = e,
                Terminal = false,
                Processes = Processes,
                Retry = new RetryPolicy { Delay = (t, ct) => Task.CompletedTask },
                QueueTimeout = TimeSpan.FromMilliseconds(500),
                QueueRetryDelay = TimeSpan.FromMilliseconds(10),
                LaunchGame = () => "Starting Bannerlord (test).",
            };
            configure?.Invoke(host);
            int code = CliApp.RunAsync(args, host).GetAwaiter().GetResult();
            return new CliRun(code, o.ToString(), e.ToString());
        }

        public void Dispose()
        {
            Server.Dispose();
            Key.Dispose();
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    public sealed class CliRun
    {
        public CliRun(int code, string stdout, string stderr)
        {
            Code = code;
            Out = stdout;
            Err = stderr;
        }

        public int Code { get; }
        public string Out { get; }
        public string Err { get; }
        public override string ToString() => "exit " + Code + "\n--- out\n" + Out + "\n--- err\n" + Err;
    }

    public sealed class FakeProbe : IProcessProbe
    {
        public List<RunningProcessInfo> Running { get; } = new List<RunningProcessInfo>();
        public IReadOnlyList<RunningProcessInfo> RunningGameProcesses() => Running;
    }

    public class CliInstallTests
    {
        [Fact]
        public void A_fresh_install_then_a_second_run_with_nothing_to_do()
        {
            using (var s = new CliSandbox())
            {
                var r = s.Publish();
                var first = s.Run(s.Dev("install", "--yes"));
                Assert.True(first.Code == ExitCodes.Ok, first.ToString());
                Assert.Contains("Release " + r.ReleaseId, first.Out);
                Assert.Contains("Done: release " + r.ReleaseId + " is installed.", first.Out);
                Assert.Contains("tick: ", first.Out);
                Assert.Equal(CliSandbox.CorePack, File.ReadAllBytes(s.ModuleFile("LotRMPCore", "AssetPackages/big.tpac")));
                Assert.Equal(CliSandbox.EventDll, File.ReadAllBytes(s.ModuleFile("LotRMPEvent", "bin/Win64_Shipping_Client/LotRMPEvent.dll")));
                Assert.Equal(1, s.Server.QueueSlotsGranted);
                Assert.Equal(1, s.Server.QueueSlotsReleased);

                var second = s.Run(s.Dev("install", "--yes"));
                Assert.True(second.Code == ExitCodes.Ok, second.ToString());
                Assert.Contains("Nothing to do", second.Out);
                Assert.Equal(1, s.Server.QueueSlotsGranted); // nothing downloaded

                var status = s.Run(s.Dev("status"));
                Assert.True(status.Code == ExitCodes.Ok, status.ToString());
                Assert.Contains("up to date", status.Out);

                var verify = s.Run(s.Dev("verify"));
                Assert.True(verify.Code == ExitCodes.Ok, verify.ToString());
                Assert.Contains("healthy", verify.Out);
            }
        }

        [Fact]
        public void Status_says_when_an_update_is_available_and_downloads_nothing()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                var r = s.Run(s.Dev("status"));
                Assert.True(r.Code == ExitCodes.UpdateAvailable, r.ToString());
                Assert.Contains("lotr-updater install", r.Out);
                Assert.False(Directory.Exists(Path.Combine(s.GameRoot, "Modules", "LotRMPEvent")));
                Assert.Equal(0, s.Server.QueueSlotRequests);
            }
        }

        [Fact]
        public void An_update_replaces_only_what_changed()
        {
            using (var s = new CliSandbox())
            {
                s.Publish("r1");
                Assert.Equal(ExitCodes.Ok, s.Run(s.Dev("install", "--yes")).Code);
                s.Publish("r2", "<Items><Item id=\"new\"/></Items>");
                var r = s.Run(s.Dev("install", "--yes", "--list"));
                Assert.True(r.Code == ExitCodes.Ok, r.ToString());
                Assert.Contains("~ ModuleData/items.xml", r.Out);
                Assert.Equal("<Items><Item id=\"new\"/></Items>", File.ReadAllText(s.ModuleFile("LotRMPEvent", "ModuleData/items.xml")));
                Assert.Equal(1, s.Server.QueueSlotsGranted); // the Core module did not change: no second slot
            }
        }

        [Fact]
        public void Answering_no_changes_nothing()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                var r = s.Run(s.Dev("install"), input: "n\n");
                Assert.True(r.Code == ExitCodes.Cancelled, r.ToString());
                Assert.Contains("Install these changes? [y/N]", r.Out);
                Assert.Contains("Nothing was changed.", r.Out);
                Assert.False(Directory.Exists(Path.Combine(s.GameRoot, "Modules", "LotRMPEvent")));
                Assert.False(Directory.Exists(Path.Combine(s.GameRoot, "Modules", "LotRMPCore")));
            }
        }

        [Fact]
        public void Answering_yes_installs()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                var r = s.Run(s.Dev("install"), input: "y\n");
                Assert.True(r.Code == ExitCodes.Ok, r.ToString());
                Assert.True(File.Exists(s.ModuleFile("LotRMPEvent", "SubModule.xml")));
            }
        }

        [Fact]
        public void A_tampered_manifest_is_refused()
        {
            using (var s = new CliSandbox())
            {
                var rel = s.Publish();
                var manifest = Path.Combine(rel.ReleaseDir, "manifest.json");
                File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("Test release", "Test releasX"));
                var r = s.Run(s.Dev("install", "--yes"));
                Assert.True(r.Code == ExitCodes.ReleaseRefused, r.ToString());
                Assert.False(Directory.Exists(Path.Combine(s.GameRoot, "Modules", "LotRMPEvent")));
            }
        }

        [Fact]
        public void A_release_signed_with_another_key_is_refused()
        {
            using (var s = new CliSandbox())
            using (var other = ReleaseBuilder.NewTestKey())
            {
                s.Publish();
                File.WriteAllText(s.KeyFile, ReleaseBuilder.PublicPem(other));
                var r = s.Run(s.Dev("status"));
                Assert.True(r.Code == ExitCodes.ReleaseRefused, r.ToString());
            }
        }

        [Theory]
        [InlineData(530)]
        [InlineData(503)]
        [InlineData(404)]
        public void A_queue_that_is_down_or_absent_does_not_stop_the_install(int status)
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                s.Server.QueueFailStatus = status;
                var r = s.Run(s.Dev("install", "--yes"));
                Assert.True(r.Code == ExitCodes.Ok, r.ToString());
                Assert.Equal(CliSandbox.CorePack, File.ReadAllBytes(s.ModuleFile("LotRMPCore", "AssetPackages/big.tpac")));
                Assert.Equal(status == 404 ? 1 : 2, s.Server.QueueSlotRequests);
                if (status != 404) Assert.Contains("downloading directly", r.Out);
            }
        }

        [Fact]
        public void A_queue_that_hangs_does_not_stop_the_install()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                s.Server.QueueHang = true;
                var r = s.Run(s.Dev("install", "--yes"));
                Assert.True(r.Code == ExitCodes.Ok, r.ToString());
                Assert.Equal(2, s.Server.QueueSlotRequests);
            }
        }

        [Fact]
        public void A_busy_queue_is_waited_for()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                s.Server.QueueBusyAnswers = 2;
                var r = s.Run(s.Dev("install", "--yes"));
                Assert.True(r.Code == ExitCodes.Ok, r.ToString());
                Assert.Equal(3, s.Server.QueueSlotRequests);
                Assert.Equal(1, s.Server.QueueSlotsGranted);
            }
        }

        [Fact]
        public void Paused_downloads_show_the_server_notice()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                Directory.CreateDirectory(Path.Combine(s.ServerFiles, "site"));
                File.WriteAllText(Path.Combine(s.ServerFiles, "site", "status.json"),
                    "{\"schema\":1,\"state\":\"event-lock\",\"title\":\"Downloads are paused\",\"message\":\"Battle night.\"}");
                s.Server.Faults = (path, hit) => path == "/status.json" ? null : new Fault { Status = 423 };
                var r = s.Run(s.Dev("install", "--yes"));
                Assert.True(r.Code == ExitCodes.DownloadsPaused, r.ToString());
                Assert.Contains("Downloads are paused", r.Err);
                Assert.Contains("lotr-updater play", r.Err);
            }
        }

        [Fact]
        public void A_server_that_is_down_is_a_network_error()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                s.Server.Faults = (path, hit) => new Fault { Status = 502 };
                var r = s.Run(s.Dev("status"));
                Assert.True(r.Code == ExitCodes.Network, r.ToString());
            }
        }

        [Fact]
        public void The_game_running_stops_the_install_before_anything_changes()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                s.Processes.Running.Add(new RunningProcessInfo { Name = "Bannerlord.exe", Pid = 4242 });
                var r = s.Run(s.Dev("install", "--yes"));
                Assert.True(r.Code == ExitCodes.GameRunning, r.ToString());
                Assert.Contains("Bannerlord", r.Err);
                Assert.False(Directory.Exists(Path.Combine(s.GameRoot, "Modules", "LotRMPEvent")));
            }
        }

        [Fact]
        public void Verify_finds_a_damaged_file_and_repair_puts_it_back()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                Assert.Equal(ExitCodes.Ok, s.Run(s.Dev("install", "--yes")).Code);
                var dll = s.ModuleFile("LotRMPEvent", "bin/Win64_Shipping_Client/LotRMPEvent.dll");
                var bytes = File.ReadAllBytes(dll);
                bytes[100] ^= 0xFF;
                File.WriteAllBytes(dll, bytes);
                File.Delete(s.ModuleFile("LotRMPEvent", "ModuleData/items.xml"));

                var verify = s.Run(s.Dev("verify"));
                Assert.True(verify.Code == ExitCodes.VerifyDifferences, verify.ToString());
                Assert.Contains("1 missing, 1 different", verify.Out);
                Assert.Contains("verify --repair", verify.Out);

                var repair = s.Run(s.Dev("verify", "--repair", "--yes"));
                Assert.True(repair.Code == ExitCodes.Ok, repair.ToString());
                Assert.Contains("Repaired: your install is healthy.", repair.Out);
                Assert.Equal(CliSandbox.EventDll, File.ReadAllBytes(dll));
                Assert.Equal("<Items/>", File.ReadAllText(s.ModuleFile("LotRMPEvent", "ModuleData/items.xml")));
            }
        }

        [Fact]
        public void A_second_updater_on_the_same_home_is_refused()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                Directory.CreateDirectory(s.Home);
                using (new FileStream(Path.Combine(s.Home, "lotr-updater.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var r = s.Run(s.Dev("status"));
                    Assert.True(r.Code == ExitCodes.AlreadyRunning, r.ToString());
                    Assert.Contains("already running", r.Err);
                }
                Assert.Equal(ExitCodes.UpdateAvailable, s.Run(s.Dev("status")).Code); // the lock is gone with its holder
            }
        }

        [Fact]
        public void The_menu_shortcut_window_stays_open_when_the_menu_cannot_start()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                Directory.CreateDirectory(s.Home);
                using (new FileStream(Path.Combine(s.Home, "lotr-updater.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    // "menu" by name = what the .desktop entry runs, in a terminal window that closes with the program.
                    var r = s.Run(s.Dev("menu"), input: "\n", configure: h => h.Terminal = true);
                    Assert.True(r.Code == ExitCodes.AlreadyRunning, r.ToString());
                    Assert.Contains("Press Enter to close this window.", r.Out);
                    // From a script (no terminal) it never waits.
                    var plain = s.Run(s.Dev("status"));
                    Assert.DoesNotContain("Press Enter", plain.Out);
                }
            }
        }

        [Fact]
        public void Cancelling_stops_with_exit_130()
        {
            using (var s = new CliSandbox())
            using (var cts = new CancellationTokenSource())
            {
                s.Publish();
                s.Server.QueueBusyAnswers = 100000;
                int slotCalls = 0;
                s.Server.OnRequest = req => { if (req.Contains("/api/queue/slot") && Interlocked.Increment(ref slotCalls) >= 3) cts.Cancel(); };
                var r = s.Run(s.Dev("install", "--yes"), configure: h => { h.Cancel = cts.Token; h.Retry = new RetryPolicy { Delay = (t, ct) => Task.Delay(TimeSpan.FromMilliseconds(20), ct) }; });
                Assert.True(r.Code == ExitCodes.Cancelled, r.ToString());
                Assert.Contains("Cancelled", r.Err);
                Assert.False(File.Exists(s.ModuleFile("LotRMPCore", "AssetPackages/big.tpac")));
            }
        }

        [Fact]
        public void Play_starts_the_game_when_up_to_date()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                Assert.Equal(ExitCodes.Ok, s.Run(s.Dev("install", "--yes")).Code);
                var r = s.Run(s.Dev("play"));
                Assert.True(r.Code == ExitCodes.Ok, r.ToString());
                Assert.Contains("Starting Bannerlord (test).", r.Out);
            }
        }

        [Fact]
        public void Play_warns_about_an_update_and_asks()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                bool launched = false;
                var r = s.Run(s.Dev("play"), input: "n\n", configure: h => h.LaunchGame = () => { launched = true; return "x"; });
                Assert.True(r.Code == ExitCodes.UpdateAvailable, r.ToString());
                Assert.False(launched);
                var r2 = s.Run(s.Dev("play"), input: "y\n", configure: h => h.LaunchGame = () => { launched = true; return "started"; });
                Assert.True(r2.Code == ExitCodes.Ok, r2.ToString());
                Assert.True(launched);
            }
        }

        [Fact]
        public void Play_explains_when_steam_cannot_be_started()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                var r = s.Run(s.Dev("play", "--yes"), configure: h => h.LaunchGame = () => null);
                Assert.NotEqual(ExitCodes.Ok, r.Code);
                Assert.Contains("Steam library", r.Err);
            }
        }

        [Fact]
        public void Support_shows_the_log_then_sends_it_and_gives_a_code()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                var no = s.Run(s.Dev("support"), input: "n\n");
                Assert.True(no.Code == ExitCodes.Cancelled, no.ToString());
                Assert.Contains("----- support log", no.Out);
                Assert.Contains("Not sent.", no.Out);
                Assert.Null(s.Server.LastSupportBody);

                var yes = s.Run(s.Dev("support", "--yes"));
                Assert.True(yes.Code == ExitCodes.Ok, yes.ToString());
                Assert.Contains("Your support code: ", yes.Out);
                Assert.NotNull(s.Server.LastSupportBody);
                Assert.DoesNotContain(Environment.UserName + Path.DirectorySeparatorChar, s.Server.LastSupportBody);
            }
        }

        [Fact]
        public void The_menu_runs_the_chosen_command_and_quits()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                var r = s.Run(s.Dev(), input: "1\n9\n6\n", configure: h => h.Terminal = true);
                Assert.True(r.Code == ExitCodes.UpdateAvailable, r.ToString()); // the last command's code
                Assert.Contains("1) Status", r.Out);
                Assert.Contains("An update is available", r.Out);
                Assert.Contains("Type a number from 1 to 6.", r.Out);
            }
        }
    }

    public class CliCommandLineTests
    {
        [Theory]
        [InlineData("frobnicate")]
        [InlineData("--frobnicate")]
        [InlineData("install", "verify")]
        [InlineData("install", "--connections", "33")]
        [InlineData("install", "--connections", "0")]
        [InlineData("install", "--connections")]
        [InlineData("install", "--channel", "Beta!")]
        [InlineData("install", "--server", "http://localhost:1/")]                 // dev option without --dev
        [InlineData("install", "--home", "/tmp/x")]
        [InlineData("install", "--dev", "--server", "https://example.com/", "--home", "h", "--game", "g")] // not loopback
        [InlineData("install", "--dev", "--server", "http://localhost:1/", "--game", "g")]               // no --home
        [InlineData("install", "--dev", "--server", "http://localhost:1/", "--home", "h")]               // no --game
        public void Bad_command_lines_are_usage_errors(params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            int code = CliSandbox.Exec(args, new CliHost { In = new StringReader(""), Out = o, Err = e });
            Assert.Equal(ExitCodes.Usage, code);
            Assert.Contains("--help", e.ToString());
        }

        [Fact]
        public void Help_and_version_need_no_server()
        {
            var o = new StringWriter();
            Assert.Equal(ExitCodes.Ok, CliSandbox.Exec(new[] { "--help" }, new CliHost { Out = o, Err = o }));
            Assert.Contains("Exit codes:", o.ToString());
            var v = new StringWriter();
            Assert.Equal(ExitCodes.Ok, CliSandbox.Exec(new[] { "version" }, new CliHost { Out = v, Err = v }));
            Assert.StartsWith("lotr-updater " + CliApp.Version, v.ToString());
        }

        [Fact]
        public void No_command_outside_a_terminal_prints_the_usage()
        {
            var o = new StringWriter();
            Assert.Equal(ExitCodes.Usage, CliSandbox.Exec(new string[0], new CliHost { In = new StringReader(""), Out = o, Err = o, Terminal = false }));
            Assert.Contains("Usage: lotr-updater", o.ToString());
        }

        [Fact]
        public void A_test_build_without_a_server_address_refuses_to_run()
        {
            // Test builds are made without -p:LotrBaseUrl: the binary must say so instead of calling a placeholder domain.
            Assert.Equal(ServerEndpoints.PlaceholderBase, ServerEndpoints.ProductionBase);
            var o = new StringWriter();
            var e = new StringWriter();
            Assert.Equal(ExitCodes.Usage, CliSandbox.Exec(new[] { "status" }, new CliHost { In = new StringReader(""), Out = o, Err = e }));
            Assert.Contains("test build without a server address", e.ToString());
        }

        [Fact]
        public void Update_is_install_and_options_are_read()
        {
            var o = CliOptions.Parse(new[] { "update", "-y", "--channel", "beta", "--connections", "3", "--plain", "--list", "--accept-rollback" });
            Assert.Null(o.Error);
            Assert.Equal("install", o.Command);
            Assert.True(o.Yes && o.Plain && o.ListFiles && o.AcceptRollback);
            Assert.Equal("beta", o.Channel);
            Assert.Equal(3, o.Connections);
            Assert.False(o.Dev);
        }

        [Theory]
        [InlineData("/home/deck/Downloads/lotr-updater-0.1.0/lotr-updater", "\"/home/deck/Downloads/lotr-updater-0.1.0/lotr-updater\"")]
        [InlineData("/home/deck/My Games/lotr-updater", "\"/home/deck/My Games/lotr-updater\"")]
        [InlineData("/home/a/100%/x", "\"/home/a/100%%/x\"")]
        [InlineData("/home/a/$HOME/x", "\"/home/a/\\\\$HOME/x\"")]          // file text \\$ = exec \$ = literal $
        [InlineData("/home/a/q\"b/x", "\"/home/a/q\\\\\"b/x\"")]            // file text \\" = exec \" = literal "
        [InlineData("/home/a/back\\slash", "\"/home/a/back\\\\\\\\slash\"")] // four backslashes for one (the spec's example)
        public void Desktop_entry_exec_paths_are_quoted_as_the_specification_says(string path, string expected)
        {
            Assert.Equal(expected, DesktopShortcut.ExecArgument(path));
        }

        [Fact]
        public void The_shortcut_command_writes_a_menu_entry_that_opens_the_menu_in_a_terminal()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lotr-cli-shortcut", Guid.NewGuid().ToString("N").Substring(0, 10));
            try
            {
                var apps = Path.Combine(dir, "applications");
                var o = new StringWriter();
                var e = new StringWriter();
                var host = new CliHost { In = new StringReader(""), Out = o, Err = e, ApplicationsFolder = apps, ProgramPath = Path.Combine(dir, "My Games", "lotr-updater") };
                Assert.Equal(ExitCodes.Ok, CliSandbox.Exec(new[] { "shortcut" }, host));
                var text = File.ReadAllText(Path.Combine(apps, "lotr-updater.desktop"));
                Assert.StartsWith("[Desktop Entry]\n", text);
                Assert.Contains("\nExec=" + DesktopShortcut.ExecArgument(Path.Combine(dir, "My Games", "lotr-updater")) + " menu\n", text);
                Assert.Contains("\nTerminal=true\n", text);
                Assert.DoesNotContain("\r", text);
                Assert.Contains("application menu", o.ToString());
                Assert.Equal(ExitCodes.Ok, CliSandbox.Exec(new[] { "shortcut" }, host)); // again: replaced, not duplicated
                Assert.Single(Directory.GetFiles(apps));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void The_applications_folder_follows_xdg_data_home()
        {
            Assert.Equal(Path.Combine("/x", "applications"), DesktopShortcut.ApplicationsFolder("/home/deck", n => n == "XDG_DATA_HOME" ? "/x" : null));
            Assert.Equal(Path.Combine("/home/deck", ".local", "share", "applications"), DesktopShortcut.ApplicationsFolder("/home/deck", n => null));
            Assert.Equal(Path.Combine("/home/deck", ".local", "share", "applications"), DesktopShortcut.ApplicationsFolder("/home/deck", n => "relative/ignored"));
        }

        [Fact]
        public void Steam_is_started_by_the_steam_command_then_flatpak_then_xdg_open()
        {
            var all = SteamLaunch.Choose(_ => true, () => true);
            Assert.Equal("steam", all.File);
            Assert.Equal(new[] { "steam://rungameid/261550" }, all.Args);

            var flatpak = SteamLaunch.Choose(p => p != "steam", () => true);
            Assert.Equal("flatpak", flatpak.File);
            Assert.Equal(new[] { "run", "com.valvesoftware.Steam", "steam://rungameid/261550" }, flatpak.Args);

            var xdg = SteamLaunch.Choose(p => p == "xdg-open" || p == "flatpak", () => false);
            Assert.Equal("xdg-open", xdg.File);

            Assert.Null(SteamLaunch.Choose(_ => false, () => true));
        }

        [Fact]
        public void Steam_is_started_in_its_own_session_when_setsid_exists()
        {
            var steam = SteamLaunch.Choose(_ => true, () => true);
            var detached = SteamLaunch.Detached(steam, p => p == "setsid");
            Assert.Equal("setsid", detached.File);
            Assert.Equal(new[] { "steam", "steam://rungameid/261550" }, detached.Args);

            var flatpak = SteamLaunch.Detached(SteamLaunch.Choose(p => p != "steam", () => true), _ => true);
            Assert.Equal(new[] { "flatpak", "run", "com.valvesoftware.Steam", "steam://rungameid/261550" }, flatpak.Args);

            Assert.Same(steam, SteamLaunch.Detached(steam, _ => false)); // no setsid: started as it is
            Assert.Null(SteamLaunch.Detached(null, _ => true));
        }
    }

    public class CliDetectTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lotr-cli-detect", Guid.NewGuid().ToString("N").Substring(0, 10));

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private CliRun Detect(Func<IGameLocator> locator, params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            int code = CliSandbox.Exec(new[] { "detect" }.Concat(args).ToArray(), new CliHost { In = new StringReader(""), Out = o, Err = e, Locator = locator });
            return new CliRun(code, o.ToString(), e.ToString());
        }

        [Fact]
        public void Detect_reports_the_steam_deck_layout_as_json()
        {
            var home = Path.Combine(_root, "home", "deck");
            var steam = Path.Combine(home, ".local", "share", "Steam");
            var game = FakeGame.Create(Path.Combine(steam, "steamapps", "common", "Mount & Blade II Bannerlord"));
            var pfx = Path.Combine(steam, "steamapps", "compatdata", "261550", "pfx");
            Directory.CreateDirectory(pfx);
            Directory.CreateDirectory(Path.Combine(steam, "steamapps", "workshop", "content", "261550", "2859251899"));

            var r = Detect(() => new LinuxSteamLocator(home, _ => null, new string[0]), "--json");
            Assert.True(r.Code == ExitCodes.Ok, r.ToString());
            using (var doc = JsonDocument.Parse(r.Out))
            {
                Assert.Equal(1, doc.RootElement.GetProperty("found").GetInt32());
                var g = doc.RootElement.GetProperty("installs")[0];
                Assert.Equal(game, g.GetProperty("root").GetString());
                Assert.Equal("Steam", g.GetProperty("store").GetString());
                Assert.Equal("v1.2.12", g.GetProperty("version").GetString());
                Assert.Equal(pfx, g.GetProperty("protonPrefix").GetString());
                Assert.True(g.GetProperty("writable").GetBoolean());
                Assert.Contains("261550", g.GetProperty("workshop").GetString());
            }

            var text = Detect(() => new LinuxSteamLocator(home, _ => null, new string[0]));
            Assert.Equal(ExitCodes.Ok, text.Code);
            Assert.Contains("Proton prefix:  " + pfx, text.Out);
        }

        [Fact]
        public void Detect_without_the_game_says_how_to_point_to_it()
        {
            var home = Path.Combine(_root, "empty-home");
            Directory.CreateDirectory(home);
            var r = Detect(() => new LinuxSteamLocator(home, _ => null, new string[0]));
            Assert.True(r.Code == ExitCodes.GameNotFound, r.ToString());
            Assert.Contains("--game", r.Err);
            var json = Detect(() => new LinuxSteamLocator(home, _ => null, new string[0]), "--json");
            Assert.Equal(ExitCodes.GameNotFound, json.Code);
            using (var doc = JsonDocument.Parse(json.Out)) Assert.Equal(0, doc.RootElement.GetProperty("found").GetInt32());
        }

        [Fact]
        public void Detect_checks_a_chosen_folder()
        {
            var game = FakeGame.Create(Path.Combine(_root, "Games", "Bannerlord"));
            var r = Detect(() => new LinuxSteamLocator(Path.Combine(_root, "nohome"), _ => null, new string[0]), "--game", Path.Combine(game, "bin", "Win64_Shipping_Client"));
            Assert.True(r.Code == ExitCodes.Ok, r.ToString());
            Assert.Contains("Game folder:    " + game, r.Out);
        }
    }
}
