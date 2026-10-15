using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Lotr.Updater
{
    /// <summary>
    /// Entry point. Loads the embedded DLLs (Core, GameDetection, ZstdSharp + System.* helpers) from the exe's resources
    /// before any of their types is touched, so LotRMPUpdater.exe is one self-contained file.
    /// </summary>
    public static class Program
    {
        private static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        [STAThread]
        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
            return Run(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            var options = StartOptions.Parse(args);
            var app = new App(options);
            app.InitializeComponent();
            return app.Run();
        }

        private static Assembly ResolveEmbedded(object sender, ResolveEventArgs e)
        {
            var name = new AssemblyName(e.Name).Name;
            lock (Loaded)
            {
                if (Loaded.TryGetValue(name, out var a)) return a;
                using (var s = typeof(Program).Assembly.GetManifestResourceStream("lib." + name + ".dll"))
                {
                    if (s == null) return null;
                    var bytes = new byte[s.Length];
                    int read = 0;
                    while (read < bytes.Length) read += s.Read(bytes, read, bytes.Length - read);
                    a = Assembly.Load(bytes);
                    Loaded[name] = a;
                    return a;
                }
            }
        }
    }

    /// <summary>Command line. Everything except --uninstall is for development against a LOCAL test server.</summary>
    public sealed class StartOptions
    {
        public bool Dev { get; private set; }
        public string Server { get; private set; }
        public string Game { get; private set; }
        public string Home { get; private set; }
        public List<string> TrustPem { get; } = new List<string>();
        public string TourDir { get; private set; }
        public bool Uninstall { get; private set; }
        public bool Minimized { get; private set; }
        public string Error { get; private set; }

        public static StartOptions Parse(string[] args)
        {
            var o = new StartOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : null;
                switch (args[i].ToLowerInvariant())
                {
                    case "--dev": o.Dev = true; break;
                    case "--server": o.Server = Next(); break;
                    case "--game": o.Game = Next(); break;
                    case "--home": o.Home = Next(); break;
                    case "--trust": var p = Next(); if (p != null && File.Exists(p)) o.TrustPem.Add(File.ReadAllText(p)); break;
                    case "--tour": o.TourDir = Next(); break;
                    case "--uninstall": o.Uninstall = true; break;
                    case "--minimized": o.Minimized = true; break;
                }
            }
            if (o.Dev)
            {
                // Dev mode is only allowed against a local server and a non-default home: it can never reach players' data.
                if (o.Server == null || !Uri.TryCreate(o.Server, UriKind.Absolute, out var u) || !u.IsLoopback)
                    o.Error = "--dev needs --server http://localhost:<port>/";
                if (o.Home == null) o.Error = "--dev needs --home <folder>";
                if (o.Game == null) o.Error = "--dev needs --game <fake game folder>";
            }
            else
            {
                o.Server = null; o.Game = null; o.Home = null; o.TrustPem.Clear(); o.TourDir = null;
            }
            return o;
        }
    }
}
