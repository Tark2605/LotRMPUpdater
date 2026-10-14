using System;
using System.Collections.Generic;
using System.Linq;
using LotR.GameDetection.Abstractions;

namespace LotR.GameDetection
{
    /// <summary>Outcome of validating one candidate path.</summary>
    public sealed class RootResolution
    {
        public bool Success { get; set; }
        public string RootPath { get; set; }
        public string ExecutablePath { get; set; }
        public string BinFolderName { get; set; }
        public RejectionReason Reason { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Anchors on Bannerlord.exe and walks up to the game root. A folder D is a game root when
    /// D\bin\&lt;platform&gt;\Bannerlord.exe exists AND D\Modules\Native\SubModule.xml exists.
    /// Accepts the exe itself, a bin folder, the root, the Modules folder (or anything inside it),
    /// or an Xbox outer folder (the root is then its "Content" sub folder).
    /// </summary>
    public sealed class GameRootResolver
    {
        private const int MaxLevelsUp = 6;
        private readonly IFileSystem _fs;

        public GameRootResolver(IFileSystem fs)
        {
            _fs = fs;
        }

        public RootResolution Resolve(string candidatePath)
        {
            var path = PathUtil.Normalize(candidatePath);
            if (path == null)
                return Fail(RejectionReason.PathNotFound, "No path was given.");

            string anchorExe = null;
            string start;
            if (_fs.FileExists(path))
            {
                if (string.Equals(PathUtil.GetFileName(path), BannerlordConstants.GameExecutable, StringComparison.OrdinalIgnoreCase))
                    anchorExe = path;
                start = PathUtil.GetParent(path);
            }
            else if (_fs.DirectoryExists(path))
            {
                start = path;
            }
            else
            {
                return Fail(RejectionReason.PathNotFound, "This folder or file does not exist (moved, renamed or uninstalled?).");
            }

            var dirs = new List<string> { start, PathUtil.Combine(start, "Content") };
            var cur = start;
            for (int i = 0; i < MaxLevelsUp; i++)
            {
                cur = PathUtil.GetParent(cur);
                if (cur == null) break;
                dirs.Add(cur);
            }

            RootResolution best = null;
            foreach (var dir in dirs)
            {
                if (!_fs.DirectoryExists(dir))
                    continue;

                var exes = FindGameExecutables(dir);
                var native = PathUtil.Combine(dir, BannerlordConstants.ModulesFolder, BannerlordConstants.NativeModule);
                bool hasNative = _fs.DirectoryExists(native);
                bool hasNativeXml = hasNative && _fs.FileExists(PathUtil.Combine(native, BannerlordConstants.SubModuleXml));

                if (exes.Count > 0 && hasNativeXml)
                {
                    var exe = anchorExe != null && exes.Any(e => PathUtil.PathEquals(e, anchorExe)) ? anchorExe : exes[0];
                    return new RootResolution
                    {
                        Success = true,
                        RootPath = dir,
                        ExecutablePath = exe,
                        BinFolderName = PathUtil.GetFileName(PathUtil.GetParent(exe)),
                    };
                }

                RootResolution failure = null;
                if (exes.Count > 0 && !hasNative)
                    failure = Fail(RejectionReason.NativeModuleMissing,
                        $"Bannerlord.exe is here but Modules\\Native is missing in {dir}: the installation is incomplete or damaged. Verify the game files in your store launcher.");
                else if (exes.Count > 0)
                    failure = Fail(RejectionReason.NativeSubModuleXmlMissing,
                        $"Modules\\Native\\SubModule.xml is missing in {dir}: the installation is damaged. Verify the game files in your store launcher.");
                else if (hasNativeXml && _fs.DirectoryExists(PathUtil.Combine(dir, "bin", BannerlordConstants.BinServer)))
                    failure = Fail(RejectionReason.DedicatedServer,
                        $"{dir} is the Bannerlord Dedicated Server, not the game client. Choose the folder of the game itself.");
                else if (_fs.FileExists(PathUtil.Combine(dir, "bin", BannerlordConstants.BinEditor, BannerlordConstants.GameExecutable)))
                    failure = Fail(RejectionReason.ModdingKitOnly,
                        $"{dir} only contains the Bannerlord Modding Kit (bin\\{BannerlordConstants.BinEditor}), not the game client.");

                if (failure != null && (best == null || Rank(failure.Reason) > Rank(best.Reason)))
                    best = failure;
            }

            return best ?? Fail(RejectionReason.NoGameExecutable,
                "No Bannerlord.exe was found in bin\\Win64_Shipping_Client or bin\\Gaming.Desktop.x64_Shipping_Client at or above this folder.");
        }

        /// <summary>Bannerlord.exe files under &lt;dir&gt;\bin\*, preferred platform first.</summary>
        public List<string> FindGameExecutables(string dir)
        {
            var result = new List<string>();
            var bin = PathUtil.Combine(dir, "bin");
            if (!_fs.DirectoryExists(bin))
                return result;

            foreach (var preferred in new[] { BannerlordConstants.BinWin64, BannerlordConstants.BinXbox })
            {
                var exe = PathUtil.Combine(bin, preferred, BannerlordConstants.GameExecutable);
                if (_fs.FileExists(exe))
                    result.Add(exe);
            }
            // Other bin folders (Win64_Shipping_wEditor of the Modding Kit, BLSE helpers...) are not the game client.
            return result;
        }

        private static int Rank(RejectionReason r)
        {
            switch (r)
            {
                case RejectionReason.NativeModuleMissing: return 3;
                case RejectionReason.NativeSubModuleXmlMissing: return 3;
                case RejectionReason.DedicatedServer: return 2;
                case RejectionReason.ModdingKitOnly: return 1;
                default: return 0;
            }
        }

        private static RootResolution Fail(RejectionReason reason, string message) => new RootResolution { Success = false, Reason = reason, Message = message };
    }
}
