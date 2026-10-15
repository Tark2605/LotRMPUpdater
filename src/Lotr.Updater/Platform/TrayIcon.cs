using System;
using System.Drawing;
using System.Windows.Forms;

namespace Lotr.Updater.Platform
{
    /// <summary>
    /// Notification-area icon, only while the window is hidden ("Start with Windows" or closed with that option on):
    /// double-click / "Open" shows the window, "Check for updates now", "Exit" quits. Balloons tell when an update is ready.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;

        public TrayIcon(string exePath, Action open, Action check, Action exit)
        {
            Icon ico = null;
            try { ico = Icon.ExtractAssociatedIcon(exePath); } catch (Exception) { }
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open LotR MP Event updater", null, (s, e) => open());
            menu.Items.Add("Check for updates now", null, (s, e) => check());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => exit());
            _icon = new NotifyIcon
            {
                Icon = ico ?? SystemIcons.Application,
                Text = "LotR MP Event updater",
                ContextMenuStrip = menu,
                Visible = false,
            };
            _icon.DoubleClick += (s, e) => open();
            _icon.BalloonTipClicked += (s, e) => open();
        }

        public bool Visible
        {
            get => _icon.Visible;
            set => _icon.Visible = value;
        }

        public void Notify(string title, string text)
        {
            if (!_icon.Visible) return;
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(8000);
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
        }
    }
}
