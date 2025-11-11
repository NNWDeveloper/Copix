using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace ClipboardManager
{
    public class ClipboardItem
    {
        public DateTime Time { get; set; }
        public string Text { get; set; }
    }

    public partial class MainForm : Form
    {
        private List<ClipboardItem> history = new List<ClipboardItem>();
        private System.Windows.Forms.Timer clipboardTimer;
        private readonly string historyFile = Path.Combine(Application.UserAppDataPath, "clipboard_history.json");
        private const int MaxHistory = 30;

        // Ovládací prvky
        private ListBox listBoxHistory;
        private Button btnCopySelected;
        private Button btnClear;
        private Button btnExport;
        private Button btnImport;

        // Hotkey
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 1;
        private const uint MOD_CONTROL = 0x2;
        private const uint MOD_SHIFT = 0x4;
        private const uint VK_V = 0x56;

        private NotifyIcon trayIcon;

        public MainForm()
        {
            InitializeComponent();

            this.Icon = new Icon("Copix.ico");

            trayIcon = new NotifyIcon()
            {
                Icon = this.Icon,
                Visible = true,
                Text = "Clipboard Manager"
            };
            trayIcon.DoubleClick += (s, e) => ToggleWindow();

            clipboardTimer = new System.Windows.Forms.Timer();
            clipboardTimer.Interval = 500;
            clipboardTimer.Tick += ClipboardTimer_Tick;
            clipboardTimer.Start();

            LoadHistory();
            RefreshListBox();

            btnCopySelected.Click += (s, e) => CopySelected();
            btnClear.Click += (s, e) => ClearHistory();
            btnExport.Click += (s, e) => ExportHistory();
            btnImport.Click += (s, e) => ImportHistory();

            Shown += (s, e) => Hide();

            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Color.FromArgb(30, 30, 30);
            ForeColor = Color.White;

            RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_V);
        }

        private void InitializeComponent()
        {
            listBoxHistory = new ListBox();
            btnCopySelected = new Button();
            btnClear = new Button();
            btnExport = new Button();
            btnImport = new Button();

            listBoxHistory.Location = new Point(12, 12);
            listBoxHistory.Size = new Size(460, 300);
            listBoxHistory.DoubleClick += listBoxHistory_DoubleClick;

            btnCopySelected.Text = "Copy";
            btnCopySelected.Location = new Point(12, 320);

            btnClear.Text = "Delete all";
            btnClear.Location = new Point(120, 320);

            btnExport.Text = "Export";
            btnExport.Location = new Point(230, 320);

            btnImport.Text = "Import";
            btnImport.Location = new Point(340, 320);

            Controls.Add(listBoxHistory);
            Controls.Add(btnCopySelected);
            Controls.Add(btnClear);
            Controls.Add(btnExport);
            Controls.Add(btnImport);

            this.ClientSize = new Size(484, 361);
            this.Text = "Clipboard Manager";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            trayIcon.Visible = false;
            UnregisterHotKey(Handle, HOTKEY_ID);
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_HOTKEY = 0x0312;
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                ToggleWindow();
            }
            base.WndProc(ref m);
        }

        private void ToggleWindow()
        {
            if (Visible)
                Hide();
            else
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
            }
        }

        private void LoadHistory()
        {
            if (File.Exists(historyFile))
            {
                try
                {
                    history = JsonSerializer.Deserialize<List<ClipboardItem>>(File.ReadAllText(historyFile)) ?? new List<ClipboardItem>();
                }
                catch
                {
                    history = new List<ClipboardItem>();
                }
            }
        }

        private void SaveHistory()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(historyFile));
            File.WriteAllText(historyFile, JsonSerializer.Serialize(history));
        }

        private bool IsValidClipboardText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Length < 3) return false;
            if (text.Any(ch => ch < 32 && ch != '\n' && ch != '\r' && ch != '\t')) return false;

            string[] banned = { "usong system", "system", "internal", "sync" };
            string lower = text.ToLower();
            if (banned.Any(b => lower.Contains(b))) return false;

            if (text.Length > 2000) return false;
            return true;
        }

        private void ClipboardTimer_Tick(object sender, EventArgs e)
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText().Trim();
                if (!IsValidClipboardText(text)) return;

                if (!history.Any(h => h.Text == text))
                {
                    history.Insert(0, new ClipboardItem { Time = DateTime.Now, Text = text });
                    if (history.Count > MaxHistory) history.RemoveAt(history.Count - 1);
                    SaveHistory();
                    RefreshListBox();
                }
            }
        }

        private void RefreshListBox()
        {
            listBoxHistory.Items.Clear();
            foreach (var item in history)
            {
                string shortText = item.Text.Length > 60 ? item.Text.Substring(0, 60) + "..." : item.Text;
                listBoxHistory.Items.Add($"{item.Time:HH:mm:ss} • {shortText}");
            }
        }

        private void CopySelected()
        {
            if (listBoxHistory.SelectedItem != null)
            {
                string value = listBoxHistory.SelectedItem.ToString();
                int index = value.IndexOf("•");
                if (index >= 0)
                {
                    string clean = value.Substring(index + 2).Trim();
                    Clipboard.SetText(clean);
                }
            }
        }

        private void ClearHistory()
        {
            history.Clear();
            listBoxHistory.Items.Clear();
            SaveHistory();
        }

        private void ExportHistory()
        {
            SaveFileDialog dlg = new SaveFileDialog { Filter = "JSON Files|*.json" };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(history));
            }
        }

        private void ImportHistory()
        {
            OpenFileDialog dlg = new OpenFileDialog { Filter = "JSON Files|*.json" };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    var imported = JsonSerializer.Deserialize<List<ClipboardItem>>(File.ReadAllText(dlg.FileName));
                    if (imported != null)
                    {
                        foreach (var item in imported.Reverse<ClipboardItem>())
                        {
                            if (!history.Any(h => h.Text == item.Text))
                                history.Insert(0, item);
                        }
                        if (history.Count > MaxHistory) history = history.Take(MaxHistory).ToList();
                        RefreshListBox();
                        SaveHistory();
                    }
                }
                catch { }
            }
        }

        private void listBoxHistory_DoubleClick(object sender, EventArgs e)
        {
            CopySelected();
        }
    }
}
