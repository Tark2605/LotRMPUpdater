using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LotR.GameDetection.Abstractions;

namespace LotR.GameDetection.Tests.Bench
{
    /// <summary>In-memory registry. LocalMachine has separate 32/64-bit views (Default = 64-bit); CurrentUser is shared.</summary>
    public sealed class FakeRegistry : IRegistryReader
    {
        private readonly Dictionary<string, Dictionary<string, object>> _keys = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _keyCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool ThrowOnAccess { get; set; }

        public FakeRegistry Set(RegistryHiveKind hive, RegistryViewKind view, string keyPath, string valueName, object value)
        {
            var id = Id(hive, view, keyPath);
            if (!_keys.TryGetValue(id, out var values))
            {
                values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                _keys[id] = values;
                _keyCase[id] = keyPath.Trim('\\');
            }
            values[valueName] = value;
            return this;
        }

        public object GetValue(RegistryHiveKind hive, RegistryViewKind view, string keyPath, string valueName)
        {
            if (ThrowOnAccess) throw new UnauthorizedAccessException("fake registry failure");
            return _keys.TryGetValue(Id(hive, view, keyPath), out var values) && values.TryGetValue(valueName, out var v) ? v : null;
        }

        public IReadOnlyList<string> GetSubKeyNames(RegistryHiveKind hive, RegistryViewKind view, string keyPath)
        {
            if (ThrowOnAccess) throw new UnauthorizedAccessException("fake registry failure");
            var prefix = Id(hive, view, keyPath) + "\\";
            var result = new List<string>();
            foreach (var id in _keys.Keys)
            {
                if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var original = _keyCase[id];
                var rest = id.Substring(prefix.Length);
                var first = rest.Split('\\')[0];
                // recover original case of that segment
                var segIndex = original.Split('\\').Length - rest.Split('\\').Length;
                var name = original.Split('\\')[segIndex];
                if (!result.Contains(name, StringComparer.OrdinalIgnoreCase) && first.Length > 0)
                    result.Add(name);
            }
            return result;
        }

        private static string Id(RegistryHiveKind hive, RegistryViewKind view, string keyPath)
        {
            string v = hive == RegistryHiveKind.CurrentUser ? "shared" : (view == RegistryViewKind.Registry32 ? "32" : "64");
            return hive + "|" + v + "|" + keyPath.Trim('\\');
        }
    }

    public sealed class FakeProcessList : IProcessList
    {
        public List<RunningProcess> Processes { get; } = new List<RunningProcess>();

        public IReadOnlyList<RunningProcess> GetProcesses(IEnumerable<string> processNames)
        {
            var names = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
            return Processes.Where(p => names.Contains(p.Name)).ToList();
        }
    }

    public sealed class FakeEnvironment : ISystemEnvironment
    {
        public Dictionary<KnownFolderKind, string> Folders { get; } = new Dictionary<KnownFolderKind, string>();
        public Dictionary<string, string> Variables { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string GetFolder(KnownFolderKind kind) => Folders.TryGetValue(kind, out var p) ? p : null;

        public string ExpandEnvironmentVariables(string value)
        {
            if (value == null) return null;
            foreach (var kv in Variables)
                value = value.Replace("%" + kv.Key + "%", kv.Value);
            return value;
        }
    }

    /// <summary>
    /// Writes minimal but valid [MS-SHLLINK] files: header + LinkInfo (ANSI and Unicode local base path)
    /// + optional WORKING_DIR / RELATIVE_PATH string data + optional EnvironmentVariableDataBlock.
    /// </summary>
    public static class LnkBuilder
    {
        public static byte[] Build(string target, string workingDir = null, string relativePath = null, string envTarget = null, bool withLinkInfo = true)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                uint flags = 0x80; // IsUnicode
                if (withLinkInfo) flags |= 0x2;
                if (relativePath != null) flags |= 0x8;
                if (workingDir != null) flags |= 0x10;
                if (envTarget != null) flags |= 0x200;

                w.Write(0x4Cu);
                w.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
                w.Write(flags);
                w.Write(new byte[76 - 24]); // attributes, times, size, icon, showcmd, hotkey, reserved

                if (withLinkInfo)
                {
                    var ansi = Encoding.ASCII.GetBytes(target.Select(c => c < 128 ? c : '?').ToArray());
                    var uni = Encoding.Unicode.GetBytes(target);
                    var volume = new byte[] { 0x11, 0, 0, 0, 3, 0, 0, 0, 0x78, 0x56, 0x34, 0x12, 0x10, 0, 0, 0, 0 };

                    int headerSize = 0x24;
                    int volOff = headerSize;
                    int localOff = volOff + volume.Length;
                    int suffixOff = localOff + ansi.Length + 1;
                    int localUniOff = suffixOff + 1;
                    localUniOff += localUniOff % 2;
                    int suffixUniOff = localUniOff + uni.Length + 2;
                    int total = suffixUniOff + 2;

                    var li = new byte[total];
                    void Put(int o, int v) => BitConverter.GetBytes(v).CopyTo(li, o);
                    Put(0, total);
                    Put(4, headerSize);
                    Put(8, 1);
                    Put(12, volOff);
                    Put(16, localOff);
                    Put(20, 0);
                    Put(24, suffixOff);
                    Put(28, localUniOff);
                    Put(32, suffixUniOff);
                    volume.CopyTo(li, volOff);
                    ansi.CopyTo(li, localOff);
                    uni.CopyTo(li, localUniOff);
                    w.Write(li);
                }

                void WriteString(string s)
                {
                    w.Write((ushort)s.Length);
                    w.Write(Encoding.Unicode.GetBytes(s));
                }
                if (relativePath != null) WriteString(relativePath);
                if (workingDir != null) WriteString(workingDir);

                if (envTarget != null)
                {
                    var block = new byte[0x314];
                    BitConverter.GetBytes(0x314).CopyTo(block, 0);
                    BitConverter.GetBytes(0xA0000001).CopyTo(block, 4);
                    Encoding.ASCII.GetBytes(envTarget).CopyTo(block, 8);
                    Encoding.Unicode.GetBytes(envTarget).CopyTo(block, 268);
                    w.Write(block);
                }
                w.Write(0u); // terminal block
                return ms.ToArray();
            }
        }
    }
}
