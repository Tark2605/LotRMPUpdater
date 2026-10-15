using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using LotR.Releases.Model;

namespace LotR.Releases.Modules
{
    /// <summary>
    /// Reads the identity of each scene: SceneObj/&lt;folder&gt;/scene.xscene root attributes unique_token and revision
    /// (what the game compares between client and server, with the scene name). Streaming: only the root start tag is
    /// read, whatever the file size.
    /// </summary>
    public static class SceneReader
    {
        public static List<SceneInfo> ReadModule(string moduleFolder, List<string> warnings = null)
        {
            var result = new List<SceneInfo>();
            string sceneObj = Path.Combine(moduleFolder, "SceneObj");
            if (!Directory.Exists(sceneObj))
            {
                return result;
            }

            foreach (var dir in Directory.GetDirectories(sceneObj).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
            {
                string file = Path.Combine(dir, "scene.xscene");
                if (!File.Exists(file))
                {
                    warnings?.Add("SceneObj/" + Path.GetFileName(dir) + " has no scene.xscene");
                    continue;
                }

                var s = ReadScene(file);
                string folder = Path.GetFileName(dir);
                if (s.Name != null && !string.Equals(s.Name, folder, StringComparison.Ordinal))
                {
                    warnings?.Add("SceneObj/" + folder + "/scene.xscene declares name '" + s.Name + "' (the folder name is used)");
                }

                s.Name = folder;
                if (s.UniqueToken == null || s.Revision == null)
                {
                    warnings?.Add("SceneObj/" + folder + "/scene.xscene lacks unique_token or revision");
                }

                result.Add(s);
            }

            return result;
        }

        public static SceneInfo ReadScene(string path)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, CheckCharacters = false };
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
            using (var r = XmlReader.Create(fs, settings))
            {
                try
                {
                    if (r.MoveToContent() != XmlNodeType.Element)
                    {
                        throw new InvalidDataException(path + ": no root element");
                    }
                }
                catch (XmlException e)
                {
                    throw new InvalidDataException(path + ": " + e.Message, e);
                }

                return new SceneInfo
                {
                    Name = r.GetAttribute("name"),
                    UniqueToken = r.GetAttribute("unique_token"),
                    Revision = r.GetAttribute("revision"),
                };
            }
        }
    }
}
