using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace ClipboardManager
{
    public class ClipboardItem
    {
        public DateTime Timestamp { get; set; }
        public string TextContent { get; set; }    // null if image
        public byte[] ImageContent { get; set; }   // null if text
        public bool IsPinned { get; set; }

        [JsonIgnore]
        public bool IsImage => ImageContent != null && ImageContent.Length > 0;

        public string DisplayPreview(int maxChars = 60)
        {
            if (IsImage)
            {
                try
                {
                    using var ms = new MemoryStream(ImageContent);
                    using var bmp = new Bitmap(ms);
                    return $"[Image] {bmp.Width}x{bmp.Height}";
                }
                catch
                {
                    return "[Image]";
                }
            }
            if (!string.IsNullOrEmpty(TextContent))
            {
                var s = TextContent.Replace("\r", " ").Replace("\n", " ");
                if (s.Length <= maxChars) return s;
                return s.Substring(0, maxChars) + "...";
            }
            return "(empty)";
        }
    }

    public partial class MainForm : Form
    {
        private List<ClipboardItem> history = new List<ClipboardItem>();
        private System.Windows.Forms.Timer clipboardTimer;
        private readonly string historyFile = Path.Combine(Application.UserAppDataPath, "clipboard_history.json");
        private const int MaxHistory = 30;

        // UI controls
        private ListBox listBoxHistory;
        private Button btnCopySelected;
        private Button btnPinToggle;
        private Button btnClear;
        private Button btnExport;
        private Button btnImport;
        private TextBox txtSearch;
        private Label lblCount;

        // Tray & Hotkey (kept from původní)
        private NotifyIcon trayIcon;
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 1;
        private const uint MOD_CONTROL = 0x2;
        private const uint MOD_SHIFT = 0x4;
        private const uint VK_V = 0x56;

        public MainForm()
        {
            InitializeComponent();

            // small window styling
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Color.FromArgb(30, 30, 30);
            ForeColor = Color.White;

            // Tray
            try
            {
                this.Icon = new Icon("Copix.ico");
            }
            catch { }
            trayIcon = new NotifyIcon()
            {
                Icon = this.Icon,
                Visible = true,
                Text = "Clipboard Manager"
            };
            trayIcon.DoubleClick += (s, e) => ToggleWindow();

            // Timer
            clipboardTimer = new System.Windows.Forms.Timer();
            clipboardTimer.Interval = 500;
            clipboardTimer.Tick += ClipboardTimer_Tick;
            clipboardTimer.Start();

            // Hotkey (try/catch to avoid crashes if registration fails)
            try { RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_V); } catch { }

            // load
            LoadHistory();
            SortHistory();
            RefreshListBox();
        }

        private void InitializeComponent()
        {
            // Controls
            listBoxHistory = new ListBox()
            {
                Location = new Point(12, 44),
                Size = new Size(560, 360),
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.White
            };
            listBoxHistory.DoubleClick += ListBoxHistory_DoubleClick;
            listBoxHistory.SelectedIndexChanged += ListBoxHistory_SelectedIndexChanged;

            // Buttons
            btnCopySelected = new Button() { Text = "Copy", Location = new Point(12, 410), Size = new Size(90, 28) };
            btnCopySelected.Click += (s, e) => CopySelected();

            btnPinToggle = new Button() { Text = "Pin / Unpin", Location = new Point(110, 410), Size = new Size(140, 28) };
            btnPinToggle.Click += (s, e) => TogglePinSelected();

            btnClear = new Button() { Text = "Delete all", Location = new Point(260, 410), Size = new Size(100, 28) };
            btnClear.Click += (s, e) => ClearHistory();

            btnExport = new Button() { Text = "Export", Location = new Point(370, 410), Size = new Size(90, 28) };
            btnExport.Click += (s, e) => ExportHistory();

            btnImport = new Button() { Text = "Import", Location = new Point(470, 410), Size = new Size(90, 28) };
            btnImport.Click += (s, e) => ImportHistory();

            // Search box
            txtSearch = new TextBox() { Location = new Point(12, 12), Size = new Size(400, 22) };
            txtSearch.PlaceholderText = "Search...";
            txtSearch.TextChanged += TxtSearch_TextChanged;

            lblCount = new Label() { Location = new Point(430, 12), Size = new Size(140, 22), ForeColor = Color.LightGray, Text = "" };

            Controls.Add(listBoxHistory);
            Controls.Add(btnCopySelected);
            Controls.Add(btnPinToggle);
            Controls.Add(btnClear);
            Controls.Add(btnExport);
            Controls.Add(btnImport);
            Controls.Add(txtSearch);
            Controls.Add(lblCount);

            this.ClientSize = new Size(584, 450);
            this.Text = "Clipboard Manager";
            this.FormClosing += MainForm_FormClosing;
            this.Shown += (s, e) => Hide(); // hide on start
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // ensure tray icon cleared
            trayIcon.Visible = false;
            try { UnregisterHotKey(Handle, HOTKEY_ID); } catch { }
            base.OnFormClosing(e);
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            trayIcon.Visible = false;
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
            if (Visible) Hide();
            else
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
            }
        }

        private void ClipboardTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // Prefer image if both available
                if (Clipboard.ContainsImage())
                {
                    Image img = null;
                    try { img = Clipboard.GetImage(); } catch { img = null; }
                    if (img != null)
                    {
                        using var ms = new MemoryStream();
                        img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        var bytes = ms.ToArray();

                        // ignore if empty or invalid
                        if (bytes.Length > 0 && !history.Any(h => h.ImageContent != null && h.ImageContent.SequenceEqual(bytes)))
                        {
                            var item = new ClipboardItem
                            {
                                Timestamp = DateTime.Now,
                                ImageContent = bytes,
                                TextContent = null,
                                IsPinned = false
                            };
                            AddToHistory(item);
                        }
                        return;
                    }
                }

                if (Clipboard.ContainsText())
                {
                    string text = "";
                    try { text = Clipboard.GetText().Trim(); } catch { text = ""; }
                    if (!IsValidClipboardText(text)) return;

                    if (!history.Any(h => !h.IsImage && h.TextContent == text))
                    {
                        var item = new ClipboardItem
                        {
                            Timestamp = DateTime.Now,
                            TextContent = text,
                            ImageContent = null,
                            IsPinned = false
                        };
                        AddToHistory(item);
                    }
                }
            }
            catch
            {
                // ignore clipboard transient errors
            }
        }

        private bool IsValidClipboardText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Length < 1) return false;
            if (text.Any(ch => ch < 32 && ch != '\n' && ch != '\r' && ch != '\t')) return false;

            string[] banned = { "using system", "system", "internal", "sync" };
            string lower = text.ToLower();
            if (banned.Any(b => lower.Contains(b))) return false;

            if (text.Length > 5000) return false;
            return true;
        }

        private void AddToHistory(ClipboardItem item)
        {
            history.Insert(0, item);
            // trim but keep pinned items
            var pinned = history.Where(h => h.IsPinned).ToList();
            var notPinned = history.Where(h => !h.IsPinned).ToList();

            if (pinned.Count + notPinned.Count > MaxHistory)
            {
                int keepNotPinned = Math.Max(0, MaxHistory - pinned.Count);
                notPinned = notPinned.Take(keepNotPinned).ToList();
            }

            history = pinned.Concat(notPinned).OrderByDescending(h => h.IsPinned).ThenByDescending(h => h.Timestamp).ToList();
            SaveHistory();
            RefreshListBox();
        }

        private void SortHistory()
        {
            history = history.OrderByDescending(h => h.IsPinned).ThenByDescending(h => h.Timestamp).ToList();
        }

        private void RefreshListBox(IEnumerable<ClipboardItem> source = null)
        {
            var current = source ?? history;
            listBoxHistory.Items.Clear();
            foreach (var item in current)
            {
                string pinMark = item.IsPinned ? "📌 " : "";
                string time = item.Timestamp.ToString("HH:mm:ss");
                string preview = item.DisplayPreview(80);
                listBoxHistory.Items.Add($"{pinMark}{time} • {preview}");
            }
            lblCount.Text = $"Items: {history.Count}";
        }

        private void CopySelected()
        {
            var idx = listBoxHistory.SelectedIndex;
            if (idx < 0) return;

            // map index in displayed list to history index: we use current filtered list (search)
            var displayed = GetFilteredList();
            if (idx >= displayed.Count) return;
            var item = displayed[idx];

            try
            {
                if (item.IsImage)
                {
                    using var ms = new MemoryStream(item.ImageContent);
                    var bmp = new Bitmap(ms);
                    Clipboard.SetImage(bmp);
                }
                else
                {
                    Clipboard.SetText(item.TextContent ?? "");
                }
            }
            catch
            {
                MessageBox.Show("Unable to set clipboard (maybe access denied).", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void TogglePinSelected()
        {
            var idx = listBoxHistory.SelectedIndex;
            if (idx < 0) return;
            var displayed = GetFilteredList();
            if (idx >= displayed.Count) return;
            var item = displayed[idx];

            // toggle pin
            item.IsPinned = !item.IsPinned;
            SortHistory();
            SaveHistory();
            RefreshListBox(GetFilteredList()); // keep filter
        }

        private void ClearHistory()
        {
            if (MessageBox.Show("Are you sure you want to delete entire history?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            history.Clear();
            SaveHistory();
            RefreshListBox();
        }

        private void ExportHistory()
        {
            using var dlg = new SaveFileDialog { Filter = "JSON Files|*.json", DefaultExt = "json" };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(history, options));
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Export failed: " + ex.Message);
                }
            }
        }

        private void ImportHistory()
        {
            using var dlg = new OpenFileDialog { Filter = "JSON Files|*.json" };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    var imported = JsonSerializer.Deserialize<List<ClipboardItem>>(File.ReadAllText(dlg.FileName));
                    if (imported != null)
                    {
                        // add preserving pins and avoiding duplicates
                        foreach (var item in imported.OrderByDescending(i => i.Timestamp))
                        {
                            bool duplicate = false;
                            if (item.IsImage)
                                duplicate = history.Any(h => h.IsImage && h.ImageContent != null && h.ImageContent.SequenceEqual(item.ImageContent));
                            else
                                duplicate = history.Any(h => !h.IsImage && h.TextContent == item.TextContent);

                            if (!duplicate)
                                history.Insert(0, item);
                        }
                        // trim/preserve pins
                        var pinned = history.Where(h => h.IsPinned).ToList();
                        var notPinned = history.Where(h => !h.IsPinned).ToList();
                        int keepNotPinned = Math.Max(0, MaxHistory - pinned.Count);
                        notPinned = notPinned.Take(keepNotPinned).ToList();
                        history = pinned.Concat(notPinned).OrderByDescending(h => h.IsPinned).ThenByDescending(h => h.Timestamp).ToList();

                        SaveHistory();
                        RefreshListBox();
                    }
                }
                catch
                {
                    MessageBox.Show("Import failed (invalid file).", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(historyFile))
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    history = JsonSerializer.Deserialize<List<ClipboardItem>>(File.ReadAllText(historyFile), options) ?? new List<ClipboardItem>();
                    SortHistory();
                }
            }
            catch
            {
                history = new List<ClipboardItem>();
            }
        }

        private void SaveHistory()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(historyFile) ?? AppDomain.CurrentDomain.BaseDirectory);
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(historyFile, JsonSerializer.Serialize(history, options));
            }
            catch
            {
                // ignore save errors
            }
        }

        private void ListBoxHistory_DoubleClick(object sender, EventArgs e)
        {
            CopySelected();
        }

        private void ListBoxHistory_SelectedIndexChanged(object sender, EventArgs e)
        {
            // optional: show preview window or tooltip
        }

        private void TxtSearch_TextChanged(object sender, EventArgs e)
        {
            var filtered = GetFilteredList();
            RefreshListBox(filtered);
        }

        private List<ClipboardItem> GetFilteredList()
        {
            string q = txtSearch.Text?.Trim().ToLower() ?? "";
            if (string.IsNullOrEmpty(q)) return history.ToList();

            return history.Where(item =>
                (item.IsImage && ("image".Contains(q) || $"[{item.DisplayPreview()}]".ToLower().Contains(q)))
                || (!item.IsImage && (item.TextContent ?? "").ToLower().Contains(q))
            ).ToList();
        }
    }
}

