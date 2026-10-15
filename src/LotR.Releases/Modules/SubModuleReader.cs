using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using LotR.Releases.Model;

namespace LotR.Releases.Modules
{
    public sealed class SubModuleInfo
    {
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>Version string verbatim (trimmed); may not parse as a <see cref="ModuleVersion"/>.</summary>
        public string Version { get; set; }

        public string ModuleCategory { get; set; }

        public List<ModuleDependency> DependedModules { get; } = new List<ModuleDependency>();

        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// Reads SubModule.xml the way the launcher sees it: Module/Id, Name, Version (@value), DependedModules/DependedModule
    /// (@Id, @DependentVersion, @Optional) and, if present, the BLSE-style DependedModuleMetadatas (@id, @version,
    /// @optional, @order="LoadBeforeThis" only). DTDs are refused; leading whitespace/BOM tolerated.
    /// </summary>
    public static class SubModuleReader
    {
        public static SubModuleInfo ReadFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Parse(bytes, path);
        }

        public static SubModuleInfo Parse(byte[] bytes, string sourceName = "SubModule.xml")
        {
            // Decode ourselves (UTF-8, BOM-aware, lenient on invalid bytes inside comments) and drop anything before the
            // first '<', which the engine tolerates but XmlReader does not.
            string text = new UTF8Encoding(false, false).GetString(bytes);
            if (text.Length > 0 && text[0] == '﻿')
            {
                text = text.Substring(1);
            }

            int lt = text.IndexOf('<');
            if (lt > 0)
            {
                text = text.Substring(lt);
            }

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                CheckCharacters = false,
            };

            var info = new SubModuleInfo();
            try
            {
                using (var sr = new StringReader(text))
                using (var r = XmlReader.Create(sr, settings))
                {
                    var path = new List<string>();
                    while (r.Read())
                    {
                        if (r.NodeType == XmlNodeType.EndElement)
                        {
                            path.RemoveAt(path.Count - 1);
                            continue;
                        }

                        if (r.NodeType != XmlNodeType.Element)
                        {
                            continue;
                        }

                        string name = r.LocalName;
                        bool empty = r.IsEmptyElement;
                        string parent = path.Count > 0 ? path[path.Count - 1] : null;
                        int depth = path.Count;

                        if (depth == 0 && !Eq(name, "Module"))
                        {
                            throw new InvalidDataException(sourceName + ": root element is <" + name + ">, expected <Module>");
                        }

                        if (depth == 1)
                        {
                            if (Eq(name, "Id"))
                            {
                                info.Id = Value(r);
                            }
                            else if (Eq(name, "Name"))
                            {
                                info.Name = Value(r);
                            }
                            else if (Eq(name, "Version"))
                            {
                                info.Version = Value(r);
                            }
                            else if (Eq(name, "ModuleCategory"))
                            {
                                info.ModuleCategory = Value(r);
                            }
                        }
                        else if (depth == 2 && Eq(parent, "DependedModules") && Eq(name, "DependedModule"))
                        {
                            string id = Attr(r, "Id");
                            if (string.IsNullOrWhiteSpace(id))
                            {
                                info.Warnings.Add("DependedModule without Id ignored");
                            }
                            else
                            {
                                Add(info, id.Trim(), Attr(r, "DependentVersion"), IsTrue(Attr(r, "Optional")));
                            }
                        }
                        else if (depth == 2 && Eq(parent, "DependedModuleMetadatas") && Eq(name, "DependedModuleMetadata"))
                        {
                            string order = Attr(r, "order");
                            string id = Attr(r, "id");
                            if (!string.IsNullOrWhiteSpace(id) && (order == null || Eq(order, "LoadBeforeThis")))
                            {
                                Add(info, id.Trim(), Attr(r, "version"), IsTrue(Attr(r, "optional")));
                            }
                        }

                        if (!empty)
                        {
                            path.Add(name);
                        }
                    }
                }
            }
            catch (XmlException e)
            {
                throw new InvalidDataException(sourceName + ": not well-formed XML: " + e.Message, e);
            }

            if (string.IsNullOrWhiteSpace(info.Id))
            {
                throw new InvalidDataException(sourceName + ": <Id value=...> is missing");
            }

            if (string.IsNullOrWhiteSpace(info.Version))
            {
                throw new InvalidDataException(sourceName + ": <Version value=...> is missing");
            }

            if (ModuleVersion.TryParse(info.Version) == null)
            {
                info.Warnings.Add("version '" + info.Version + "' is not a Bannerlord version; kept verbatim");
            }

            if (string.IsNullOrWhiteSpace(info.Name))
            {
                info.Warnings.Add("<Name> missing; using the Id");
                info.Name = info.Id;
            }

            return info;
        }

        private static void Add(SubModuleInfo info, string id, string version, bool optional)
        {
            if (info.DependedModules.Exists(d => string.Equals(d.Id, id, StringComparison.Ordinal)))
            {
                return; // DependedModules and the BLSE metadata often repeat each other
            }

            info.DependedModules.Add(new ModuleDependency
            {
                Id = id,
                Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
                Optional = optional,
            });
        }

        private static string Value(XmlReader r) => Attr(r, "value")?.Trim();

        private static string Attr(XmlReader r, string name)
        {
            if (!r.HasAttributes)
            {
                return null;
            }

            for (int i = 0; i < r.AttributeCount; i++)
            {
                r.MoveToAttribute(i);
                if (Eq(r.LocalName, name))
                {
                    string v = r.Value;
                    r.MoveToElement();
                    return v;
                }
            }

            r.MoveToElement();
            return null;
        }

        private static bool IsTrue(string v) => v != null && v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
