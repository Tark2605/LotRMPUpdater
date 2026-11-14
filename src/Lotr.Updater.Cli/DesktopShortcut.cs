using System;
using System.IO;
using System.Text;

namespace Lotr.Updater.Cli
{
    /// <summary>
    /// "lotr-updater shortcut": adds "LotR MP Event updater" to the desktop's application menu (KDE on the Steam Deck, GNOME,
    /// ...) as ~/.local/share/applications/lotr-updater.desktop. It opens a terminal with the menu. From there, Steam's
    /// "Add a Non-Steam Game" also lists it (Game Mode). Written by code because only the program knows its absolute path.
    /// </summary>
    public static class DesktopShortcut
    {
        public const string FileName = "lotr-updater.desktop";

        /// <summary>$XDG_DATA_HOME/applications, else ~/.local/share/applications.</summary>
        public static string ApplicationsFolder(string home, Func<string, string> environment)
        {
            var xdg = environment("XDG_DATA_HOME");
            var data = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".local", "share");
            return Path.Combine(data, "applications");
        }

        /// <summary>The .desktop text for the program at <paramref name="programPath"/> (absolute).</summary>
        public static string Content(string programPath)
        {
            var sb = new StringBuilder();
            sb.Append("[Desktop Entry]\n");
            sb.Append("Type=Application\n");
            sb.Append("Name=LotR MP Event updater\n");
            sb.Append("Comment=Install or update the LotR MP Event mod for Bannerlord\n");
            sb.Append("Exec=").Append(ExecArgument(programPath)).Append(" menu\n");
            sb.Append("Icon=applications-games\n");
            sb.Append("Terminal=true\n");
            sb.Append("Categories=Game;\n");
            return sb.ToString();
        }

        /// <summary>
        /// One Exec argument, quoted as the Desktop Entry Specification says: inside double quotes, '"', '`', '$' and '\'
        /// take a backslash; '%' is doubled (field codes); then the value's own escape rule doubles every backslash.
        /// </summary>
        public static string ExecArgument(string arg)
        {
            var q = new StringBuilder("\"");
            foreach (var ch in arg)
            {
                if (ch == '"' || ch == '`' || ch == '$' || ch == '\\') q.Append('\\');
                if (ch == '%') q.Append('%');
                q.Append(ch);
            }
            q.Append('"');
            return q.ToString().Replace("\\", "\\\\");
        }

        /// <summary>Writes the entry (replacing an older one) and marks it executable. Returns its path.</summary>
        public static string Write(string folder, string programPath)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName);
            File.WriteAllText(path, Content(programPath), new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                           UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return path;
        }
    }
}
