using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Reflection;

[assembly: AssemblyTitle("S2MM")]
[assembly: AssemblyDescription("Subnautica 2 Mod Manager")]
[assembly: AssemblyCompany("Tj-Ace")]
[assembly: AssemblyProduct("S2MM")]
[assembly: AssemblyCopyright("Copyright (c) Tj-Ace")]
[assembly: AssemblyVersion("1.0.33.0")]
[assembly: AssemblyFileVersion("1.0.33.0")]
[assembly: AssemblyInformationalVersion("33")]

namespace S2MM
{
    internal static class Program
    {
        private const string SingleInstanceMutexName = "S2MM_SINGLE_INSTANCE_MUTEX";
        private const string IpcPipeName = "S2MM_NXM_PIPE";

        [STAThread]
        private static void Main()
        {
            string pendingProtocol = ExtractNxmProtocolFromArgs(Environment.GetCommandLineArgs());
            bool createdNew;
            using (Mutex mutex = new Mutex(true, SingleInstanceMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    string payload = string.IsNullOrWhiteSpace(pendingProtocol) ? "__SHOW__" : pendingProtocol;
                    TrySendToRunningInstance(payload);
                    return;
                }

                EnableModernTls();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(pendingProtocol));
            }
        }

        private static string ExtractNxmProtocolFromArgs(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return string.Empty;
            }

            foreach (string arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                {
                    continue;
                }

                string trimmed = arg.Trim().Trim('"');
                if (trimmed.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed;
                }
            }

            return string.Empty;
        }

        private static void EnableModernTls()
        {
            try
            {
                SecurityProtocolType tls12 = (SecurityProtocolType)3072;
                SecurityProtocolType tls13 = (SecurityProtocolType)12288;
                ServicePointManager.SecurityProtocol |= tls12 | tls13;
            }
            catch
            {
            }
        }

        private static void TrySendToRunningInstance(string payload)
        {
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", IpcPipeName, PipeDirection.Out))
                {
                    pipe.Connect(700);
                    using (StreamWriter writer = new StreamWriter(pipe))
                    {
                        writer.AutoFlush = true;
                        writer.WriteLine(payload ?? string.Empty);
                    }
                }
            }
            catch
            {
            }
        }
    }

    public class MainForm : Form
    {
        private const string IpcPipeName = "S2MM_NXM_PIPE";
        private const string SteamSubnautica2AppId = "1962700";
        private const string DefaultNexusGameDomain = "subnautica2";
        private const string CategoryUe4ss = "UE4SS";
        private const string CategorySn2Settings = "MAIN - SN2 Mod Settings";
        private const string Ue4ssNexusModId = "36";
        private const string Ue4ssNexusPageUrl = "https://www.nexusmods.com/subnautica2/mods/36?tab=files";
        private const string NexusShopUrl = "https://www.nexusmods.com/subnautica2/mods/";
        private const string S2mmNexusPageUrl = "https://www.nexusmods.com/subnautica2/mods/268?tab=description";
        private const string S2mmGithubUrl = "https://github.com/Tj-Ace/S2MM";

        private readonly string _baseDir;
        private readonly string _fileStructureDir;
        private readonly string _modsDir;
        private readonly string _configPath;
        private readonly string _modListPath;
        private readonly string _assetsDir;
        private readonly string _logsDir;
        private readonly string _nexusCacheDir;
        private readonly string _nexusImageCacheDir;
        private readonly string _startupProtocolUrl;

        private readonly FlowLayoutPanel _modCardsHost;
        private readonly Panel _contentPanel;
        private readonly TextBox _status;
        private readonly PictureBox _selectedIcon;
        private readonly Label _selectedTitle;
        private readonly Label _selectedAuthor;
        private readonly TextBox _selectedDescriptionEditor;
        private readonly ComboBox _selectedCategoryCombo;
        private readonly Label _dropHint;
        private readonly Panel _selectedDetailsPanel;
        private readonly Image _pinBadgeImage;
        private readonly Image _linkBadgeImage;
        private readonly PictureBox _reaperDecoration;
        private readonly PictureBox _nexusFooterLinkIcon;
        private readonly PictureBox _githubFooterLinkIcon;

        private readonly List<ModInfo> _mods = new List<ModInfo>();
        private readonly List<string> _uiLogLines = new List<string>();
        private readonly HashSet<string> _nexusSkipLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _nexusSearchTried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _ipcCancel = new CancellationTokenSource();

        private ModInfo _selectedMod;
        private Panel _selectedCard;

        private AppConfig _config;
        private ModListData _modListData;
        private string _currentLogPath;
        private int _uiVersion;
        private bool _subnauticaDetected;
        private string _subnauticaWin64Path;
        private string _subnauticaExePath;
        private bool _nexusAuthFailureLogged;
        private bool _nexusKeyMissingLogged;
        private bool _suppressCategorySave;
        private bool _isRefreshingModList;
        private bool _renameInProgress;
        private List<ManagedInstallRecord> _legacyInstalledMods = new List<ManagedInstallRecord>();
        private List<ModNote> _legacyModNotes = new List<ModNote>();
        private List<ModCategoryAssignment> _legacyCategories = new List<ModCategoryAssignment>();
        private List<ModLinkAssignment> _legacyLinks = new List<ModLinkAssignment>();
        private List<PinnedCategoryAssignment> _legacyPinnedCategories = new List<PinnedCategoryAssignment>();
        private List<PinnedModAssignment> _legacyPinnedMods = new List<PinnedModAssignment>();
        private Thread _ipcServerThread;

        public MainForm(string startupProtocolUrl)
        {
            _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _fileStructureDir = Path.Combine(_baseDir, "_filestructure");
            _modsDir = Path.Combine(_fileStructureDir, "mods");
            _configPath = Path.Combine(_fileStructureDir, "config.json");
            _modListPath = Path.Combine(_fileStructureDir, "modlist.json");
            _assetsDir = Path.Combine(_fileStructureDir, "assets");
            _logsDir = Path.Combine(_fileStructureDir, "logs");
            _nexusCacheDir = Path.Combine(_assetsDir, "nexus_cache");
            _nexusImageCacheDir = Path.Combine(_nexusCacheDir, "images");
            _subnauticaWin64Path = string.Empty;
            _subnauticaExePath = string.Empty;
            _startupProtocolUrl = startupProtocolUrl ?? string.Empty;

            EnsureLayout();
            EnsureNxmProtocolRegistration();
            StartIpcServer();
            SetupLogFile();
            LoadConfig();
            LoadModList();
            _uiVersion = Math.Max(1, _config.version);
            _pinBadgeImage = LoadPinBadgeImage();
            _linkBadgeImage = LoadLinkBadgeImage();

            Text = "Subnautica 2 Mod Manager";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1120, 920);
            MinimumSize = new Size(1120, 620);
            MaximumSize = new Size(1120, 2400);
            BackColor = Color.FromArgb(3, 14, 30);
            ForeColor = Color.FromArgb(225, 238, 255);
            Font = new Font("Segoe UI", 10f);
            DoubleBuffered = true;
            Paint += FormPaint;
            ApplyWindowIcon();

            Label header = new Label
            {
                Text = "Subnautica 2 Mod Manager",
                AutoSize = true,
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = Color.FromArgb(201, 244, 255),
                Location = new Point(18, 14)
            };
            Controls.Add(header);

            PictureBox logoTop = new PictureBox
            {
                Size = new Size(64, 64),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent
            };
            string logoPath = Path.Combine(_assetsDir, "s2mm.png");
            if (File.Exists(logoPath))
            {
                try
                {
                    using (Image raw = Image.FromFile(logoPath))
                    {
                        logoTop.Image = new Bitmap(raw);
                    }
                }
                catch
                {
                }
            }
            logoTop.Location = new Point((ClientSize.Width / 2) - 32, 6);
            logoTop.Anchor = AnchorStyles.Top;
            Controls.Add(logoTop);

            Label versionLabel = new Label
            {
                Text = "v" + _uiVersion,
                AutoSize = true,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(156, 226, 242),
                Location = new Point(1000, 18)
            };
            versionLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(versionLabel);

            Label subHeader = new Label
            {
                Text = "Drop .zip, .7z, or folders directly into Mod List",
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Italic),
                ForeColor = Color.FromArgb(126, 214, 233),
                Location = new Point(22, 43)
            };
            Controls.Add(subHeader);

            Panel content = new Panel
            {
                Location = new Point(18, 60),
                Size = new Size(1068, 734),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(6, 18, 36)
            };
            _contentPanel = content;
            content.Paint += ContentPaint;
            Controls.Add(content);

            Panel leftPanel = new Panel
            {
                Location = new Point(14, 14),
                Size = new Size(520, 706),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
                BackColor = Color.FromArgb(10, 31, 58),
                BorderStyle = BorderStyle.FixedSingle
            };
            content.Controls.Add(leftPanel);

            Label leftTitle = new Label
            {
                Text = "Mod List",
                AutoSize = true,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(214, 245, 255),
                Location = new Point(12, 10)
            };
            leftPanel.Controls.Add(leftTitle);

            _dropHint = new Label
            {
                Text = "Drop mods here to auto-install",
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Italic),
                ForeColor = Color.FromArgb(142, 210, 230),
                Location = new Point(332, 14)
            };
            leftPanel.Controls.Add(_dropHint);

            _modCardsHost = new FlowLayoutPanel
            {
                Location = new Point(10, 42),
                Size = new Size(488, 636),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(7, 24, 46),
                BorderStyle = BorderStyle.FixedSingle,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(8),
                AllowDrop = true
            };
            _modCardsHost.Resize += delegate { ReflowCards(); };
            _modCardsHost.DragEnter += ModCardsDragEnter;
            _modCardsHost.DragDrop += ModCardsDragDrop;
            leftPanel.Controls.Add(_modCardsHost);

            Panel rightPanel = new Panel
            {
                Location = new Point(548, 14),
                Size = new Size(506, 706),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(9, 29, 52),
                BorderStyle = BorderStyle.FixedSingle
            };
            content.Controls.Add(rightPanel);

            Label actionsTitle = new Label
            {
                Text = "Actions",
                AutoSize = true,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(214, 245, 255),
                Location = new Point(12, 10)
            };
            rightPanel.Controls.Add(actionsTitle);

            Button applyMods = CreateActionButton("Apply Mods", new Point(18, 52), 468);
            applyMods.Click += delegate { ApplyAllModsToGame(); };
            rightPanel.Controls.Add(applyMods);

            Button purgeAll = CreateActionButton("Purge All", new Point(18, 100), 468);
            purgeAll.Click += delegate { PurgeAllManagedMods(); };
            rightPanel.Controls.Add(purgeAll);

            Button refresh = CreateActionButton("Refresh", new Point(18, 148), 220);
            refresh.Click += delegate { RefreshModList(); };
            rightPanel.Controls.Add(refresh);

            Button linkAccount = CreateActionButton("Link", new Point(266, 148), 220);
            linkAccount.Click += delegate { OpenNexusAccountLinkPage(); };
            rightPanel.Controls.Add(linkAccount);

            Button installUe4ss = CreateActionButton("Install UE4SS", new Point(18, 196), 220);
            installUe4ss.Click += delegate { OpenExternalUrl(Ue4ssNexusPageUrl, "UE4SS Nexus Files Page"); };
            rightPanel.Controls.Add(installUe4ss);

            Button shopOnNexus = CreateActionButton("Shop On Nexus", new Point(266, 196), 220);
            shopOnNexus.Click += delegate { OpenExternalUrl(NexusShopUrl, "Nexus Shop"); };
            rightPanel.Controls.Add(shopOnNexus);

            Label openFoldersTitle = new Label
            {
                Text = "Open Folders",
                AutoSize = true,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(201, 242, 254),
                Location = new Point(12, 246)
            };
            rightPanel.Controls.Add(openFoldersTitle);

            Button openSubnautica = CreateActionButton("Subnautica", new Point(18, 274), 150);
            openSubnautica.Click += delegate { OpenSubnauticaFolder(); };
            rightPanel.Controls.Add(openSubnautica);

            Button openMods = CreateActionButton("Mods", new Point(178, 274), 150);
            openMods.Click += delegate { OpenManagedModsFolder(); };
            rightPanel.Controls.Add(openMods);

            Button openPaks = CreateActionButton("Paks", new Point(338, 274), 148);
            openPaks.Click += delegate { OpenPaksFolder(); };
            rightPanel.Controls.Add(openPaks);

            Label selectedTitle = new Label
            {
                Text = "Selected Mod",
                AutoSize = true,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(201, 242, 254),
                Location = new Point(12, 326)
            };
            rightPanel.Controls.Add(selectedTitle);

            Panel selectedCard = new Panel
            {
                Location = new Point(18, 358),
                Size = new Size(468, 188),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(8, 24, 44),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            rightPanel.Controls.Add(selectedCard);
            _selectedDetailsPanel = selectedCard;

            _selectedIcon = new PictureBox
            {
                Location = new Point(14, 8),
                Size = new Size(62, 62),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(11, 35, 64)
            };
            selectedCard.Controls.Add(_selectedIcon);

            _selectedTitle = new Label
            {
                Text = "No mod selected",
                Location = new Point(90, 12),
                Size = new Size(360, 24),
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(229, 244, 255),
                AutoEllipsis = true
            };
            _selectedTitle.Cursor = Cursors.Hand;
            _selectedTitle.MouseDoubleClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    EditSelectedTitle();
                }
            };
            selectedCard.Controls.Add(_selectedTitle);

            _selectedAuthor = new Label
            {
                Text = "Author: -",
                Location = new Point(90, 38),
                Size = new Size(360, 22),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                ForeColor = Color.FromArgb(164, 222, 240),
                AutoEllipsis = true
            };
            _selectedAuthor.Cursor = Cursors.Hand;
            _selectedAuthor.MouseDoubleClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    EditSelectedAuthor();
                }
            };
            selectedCard.Controls.Add(_selectedAuthor);

            _selectedDescriptionEditor = new TextBox
            {
                Location = new Point(14, 70),
                Size = new Size(436, 70),
                Font = new Font("Segoe UI", 9.3f, FontStyle.Regular),
                ForeColor = Color.FromArgb(219, 240, 255),
                BackColor = Color.FromArgb(10, 29, 52),
                BorderStyle = BorderStyle.FixedSingle,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical
            };
            _selectedDescriptionEditor.KeyDown += SelectedDescriptionEditor_KeyDown;
            selectedCard.Controls.Add(_selectedDescriptionEditor);

            Button saveDescription = CreateActionButton("Save Description", new Point(14, 146), 150);
            saveDescription.Click += delegate { SaveSelectedDescription(); };
            selectedCard.Controls.Add(saveDescription);

            _selectedCategoryCombo = new ComboBox
            {
                Location = new Point(174, 152),
                Size = new Size(276, 26),
                DropDownStyle = ComboBoxStyle.DropDown
            };
            _selectedCategoryCombo.Items.AddRange(new object[]
            {
                CategoryUe4ss,
                CategorySn2Settings,
                "MOD",
                "UTILITY",
                "VISUAL",
                "QOL",
                "GAMEPLAY",
                "OTHER"
            });
            _selectedCategoryCombo.SelectionChangeCommitted += delegate { SaveSelectedCategory(); };
            _selectedCategoryCombo.Leave += delegate { SaveSelectedCategory(); };
            _selectedCategoryCombo.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (HandleCtrlBackspace(_selectedCategoryCombo, e))
                {
                    return;
                }
                if (e.KeyCode == Keys.Enter)
                {
                    SaveSelectedCategory();
                    e.SuppressKeyPress = true;
                }
            };
            selectedCard.Controls.Add(_selectedCategoryCombo);

            _status = new TextBox
            {
                Text = "Ready",
                Size = new Size(468, 48),
                Location = new Point(18, 554),
                BorderStyle = BorderStyle.Fixed3D,
                BackColor = Color.FromArgb(6, 20, 39),
                ForeColor = Color.FromArgb(226, 240, 255),
                Font = new Font("Consolas", 8.5f, FontStyle.Regular),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = false
            };
            rightPanel.Controls.Add(_status);

            _reaperDecoration = new PictureBox
            {
                Size = new Size(132, 132),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            string reaperPath = Path.Combine(_assetsDir, "reaper.png");
            if (File.Exists(reaperPath))
            {
                try
                {
                    using (Image raw = Image.FromFile(reaperPath))
                    {
                        _reaperDecoration.Image = CreateTransparentImage(raw, 0.25f);
                    }
                }
                catch
                {
                }
            }
            Controls.Add(_reaperDecoration);
            _nexusFooterLinkIcon = CreateFooterLinkIcon("nexus.jpg");
            _githubFooterLinkIcon = CreateFooterLinkIcon("github.png");
            _nexusFooterLinkIcon.Click += delegate { OpenExternalUrl(S2mmNexusPageUrl, "S2MM Nexus page"); };
            _githubFooterLinkIcon.Click += delegate { OpenExternalUrl(S2mmGithubUrl, "S2MM GitHub page"); };
            Controls.Add(_nexusFooterLinkIcon);
            Controls.Add(_githubFooterLinkIcon);
            rightPanel.Resize += delegate { LayoutStatusPanel(rightPanel); };
            selectedCard.Resize += delegate { LayoutStatusPanel(rightPanel); };
            Resize += delegate
            {
                LayoutReaperDecoration();
                LayoutFooterLinks();
            };
            LayoutStatusPanel(rightPanel);
            LayoutReaperDecoration();
            LayoutFooterLinks();

            LogInfo("S2MM startup complete.");
            DetectAndPersistSubnautica(false);
            SetSelectedModDetails(null);
            RefreshModList();
            RenderStatusPanel();
            HandleStartupProtocolUrl();
            ApplyTitleCaseToUi();
            FormClosing += MainForm_FormClosing;
        }

        private void FormPaint(object sender, PaintEventArgs e)
        {
            using (LinearGradientBrush brush = new LinearGradientBrush(
                ClientRectangle,
                Color.FromArgb(4, 16, 35),
                Color.FromArgb(1, 40, 66),
                135f))
            {
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }

            using (Pen glow = new Pen(Color.FromArgb(40, 94, 163), 1))
            {
                e.Graphics.DrawEllipse(glow, Width - 280, -120, 420, 300);
            }
        }

        private void ContentPaint(object sender, PaintEventArgs e)
        {
            Rectangle rect = ((Control)sender).ClientRectangle;
            using (LinearGradientBrush brush = new LinearGradientBrush(
                rect,
                Color.FromArgb(7, 20, 41),
                Color.FromArgb(3, 34, 59),
                100f))
            {
                e.Graphics.FillRectangle(brush, rect);
            }
        }

        private Button CreateActionButton(string text, Point location, int width)
        {
            Button button = new Button
            {
                Text = text,
                Location = location,
                Size = new Size(width, 38),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(12, 73, 113),
                ForeColor = Color.FromArgb(235, 248, 255),
                Font = new Font("Segoe UI", 9.2f, FontStyle.Bold)
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(67, 149, 197);
            button.FlatAppearance.BorderSize = 1;
            return button;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private void ApplyWindowIcon()
        {
            string iconPng = Path.Combine(_assetsDir, "icon_main.png");
            if (!File.Exists(iconPng))
            {
                return;
            }

            try
            {
                using (Bitmap source = new Bitmap(iconPng))
                using (Bitmap bmp = CropTransparentBitmap(source))
                {
                    IntPtr handle = bmp.GetHicon();
                    Icon temp = Icon.FromHandle(handle);
                    Icon cloned = (Icon)temp.Clone();
                    temp.Dispose();
                    DestroyIcon(handle);
                    Icon = cloned;
                }
            }
            catch (Exception ex)
            {
                LogInfo("Icon apply failed: " + ex.Message);
            }
        }

        private Image LoadPinBadgeImage()
        {
            string pinPath = Path.Combine(_assetsDir, "pin.png");
            if (!File.Exists(pinPath))
            {
                return null;
            }

            try
            {
                using (Image source = Image.FromFile(pinPath))
                {
                    return new Bitmap(source);
                }
            }
            catch
            {
                return null;
            }
        }

        private Image LoadLinkBadgeImage()
        {
            string linkPath = Path.Combine(_assetsDir, "link.png");
            if (!File.Exists(linkPath))
            {
                return null;
            }

            try
            {
                using (Image source = Image.FromFile(linkPath))
                {
                    return new Bitmap(source);
                }
            }
            catch
            {
                return null;
            }
        }

        private Bitmap CropTransparentBitmap(Bitmap source)
        {
            if (source == null)
            {
                return new Bitmap(256, 256);
            }

            int minX = source.Width;
            int minY = source.Height;
            int maxX = -1;
            int maxY = -1;
            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    if (source.GetPixel(x, y).A > 8)
                    {
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (maxX < minX || maxY < minY)
            {
                return new Bitmap(source);
            }

            Rectangle bounds = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            Bitmap trimmed = source.Clone(bounds, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            int size = Math.Max(64, Math.Max(trimmed.Width, trimmed.Height));
            Bitmap padded = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(padded))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                float scale = 0.95f;
                int drawW = Math.Max(1, (int)(trimmed.Width * scale * size / Math.Max(trimmed.Width, trimmed.Height)));
                int drawH = Math.Max(1, (int)(trimmed.Height * scale * size / Math.Max(trimmed.Width, trimmed.Height)));
                int x = (size - drawW) / 2;
                int y = (size - drawH) / 2;
                g.DrawImage(trimmed, new Rectangle(x, y, drawW, drawH));
            }

            trimmed.Dispose();
            return padded;
        }

        private void LayoutStatusPanel(Control rightPanel)
        {
            if (rightPanel == null || _status == null || _selectedDetailsPanel == null)
            {
                return;
            }

            int margin = 18;
            int spacing = 8;
            int top = _selectedDetailsPanel.Bottom + spacing;
            int width = Math.Max(120, rightPanel.ClientSize.Width - (margin * 2));
            int height = Math.Max(48, rightPanel.ClientSize.Height - top - 10);

            _status.Location = new Point(margin, top);
            _status.Size = new Size(width, height);
        }

        private void LayoutReaperDecoration()
        {
            if (_reaperDecoration != null)
            {
                int rightMargin = 18;
                int bottomMargin = 6;
                int x = ClientSize.Width - rightMargin - _reaperDecoration.Width;
                int y = ClientSize.Height - bottomMargin - _reaperDecoration.Height;
                if (_contentPanel != null)
                {
                    y = Math.Max(_contentPanel.Bottom + 4, y);
                }
                _reaperDecoration.Location = new Point(x, y);
                _reaperDecoration.BringToFront();
            }
        }

        private PictureBox CreateFooterLinkIcon(params string[] fileNames)
        {
            PictureBox box = new PictureBox
            {
                Size = new Size(40, 40),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };

            foreach (string name in fileNames)
            {
                string path = Path.Combine(_assetsDir, name);
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    using (Image raw = Image.FromFile(path))
                    {
                        box.Image = new Bitmap(raw);
                    }
                    break;
                }
                catch
                {
                }
            }

            return box;
        }

        private void LayoutFooterLinks()
        {
            if (_githubFooterLinkIcon == null || _nexusFooterLinkIcon == null)
            {
                return;
            }

            int left = 20;
            int spacing = 10;
            int y = ClientSize.Height - _githubFooterLinkIcon.Height - 14;
            _githubFooterLinkIcon.Location = new Point(left, y);
            _nexusFooterLinkIcon.Location = new Point(left + _githubFooterLinkIcon.Width + spacing, y);
            _githubFooterLinkIcon.BringToFront();
            _nexusFooterLinkIcon.BringToFront();
        }

        private void OpenExternalUrl(string url, string label)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };
                Process.Start(psi);
                LogInfo("Opened " + label + ": " + url);
            }
            catch (Exception ex)
            {
                LogInfo("Open " + label + " failed: " + ex.Message);
            }
        }

        private void StartIpcServer()
        {
            _ipcServerThread = new Thread(IpcServerLoop);
            _ipcServerThread.IsBackground = true;
            _ipcServerThread.Name = "S2MM_IPC_Server";
            _ipcServerThread.Start();
        }

        private void IpcServerLoop()
        {
            while (!_ipcCancel.IsCancellationRequested)
            {
                try
                {
                    using (NamedPipeServerStream server = new NamedPipeServerStream(IpcPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        server.WaitForConnection();
                        using (StreamReader reader = new StreamReader(server))
                        {
                            string payload = reader.ReadLine() ?? string.Empty;
                            if (IsHandleCreated && !IsDisposed)
                            {
                                BeginInvoke((Action)(delegate
                                {
                                    HandleIpcPayload(payload);
                                }));
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private void HandleIpcPayload(string payload)
        {
            BringMainWindowToFront();
            string data = (payload ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(data) || string.Equals(data, "__SHOW__", StringComparison.OrdinalIgnoreCase))
            {
                LogInfo("Received IPC show request.");
                return;
            }

            if (data.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
            {
                LogInfo("Received NXM link via IPC.");
                ProcessNxmProtocolUrl(data);
            }
        }

        private void BringMainWindowToFront()
        {
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
            Show();
            Activate();
            BringToFront();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                _ipcCancel.Cancel();
            }
            catch
            {
            }
        }

        private void ApplyTitleCaseToUi()
        {
            ApplyTitleCaseRecursive(this);
        }

        private void ApplyTitleCaseRecursive(Control root)
        {
            if (root == null)
            {
                return;
            }

            if (!(root is TextBoxBase))
            {
                string text = root.Text ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text) && !text.Contains("://"))
                {
                    root.Text = ToUiTitleCase(text);
                }
            }

            foreach (Control child in root.Controls)
            {
                ApplyTitleCaseRecursive(child);
            }
        }

        private string ToUiTitleCase(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string value = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
            value = value.Replace("Ue4Ss", "UE4SS");
            value = value.Replace("Sn2", "SN2");
            value = value.Replace("S2Mm", "S2MM");
            value = value.Replace(" Url", " URL");
            value = value.Replace("Api", "API");
            value = value.Replace("Nxm", "NXM");
            return value;
        }

        private Image CreateTransparentImage(Image source, float opacity)
        {
            if (source == null)
            {
                return null;
            }

            float alpha = Math.Max(0f, Math.Min(1f, opacity));
            Bitmap output = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(output))
            using (ImageAttributes attrs = new ImageAttributes())
            {
                ColorMatrix matrix = new ColorMatrix();
                matrix.Matrix33 = alpha;
                attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.DrawImage(source,
                    new Rectangle(0, 0, output.Width, output.Height),
                    0, 0, source.Width, source.Height,
                    GraphicsUnit.Pixel,
                    attrs);
            }
            return output;
        }

        private void EnsureLayout()
        {
            Directory.CreateDirectory(_fileStructureDir);
            Directory.CreateDirectory(_modsDir);
            Directory.CreateDirectory(_assetsDir);
            Directory.CreateDirectory(_logsDir);
            Directory.CreateDirectory(_nexusCacheDir);
            Directory.CreateDirectory(_nexusImageCacheDir);
        }

        private void SetupLogFile()
        {
            string fileName = "s2mm_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log";
            _currentLogPath = Path.Combine(_logsDir, fileName);
            File.AppendAllText(_currentLogPath, "=== S2MM Session Start " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===" + Environment.NewLine);
        }

        private void LoadConfig()
        {
            AppConfig cfg = null;
            try
            {
                if (!File.Exists(_configPath))
                {
                    cfg = CreateDefaultConfig();
                    SaveConfig(cfg);
                    _config = cfg;
                    LogInfo("Created new config.json.");
                    return;
                }

                string content = File.ReadAllText(_configPath).Trim();
                if (string.IsNullOrWhiteSpace(content) || content.StartsWith("["))
                {
                    cfg = CreateDefaultConfig();
                    SaveConfig(cfg);
                    _config = cfg;
                    LogInfo("Config format updated to object schema.");
                    return;
                }

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                try
                {
                    LegacyConfig legacy = serializer.Deserialize<LegacyConfig>(content);
                    if (legacy != null)
                    {
                        _legacyInstalledMods = legacy.installedMods ?? new List<ManagedInstallRecord>();
                        _legacyModNotes = legacy.modNotes ?? new List<ModNote>();
                        _legacyCategories = legacy.modCategories ?? new List<ModCategoryAssignment>();
                        _legacyLinks = legacy.modLinks ?? new List<ModLinkAssignment>();
                        _legacyPinnedCategories = legacy.pinnedCategories ?? new List<PinnedCategoryAssignment>();
                        _legacyPinnedMods = legacy.pinnedMods ?? new List<PinnedModAssignment>();
                    }
                }
                catch
                {
                }
                cfg = serializer.Deserialize<AppConfig>(content);
            }
            catch (Exception ex)
            {
                LogInfo("Config load failed, using defaults: " + ex.Message);
                cfg = CreateDefaultConfig();
            }

            _config = NormalizeConfig(cfg);
            SaveConfig(_config);
            LogInfo("Config loaded.");
        }

        private void LoadModList()
        {
            ModListData data = null;
            try
            {
                if (!File.Exists(_modListPath))
                {
                    data = CreateDefaultModListData();
                    if (_legacyInstalledMods.Count > 0)
                    {
                        data.installedMods = _legacyInstalledMods;
                    }
                    if (_legacyModNotes.Count > 0)
                    {
                        data.modNotes = _legacyModNotes;
                    }
                    if (_legacyCategories.Count > 0)
                    {
                        data.modCategories = _legacyCategories;
                    }
                    if (_legacyLinks.Count > 0)
                    {
                        data.modLinks = _legacyLinks;
                    }
                    if (_legacyPinnedCategories.Count > 0)
                    {
                        data.pinnedCategories = _legacyPinnedCategories;
                    }
                    if (_legacyPinnedMods.Count > 0)
                    {
                        data.pinnedMods = _legacyPinnedMods;
                    }
                    SaveModListData(data);
                    _modListData = data;
                    LogInfo("Created new modlist.json.");
                    return;
                }

                string content = File.ReadAllText(_modListPath).Trim();
                if (string.IsNullOrWhiteSpace(content))
                {
                    data = CreateDefaultModListData();
                    SaveModListData(data);
                    _modListData = data;
                    LogInfo("modlist.json was empty and has been reset.");
                    return;
                }

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                data = serializer.Deserialize<ModListData>(content);
            }
            catch (Exception ex)
            {
                LogInfo("Modlist load failed, using defaults: " + ex.Message);
                data = CreateDefaultModListData();
            }

            _modListData = NormalizeModListData(data);
            NormalizeCategoryAliasesInModList(_modListData);
            SaveModListData(_modListData);
            LogInfo("Modlist loaded.");
        }

        private AppConfig CreateDefaultConfig()
        {
            return new AppConfig
            {
                subnauticaExePath = string.Empty,
                subnauticaWin64Path = string.Empty,
                nexusApiKey = string.Empty,
                nexusGameDomain = DefaultNexusGameDomain,
                version = 0
            };
        }

        private AppConfig NormalizeConfig(AppConfig cfg)
        {
            AppConfig result = cfg ?? CreateDefaultConfig();
            if (string.IsNullOrWhiteSpace(result.nexusGameDomain))
            {
                result.nexusGameDomain = DefaultNexusGameDomain;
            }
            if (result.subnauticaExePath == null)
            {
                result.subnauticaExePath = string.Empty;
            }
            if (result.subnauticaWin64Path == null)
            {
                result.subnauticaWin64Path = string.Empty;
            }
            if (result.nexusApiKey == null)
            {
                result.nexusApiKey = string.Empty;
            }
            if (result.version < 0)
            {
                result.version = 0;
            }

            return result;
        }

        private ModListData CreateDefaultModListData()
        {
            return new ModListData
            {
                installedMods = new List<ManagedInstallRecord>(),
                modNotes = new List<ModNote>(),
                modCategories = new List<ModCategoryAssignment>(),
                modLinks = new List<ModLinkAssignment>(),
                modIdentityOverrides = new List<ModIdentityOverride>(),
                pinnedCategories = new List<PinnedCategoryAssignment>(),
                pinnedMods = new List<PinnedModAssignment>()
            };
        }

        private ModListData NormalizeModListData(ModListData data)
        {
            ModListData result = data ?? CreateDefaultModListData();
            if (result.installedMods == null)
            {
                result.installedMods = new List<ManagedInstallRecord>();
            }
            if (result.modNotes == null)
            {
                result.modNotes = new List<ModNote>();
            }
            if (result.modCategories == null)
            {
                result.modCategories = new List<ModCategoryAssignment>();
            }
            if (result.modLinks == null)
            {
                result.modLinks = new List<ModLinkAssignment>();
            }
            if (result.modIdentityOverrides == null)
            {
                result.modIdentityOverrides = new List<ModIdentityOverride>();
            }
            if (result.pinnedCategories == null)
            {
                result.pinnedCategories = new List<PinnedCategoryAssignment>();
            }
            if (result.pinnedMods == null)
            {
                result.pinnedMods = new List<PinnedModAssignment>();
            }

            foreach (ManagedInstallRecord item in result.installedMods)
            {
                if (item.deployedPaths == null)
                {
                    item.deployedPaths = new List<string>();
                }
                if (item.modFolderName == null)
                {
                    item.modFolderName = string.Empty;
                }
                if (item.installedAtUtc == null)
                {
                    item.installedAtUtc = string.Empty;
                }
            }

            foreach (ModNote note in result.modNotes)
            {
                if (note.modFolderName == null)
                {
                    note.modFolderName = string.Empty;
                }
                if (note.description == null)
                {
                    note.description = string.Empty;
                }
            }

            foreach (ModCategoryAssignment category in result.modCategories)
            {
                if (category.modFolderName == null)
                {
                    category.modFolderName = string.Empty;
                }
                if (category.category == null)
                {
                    category.category = string.Empty;
                }
            }

            foreach (ModLinkAssignment link in result.modLinks)
            {
                if (link.modFolderName == null)
                {
                    link.modFolderName = string.Empty;
                }
                if (link.url == null)
                {
                    link.url = string.Empty;
                }
            }

            foreach (ModIdentityOverride row in result.modIdentityOverrides)
            {
                if (row.modFolderName == null)
                {
                    row.modFolderName = string.Empty;
                }
                if (row.title == null)
                {
                    row.title = string.Empty;
                }
                if (row.author == null)
                {
                    row.author = string.Empty;
                }
            }

            foreach (PinnedCategoryAssignment pin in result.pinnedCategories)
            {
                if (pin.category == null)
                {
                    pin.category = string.Empty;
                }
            }

            foreach (PinnedModAssignment pin in result.pinnedMods)
            {
                if (pin.modFolderName == null)
                {
                    pin.modFolderName = string.Empty;
                }
            }

            return result;
        }

        private void NormalizeCategoryAliasesInModList(ModListData data)
        {
            if (data == null)
            {
                return;
            }

            if (data.modCategories != null)
            {
                foreach (ModCategoryAssignment row in data.modCategories)
                {
                    row.category = NormalizeCategoryName(row.category);
                }
            }

            if (data.pinnedCategories != null)
            {
                foreach (PinnedCategoryAssignment row in data.pinnedCategories)
                {
                    row.category = NormalizeCategoryName(row.category);
                }

                data.pinnedCategories = data.pinnedCategories
                    .GroupBy(delegate(PinnedCategoryAssignment n) { return NormalizeCategoryName(n.category); }, StringComparer.OrdinalIgnoreCase)
                    .Select(delegate(IGrouping<string, PinnedCategoryAssignment> group)
                    {
                        return new PinnedCategoryAssignment
                        {
                            category = group.Key,
                            pinned = group.Any(delegate(PinnedCategoryAssignment x) { return x.pinned; })
                        };
                    })
                    .ToList();
            }
        }

        private void SaveConfig(AppConfig config)
        {
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(config);
                string pretty = FormatJsonPretty(json);
                File.WriteAllText(_configPath, pretty);
            }
            catch (Exception ex)
            {
                LogInfo("Config save failed: " + ex.Message);
            }
        }

        private void SaveModListData(ModListData data)
        {
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(data);
                string pretty = FormatJsonPretty(json);
                File.WriteAllText(_modListPath, pretty);
            }
            catch (Exception ex)
            {
                LogInfo("Modlist save failed: " + ex.Message);
            }
        }

        private string FormatJsonPretty(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return "{}";
            }

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                object root = serializer.DeserializeObject(rawJson);
                StringBuilder sb = new StringBuilder();
                AppendJsonValue(sb, root, 0);
                return sb.ToString();
            }
            catch
            {
                return rawJson;
            }
        }

        private void AppendJsonValue(StringBuilder sb, object value, int depth)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            IDictionary<string, object> map = value as IDictionary<string, object>;
            if (map != null)
            {
                AppendJsonObject(sb, map, depth);
                return;
            }

            object[] arr = value as object[];
            if (arr != null)
            {
                AppendJsonArray(sb, arr, depth);
                return;
            }

            if (value is string)
            {
                sb.Append("\"");
                sb.Append(EscapeJsonString((string)value));
                sb.Append("\"");
                return;
            }

            if (value is bool)
            {
                sb.Append((bool)value ? "true" : "false");
                return;
            }

            if (value is int || value is long || value is float || value is double || value is decimal)
            {
                sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
            }

            sb.Append("\"");
            sb.Append(EscapeJsonString(value.ToString()));
            sb.Append("\"");
        }

        private void AppendJsonObject(StringBuilder sb, IDictionary<string, object> map, int depth)
        {
            if (map.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append("{");
            sb.AppendLine();

            int index = 0;
            foreach (KeyValuePair<string, object> kv in map)
            {
                sb.Append(new string(' ', (depth + 1) * 2));
                sb.Append("\"");
                sb.Append(EscapeJsonString(kv.Key));
                sb.Append("\": ");
                AppendJsonValue(sb, kv.Value, depth + 1);
                index++;
                if (index < map.Count)
                {
                    sb.Append(",");
                }
                sb.AppendLine();
            }

            sb.Append(new string(' ', depth * 2));
            sb.Append("}");
        }

        private void AppendJsonArray(StringBuilder sb, object[] arr, int depth)
        {
            if (arr.Length == 0)
            {
                sb.Append("[]");
                return;
            }

            sb.Append("[");
            sb.AppendLine();

            for (int i = 0; i < arr.Length; i++)
            {
                sb.Append(new string(' ', (depth + 1) * 2));
                AppendJsonValue(sb, arr[i], depth + 1);
                if (i < arr.Length - 1)
                {
                    sb.Append(",");
                }
                sb.AppendLine();
            }

            sb.Append(new string(' ', depth * 2));
            sb.Append("]");
        }

        private string EscapeJsonString(string s)
        {
            if (s == null)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 32)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        private void DetectAndPersistSubnautica(bool manualButtonPressed)
        {
            string foundExe = string.Empty;
            string foundWin64 = string.Empty;

            if (IsValidSubnauticaPaths(_config.subnauticaExePath, _config.subnauticaWin64Path))
            {
                foundExe = _config.subnauticaExePath;
                foundWin64 = _config.subnauticaWin64Path;
                LogInfo("Using saved Subnautica 2 location from config.");
            }
            else
            {
                if (!TryAutoDetectSubnautica(out foundExe, out foundWin64))
                {
                    _subnauticaDetected = false;
                    _subnauticaExePath = string.Empty;
                    _subnauticaWin64Path = string.Empty;
                    _config.subnauticaExePath = string.Empty;
                    _config.subnauticaWin64Path = string.Empty;
                    SaveConfig(_config);
                    if (manualButtonPressed)
                    {
                        LogInfo("Subnautica 2 not detected automatically.");
                    }
                    RenderStatusPanel();
                    return;
                }

                _config.subnauticaExePath = foundExe;
                _config.subnauticaWin64Path = foundWin64;
                SaveConfig(_config);
                LogInfo("Auto-detected Subnautica 2 at: " + foundWin64);
            }

            _subnauticaDetected = true;
            _subnauticaExePath = foundExe;
            _subnauticaWin64Path = foundWin64;
            RenderStatusPanel();
        }

        private bool TryAutoDetectSubnautica(out string exePath, out string win64Path)
        {
            exePath = string.Empty;
            win64Path = string.Empty;

            if (TryDetectFromSteamLibraries(out exePath, out win64Path))
            {
                return true;
            }

            if (TryDetectFromCommonLocations(out exePath, out win64Path))
            {
                return true;
            }

            return false;
        }

        private bool TryDetectFromSteamLibraries(out string exePath, out string win64Path)
        {
            exePath = string.Empty;
            win64Path = string.Empty;

            List<string> steamRoots = GetSteamRoots();
            foreach (string root in steamRoots)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                string steamApps = Path.Combine(root, "steamapps");
                if (!Directory.Exists(steamApps))
                {
                    continue;
                }

                List<string> librarySteamApps = new List<string>();
                librarySteamApps.Add(steamApps);

                string librariesVdf = Path.Combine(steamApps, "libraryfolders.vdf");
                foreach (string path in ParseLibraryVdfPaths(librariesVdf))
                {
                    string candidateSteamApps = Path.Combine(path, "steamapps");
                    if (Directory.Exists(candidateSteamApps))
                    {
                        librarySteamApps.Add(candidateSteamApps);
                    }
                }

                foreach (string libSteamApps in librarySteamApps.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string manifest = Path.Combine(libSteamApps, "appmanifest_" + SteamSubnautica2AppId + ".acf");
                    if (!File.Exists(manifest))
                    {
                        continue;
                    }

                    string installDir = ParseInstallDirFromManifest(manifest);
                    if (string.IsNullOrWhiteSpace(installDir))
                    {
                        continue;
                    }

                    string common = Path.Combine(libSteamApps, "common");
                    string baseDir = Path.Combine(common, installDir);
                    if (TryResolveGamePathsFromBase(baseDir, out exePath, out win64Path))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryDetectFromCommonLocations(out string exePath, out string win64Path)
        {
            exePath = string.Empty;
            win64Path = string.Empty;

            string[] candidates = new[]
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\Subnautica2",
                @"C:\Program Files\Steam\steamapps\common\Subnautica2",
                @"D:\SteamLibrary\steamapps\common\Subnautica2",
                @"E:\SteamLibrary\steamapps\common\Subnautica2",
                @"F:\SteamLibrary\steamapps\common\Subnautica2"
            };

            foreach (string baseDir in candidates)
            {
                if (TryResolveGamePathsFromBase(baseDir, out exePath, out win64Path))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryResolveGamePathsFromBase(string baseDir, out string exePath, out string win64Path)
        {
            exePath = string.Empty;
            win64Path = string.Empty;

            if (!Directory.Exists(baseDir))
            {
                return false;
            }

            string[] win64Candidates = new[]
            {
                Path.Combine(baseDir, "Subnautica2", "Binaries", "Win64"),
                Path.Combine(baseDir, "Binaries", "Win64")
            };

            foreach (string candidateWin64 in win64Candidates)
            {
                if (!Directory.Exists(candidateWin64))
                {
                    continue;
                }

                string shippingExe = Path.Combine(candidateWin64, "Subnautica2-Win64-Shipping.exe");
                string fallbackExe = Path.Combine(baseDir, "Subnautica2.exe");
                if (File.Exists(shippingExe))
                {
                    exePath = shippingExe;
                    win64Path = candidateWin64;
                    return true;
                }
                if (File.Exists(fallbackExe))
                {
                    exePath = fallbackExe;
                    win64Path = candidateWin64;
                    return true;
                }
            }

            string directExe = Path.Combine(baseDir, "Subnautica2.exe");
            if (File.Exists(directExe))
            {
                string win64 = ResolveWin64FromExePath(directExe);
                if (!string.IsNullOrEmpty(win64))
                {
                    exePath = directExe;
                    win64Path = win64;
                    return true;
                }
            }

            return false;
        }

        private List<string> GetSteamRoots()
        {
            List<string> roots = new List<string>();
            roots.Add(ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"));
            roots.Add(ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
            roots.Add(ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"));
            roots.Add(@"C:\Program Files (x86)\Steam");
            roots.Add(@"C:\Program Files\Steam");

            return roots.Where(delegate(string p) { return !string.IsNullOrWhiteSpace(p); })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private string ReadRegistryString(RegistryKey hive, string keyPath, string valueName)
        {
            try
            {
                RegistryKey key = hive.OpenSubKey(keyPath);
                if (key == null)
                {
                    return string.Empty;
                }
                object value = key.GetValue(valueName);
                if (value == null)
                {
                    return string.Empty;
                }
                return value.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private IEnumerable<string> ParseLibraryVdfPaths(string librariesVdfPath)
        {
            List<string> output = new List<string>();
            if (!File.Exists(librariesVdfPath))
            {
                return output;
            }

            try
            {
                string text = File.ReadAllText(librariesVdfPath);
                MatchCollection matches = Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    string raw = match.Groups[1].Value;
                    string normalized = raw.Replace(@"\\", @"\");
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        output.Add(normalized);
                    }
                }
            }
            catch
            {
            }

            return output;
        }

        private string ParseInstallDirFromManifest(string manifestPath)
        {
            try
            {
                string text = File.ReadAllText(manifestPath);
                Match m = Regex.Match(text, "\"installdir\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    return m.Groups[1].Value.Trim();
                }
            }
            catch
            {
            }
            return string.Empty;
        }

        private bool IsValidSubnauticaPaths(string exePath, string win64Path)
        {
            if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(win64Path))
            {
                return false;
            }
            if (!File.Exists(exePath))
            {
                return false;
            }
            if (!Directory.Exists(win64Path))
            {
                return false;
            }
            return true;
        }

        private void ChooseSubnauticaInstallation()
        {
            DetectAndPersistSubnautica(true);

            string initialDir = _subnauticaDetected
                ? _subnauticaWin64Path
                : GetBestDefaultBrowsePath();

            using (OpenFileDialog picker = new OpenFileDialog())
            {
                picker.Title = "Select Subnautica 2 executable";
                picker.Filter = "Executable files (*.exe)|*.exe";
                picker.CheckFileExists = true;
                picker.Multiselect = false;
                if (Directory.Exists(initialDir))
                {
                    picker.InitialDirectory = initialDir;
                }

                if (picker.ShowDialog(this) != DialogResult.OK)
                {
                    LogInfo("Manual Subnautica installation selection canceled.");
                    return;
                }

                string selectedExe = picker.FileName;
                string win64 = ResolveWin64FromExePath(selectedExe);
                if (string.IsNullOrEmpty(win64))
                {
                    LogInfo("Selected exe did not resolve to Subnautica2\\Binaries\\Win64.");
                    MessageBox.Show(
                        this,
                        "Selected file did not match a Subnautica 2 Win64 install path.",
                        "Invalid Selection",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                    return;
                }

                _subnauticaDetected = true;
                _subnauticaExePath = selectedExe;
                _subnauticaWin64Path = win64;
                _config.subnauticaExePath = selectedExe;
                _config.subnauticaWin64Path = win64;
                SaveConfig(_config);
                LogInfo("Manual Subnautica installation set to: " + win64);
                RenderStatusPanel();
            }
        }

        private string GetBestDefaultBrowsePath()
        {
            string[] options = new[]
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\Subnautica2",
                @"C:\Program Files\Steam\steamapps\common\Subnautica2",
                @"D:\SteamLibrary\steamapps\common\Subnautica2",
                @"E:\SteamLibrary\steamapps\common\Subnautica2",
                @"F:\SteamLibrary\steamapps\common\Subnautica2"
            };
            foreach (string option in options)
            {
                if (Directory.Exists(option))
                {
                    return option;
                }
            }

            string steam = @"C:\Program Files (x86)\Steam";
            if (Directory.Exists(steam))
            {
                return steam;
            }

            return _baseDir;
        }

        private string ResolveWin64FromExePath(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return string.Empty;
            }

            try
            {
                string exeDir = Path.GetDirectoryName(exePath);
                if (string.IsNullOrWhiteSpace(exeDir))
                {
                    return string.Empty;
                }

                if (string.Equals(Path.GetFileName(exeDir), "Win64", StringComparison.OrdinalIgnoreCase))
                {
                    return exeDir;
                }

                string candidate1 = Path.Combine(exeDir, "Subnautica2", "Binaries", "Win64");
                if (Directory.Exists(candidate1))
                {
                    return candidate1;
                }

                string candidate2 = Path.Combine(exeDir, "Binaries", "Win64");
                if (Directory.Exists(candidate2))
                {
                    return candidate2;
                }

                string candidate3 = Path.Combine(Directory.GetParent(exeDir).FullName, "Binaries", "Win64");
                if (Directory.Exists(candidate3))
                {
                    return candidate3;
                }

                string candidate4 = Path.Combine(Directory.GetParent(exeDir).FullName, "Subnautica2", "Binaries", "Win64");
                if (Directory.Exists(candidate4))
                {
                    return candidate4;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private void OpenNexusAccountLinkPage()
        {
            string nexusUrl = "https://www.nexusmods.com/users/myaccount?tab=api%20access";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = nexusUrl;
                psi.UseShellExecute = true;
                Process.Start(psi);
                LogInfo("Opened Nexus account link page.");
            }
            catch (Exception ex)
            {
                LogInfo("Failed to open Nexus link page: " + ex.Message);
                MessageBox.Show(
                    this,
                    "Could not open Nexus link page.\nURL: " + nexusUrl,
                    "Open Link Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }

        private void OpenSubnauticaFolder()
        {
            string path = GetSubnauticaRootPath();
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                LogInfo("Open Subnautica folder failed: game path not detected.");
                return;
            }

            OpenFolderInExplorer(path, "Subnautica");
        }

        private void OpenManagedModsFolder()
        {
            Directory.CreateDirectory(_modsDir);
            OpenFolderInExplorer(_modsDir, "Mods");
        }

        private void OpenPaksFolder()
        {
            string pakMods = GetPakModsTargetPath();
            if (string.IsNullOrWhiteSpace(pakMods))
            {
                string gameRoot = GetSubnauticaRootPath();
                if (!string.IsNullOrWhiteSpace(gameRoot))
                {
                    pakMods = Path.Combine(gameRoot, "Content", "Paks");
                }
            }

            if (string.IsNullOrWhiteSpace(pakMods))
            {
                LogInfo("Open Paks folder failed: game path not detected.");
                return;
            }

            Directory.CreateDirectory(pakMods);
            OpenFolderInExplorer(pakMods, "Paks");
        }

        private void OpenFolderInExplorer(string folderPath, string label)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = folderPath,
                    UseShellExecute = true
                };
                Process.Start(psi);
                LogInfo("Opened " + label + " folder: " + folderPath);
            }
            catch (Exception ex)
            {
                LogInfo("Open " + label + " folder failed: " + ex.Message);
            }
        }

        private string GetSubnauticaRootPath()
        {
            if (string.IsNullOrWhiteSpace(_subnauticaWin64Path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(Path.Combine(_subnauticaWin64Path, "..", "..", ".."));
            }
            catch
            {
                return string.Empty;
            }
        }

        private void RefreshModList()
        {
            if (_isRefreshingModList)
            {
                return;
            }

            _isRefreshingModList = true;
            try
            {
                _mods.Clear();
                _modCardsHost.Controls.Clear();

                if (!Directory.Exists(_modsDir))
                {
                    Directory.CreateDirectory(_modsDir);
                }

                string[] modFolders = Directory.GetDirectories(_modsDir).OrderBy(delegate(string n) { return n; }).ToArray();
                foreach (string folder in modFolders)
                {
                    ModInfo info = BuildModInfo(folder);
                    info.Category = NormalizeCategoryName(info.Category);
                    _mods.Add(info);
                }

                bool autoPinChanged = EnsureAutoPinnedSystemCategories();
                if (autoPinChanged)
                {
                    SaveModListData(_modListData);
                }

                List<ModInfo> ordered = _mods
                    .OrderBy(delegate(ModInfo m) { return GetCategoryPinRank(m.Category); })
                    .ThenBy(delegate(ModInfo m) { return GetCategoryRank(m.Category); })
                    .ThenBy(delegate(ModInfo m) { return m.Category; }, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(delegate(ModInfo m) { return GetModPinRank(Path.GetFileName(m.FolderPath)); })
                    .ThenBy(delegate(ModInfo m) { return m.Title; }, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _mods.Clear();
                _mods.AddRange(ordered);

                string activeCategory = string.Empty;
                foreach (ModInfo item in _mods)
                {
                    if (!string.Equals(activeCategory, item.Category, StringComparison.OrdinalIgnoreCase))
                    {
                        activeCategory = item.Category;
                        _modCardsHost.Controls.Add(CreateCategoryHeader(activeCategory));
                    }
                    _modCardsHost.Controls.Add(CreateModCard(item));
                }

                _dropHint.Visible = _mods.Count == 0;
                LogInfo("Refreshed mod list (" + _mods.Count + " item(s)).");

                if (_selectedMod != null)
                {
                    ModInfo reselect = _mods.FirstOrDefault(delegate(ModInfo m)
                    {
                        return string.Equals(m.FolderPath, _selectedMod.FolderPath, StringComparison.OrdinalIgnoreCase);
                    });
                    SetSelectedModDetails(reselect);
                }
                else
                {
                    SetSelectedModDetails(null);
                }

                ReflowCards();
                RenderStatusPanel();
            }
            finally
            {
                _isRefreshingModList = false;
            }
        }

        private bool EnsureAutoPinnedSystemCategories()
        {
            bool changed = false;
            bool hasUe4ss = _mods.Any(delegate(ModInfo mod)
            {
                return string.Equals(NormalizeCategoryName(mod.Category), CategoryUe4ss, StringComparison.OrdinalIgnoreCase);
            });

            if (hasUe4ss && !IsCategoryPinned(CategoryUe4ss))
            {
                SetCategoryPinned(CategoryUe4ss, true);
                changed = true;
                LogInfo("Auto-pinned UE4SS category.");
            }

            return changed;
        }

        private Panel CreateCategoryHeader(string category)
        {
            bool pinned = IsCategoryPinned(category);
            Panel row = new Panel
            {
                Height = 28,
                Width = Math.Max(120, _modCardsHost.ClientSize.Width - 28),
                Margin = new Padding(0, 0, 0, 8),
                BackColor = Color.FromArgb(6, 37, 62),
                BorderStyle = BorderStyle.FixedSingle,
                Tag = "CATEGORY_HEADER"
            };

            Label title = new Label
            {
                Text = category,
                Location = new Point(8, 4),
                Size = new Size(row.Width - (pinned ? 40 : 16), 18),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(179, 234, 248)
            };
            row.Controls.Add(title);

            if (pinned && _pinBadgeImage != null)
            {
                PictureBox pin = new PictureBox
                {
                    Size = new Size(16, 16),
                    Location = new Point(row.Width - 24, 5),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Color.Transparent,
                    Image = _pinBadgeImage,
                    Tag = "PIN_BADGE"
                };
                row.Controls.Add(pin);
                AttachCategoryPinHandler(pin, category);
                AttachCategoryRenameHandler(pin, category);
            }

            AttachCategoryPinHandler(row, category);
            AttachCategoryPinHandler(title, category);
            AttachCategoryRenameHandler(row, category);
            AttachCategoryRenameHandler(title, category);
            return row;
        }

        private Panel CreateModCard(ModInfo mod)
        {
            bool isPinned = IsModPinned(Path.GetFileName(mod.FolderPath));
            bool isLinked = IsModLinked(mod);
            int badgeCount = (isPinned ? 1 : 0) + (isLinked ? 1 : 0);
            Panel card = new Panel
            {
                Height = 106,
                Width = Math.Max(120, _modCardsHost.ClientSize.Width - 28),
                Margin = new Padding(0, 0, 0, 10),
                BackColor = Color.FromArgb(8, 30, 56),
                BorderStyle = BorderStyle.FixedSingle,
                Cursor = Cursors.Hand,
                Tag = mod
            };

            PictureBox icon = new PictureBox
            {
                Location = new Point(10, 10),
                Size = new Size(70, 70),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(11, 36, 65),
                Image = LoadModIcon(mod)
            };
            card.Controls.Add(icon);

            Label title = new Label
            {
                Text = mod.Title,
                Location = new Point(90, 10),
                Size = new Size(card.Width - (210 + (badgeCount * 20)), 24),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(231, 245, 255),
                AutoEllipsis = true
            };
            card.Controls.Add(title);

            int badgeX = card.Width - 136;
            if (isPinned && _pinBadgeImage != null)
            {
                PictureBox pin = new PictureBox
                {
                    Size = new Size(16, 16),
                    Location = new Point(badgeX, 12),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Color.Transparent,
                    Image = _pinBadgeImage,
                    Tag = "PIN_BADGE"
                };
                card.Controls.Add(pin);
                badgeX -= 18;
            }

            if (isLinked && _linkBadgeImage != null)
            {
                PictureBox link = new PictureBox
                {
                    Size = new Size(12, 12),
                    Location = new Point(badgeX + 2, 14),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Color.Transparent,
                    Image = _linkBadgeImage,
                    Tag = "LINK_BADGE"
                };
                card.Controls.Add(link);
            }

            Label category = new Label
            {
                Text = mod.Category,
                Location = new Point(card.Width - 118, 10),
                Size = new Size(108, 18),
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 7.8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(149, 224, 240),
                AutoEllipsis = true
            };
            category.Tag = "CATEGORY_LABEL";
            card.Controls.Add(category);

            Label author = new Label
            {
                Text = "By " + mod.Author + GetVersionSuffix(mod),
                Location = new Point(90, 34),
                Size = new Size(card.Width - 102, 20),
                Font = new Font("Segoe UI", 8.8f, FontStyle.Regular),
                ForeColor = Color.FromArgb(150, 220, 240),
                AutoEllipsis = true
            };
            card.Controls.Add(author);

            Label description = new Label
            {
                Text = string.IsNullOrWhiteSpace(mod.Description) ? "No manual description set." : mod.Description,
                Location = new Point(90, 56),
                Size = new Size(card.Width - 102, 38),
                Font = new Font("Segoe UI", 8.7f, FontStyle.Regular),
                ForeColor = Color.FromArgb(185, 225, 240),
                AutoEllipsis = true
            };
            card.Controls.Add(description);

            AttachCardSelectHandler(card, card);
            AttachCardSelectHandler(icon, card);
            AttachCardSelectHandler(title, card);
            AttachCardSelectHandler(category, card);
            AttachCardSelectHandler(author, card);
            AttachCardSelectHandler(description, card);

            AttachModPinHandler(card, mod);
            AttachModPinHandler(icon, mod);
            AttachModPinHandler(title, mod);
            AttachModPinHandler(category, mod);
            AttachModPinHandler(author, mod);
            AttachModPinHandler(description, mod);
            foreach (Control child in card.Controls)
            {
                if (object.Equals(child.Tag, "PIN_BADGE") || object.Equals(child.Tag, "LINK_BADGE"))
                {
                    AttachCardSelectHandler(child, card);
                    AttachModPinHandler(child, mod);
                }
            }

            return card;
        }

        private string GetVersionSuffix(ModInfo mod)
        {
            if (mod == null)
            {
                return string.Empty;
            }

            string value = FirstValue(mod.DisplayVersion, mod.LocalVersion, mod.NexusVersion);
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return " | v" + value;
        }

        private bool IsModLinked(ModInfo mod)
        {
            if (mod == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(mod.NexusUrl))
            {
                return true;
            }

            string folderName = Path.GetFileName(mod.FolderPath) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(GetManualModUrl(folderName));
        }

        private void AttachCardSelectHandler(Control clickable, Panel card)
        {
            clickable.Click += delegate { SelectCard(card); };
        }

        private void AttachCategoryPinHandler(Control clickable, string category)
        {
            clickable.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right)
                {
                    return;
                }
                ShowCategoryContextMenu(clickable, e.Location, category);
            };
        }

        private void AttachCategoryRenameHandler(Control clickable, string category)
        {
            clickable.MouseDoubleClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left)
                {
                    return;
                }
                RenameCategory(category);
            };
        }

        private void AttachModPinHandler(Control clickable, ModInfo mod)
        {
            clickable.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right)
                {
                    return;
                }
                ShowModContextMenu(clickable, e.Location, mod);
            };
        }

        private void ShowCategoryContextMenu(Control owner, Point location, string category)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                return;
            }

            ContextMenuStrip menu = new ContextMenuStrip();
            bool pinned = IsCategoryPinned(category);
            ToolStripMenuItem pinItem = new ToolStripMenuItem(pinned ? "Unpin Category From Top" : "Pin Category To Top");
            if (_pinBadgeImage != null)
            {
                pinItem.Image = _pinBadgeImage;
            }
            pinItem.Click += delegate
            {
                BeginInvoke((Action)(delegate
                {
                    SetCategoryPinned(category, !pinned);
                    SaveModListData(_modListData);
                    RefreshModList();
                    LogInfo((!pinned ? "Pinned" : "Unpinned") + " category: " + category);
                }));
            };
            menu.Items.Add(pinItem);

            ToolStripMenuItem renameCategory = new ToolStripMenuItem("Rename Category");
            renameCategory.Click += delegate
            {
                BeginInvoke((Action)(delegate
                {
                    RenameCategory(category);
                }));
            };
            menu.Items.Add(renameCategory);
            menu.Show(owner, location);
        }

        private void ShowModContextMenu(Control owner, Point location, ModInfo mod)
        {
            if (mod == null)
            {
                return;
            }

            string modFolderName = Path.GetFileName(mod.FolderPath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(modFolderName))
            {
                return;
            }

            ContextMenuStrip menu = new ContextMenuStrip();
            bool pinned = IsModPinned(modFolderName);
            ToolStripMenuItem pinItem = new ToolStripMenuItem(pinned ? "Unpin Mod From Category Top" : "Pin Mod To Category Top");
            if (_pinBadgeImage != null)
            {
                pinItem.Image = _pinBadgeImage;
            }
            pinItem.Click += delegate
            {
                BeginInvoke((Action)(delegate
                {
                    SetModPinned(modFolderName, !pinned);
                    SaveModListData(_modListData);
                    RefreshModList();
                    LogInfo((!pinned ? "Pinned" : "Unpinned") + " mod: " + mod.Title);
                }));
            };
            menu.Items.Add(pinItem);

            ToolStripMenuItem renameMod = new ToolStripMenuItem("Rename Mod");
            renameMod.Click += delegate
            {
                BeginInvoke((Action)(delegate
                {
                    SetSelectedModDetails(mod);
                    RenameSelectedMod();
                }));
            };
            menu.Items.Add(renameMod);

            ToolStripMenuItem removeMod = new ToolStripMenuItem("Remove Mod");
            removeMod.Click += delegate
            {
                BeginInvoke((Action)(delegate
                {
                    SetSelectedModDetails(mod);
                    RemoveSelectedMod();
                }));
            };
            menu.Items.Add(removeMod);

            ToolStripMenuItem linkNexus = new ToolStripMenuItem("Link To Nexus Via Url");
            linkNexus.Click += delegate
            {
                BeginInvoke((Action)(delegate
                {
                    SetSelectedModDetails(mod);
                    LinkModToNexusViaUrl(mod);
                }));
            };
            menu.Items.Add(linkNexus);

            ToolStripMenuItem autoLinkNexus = new ToolStripMenuItem("Auto Link");
            autoLinkNexus.Click += delegate
            {
                BeginInvoke((Action)(delegate
                {
                    SetSelectedModDetails(mod);
                    AutoLinkModToNexus(mod);
                }));
            };
            menu.Items.Add(autoLinkNexus);

            ToolStripMenuItem categoryMenu = new ToolStripMenuItem("Set Category");
            List<string> categoryOptions = GetCategoryOptions();
            foreach (string option in categoryOptions)
            {
                string categoryChoice = option;
                ToolStripMenuItem item = new ToolStripMenuItem(categoryChoice);
                item.Checked = string.Equals(NormalizeCategoryName(mod.Category), NormalizeCategoryName(categoryChoice), StringComparison.OrdinalIgnoreCase);
                item.Click += delegate
                {
                    BeginInvoke((Action)(delegate
                    {
                        SetModCategoryFromContextMenu(mod, categoryChoice);
                    }));
                };
                categoryMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(categoryMenu);
            menu.Show(owner, location);
        }

        private List<string> GetCategoryOptions()
        {
            List<string> values = new List<string>();
            foreach (object item in _selectedCategoryCombo.Items)
            {
                string text = NormalizeCategoryName(item == null ? string.Empty : item.ToString());
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }
                if (!values.Any(delegate(string v) { return string.Equals(v, text, StringComparison.OrdinalIgnoreCase); }))
                {
                    values.Add(text);
                }
            }

            foreach (ModInfo mod in _mods)
            {
                if (string.IsNullOrWhiteSpace(mod.Category))
                {
                    continue;
                }
                string normalized = NormalizeCategoryName(mod.Category);
                if (!values.Any(delegate(string v) { return string.Equals(v, normalized, StringComparison.OrdinalIgnoreCase); }))
                {
                    values.Add(normalized);
                }
            }

            return values;
        }

        private void SetModCategoryFromContextMenu(ModInfo mod, string category)
        {
            if (mod == null || string.IsNullOrWhiteSpace(category))
            {
                return;
            }
            category = NormalizeCategoryName(category);

            string folderName = Path.GetFileName(mod.FolderPath);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return;
            }

            SetManualCategory(folderName, category);
            mod.Category = category;
            SaveModListData(_modListData);
            RefreshModList();
            LogInfo("Saved category '" + category + "' for: " + mod.Title);
        }

        private void RenameCategory(string oldCategory)
        {
            if (string.IsNullOrWhiteSpace(oldCategory))
            {
                return;
            }

            string entered = PromptForText("Rename Category", "Enter new category name:", oldCategory);
            if (entered == null)
            {
                return;
            }

            string newCategory = NormalizeCategoryName(entered.Trim());
            if (string.IsNullOrWhiteSpace(newCategory))
            {
                LogInfo("Rename category failed: empty name.");
                return;
            }
            if (string.Equals(newCategory, oldCategory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            int moved = 0;
            foreach (ModInfo mod in _mods.Where(delegate(ModInfo m)
            {
                return string.Equals(m.Category, oldCategory, StringComparison.OrdinalIgnoreCase);
            }).ToList())
            {
                string folderName = Path.GetFileName(mod.FolderPath);
                if (string.IsNullOrWhiteSpace(folderName))
                {
                    continue;
                }

                SetManualCategory(folderName, newCategory);
                moved++;
            }

            bool wasPinned = IsCategoryPinned(oldCategory);
            SetCategoryPinned(oldCategory, false);
            if (wasPinned)
            {
                SetCategoryPinned(newCategory, true);
            }

            SaveModListData(_modListData);
            RefreshModList();
            LogInfo("Renamed category '" + oldCategory + "' -> '" + newCategory + "' (" + moved + " mod(s)).");
        }

        private void SelectCard(Panel card)
        {
            if (_selectedCard != null)
            {
                _selectedCard.BackColor = Color.FromArgb(8, 30, 56);
            }

            _selectedCard = card;
            _selectedCard.BackColor = Color.FromArgb(15, 52, 88);

            ModInfo mod = card.Tag as ModInfo;
            SetSelectedModDetails(mod);
        }

        private void ReflowCards()
        {
            int width = Math.Max(120, _modCardsHost.ClientSize.Width - 28);
            foreach (Control c in _modCardsHost.Controls)
            {
                Panel card = c as Panel;
                if (card == null)
                {
                    continue;
                }

                card.Width = width;
                if (object.Equals(card.Tag, "CATEGORY_HEADER"))
                {
                    bool hasPin = card.Controls.Cast<Control>().Any(delegate(Control x)
                    {
                        return object.Equals(x.Tag, "PIN_BADGE");
                    });
                    if (card.Controls.Count > 0 && card.Controls[0] is Label)
                    {
                        card.Controls[0].Width = Math.Max(20, card.Width - (hasPin ? 40 : 16));
                    }
                    foreach (Control child in card.Controls)
                    {
                        if (object.Equals(child.Tag, "PIN_BADGE"))
                        {
                            child.Left = Math.Max(8, card.Width - 24);
                        }
                    }
                    continue;
                }
                bool cardPinned = card.Controls.Cast<Control>().Any(delegate(Control x)
                {
                    return object.Equals(x.Tag, "PIN_BADGE");
                });
                bool cardLinked = card.Controls.Cast<Control>().Any(delegate(Control x)
                {
                    return object.Equals(x.Tag, "LINK_BADGE");
                });
                foreach (Control child in card.Controls)
                {
                    if (child is Label && child.Left == 90)
                    {
                        if (child.Top == 10)
                        {
                            int badgeCount = (cardPinned ? 1 : 0) + (cardLinked ? 1 : 0);
                            child.Width = Math.Max(40, card.Width - (210 + (badgeCount * 20)));
                        }
                        else
                        {
                            child.Width = Math.Max(40, card.Width - 102);
                        }
                    }
                    if (child is Label && object.Equals(child.Tag, "CATEGORY_LABEL"))
                    {
                        child.Left = Math.Max(90, card.Width - 118);
                    }
                    if (object.Equals(child.Tag, "PIN_BADGE"))
                    {
                        child.Left = Math.Max(90, card.Width - 136);
                    }
                    if (object.Equals(child.Tag, "LINK_BADGE"))
                    {
                        child.Left = Math.Max(90, card.Width - (cardPinned ? 154 : 136));
                    }
                }
            }
        }

        private void SetSelectedModDetails(ModInfo mod)
        {
            _selectedMod = mod;
            _suppressCategorySave = true;

            if (mod == null)
            {
                _selectedIcon.Image = LoadModIcon(new ModInfo { IconPath = Path.Combine(_assetsDir, "icon_main.png"), Title = "Main" });
                _selectedTitle.Text = "No mod selected";
                _selectedAuthor.Text = "Author: -";
                _selectedDescriptionEditor.Text = "Select a mod card from the left, then write a manual description here.";
                _selectedDescriptionEditor.Enabled = false;
                _selectedCategoryCombo.Text = string.Empty;
                _selectedCategoryCombo.Enabled = false;
                _suppressCategorySave = false;
                return;
            }

            _selectedIcon.Image = LoadModIcon(mod);
            _selectedTitle.Text = mod.Title;
            _selectedAuthor.Text = "Author: " + mod.Author + " | Version: " + FirstValue(mod.DisplayVersion, "Unknown");
            _selectedDescriptionEditor.Enabled = true;
            _selectedDescriptionEditor.Text = mod.Description;
            _selectedCategoryCombo.Enabled = true;
            _selectedCategoryCombo.Text = mod.Category;
            _suppressCategorySave = false;
            LogInfo("Selected mod: " + mod.Title);
            RenderStatusPanel();
        }

        private void SelectedDescriptionEditor_KeyDown(object sender, KeyEventArgs e)
        {
            HandleCtrlBackspace(_selectedDescriptionEditor, e);
        }

        private bool HandleCtrlBackspace(TextBoxBase input, KeyEventArgs e)
        {
            if (input == null || e == null)
            {
                return false;
            }
            if (!(e.Control && e.KeyCode == Keys.Back))
            {
                return false;
            }
            if (input.ReadOnly)
            {
                return true;
            }

            string text = input.Text ?? string.Empty;
            int start = input.SelectionStart;
            int length = input.SelectionLength;
            if (length > 0)
            {
                input.Text = text.Remove(start, length);
                input.SelectionStart = start;
                input.SelectionLength = 0;
            }
            else if (start > 0)
            {
                int pos = start;
                while (pos > 0 && char.IsWhiteSpace(text[pos - 1]))
                {
                    pos--;
                }
                while (pos > 0 && !char.IsWhiteSpace(text[pos - 1]))
                {
                    pos--;
                }
                input.Text = text.Remove(pos, start - pos);
                input.SelectionStart = pos;
                input.SelectionLength = 0;
            }

            e.SuppressKeyPress = true;
            e.Handled = true;
            return true;
        }

        private bool HandleCtrlBackspace(ComboBox combo, KeyEventArgs e)
        {
            if (combo == null || e == null)
            {
                return false;
            }
            if (!(e.Control && e.KeyCode == Keys.Back))
            {
                return false;
            }
            if (combo.DropDownStyle == ComboBoxStyle.DropDownList)
            {
                return false;
            }

            string text = combo.Text ?? string.Empty;
            int start = combo.SelectionStart;
            int length = combo.SelectionLength;
            if (length > 0)
            {
                combo.Text = text.Remove(start, length);
                combo.SelectionStart = start;
                combo.SelectionLength = 0;
            }
            else if (start > 0)
            {
                int pos = start;
                while (pos > 0 && char.IsWhiteSpace(text[pos - 1]))
                {
                    pos--;
                }
                while (pos > 0 && !char.IsWhiteSpace(text[pos - 1]))
                {
                    pos--;
                }
                combo.Text = text.Remove(pos, start - pos);
                combo.SelectionStart = pos;
                combo.SelectionLength = 0;
            }

            e.SuppressKeyPress = true;
            e.Handled = true;
            return true;
        }

        private void SaveSelectedDescription()
        {
            if (_selectedMod == null)
            {
                LogInfo("Save description skipped (no mod selected).");
                return;
            }

            string folderName = Path.GetFileName(_selectedMod.FolderPath);
            string title = _selectedMod.Title;
            string text = _selectedDescriptionEditor.Text ?? string.Empty;
            SetManualDescription(folderName, text);
            _selectedMod.Description = text;
            SaveModListData(_modListData);
            RefreshModList();
            LogInfo("Saved manual description for: " + title);
        }

        private void SaveSelectedCategory()
        {
            if (_suppressCategorySave || _renameInProgress || _isRefreshingModList || !_selectedCategoryCombo.Enabled)
            {
                return;
            }
            if (_selectedMod == null)
            {
                LogInfo("Save category skipped (no mod selected).");
                return;
            }

            string chosen = NormalizeCategoryName((_selectedCategoryCombo.Text ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(chosen))
            {
                LogInfo("Save category skipped (empty category).");
                return;
            }

            string folderName = Path.GetFileName(_selectedMod.FolderPath);
            string current = GetManualCategory(folderName);
            if (string.Equals(current, chosen, StringComparison.OrdinalIgnoreCase))
            {
                _selectedMod.Category = chosen;
                return;
            }
            SetManualCategory(folderName, chosen);
            _selectedMod.Category = chosen;
            SaveModListData(_modListData);
            BeginInvoke((Action)(delegate
            {
                RefreshModList();
            }));
            LogInfo("Saved category '" + chosen + "' for: " + _selectedMod.Title);
        }

        private void LinkSelectedModUrl()
        {
            if (_selectedMod == null)
            {
                LogInfo("Link mod URL skipped (no mod selected).");
                return;
            }

            LinkModToNexusViaUrl(_selectedMod);
        }

        private void AutoLinkModToNexus(ModInfo mod)
        {
            if (mod == null)
            {
                LogInfo("Auto link skipped (no mod selected).");
                return;
            }

            string folderName = Path.GetFileName(mod.FolderPath) ?? string.Empty;
            bool found = EnsureUe4ssNexusLink(mod, true);

            if (!found)
            {
                ApplyManualModLink(mod);
                TryInferNexusFromUrl(mod);
                TryInferNexusFromFolderName(mod);
                TryInferNexusFromFileContents(mod);
                TryInferNexusFromWebSearch(mod);
                found = !string.IsNullOrWhiteSpace(mod.NexusModId) || !string.IsNullOrWhiteSpace(mod.NexusUrl);
                if (found && !string.IsNullOrWhiteSpace(mod.NexusUrl))
                {
                    SetManualModUrl(folderName, mod.NexusUrl);
                }
            }

            if (!found)
            {
                SaveModListData(_modListData);
                LogInfo("Auto link failed for '" + mod.Title + "': no Nexus match found.");
                RefreshModList();
                return;
            }

            _nexusSkipLogged.Remove(mod.FolderPath);
            _nexusSearchTried.Remove(mod.FolderPath);
            TryInferNexusFromUrl(mod);
            TryEnrichWithNexusData(mod);
            SaveModListData(_modListData);
            RefreshModList();
            LogInfo("Auto linked '" + mod.Title + "' to " + mod.NexusUrl);
        }

        private void EditSelectedTitle()
        {
            if (_selectedMod == null)
            {
                LogInfo("Edit title skipped (no mod selected).");
                return;
            }

            string folderName = Path.GetFileName(_selectedMod.FolderPath);
            string current = FirstValue(GetManualTitle(folderName), _selectedMod.Title);
            string entered = PromptForText("Edit Mod Name", "Enter custom display name:", current);
            if (entered == null)
            {
                return;
            }

            string value = entered.Trim();
            SetManualTitle(folderName, value);
            SaveModListData(_modListData);
            RefreshModList();
            LogInfo("Saved custom mod name for: " + _selectedMod.Title);
        }

        private void EditSelectedAuthor()
        {
            if (_selectedMod == null)
            {
                LogInfo("Edit author skipped (no mod selected).");
                return;
            }

            string folderName = Path.GetFileName(_selectedMod.FolderPath);
            string current = FirstValue(GetManualAuthor(folderName), _selectedMod.Author);
            string entered = PromptForText("Edit Mod Author", "Enter custom author name:", current);
            if (entered == null)
            {
                return;
            }

            string value = entered.Trim();
            SetManualAuthor(folderName, value);
            SaveModListData(_modListData);
            RefreshModList();
            LogInfo("Saved custom mod author for: " + _selectedMod.Title);
        }

        private void LinkModToNexusViaUrl(ModInfo mod)
        {
            if (mod == null)
            {
                LogInfo("Link mod URL skipped (no mod selected).");
                return;
            }

            string folderName = Path.GetFileName(mod.FolderPath);
            string current = GetManualModUrl(folderName);
            string entered = PromptForText("Link Subnautica Mod URL", "Paste Nexus mod page URL:", current);
            if (entered == null)
            {
                return;
            }

            string url = entered.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                LogInfo("Link mod URL skipped (empty value).");
                return;
            }
            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                LogInfo("Link mod URL failed: invalid URL.");
                return;
            }

            SetManualModUrl(folderName, url);
            mod.NexusUrl = url;
            TryInferNexusFromUrl(mod);
            _nexusSkipLogged.Remove(mod.FolderPath);
            _nexusSearchTried.Remove(mod.FolderPath);
            SaveModListData(_modListData);
            RefreshModList();
            LogInfo("Linked Nexus URL for: " + mod.Title);
        }

        private string PromptForText(string title, string prompt, string initialValue)
        {
            using (Form promptForm = new Form())
            {
                promptForm.Text = title;
                promptForm.StartPosition = FormStartPosition.CenterParent;
                promptForm.Size = new Size(640, 180);
                promptForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                promptForm.MinimizeBox = false;
                promptForm.MaximizeBox = false;

                Label textLabel = new Label { Left = 12, Top = 14, Width = 600, Text = prompt };
                TextBox input = new TextBox { Left = 12, Top = 40, Width = 600, Text = initialValue ?? string.Empty };
                input.KeyDown += delegate(object sender, KeyEventArgs e) { HandleCtrlBackspace(input, e); };
                Button ok = new Button { Text = "OK", Left = 440, Width = 80, Top = 78, DialogResult = DialogResult.OK };
                Button cancel = new Button { Text = "Cancel", Left = 532, Width = 80, Top = 78, DialogResult = DialogResult.Cancel };

                promptForm.Controls.Add(textLabel);
                promptForm.Controls.Add(input);
                promptForm.Controls.Add(ok);
                promptForm.Controls.Add(cancel);
                promptForm.AcceptButton = ok;
                promptForm.CancelButton = cancel;

                DialogResult result = promptForm.ShowDialog(this);
                return result == DialogResult.OK ? input.Text : null;
            }
        }

        private Image LoadModIcon(ModInfo mod)
        {
            try
            {
                if (!string.IsNullOrEmpty(mod.IconPath) && File.Exists(mod.IconPath))
                {
                    using (FileStream fs = new FileStream(mod.IconPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (Image raw = Image.FromStream(fs))
                    {
                        return new Bitmap(raw);
                    }
                }
            }
            catch
            {
            }

            if (File.Exists(Path.Combine(_assetsDir, "icon_main.png")))
            {
                try
                {
                    using (Image raw = Image.FromFile(Path.Combine(_assetsDir, "icon_main.png")))
                    {
                        return new Bitmap(raw);
                    }
                }
                catch
                {
                }
            }

            return CreateFallbackIcon(mod.Title);
        }

        private Image CreateFallbackIcon(string text)
        {
            Bitmap bmp = new Bitmap(72, 72);
            using (Graphics g = Graphics.FromImage(bmp))
            using (LinearGradientBrush brush = new LinearGradientBrush(
                new Rectangle(0, 0, 72, 72),
                Color.FromArgb(10, 73, 113),
                Color.FromArgb(35, 160, 195),
                120f))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillRectangle(brush, 0, 0, 72, 72);
                using (Pen border = new Pen(Color.FromArgb(210, 243, 255), 2))
                {
                    g.DrawRectangle(border, 1, 1, 69, 69);
                }

                string label = string.IsNullOrWhiteSpace(text) ? "?" : text.Trim().Substring(0, 1).ToUpperInvariant();
                using (Font f = new Font("Segoe UI", 24f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush b = new SolidBrush(Color.FromArgb(232, 248, 255)))
                {
                    SizeF size = g.MeasureString(label, f);
                    g.DrawString(label, f, b, (72 - size.Width) / 2f, (72 - size.Height) / 2f);
                }
            }

            return bmp;
        }

        private void ModCardsDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void ModCardsDragDrop(object sender, DragEventArgs e)
        {
            object dropped = e.Data.GetData(DataFormats.FileDrop);
            string[] paths = dropped as string[];
            if (paths == null || paths.Length == 0)
            {
                return;
            }

            InstallSources(paths);
        }

        private void InstallSources(IEnumerable<string> sources)
        {
            int success = 0;
            int failed = 0;
            List<string> errors = new List<string>();
            List<ManagedInstallRecord> records = new List<ManagedInstallRecord>();

            foreach (string source in sources)
            {
                try
                {
                    ManagedInstallRecord record = InstallOne(source);
                    records.Add(record);
                    success++;
                    LogInfo("Installed source: " + source);
                }
                catch (Exception ex)
                {
                    failed++;
                    string shortName = Path.GetFileName(source);
                    errors.Add(shortName + ": " + ex.Message);
                    LogInfo("Install failed for '" + source + "': " + ex.Message);
                }
            }

            foreach (ManagedInstallRecord record in records)
            {
                UpsertManagedRecord(record);
            }
            SaveModListData(_modListData);

            RefreshModList();

            if (failed == 0)
            {
                LogInfo("Install complete. " + success + " item(s) installed.");
                return;
            }

            string errorSummary = string.Join(" | ", errors.Take(2).ToArray());
            if (errors.Count > 2)
            {
                errorSummary = errorSummary + " | +" + (errors.Count - 2) + " more";
            }
            LogInfo("Install complete. Success: " + success + ", Failed: " + failed + ". " + errorSummary);
        }

        private ManagedInstallRecord InstallOne(string sourcePath)
        {
            string stagedFolder = StageSourceIntoManagerStore(sourcePath);
            ManagedInstallRecord record = new ManagedInstallRecord();
            record.modFolderName = Path.GetFileName(stagedFolder);
            record.installedAtUtc = DateTime.UtcNow.ToString("o");
            record.deployedPaths = new List<string>();

            if (_subnauticaDetected)
            {
                record.deployedPaths = DeployModToSubnautica(stagedFolder);
            }
            else
            {
                LogInfo("Subnautica 2 not detected; mod staged only in manager store.");
            }

            return record;
        }

        private string StageSourceIntoManagerStore(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new InvalidOperationException("Source path is empty.");
            }

            if (Directory.Exists(sourcePath))
            {
                return StageFolderSource(sourcePath);
            }

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Source path not found.");
            }

            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext == ".zip")
            {
                return StageZipSource(sourcePath);
            }
            if (ext == ".7z")
            {
                return Stage7zSource(sourcePath);
            }
            if (ext == ".rar")
            {
                return StageRarSource(sourcePath);
            }

            throw new InvalidOperationException("Only .zip, .7z, .rar, and folders are supported.");
        }

        private string StageFolderSource(string folderPath)
        {
            string folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(folderName))
            {
                folderName = "Mod";
            }

            string payloadPath = folderPath;
            string stagedName = folderName;
            if (!TryResolvePayloadFromKnownContainers(folderPath, folderName, out payloadPath, out stagedName))
            {
                string[] topDirs = Directory.GetDirectories(folderPath);
                string[] topFiles = Directory.GetFiles(folderPath);
                if (topDirs.Length == 1 && topFiles.Length == 0)
                {
                    string inner = topDirs[0];
                    string innerName = Path.GetFileName(inner);
                    if (TryResolvePayloadFromKnownContainers(inner, innerName, out payloadPath, out stagedName))
                    {
                        LogInfo("Detected nested mod payload in dragged folder: " + payloadPath);
                    }
                    else
                    {
                        payloadPath = inner;
                        stagedName = string.IsNullOrWhiteSpace(innerName) ? stagedName : innerName;
                        LogInfo("Skipped wrapper folder and staged inner folder: " + stagedName);
                    }
                }
            }

            string destination = GetUniqueModFolderPath(stagedName);
            CopyDirectory(payloadPath, destination);
            return destination;
        }

        private string StageZipSource(string archivePath)
        {
            string temp = Path.Combine(Path.GetTempPath(), "S2MM_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                ZipFile.ExtractToDirectory(archivePath, temp);
                return MoveExtractedPayloadToMods(temp, Path.GetFileNameWithoutExtension(archivePath));
            }
            finally
            {
                SafeDeleteDirectory(temp);
            }
        }

        private string Stage7zSource(string archivePath)
        {
            string sevenZipExe = Resolve7ZipExecutable();
            string temp = Path.Combine(Path.GetTempPath(), "S2MM_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);

            try
            {
                bool extracted = false;
                if (!string.IsNullOrWhiteSpace(sevenZipExe))
                {
                    LogInfo("Using 7z extractor: " + sevenZipExe);
                    string error7z;
                    extracted = RunArchiveExtractor(sevenZipExe, "x \"" + archivePath + "\" -o\"" + temp + "\" -y", out error7z);
                    if (!extracted)
                    {
                        LogInfo("7z extractor failed, trying WinRAR fallback: " + error7z);
                    }
                }

                if (!extracted)
                {
                    string winRarExe = ResolveWinRarExecutable();
                    if (!string.IsNullOrWhiteSpace(winRarExe))
                    {
                        LogInfo("Using WinRAR extractor: " + winRarExe);
                        string targetDir = temp;
                        if (!targetDir.EndsWith("\\", StringComparison.Ordinal))
                        {
                            targetDir += "\\";
                        }
                        string errorRar;
                        extracted = RunArchiveExtractor(winRarExe, "x -y -o+ -ibck \"" + archivePath + "\" \"" + targetDir + "\"", out errorRar);
                        if (!extracted)
                        {
                            throw new InvalidOperationException("WinRAR extraction failed: " + errorRar);
                        }
                    }
                }

                if (!extracted)
                {
                    throw new InvalidOperationException("No .7z extractor found. Install 7-Zip/NanaZip or WinRAR, or add 7z.exe to PATH.");
                }

                return MoveExtractedPayloadToMods(temp, Path.GetFileNameWithoutExtension(archivePath));
            }
            finally
            {
                SafeDeleteDirectory(temp);
            }
        }

        private string StageRarSource(string archivePath)
        {
            string sevenZipExe = Resolve7ZipExecutable();
            string temp = Path.Combine(Path.GetTempPath(), "S2MM_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);

            try
            {
                bool extracted = false;
                if (!string.IsNullOrWhiteSpace(sevenZipExe))
                {
                    LogInfo("Using 7z extractor for .rar: " + sevenZipExe);
                    string error7z;
                    extracted = RunArchiveExtractor(sevenZipExe, "x \"" + archivePath + "\" -o\"" + temp + "\" -y", out error7z);
                    if (!extracted)
                    {
                        LogInfo("7z .rar extraction failed, trying WinRAR fallback: " + error7z);
                    }
                }

                if (!extracted)
                {
                    string winRarExe = ResolveWinRarExecutable();
                    if (!string.IsNullOrWhiteSpace(winRarExe))
                    {
                        LogInfo("Using WinRAR extractor for .rar: " + winRarExe);
                        string targetDir = temp;
                        if (!targetDir.EndsWith("\\", StringComparison.Ordinal))
                        {
                            targetDir += "\\";
                        }
                        string errorRar;
                        extracted = RunArchiveExtractor(winRarExe, "x -y -o+ -ibck \"" + archivePath + "\" \"" + targetDir + "\"", out errorRar);
                        if (!extracted)
                        {
                            throw new InvalidOperationException("WinRAR .rar extraction failed: " + errorRar);
                        }
                    }
                }

                if (!extracted)
                {
                    throw new InvalidOperationException("No .rar extractor found. Install 7-Zip/NanaZip or WinRAR.");
                }

                return MoveExtractedPayloadToMods(temp, Path.GetFileNameWithoutExtension(archivePath));
            }
            finally
            {
                SafeDeleteDirectory(temp);
            }
        }

        private bool RunArchiveExtractor(string exePath, string arguments, out string error)
        {
            error = string.Empty;
            try
            {
                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = exePath;
                start.Arguments = arguments;
                start.CreateNoWindow = true;
                start.UseShellExecute = false;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;

                using (Process p = Process.Start(start))
                {
                    string stdOut = p.StandardOutput.ReadToEnd();
                    string stdErr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        error = "Exit " + p.ExitCode + ": " + stdErr + stdOut;
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private string MoveExtractedPayloadToMods(string extractionRoot, string fallbackName)
        {
            string resolvedPayload;
            string resolvedName;
            if (TryResolvePayloadFromKnownContainers(extractionRoot, fallbackName, out resolvedPayload, out resolvedName))
            {
                string destinationResolved = GetUniqueModFolderPath(resolvedName);
                CopyDirectory(resolvedPayload, destinationResolved);
                return destinationResolved;
            }

            string[] dirs = Directory.GetDirectories(extractionRoot);
            string[] files = Directory.GetFiles(extractionRoot);

            if (dirs.Length == 1 && files.Length == 0)
            {
                string singlePayload = dirs[0];
                string singleName = Path.GetFileName(dirs[0]);
                if (TryResolvePayloadFromKnownContainers(dirs[0], singleName, out resolvedPayload, out resolvedName))
                {
                    singlePayload = resolvedPayload;
                    singleName = resolvedName;
                    LogInfo("Detected nested payload in archive wrapper: " + singlePayload);
                }

                string destinationSingle = GetUniqueModFolderPath(singleName);
                CopyDirectory(singlePayload, destinationSingle);
                return destinationSingle;
            }

            string safeName = string.IsNullOrWhiteSpace(fallbackName) ? "Mod" : fallbackName;
            string destinationRoot = GetUniqueModFolderPath(safeName);
            Directory.CreateDirectory(destinationRoot);

            foreach (string dir in dirs)
            {
                string target = Path.Combine(destinationRoot, Path.GetFileName(dir));
                CopyDirectory(dir, target);
            }

            foreach (string file in files)
            {
                string target = Path.Combine(destinationRoot, Path.GetFileName(file));
                File.Copy(file, target, true);
            }

            return destinationRoot;
        }

        private bool TryResolvePayloadFromKnownContainers(string rootPath, string fallbackName, out string payloadPath, out string stagedFolderName)
        {
            payloadPath = string.Empty;
            stagedFolderName = string.IsNullOrWhiteSpace(fallbackName) ? "Mod" : fallbackName;
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return false;
            }

            string inspectRoot = UnwrapSingleDirectoryChain(rootPath);
            if (!string.Equals(inspectRoot, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                LogInfo("Skipped nested wrapper chain, inspect root: " + inspectRoot);
            }

            if (IsUe4ssLibraryMod(inspectRoot))
            {
                payloadPath = inspectRoot;
                stagedFolderName = ResolveStagedFolderName(fallbackName, inspectRoot, true);
                LogInfo("Detected UE4SS root payload: " + inspectRoot);
                return true;
            }

            string embeddedWin64Root = Path.Combine(inspectRoot, "Subnautica2", "Binaries", "Win64");
            if (Directory.Exists(embeddedWin64Root) && IsUe4ssLibraryMod(embeddedWin64Root))
            {
                payloadPath = embeddedWin64Root;
                stagedFolderName = ResolveStagedFolderName(fallbackName, embeddedWin64Root, true);
                LogInfo("Detected embedded UE4SS Win64 payload: " + embeddedWin64Root);
                return true;
            }

            List<string> candidates = new List<string>
            {
                Path.Combine(inspectRoot, "mods"),
                Path.Combine(inspectRoot, "Mods"),
                Path.Combine(inspectRoot, "Subnautica2", "Mods"),
                Path.Combine(inspectRoot, "ue4ss", "Mods"),
                Path.Combine(inspectRoot, "UE4SS", "Mods"),
                Path.Combine(inspectRoot, "Subnautica2", "Binaries", "Win64", "ue4ss", "Mods")
            };

            foreach (string container in candidates)
            {
                if (!Directory.Exists(container))
                {
                    continue;
                }

                if (TrySelectPayloadInContainer(container, stagedFolderName, out payloadPath, out stagedFolderName))
                {
                    LogInfo("Using payload from nested mods container: " + container);
                    return true;
                }
            }

            return false;
        }

        private string UnwrapSingleDirectoryChain(string startPath)
        {
            if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath))
            {
                return startPath;
            }

            string current = startPath;
            for (int depth = 0; depth < 24; depth++)
            {
                string[] files;
                string[] dirs;
                try
                {
                    files = Directory.GetFiles(current);
                    dirs = Directory.GetDirectories(current);
                }
                catch
                {
                    break;
                }

                if (files.Length == 0 && dirs.Length == 1)
                {
                    current = dirs[0];
                    continue;
                }
                break;
            }

            return current;
        }

        private bool TrySelectPayloadInContainer(string containerPath, string fallbackName, out string payloadPath, out string stagedFolderName)
        {
            payloadPath = string.Empty;
            stagedFolderName = string.IsNullOrWhiteSpace(fallbackName) ? "Mod" : fallbackName;
            if (string.IsNullOrWhiteSpace(containerPath) || !Directory.Exists(containerPath))
            {
                return false;
            }

            string[] modDirs = Directory.GetDirectories(containerPath);
            if (modDirs.Length == 1)
            {
                payloadPath = modDirs[0];
                stagedFolderName = ResolveStagedFolderName(fallbackName, modDirs[0], IsUe4ssLibraryMod(modDirs[0]));
                return true;
            }

            if (modDirs.Length > 1)
            {
                string best = string.Empty;
                int bestScore = int.MinValue;
                foreach (string dir in modDirs)
                {
                    int score = GetLikelyModFolderScore(dir);
                    if (score > bestScore)
                    {
                        best = dir;
                        bestScore = score;
                    }
                    else if (score == bestScore && bestScore >= 0)
                    {
                        string a = Path.GetFileName(dir) ?? string.Empty;
                        string b = Path.GetFileName(best) ?? string.Empty;
                        if (string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            best = dir;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(best) && bestScore > 0)
                {
                    payloadPath = best;
                    stagedFolderName = ResolveStagedFolderName(fallbackName, best, IsUe4ssLibraryMod(best));
                    return true;
                }
            }

            string[] files = Directory.GetFiles(containerPath);
            bool hasDirectPakPayload = files.Any(delegate(string file)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                return ext == ".pak" || ext == ".utoc" || ext == ".ucas";
            });

            if (hasDirectPakPayload)
            {
                payloadPath = containerPath;
                string parentName = Path.GetFileName(Path.GetDirectoryName(containerPath) ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(parentName) && !string.Equals(parentName, "mods", StringComparison.OrdinalIgnoreCase))
                {
                    stagedFolderName = parentName;
                }
                return true;
            }

            return false;
        }

        private string ResolveStagedFolderName(string fallbackName, string payloadPath, bool forceUe4ssName)
        {
            if (forceUe4ssName)
            {
                return "UE4SS";
            }

            string payloadName = CleanFolderName(Path.GetFileName(payloadPath) ?? string.Empty);
            string fallback = CleanFolderName(fallbackName ?? string.Empty);

            if (!string.IsNullOrWhiteSpace(payloadName) && !IsLikelyTempOrWrapperName(payloadName))
            {
                return payloadName;
            }
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback;
            }
            return "Mod";
        }

        private bool IsLikelyTempOrWrapperName(string name)
        {
            string value = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            if (value.StartsWith("S2MM_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (value.StartsWith("temp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (Regex.IsMatch(value, @"^[0-9a-f]{8,}$", RegexOptions.IgnoreCase))
            {
                return true;
            }

            return false;
        }

        private int GetLikelyModFolderScore(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                return 0;
            }

            int score = 0;
            if (File.Exists(Path.Combine(dir, "enabled.txt"))) score += 3;
            if (Directory.Exists(Path.Combine(dir, "Scripts"))) score += 3;
            if (File.Exists(Path.Combine(dir, "main.lua"))) score += 2;
            if (Directory.GetFiles(dir, "*.pak", SearchOption.AllDirectories).Length > 0) score += 3;
            if (Directory.GetFiles(dir, "*.uplugin", SearchOption.AllDirectories).Length > 0) score += 2;
            if (Directory.GetFiles(dir, "*.lua", SearchOption.AllDirectories).Length > 0) score += 1;
            return score;
        }

        private string Resolve7ZipExecutable()
        {
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] pathSegments = pathVar.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            string[] names = new[] { "7z.exe", "7za.exe", "7zz.exe" };
            foreach (string segment in pathSegments)
            {
                string dir = segment.Trim();
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                foreach (string name in names)
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            string[] commonLocations = new[]
            {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files\7-Zip\7zz.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
                @"C:\Program Files\7-Zip\7za.exe",
                @"C:\Program Files (x86)\7-Zip\7za.exe",
                @"C:\Program Files\NanaZip\7z.exe",
                @"C:\Program Files\NanaZip\7zz.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "7zz.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "7zip", "current", "7z.exe"),
                Path.Combine(_baseDir, "7z.exe"),
                Path.Combine(_baseDir, "7zz.exe"),
                Path.Combine(_assetsDir, "7z.exe"),
                Path.Combine(_assetsDir, "7zz.exe"),
                Path.Combine(_fileStructureDir, "7z.exe")
            };

            foreach (string location in commonLocations)
            {
                if (File.Exists(location))
                {
                    return location;
                }
            }

            return null;
        }

        private string ResolveWinRarExecutable()
        {
            string[] candidates = new[]
            {
                @"C:\Program Files\WinRAR\WinRAR.exe",
                @"C:\Program Files (x86)\WinRAR\WinRAR.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WinRAR", "WinRAR.exe")
            };

            foreach (string location in candidates)
            {
                if (File.Exists(location))
                {
                    return location;
                }
            }

            return null;
        }

        private List<string> DeployModToSubnautica(string stagedFolder)
        {
            List<string> deployed = new List<string>();
            if (!_subnauticaDetected || string.IsNullOrWhiteSpace(_subnauticaWin64Path))
            {
                return deployed;
            }
            if (!Directory.Exists(_subnauticaWin64Path))
            {
                LogInfo("Deploy skipped; Win64 path no longer exists: " + _subnauticaWin64Path);
                _subnauticaDetected = false;
                RenderStatusPanel();
                return deployed;
            }

            string modFolderName = Path.GetFileName(stagedFolder);
            string pakModsTarget = GetPakModsTargetPath();

            if (ContainsPakFiles(stagedFolder) && !string.IsNullOrWhiteSpace(pakModsTarget))
            {
                Directory.CreateDirectory(pakModsTarget);
                foreach (string file in Directory.GetFiles(stagedFolder, "*.*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".pak" && ext != ".utoc" && ext != ".ucas")
                    {
                        continue;
                    }

                    string destination = Path.Combine(pakModsTarget, Path.GetFileName(file));
                    File.Copy(file, destination, true);
                    deployed.Add(destination);
                }
                LogInfo("Applied pak mod '" + modFolderName + "' to " + pakModsTarget);
                return deployed;
            }

            if (ShouldDeployToWin64Root(stagedFolder))
            {
                deployed.AddRange(CopyDirectoryContentsTracked(stagedFolder, _subnauticaWin64Path));
                LogInfo("Deployed '" + modFolderName + "' to Win64 root.");
                return deployed;
            }

            string embeddedWin64 = Path.Combine(stagedFolder, "Subnautica2", "Binaries", "Win64");
            if (Directory.Exists(embeddedWin64))
            {
                deployed.AddRange(CopyDirectoryContentsTracked(embeddedWin64, _subnauticaWin64Path));
                LogInfo("Deployed embedded Win64 payload for '" + modFolderName + "'.");
                return deployed;
            }

            if (ShouldDeployToUe4ssMods(stagedFolder))
            {
                string target = Path.Combine(_subnauticaWin64Path, "ue4ss", "Mods", modFolderName);
                deployed.AddRange(CopyDirectoryContentsTracked(stagedFolder, target));
                LogInfo("Deployed '" + modFolderName + "' to ue4ss\\Mods.");
                return deployed;
            }

            string fallbackTarget = Path.Combine(_subnauticaWin64Path, "Mods", modFolderName);
            deployed.AddRange(CopyDirectoryContentsTracked(stagedFolder, fallbackTarget));
            LogInfo("Deployed '" + modFolderName + "' to Win64\\Mods fallback.");
            return deployed;
        }

        private void ApplyAllModsToGame()
        {
            if (!_subnauticaDetected)
            {
                DetectAndPersistSubnautica(true);
            }

            if (!_subnauticaDetected)
            {
                LogInfo("Apply mods canceled: Subnautica 2 location not detected.");
                return;
            }

            int success = 0;
            int failed = 0;
            string[] modFolders = Directory.GetDirectories(_modsDir).OrderBy(delegate(string n) { return n; }).ToArray();

            foreach (string modFolder in modFolders)
            {
                try
                {
                    List<string> deployed = DeployModToSubnautica(modFolder);
                    ManagedInstallRecord record = new ManagedInstallRecord
                    {
                        modFolderName = Path.GetFileName(modFolder),
                        installedAtUtc = DateTime.UtcNow.ToString("o"),
                        deployedPaths = deployed
                    };
                    UpsertManagedRecord(record);
                    success++;
                }
                catch (Exception ex)
                {
                    failed++;
                    LogInfo("Apply failed for '" + Path.GetFileName(modFolder) + "': " + ex.Message);
                }
            }

            SaveModListData(_modListData);
            RefreshModList();
            LogInfo("Apply mods complete. Success: " + success + ", Failed: " + failed + ".");
        }

        private void InstallUe4ssToGame()
        {
            if (!_subnauticaDetected)
            {
                DetectAndPersistSubnautica(true);
            }
            if (!_subnauticaDetected)
            {
                LogInfo("Install UE4SS canceled: Subnautica 2 location not detected.");
                return;
            }

            string ue4ssFolder = Directory.GetDirectories(_modsDir)
                .FirstOrDefault(delegate(string folder) { return IsUe4ssLibraryMod(folder); });
            if (string.IsNullOrWhiteSpace(ue4ssFolder))
            {
                LogInfo("UE4SS install source not found locally. Trying Nexus mod page source.");
                if (!TryDownloadAndInstallFromNexus(DefaultNexusGameDomain, Ue4ssNexusModId, null, null, null))
                {
                    OpenExternalUrl(Ue4ssNexusPageUrl, "UE4SS Nexus files page");
                }
                return;
            }

            try
            {
                List<string> deployed = DeployModToSubnautica(ue4ssFolder);
                ManagedInstallRecord record = new ManagedInstallRecord
                {
                    modFolderName = Path.GetFileName(ue4ssFolder),
                    installedAtUtc = DateTime.UtcNow.ToString("o"),
                    deployedPaths = deployed
                };
                UpsertManagedRecord(record);
                SaveModListData(_modListData);
                RefreshModList();
                LogInfo("Installed UE4SS to detected Subnautica 2 path.");
            }
            catch (Exception ex)
            {
                LogInfo("Install UE4SS failed: " + ex.Message);
            }
        }

        private void EnsureNxmProtocolRegistration()
        {
            try
            {
                string exe = Application.ExecutablePath;
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                {
                    return;
                }

                string expectedCommand = "\"" + exe + "\" \"%1\"";
                using (RegistryKey existing = Registry.CurrentUser.OpenSubKey(@"Software\Classes\nxm\shell\open\command", false))
                {
                    string currentCommand = existing != null ? (existing.GetValue("") as string ?? string.Empty) : string.Empty;
                    if (string.Equals(currentCommand.Trim(), expectedCommand, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                using (RegistryKey root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\nxm"))
                {
                    if (root == null) return;
                    root.SetValue("", "URL:Nexus Mod Manager Protocol");
                    root.SetValue("URL Protocol", "");
                    using (RegistryKey command = root.CreateSubKey(@"shell\open\command"))
                    {
                        if (command == null) return;
                        command.SetValue("", expectedCommand);
                    }
                }
            }
            catch (Exception ex)
            {
                LogInfo("NXM protocol registration skipped: " + ex.Message);
            }
        }

        private void HandleStartupProtocolUrl()
        {
            if (string.IsNullOrWhiteSpace(_startupProtocolUrl))
            {
                return;
            }

            ProcessNxmProtocolUrl(_startupProtocolUrl);
        }

        private void ProcessNxmProtocolUrl(string nxmUrl)
        {
            string gameDomain;
            string modId;
            string fileId;
            string nxmKey;
            string expires;
            if (!TryParseNxmUrl(nxmUrl, out gameDomain, out modId, out fileId, out nxmKey, out expires))
            {
                LogInfo("NXM link parse failed: " + nxmUrl);
                return;
            }

            LogInfo("Received NXM link for " + gameDomain + " mod " + modId + " file " + fileId + ".");
            if (!TryDownloadAndInstallFromNexus(gameDomain, modId, fileId, nxmKey, expires))
            {
                string page = "https://www.nexusmods.com/" + gameDomain + "/mods/" + modId + "?tab=files";
                OpenExternalUrl(page, "Nexus files page");
            }
        }

        private bool TryParseNxmUrl(string nxmUrl, out string gameDomain, out string modId, out string fileId, out string nxmKey, out string expires)
        {
            gameDomain = string.Empty;
            modId = string.Empty;
            fileId = string.Empty;
            nxmKey = string.Empty;
            expires = string.Empty;

            if (string.IsNullOrWhiteSpace(nxmUrl))
            {
                return false;
            }

            Match m = Regex.Match(
                nxmUrl,
                @"^nxm:\/\/([^\/\?]+)\/mods\/(\d+)(?:\/files\/(\d+))?(?:\?(.*))?$",
                RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                return false;
            }

            gameDomain = m.Groups[1].Value.Trim();
            modId = m.Groups[2].Value.Trim();
            fileId = m.Groups[3].Value.Trim();
            string query = m.Groups[4].Value ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(query))
            {
                foreach (string part in query.Split('&'))
                {
                    string[] kv = part.Split(new[] { '=' }, 2);
                    if (kv.Length != 2) continue;
                    string key = Uri.UnescapeDataString((kv[0] ?? string.Empty).Trim());
                    string value = Uri.UnescapeDataString((kv[1] ?? string.Empty).Trim());
                    if (string.Equals(key, "key", StringComparison.OrdinalIgnoreCase)) nxmKey = value;
                    if (string.Equals(key, "expires", StringComparison.OrdinalIgnoreCase)) expires = value;
                }
            }

            return !string.IsNullOrWhiteSpace(gameDomain) && !string.IsNullOrWhiteSpace(modId);
        }

        private bool TryDownloadAndInstallFromNexus(string gameDomain, string modId, string fileId, string nxmKey, string expires)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_config.nexusApiKey))
                {
                    LogInfo("Nexus download skipped: missing nexusApiKey.");
                    return false;
                }

                string resolvedFileId = fileId;
                if (string.IsNullOrWhiteSpace(resolvedFileId))
                {
                    NexusModFile bestFile = GetPreferredNexusModFile(gameDomain, modId);
                    if (bestFile == null || string.IsNullOrWhiteSpace(bestFile.file_id))
                    {
                        LogInfo("Nexus download failed: no file found for mod " + modId + ".");
                        return false;
                    }
                    resolvedFileId = bestFile.file_id;
                    LogInfo("Selected Nexus file " + resolvedFileId + " for mod " + modId + ".");
                }

                List<string> links = GetNexusDownloadLinks(gameDomain, modId, resolvedFileId, nxmKey, expires);
                if (links.Count == 0)
                {
                    LogInfo("Nexus download links unavailable for mod " + modId + " file " + resolvedFileId + ".");
                    return false;
                }

                string archiveExt = ResolveArchiveExtensionForNexusDownload(gameDomain, modId, resolvedFileId, links);
                string fileName = "nexus_" + gameDomain + "_" + modId + "_" + resolvedFileId + archiveExt;
                string tempPath = Path.Combine(Path.GetTempPath(), fileName);
                using (WebClient wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "S2MM";
                    wc.DownloadFile(links[0], tempPath);
                }

                ManagedInstallRecord record = InstallOne(tempPath);
                string nexusUrl = "https://www.nexusmods.com/" + gameDomain + "/mods/" + modId;
                SetManualModUrl(record.modFolderName, nexusUrl);
                UpsertManagedRecord(record);
                SaveModListData(_modListData);
                RefreshModList();
                LogInfo("Downloaded and installed Nexus mod " + modId + " file " + resolvedFileId + " and linked URL.");
                return true;
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    LogInfo("Nexus download failed: HTTP " + (int)response.StatusCode + " for mod " + modId + ".");
                }
                else
                {
                    LogInfo("Nexus download failed: " + ex.Message);
                }
                return false;
            }
            catch (Exception ex)
            {
                LogInfo("Nexus download/install failed: " + ex.Message);
                return false;
            }
        }

        private string ResolveArchiveExtensionForNexusDownload(string gameDomain, string modId, string fileId, List<string> links)
        {
            foreach (string raw in links ?? new List<string>())
            {
                try
                {
                    Uri uri = new Uri(raw);
                    string ext = (Path.GetExtension(uri.AbsolutePath) ?? string.Empty).ToLowerInvariant();
                    if (ext == ".zip" || ext == ".7z" || ext == ".rar")
                    {
                        return ext;
                    }
                }
                catch
                {
                }
            }

            try
            {
                NexusModFile file = GetNexusModFiles(gameDomain, modId).FirstOrDefault(delegate(NexusModFile n)
                {
                    return string.Equals(n.file_id, fileId, StringComparison.OrdinalIgnoreCase);
                });
                if (file != null && !string.IsNullOrWhiteSpace(file.file_name))
                {
                    string ext = (Path.GetExtension(file.file_name) ?? string.Empty).ToLowerInvariant();
                    if (ext == ".zip" || ext == ".7z" || ext == ".rar")
                    {
                        return ext;
                    }
                }
            }
            catch
            {
            }

            return ".zip";
        }

        private NexusModFile GetPreferredNexusModFile(string gameDomain, string modId)
        {
            List<NexusModFile> files = GetNexusModFiles(gameDomain, modId);
            if (files.Count == 0)
            {
                return null;
            }

            NexusModFile main = files.FirstOrDefault(delegate(NexusModFile f)
            {
                return string.Equals(f.category_name, "MAIN", StringComparison.OrdinalIgnoreCase);
            });
            if (main != null) return main;

            NexusModFile first = files.OrderByDescending(delegate(NexusModFile f)
            {
                long ts;
                return long.TryParse(f.uploaded_timestamp, out ts) ? ts : 0L;
            }).FirstOrDefault();
            return first;
        }

        private List<NexusModFile> GetNexusModFiles(string gameDomain, string modId)
        {
            List<NexusModFile> results = new List<NexusModFile>();
            string url = "https://api.nexusmods.com/v1/games/" + gameDomain + "/mods/" + modId + "/files.json";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 20000;
            request.UserAgent = "S2MM";
            request.Headers["apikey"] = _config.nexusApiKey;
            request.Headers["application-name"] = "S2MM";

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
            {
                string json = reader.ReadToEnd();
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                object raw = serializer.DeserializeObject(json);
                IDictionary<string, object> map = raw as IDictionary<string, object>;
                if (map == null || !map.ContainsKey("files"))
                {
                    return results;
                }

                object[] files = map["files"] as object[];
                if (files == null)
                {
                    return results;
                }

                foreach (object fileObj in files)
                {
                    IDictionary<string, object> row = fileObj as IDictionary<string, object>;
                    if (row == null) continue;
                    NexusModFile f = new NexusModFile();
                    f.file_id = ReadKey(row, "file_id");
                    f.name = ReadKey(row, "name");
                    f.file_name = ReadKey(row, "file_name");
                    f.category_name = ReadKey(row, "category_name");
                    f.uploaded_timestamp = ReadKey(row, "uploaded_timestamp");
                    results.Add(f);
                }
            }

            return results;
        }

        private List<string> GetNexusDownloadLinks(string gameDomain, string modId, string fileId, string nxmKey, string expires)
        {
            List<string> urls = new List<string>();
            string endpoint = "https://api.nexusmods.com/v1/games/" + gameDomain + "/mods/" + modId + "/files/" + fileId + "/download_link.json";
            List<string> query = new List<string>();
            if (!string.IsNullOrWhiteSpace(nxmKey))
            {
                query.Add("key=" + Uri.EscapeDataString(nxmKey));
            }
            if (!string.IsNullOrWhiteSpace(expires))
            {
                query.Add("expires=" + Uri.EscapeDataString(expires));
            }
            if (query.Count > 0)
            {
                endpoint += "?" + string.Join("&", query.ToArray());
            }

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "GET";
            request.Timeout = 20000;
            request.UserAgent = "S2MM";
            request.Headers["apikey"] = _config.nexusApiKey;
            request.Headers["application-name"] = "S2MM";

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
            {
                string json = reader.ReadToEnd();
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                object raw = serializer.DeserializeObject(json);
                object[] list = raw as object[];
                if (list == null)
                {
                    return urls;
                }

                foreach (object item in list)
                {
                    IDictionary<string, object> row = item as IDictionary<string, object>;
                    if (row == null) continue;
                    string uri = FirstValue(ReadKey(row, "URI"), ReadKey(row, "uri"));
                    if (!string.IsNullOrWhiteSpace(uri))
                    {
                        urls.Add(uri);
                    }
                }
            }

            return urls;
        }

        private void CheckForModUpdates()
        {
            if (string.IsNullOrWhiteSpace(_config.nexusApiKey))
            {
                LogInfo("Check updates skipped: nexusApiKey is missing in config.json.");
                return;
            }
            if (_mods.Count == 0)
            {
                LogInfo("Check updates skipped: no mods are loaded.");
                return;
            }

            int checkedCount = 0;
            int updatesAvailable = 0;
            int unknownCount = 0;
            int failedCount = 0;

            foreach (ModInfo mod in _mods)
            {
                if (mod == null)
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(mod.NexusModId))
                {
                    unknownCount++;
                    continue;
                }

                try
                {
                    string gameDomain = string.IsNullOrWhiteSpace(mod.NexusGameDomain) ? _config.nexusGameDomain : mod.NexusGameDomain;
                    if (string.IsNullOrWhiteSpace(gameDomain))
                    {
                        gameDomain = DefaultNexusGameDomain;
                    }

                    NexusModData remote = GetNexusModData(gameDomain, mod.NexusModId, true);
                    checkedCount++;
                    if (remote == null)
                    {
                        continue;
                    }

                    string remoteVersion = FirstValue(remote.version, string.Empty);
                    if (!string.IsNullOrWhiteSpace(remoteVersion))
                    {
                        mod.NexusVersion = remoteVersion;
                        mod.DisplayVersion = FirstValue(mod.LocalVersion, mod.NexusVersion);
                    }
                    else
                    {
                        LogInfo("Update check: " + mod.Title + " has no version field in Nexus response.");
                        continue;
                    }

                    string localVersion = mod.LocalVersion;
                    if (string.IsNullOrWhiteSpace(localVersion))
                    {
                        LogInfo("Update check: " + mod.Title + " -> latest on Nexus: " + FirstValue(remoteVersion, "unknown") + " (local version unknown).");
                        continue;
                    }

                    if (!AreVersionsLikelyEqual(localVersion, remoteVersion))
                    {
                        updatesAvailable++;
                        LogInfo("Update available: " + mod.Title + " local " + localVersion + " -> Nexus " + FirstValue(remoteVersion, "unknown") + ".");
                    }
                    else
                    {
                        LogInfo("Up to date: " + mod.Title + " (" + localVersion + ").");
                    }
                }
                catch (Exception ex)
                {
                    failedCount++;
                    LogInfo("Update check failed for " + mod.Title + ": " + ex.Message);
                }
            }

            RefreshModList();
            LogInfo("Update check complete. Checked: " + checkedCount + ", Updates: " + updatesAvailable + ", Unknown Nexus ID: " + unknownCount + ", Failed: " + failedCount + ".");
        }

        private bool AreVersionsLikelyEqual(string localVersion, string remoteVersion)
        {
            string left = NormalizeVersionToken(localVersion);
            string right = NormalizeVersionToken(remoteVersion);
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private string NormalizeVersionToken(string value)
        {
            string text = (value ?? string.Empty).Trim();
            while (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(1);
            }
            text = text.Replace(" ", string.Empty);
            return text;
        }

        private bool ContainsPakFiles(string folder)
        {
            if (!Directory.Exists(folder))
            {
                return false;
            }
            return Directory.GetFiles(folder, "*.pak", SearchOption.AllDirectories).Length > 0
                || Directory.GetFiles(folder, "*.utoc", SearchOption.AllDirectories).Length > 0
                || Directory.GetFiles(folder, "*.ucas", SearchOption.AllDirectories).Length > 0;
        }

        private string GetPakModsTargetPath()
        {
            try
            {
                string gameRoot = Path.GetFullPath(Path.Combine(_subnauticaWin64Path, "..", "..", ".."));
                string paks = Path.Combine(gameRoot, "Content", "Paks");
                string mods = Path.Combine(paks, "~mods");
                return mods;
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool ShouldDeployToWin64Root(string folder)
        {
            if (File.Exists(Path.Combine(folder, "dwmapi.dll")))
            {
                return true;
            }
            if (File.Exists(Path.Combine(folder, "winmm.dll")))
            {
                return true;
            }
            if (Directory.Exists(Path.Combine(folder, "ue4ss")))
            {
                return true;
            }
            if (File.Exists(Path.Combine(folder, "UE4SS.dll")))
            {
                return true;
            }
            return false;
        }

        private bool ShouldDeployToUe4ssMods(string folder)
        {
            if (File.Exists(Path.Combine(folder, "enabled.txt")))
            {
                return true;
            }
            if (Directory.Exists(Path.Combine(folder, "Scripts")))
            {
                return true;
            }
            if (File.Exists(Path.Combine(folder, "main.lua")))
            {
                return true;
            }
            return false;
        }

        private void UpsertManagedRecord(ManagedInstallRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.modFolderName))
            {
                return;
            }

            _modListData.installedMods.RemoveAll(delegate(ManagedInstallRecord m)
            {
                return string.Equals(m.modFolderName, record.modFolderName, StringComparison.OrdinalIgnoreCase);
            });
            _modListData.installedMods.Add(record);
        }

        private void RemoveSelectedMod()
        {
            if (_selectedMod == null)
            {
                LogInfo("Remove selected skipped (no selection).");
                return;
            }

            string modFolder = _selectedMod.FolderPath;
            string modTitle = _selectedMod.Title;
            if (!Directory.Exists(modFolder))
            {
                LogInfo("Selected mod folder missing: " + modFolder);
                RefreshModList();
                return;
            }

            DialogResult confirm = MessageBox.Show(
                this,
                "Remove '" + modTitle + "' from manager and deployed locations?",
                "Confirm Remove",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );

            if (confirm != DialogResult.Yes)
            {
                return;
            }

            try
            {
                string folderName = Path.GetFileName(modFolder);
                ManagedInstallRecord record = _modListData.installedMods.FirstOrDefault(delegate(ManagedInstallRecord r)
                {
                    return string.Equals(r.modFolderName, folderName, StringComparison.OrdinalIgnoreCase);
                });

                if (record != null)
                {
                    foreach (string deployedPath in record.deployedPaths)
                    {
                        TryDeletePath(deployedPath);
                    }
                    _modListData.installedMods.Remove(record);
                    SaveModListData(_modListData);
                }

                _modListData.modNotes.RemoveAll(delegate(ModNote n)
                {
                    return string.Equals(n.modFolderName, folderName, StringComparison.OrdinalIgnoreCase);
                });
                _modListData.modCategories.RemoveAll(delegate(ModCategoryAssignment c)
                {
                    return string.Equals(c.modFolderName, folderName, StringComparison.OrdinalIgnoreCase);
                });
                _modListData.modLinks.RemoveAll(delegate(ModLinkAssignment l)
                {
                    return string.Equals(l.modFolderName, folderName, StringComparison.OrdinalIgnoreCase);
                });
                _modListData.modIdentityOverrides.RemoveAll(delegate(ModIdentityOverride l)
                {
                    return string.Equals(l.modFolderName, folderName, StringComparison.OrdinalIgnoreCase);
                });
                _modListData.pinnedMods.RemoveAll(delegate(PinnedModAssignment p)
                {
                    return string.Equals(p.modFolderName, folderName, StringComparison.OrdinalIgnoreCase);
                });
                SaveModListData(_modListData);

                Directory.Delete(modFolder, true);
                SetSelectedModDetails(null);
                RefreshModList();
                LogInfo("Removed mod: " + modTitle);
            }
            catch (Exception ex)
            {
                LogInfo("Remove failed: " + ex.Message);
            }
        }

        private void RenameSelectedMod()
        {
            if (_renameInProgress)
            {
                return;
            }

            _renameInProgress = true;
            _suppressCategorySave = true;
            try
            {
                if (_selectedMod == null)
                {
                    LogInfo("Rename skipped (no mod selected).");
                    return;
                }

                if (_modListData == null)
                {
                    _modListData = CreateDefaultModListData();
                }

                string oldFolderPath = _selectedMod.FolderPath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(oldFolderPath) || !Directory.Exists(oldFolderPath))
                {
                    LogInfo("Rename failed: selected mod folder is missing.");
                    RefreshModList();
                    return;
                }

                string oldFolderName = Path.GetFileName(oldFolderPath) ?? string.Empty;
                string newName = PromptForText("Rename Mod", "Enter new mod folder name:", oldFolderName);
                if (newName == null)
                {
                    return;
                }

                newName = CleanFolderName(newName);
                if (string.IsNullOrWhiteSpace(newName))
                {
                    LogInfo("Rename failed: empty name.");
                    return;
                }
                if (string.Equals(newName, oldFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    LogInfo("Rename skipped: same name.");
                    return;
                }

                string newFolderPath = Path.Combine(_modsDir, newName);
                if (Directory.Exists(newFolderPath))
                {
                    LogInfo("Rename failed: destination already exists.");
                    return;
                }

                Directory.Move(oldFolderPath, newFolderPath);

                if (_modListData.installedMods == null) _modListData.installedMods = new List<ManagedInstallRecord>();
                if (_modListData.modNotes == null) _modListData.modNotes = new List<ModNote>();
                if (_modListData.modCategories == null) _modListData.modCategories = new List<ModCategoryAssignment>();
                if (_modListData.modLinks == null) _modListData.modLinks = new List<ModLinkAssignment>();
                if (_modListData.modIdentityOverrides == null) _modListData.modIdentityOverrides = new List<ModIdentityOverride>();
                if (_modListData.pinnedMods == null) _modListData.pinnedMods = new List<PinnedModAssignment>();

                foreach (ManagedInstallRecord installRecord in _modListData.installedMods.Where(delegate(ManagedInstallRecord r)
                {
                    return string.Equals(r.modFolderName, oldFolderName, StringComparison.OrdinalIgnoreCase);
                }).ToList())
                {
                    installRecord.modFolderName = newName;
                }

                foreach (ModNote note in _modListData.modNotes.Where(delegate(ModNote r)
                {
                    return string.Equals(r.modFolderName, oldFolderName, StringComparison.OrdinalIgnoreCase);
                }).ToList())
                {
                    note.modFolderName = newName;
                }

                foreach (ModCategoryAssignment category in _modListData.modCategories.Where(delegate(ModCategoryAssignment r)
                {
                    return string.Equals(r.modFolderName, oldFolderName, StringComparison.OrdinalIgnoreCase);
                }).ToList())
                {
                    category.modFolderName = newName;
                }

                foreach (ModLinkAssignment link in _modListData.modLinks.Where(delegate(ModLinkAssignment r)
                {
                    return string.Equals(r.modFolderName, oldFolderName, StringComparison.OrdinalIgnoreCase);
                }).ToList())
                {
                    link.modFolderName = newName;
                }

                foreach (ModIdentityOverride identity in _modListData.modIdentityOverrides.Where(delegate(ModIdentityOverride r)
                {
                    return string.Equals(r.modFolderName, oldFolderName, StringComparison.OrdinalIgnoreCase);
                }).ToList())
                {
                    identity.modFolderName = newName;
                }

                foreach (PinnedModAssignment pin in _modListData.pinnedMods.Where(delegate(PinnedModAssignment r)
                {
                    return string.Equals(r.modFolderName, oldFolderName, StringComparison.OrdinalIgnoreCase);
                }).ToList())
                {
                    pin.modFolderName = newName;
                }

                SaveModListData(_modListData);

                ModInfo newSelection = new ModInfo
                {
                    FolderPath = newFolderPath,
                    Title = _selectedMod.Title,
                    Author = _selectedMod.Author,
                    Description = _selectedMod.Description,
                    Category = _selectedMod.Category,
                    IconPath = _selectedMod.IconPath,
                    NexusModId = _selectedMod.NexusModId,
                    NexusGameDomain = _selectedMod.NexusGameDomain,
                    NexusUrl = _selectedMod.NexusUrl
                };
                _selectedMod = newSelection;

                RefreshModList();
                LogInfo("Renamed mod folder '" + oldFolderName + "' -> '" + newName + "'.");
            }
            catch (Exception ex)
            {
                LogInfo("Rename failed: " + ex);
                try
                {
                    MessageBox.Show(this, "Rename failed: " + ex.Message, "Rename Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch
                {
                }
            }
            finally
            {
                _suppressCategorySave = false;
                _renameInProgress = false;
            }
        }

        private void PurgeAllManagedMods()
        {
            DialogResult confirm = MessageBox.Show(
                this,
                "Purge all traces of mods installed by this program?",
                "Confirm Purge All",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            int removed = 0;
            int errors = 0;

            List<ManagedInstallRecord> snapshot = _modListData.installedMods.ToList();
            foreach (ManagedInstallRecord record in snapshot)
            {
                foreach (string deployedPath in record.deployedPaths)
                {
                    try
                    {
                        if (TryDeletePath(deployedPath))
                        {
                            removed++;
                        }
                    }
                    catch
                    {
                        errors++;
                    }
                }

                string modStore = Path.Combine(_modsDir, record.modFolderName);
                try
                {
                    if (Directory.Exists(modStore))
                    {
                        Directory.Delete(modStore, true);
                        removed++;
                    }
                }
                catch
                {
                    errors++;
                }
            }

            _modListData.installedMods.Clear();
            _modListData.modNotes.Clear();
            _modListData.modCategories.Clear();
            _modListData.modLinks.Clear();
            _modListData.modIdentityOverrides.Clear();
            _modListData.pinnedMods.Clear();
            _modListData.pinnedCategories.Clear();
            SaveModListData(_modListData);
            SetSelectedModDetails(null);
            RefreshModList();
            LogInfo("Purge complete. Removed paths: " + removed + ", Errors: " + errors);
        }

        private bool TryDeletePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                if (IsProtectedDeletionPath(path))
                {
                    LogInfo("Skipped protected delete path: " + path);
                    return false;
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogInfo("Delete path failed '" + path + "': " + ex.Message);
            }

            return false;
        }

        private bool IsProtectedDeletionPath(string path)
        {
            string full = SafeFullPath(path);
            if (string.IsNullOrWhiteSpace(full))
            {
                return true;
            }

            string root = Path.GetPathRoot(full) ?? string.Empty;
            if (string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string gameWin64 = SafeFullPath(_subnauticaWin64Path);
            string gameRoot = SafeFullPath(GetSubnauticaRootPath());
            string gamePaks = string.Empty;
            string gamePakMods = string.Empty;
            if (!string.IsNullOrWhiteSpace(gameRoot))
            {
                gamePaks = SafeFullPath(Path.Combine(gameRoot, "Content", "Paks"));
                if (!string.IsNullOrWhiteSpace(gamePaks))
                {
                    gamePakMods = SafeFullPath(Path.Combine(gamePaks, "~mods"));
                }
            }

            string norm = full.TrimEnd('\\');
            if (!string.IsNullOrWhiteSpace(gameWin64) && string.Equals(norm, gameWin64.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(gameRoot) && string.Equals(norm, gameRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(gamePaks) && string.Equals(norm, gamePaks.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(gamePakMods) && string.Equals(norm, gamePakMods.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private string SafeFullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return string.Empty;
            }
        }

        private string GetUniqueModFolderPath(string preferredName)
        {
            string clean = CleanFolderName(preferredName);
            if (string.IsNullOrWhiteSpace(clean))
            {
                clean = "Mod";
            }

            string basePath = Path.Combine(_modsDir, clean);
            if (!Directory.Exists(basePath))
            {
                return basePath;
            }

            for (int i = 2; i < 5000; i++)
            {
                string candidate = basePath + " (" + i + ")";
                if (!Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(_modsDir, clean + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        }

        private string CleanFolderName(string text)
        {
            string value = text ?? string.Empty;
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value.Trim();
        }

        private void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string target = Path.Combine(destinationDir, Path.GetFileName(file));
                File.Copy(file, target, true);
            }

            foreach (string directory in Directory.GetDirectories(sourceDir))
            {
                string target = Path.Combine(destinationDir, Path.GetFileName(directory));
                CopyDirectory(directory, target);
            }
        }

        private void CopyDirectoryContents(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string target = Path.Combine(destinationDir, Path.GetFileName(file));
                File.Copy(file, target, true);
            }
            foreach (string directory in Directory.GetDirectories(sourceDir))
            {
                string target = Path.Combine(destinationDir, Path.GetFileName(directory));
                CopyDirectory(directory, target);
            }
        }

        private List<string> CopyDirectoryContentsTracked(string sourceDir, string destinationDir)
        {
            List<string> copied = new List<string>();
            if (!Directory.Exists(sourceDir))
            {
                return copied;
            }

            foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(sourceDir.Length).TrimStart('\\', '/');
                if (IsBlockedDeployFile(file))
                {
                    LogInfo("Blocked potentially unsafe file from auto-deploy: " + relative);
                    continue;
                }
                string target = Path.Combine(destinationDir, relative);
                string parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                File.Copy(file, target, true);
                copied.Add(target);
            }

            return copied;
        }

        private bool IsBlockedDeployFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(ext))
            {
                return false;
            }

            string[] blocked = new[]
            {
                ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".scr", ".com", ".pif", ".lnk"
            };
            return blocked.Contains(ext);
        }

        private void SafeDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }

        private ModInfo BuildModInfo(string modFolder)
        {
            ModInfo mod = new ModInfo();
            mod.FolderPath = modFolder;
            mod.Title = Path.GetFileName(modFolder);
            mod.Author = "Unknown";
            mod.Category = "MOD";
            mod.Description = GetManualDescription(Path.GetFileName(modFolder));
            mod.IconPath = string.Empty;
            mod.LocalVersion = string.Empty;
            mod.NexusVersion = string.Empty;
            mod.DisplayVersion = string.Empty;
            mod.NexusGameDomain = _config.nexusGameDomain;
            mod.NexusModId = string.Empty;
            mod.NexusUrl = string.Empty;

            Dictionary<string, string> metadata = TryReadMetadata(modFolder);
            if (metadata.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(metadata["title"]))
                {
                    mod.Title = metadata["title"];
                }
                if (!string.IsNullOrWhiteSpace(metadata["author"]))
                {
                    mod.Author = metadata["author"];
                }
                if (!string.IsNullOrWhiteSpace(metadata["nexus_mod_id"]))
                {
                    mod.NexusModId = metadata["nexus_mod_id"];
                }
                if (!string.IsNullOrWhiteSpace(metadata["nexus_url"]))
                {
                    mod.NexusUrl = metadata["nexus_url"];
                }
                if (!string.IsNullOrWhiteSpace(metadata["nexus_game_domain"]))
                {
                    mod.NexusGameDomain = metadata["nexus_game_domain"];
                }
                if (!string.IsNullOrWhiteSpace(metadata["version"]))
                {
                    mod.LocalVersion = metadata["version"];
                }
            }

            if (string.IsNullOrWhiteSpace(mod.LocalVersion))
            {
                mod.LocalVersion = TryInferLocalVersion(modFolder, mod.Title);
            }

            ApplyManualModLink(mod);
            EnsureUe4ssNexusLink(mod, false);
            TryInferNexusFromUrl(mod);
            TryInferNexusFromFolderName(mod);
            TryInferNexusFromFileContents(mod);
            TryInferNexusFromWebSearch(mod);
            mod.Category = DetermineCategory(modFolder, mod.Title);
            TryEnrichWithNexusData(mod);
            ApplyManualIdentityOverrides(mod);
            mod.Category = DetermineCategory(modFolder, mod.Title);
            mod.DisplayVersion = FirstValue(mod.LocalVersion, mod.NexusVersion);
            mod.IconPath = ResolveCategoryIconPath(mod);
            return mod;
        }

        private bool EnsureUe4ssNexusLink(ModInfo mod, bool persist)
        {
            if (mod == null || string.IsNullOrWhiteSpace(mod.FolderPath))
            {
                return false;
            }
            if (!IsUe4ssLibraryMod(mod.FolderPath))
            {
                return false;
            }

            mod.NexusGameDomain = DefaultNexusGameDomain;
            mod.NexusModId = Ue4ssNexusModId;
            mod.NexusUrl = "https://www.nexusmods.com/" + DefaultNexusGameDomain + "/mods/" + Ue4ssNexusModId;
            if (persist)
            {
                string folderName = Path.GetFileName(mod.FolderPath) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(folderName))
                {
                    SetManualModUrl(folderName, mod.NexusUrl);
                }
            }
            return true;
        }

        private void ApplyManualIdentityOverrides(ModInfo mod)
        {
            if (mod == null)
            {
                return;
            }

            string folderName = Path.GetFileName(mod.FolderPath) ?? string.Empty;
            string manualTitle = GetManualTitle(folderName);
            string manualAuthor = GetManualAuthor(folderName);
            if (!string.IsNullOrWhiteSpace(manualTitle))
            {
                mod.Title = manualTitle;
            }
            if (!string.IsNullOrWhiteSpace(manualAuthor))
            {
                mod.Author = manualAuthor;
            }
        }

        private string TryInferLocalVersion(string modFolder, string modTitle)
        {
            List<string> candidates = new List<string>();
            candidates.Add(Path.GetFileName(modFolder) ?? string.Empty);
            candidates.Add(modTitle ?? string.Empty);

            try
            {
                foreach (string file in Directory.GetFiles(modFolder, "*.*", SearchOption.TopDirectoryOnly).Take(16))
                {
                    candidates.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
            catch
            {
            }

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }
                Match m = Regex.Match(candidate, @"\bv?\d+(?:[._-]\d+){1,4}\b", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    return m.Value.Trim().TrimStart('v', 'V').Replace('_', '.').Replace('-', '.');
                }
            }

            return string.Empty;
        }

        private void ApplyManualModLink(ModInfo mod)
        {
            if (mod == null || _modListData == null || _modListData.modLinks == null)
            {
                return;
            }

            string folderName = Path.GetFileName(mod.FolderPath) ?? string.Empty;
            ModLinkAssignment row = _modListData.modLinks.FirstOrDefault(delegate(ModLinkAssignment x)
            {
                return string.Equals(x.modFolderName, folderName, StringComparison.OrdinalIgnoreCase);
            });
            if (row == null || string.IsNullOrWhiteSpace(row.url))
            {
                return;
            }

            mod.NexusUrl = row.url;
        }

        private Dictionary<string, string> TryReadMetadata(string modFolder)
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            values["title"] = string.Empty;
            values["author"] = string.Empty;
            values["version"] = string.Empty;
            values["nexus_mod_id"] = string.Empty;
            values["nexus_url"] = string.Empty;
            values["nexus_game_domain"] = string.Empty;

            string[] candidates = new[]
            {
                Path.Combine(modFolder, "mod.json"),
                Path.Combine(modFolder, "manifest.json"),
                Path.Combine(modFolder, "info.json")
            };

            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    string json = File.ReadAllText(candidate);
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    object data = serializer.DeserializeObject(json);
                    ExtractMetadataFromObject(data, values);
                    break;
                }
                catch
                {
                }
            }

            return values;
        }

        private void ExtractMetadataFromObject(object data, Dictionary<string, string> values)
        {
            IDictionary<string, object> map = data as IDictionary<string, object>;
            if (map == null)
            {
                return;
            }

            values["title"] = FirstValue(
                values["title"],
                ReadKey(map, "title"),
                ReadKey(map, "name"),
                ReadKey(map, "modTitle"),
                ReadNested(map, "mod", "title"),
                ReadNested(map, "metadata", "title"),
                ReadNested(map, "metadata", "name")
            );

            values["author"] = FirstValue(
                values["author"],
                ReadKey(map, "author"),
                ReadKey(map, "modAuthor"),
                ReadKey(map, "creator"),
                ReadNested(map, "metadata", "author"),
                ReadNested(map, "mod", "author")
            );

            values["version"] = FirstValue(
                values["version"],
                ReadKey(map, "version"),
                ReadKey(map, "modVersion"),
                ReadKey(map, "release"),
                ReadNested(map, "metadata", "version"),
                ReadNested(map, "mod", "version")
            );

            values["nexus_mod_id"] = FirstValue(
                values["nexus_mod_id"],
                ReadKey(map, "nexusModId"),
                ReadKey(map, "nexus_mod_id"),
                ReadKey(map, "modId"),
                ReadNested(map, "nexus", "mod_id")
            );

            values["nexus_url"] = FirstValue(
                values["nexus_url"],
                ReadKey(map, "nexusUrl"),
                ReadKey(map, "url"),
                ReadKey(map, "homepage"),
                ReadKey(map, "website"),
                ReadNested(map, "nexus", "url")
            );

            values["nexus_game_domain"] = FirstValue(
                values["nexus_game_domain"],
                ReadKey(map, "nexusGameDomain"),
                ReadKey(map, "gameDomain"),
                ReadNested(map, "nexus", "game_domain")
            );
        }

        private string ReadKey(IDictionary<string, object> map, string key)
        {
            foreach (KeyValuePair<string, object> pair in map)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value == null ? string.Empty : pair.Value.ToString();
                }
            }
            return string.Empty;
        }

        private string ReadNested(IDictionary<string, object> map, string container, string key)
        {
            foreach (KeyValuePair<string, object> pair in map)
            {
                if (!string.Equals(pair.Key, container, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                IDictionary<string, object> nested = pair.Value as IDictionary<string, object>;
                if (nested == null)
                {
                    return string.Empty;
                }

                return ReadKey(nested, key);
            }

            return string.Empty;
        }

        private string FirstValue(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
            return string.Empty;
        }

        private void TryInferNexusFromUrl(ModInfo mod)
        {
            if (mod == null)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(mod.NexusUrl))
            {
                return;
            }

            Match m = Regex.Match(mod.NexusUrl, @"nexusmods\.com\/([^\/]+)\/mods\/(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(mod.NexusGameDomain))
            {
                mod.NexusGameDomain = m.Groups[1].Value.Trim();
            }
            if (string.IsNullOrWhiteSpace(mod.NexusModId))
            {
                mod.NexusModId = m.Groups[2].Value.Trim();
            }
        }

        private void TryEnrichWithNexusData(ModInfo mod)
        {
            if (mod == null)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(mod.NexusModId))
            {
                if (_nexusSkipLogged.Add(mod.FolderPath))
                {
                    LogInfo("Nexus skipped for '" + mod.Title + "': no mod ID detected.");
                }
                return;
            }
            if (string.IsNullOrWhiteSpace(mod.NexusGameDomain))
            {
                mod.NexusGameDomain = _config.nexusGameDomain;
            }
            if (string.IsNullOrWhiteSpace(mod.NexusGameDomain))
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(_config.nexusApiKey))
            {
                if (!_nexusKeyMissingLogged)
                {
                    _nexusKeyMissingLogged = true;
                    LogInfo("Nexus API key missing in config.json; Nexus metadata pull is disabled.");
                }
                return;
            }

            try
            {
                NexusModData data = GetNexusModData(mod.NexusGameDomain, mod.NexusModId);
                if (data == null)
                {
                    return;
                }
                if (!string.IsNullOrWhiteSpace(data.name))
                {
                    mod.Title = data.name;
                }
                if (!string.IsNullOrWhiteSpace(data.author))
                {
                    mod.Author = data.author;
                }
                if (string.IsNullOrWhiteSpace(mod.Description))
                {
                    string pulledDescription = NormalizeNexusDescription(FirstValue(data.summary, data.description));
                    if (!string.IsNullOrWhiteSpace(pulledDescription))
                    {
                        mod.Description = pulledDescription;
                    }
                }
                if (!string.IsNullOrWhiteSpace(data.version))
                {
                    mod.NexusVersion = data.version;
                    if (string.IsNullOrWhiteSpace(mod.LocalVersion))
                    {
                        mod.DisplayVersion = data.version;
                    }
                }
                if (!string.IsNullOrWhiteSpace(data.picture_url))
                {
                    string localImage = EnsureNexusImageCached(mod.NexusGameDomain, mod.NexusModId, data.picture_url);
                    if (!string.IsNullOrWhiteSpace(localImage))
                    {
                        mod.IconPath = localImage;
                    }
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.Unauthorized && !_nexusAuthFailureLogged)
                {
                    _nexusAuthFailureLogged = true;
                    LogInfo("Nexus API key unauthorized. Update nexusApiKey in config.json.");
                }
            }
            catch (Exception ex)
            {
                LogInfo("Nexus metadata pull failed for mod " + mod.NexusModId + ": " + ex.Message);
            }
        }

        private string NormalizeNexusDescription(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string value = WebUtility.HtmlDecode(text);
            value = value.Replace("\r", " ").Replace("\n", " ");
            value = Regex.Replace(value, @"<[^>]+>", " ");
            value = Regex.Replace(value, @"\[[^\]]+\]", " ");
            value = Regex.Replace(value, @"\s+", " ").Trim();

            if (value.Length > 220)
            {
                value = value.Substring(0, 220).TrimEnd() + "...";
            }

            return value;
        }

        private void TryInferNexusFromFileContents(ModInfo mod)
        {
            if (mod == null || string.IsNullOrWhiteSpace(mod.FolderPath) || !Directory.Exists(mod.FolderPath))
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(mod.NexusModId) && !string.IsNullOrWhiteSpace(mod.NexusGameDomain) && !string.IsNullOrWhiteSpace(mod.NexusUrl))
            {
                return;
            }

            string[] interestingExtensions = new[] { ".txt", ".md", ".json", ".uplugin", ".url", ".ini", ".lua" };
            IEnumerable<string> files = Directory.GetFiles(mod.FolderPath, "*.*", SearchOption.AllDirectories)
                .Where(delegate(string path)
                {
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    return interestingExtensions.Contains(ext);
                })
                .Take(48);

            foreach (string file in files)
            {
                try
                {
                    FileInfo info = new FileInfo(file);
                    if (info.Length > 256000)
                    {
                        continue;
                    }

                    string text = File.ReadAllText(file);
                    Match http = Regex.Match(text, @"https?:\/\/(?:www\.)?nexusmods\.com\/([^\/\s]+)\/mods\/(\d+)", RegexOptions.IgnoreCase);
                    if (http.Success)
                    {
                        mod.NexusGameDomain = FirstValue(mod.NexusGameDomain, http.Groups[1].Value);
                        mod.NexusModId = FirstValue(mod.NexusModId, http.Groups[2].Value);
                        mod.NexusUrl = "https://www.nexusmods.com/" + mod.NexusGameDomain + "/mods/" + mod.NexusModId;
                        break;
                    }

                    Match nxm = Regex.Match(text, @"nxm:\/\/([^\/\s]+)\/mods\/(\d+)", RegexOptions.IgnoreCase);
                    if (nxm.Success)
                    {
                        mod.NexusGameDomain = FirstValue(mod.NexusGameDomain, nxm.Groups[1].Value);
                        mod.NexusModId = FirstValue(mod.NexusModId, nxm.Groups[2].Value);
                        mod.NexusUrl = "https://www.nexusmods.com/" + mod.NexusGameDomain + "/mods/" + mod.NexusModId;
                        break;
                    }
                }
                catch
                {
                }
            }

            if (string.IsNullOrWhiteSpace(mod.NexusUrl) && !string.IsNullOrWhiteSpace(mod.NexusGameDomain) && !string.IsNullOrWhiteSpace(mod.NexusModId))
            {
                mod.NexusUrl = "https://www.nexusmods.com/" + mod.NexusGameDomain + "/mods/" + mod.NexusModId;
            }
        }

        private void TryInferNexusFromFolderName(ModInfo mod)
        {
            if (mod == null)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(mod.NexusModId))
            {
                return;
            }

            string folderName = Path.GetFileName(mod.FolderPath);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return;
            }

            Match pattern1 = Regex.Match(folderName, @"-(\d+)-[A-Za-z]", RegexOptions.IgnoreCase);
            if (pattern1.Success)
            {
                mod.NexusModId = pattern1.Groups[1].Value.Trim();
                return;
            }

            Match pattern2 = Regex.Match(folderName, @"-(\d+)(?:$|-)", RegexOptions.IgnoreCase);
            if (pattern2.Success)
            {
                mod.NexusModId = pattern2.Groups[1].Value.Trim();
            }

            if (string.IsNullOrWhiteSpace(mod.NexusGameDomain))
            {
                mod.NexusGameDomain = _config.nexusGameDomain;
            }
            if (string.IsNullOrWhiteSpace(mod.NexusUrl) && !string.IsNullOrWhiteSpace(mod.NexusGameDomain) && !string.IsNullOrWhiteSpace(mod.NexusModId))
            {
                mod.NexusUrl = "https://www.nexusmods.com/" + mod.NexusGameDomain + "/mods/" + mod.NexusModId;
            }
        }

        private void TryInferNexusFromWebSearch(ModInfo mod)
        {
            if (mod == null)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(mod.NexusModId))
            {
                return;
            }

            string key = mod.FolderPath ?? string.Empty;
            if (!_nexusSearchTried.Add(key))
            {
                return;
            }

            string domain = string.IsNullOrWhiteSpace(mod.NexusGameDomain) ? _config.nexusGameDomain : mod.NexusGameDomain;
            if (string.IsNullOrWhiteSpace(domain))
            {
                domain = DefaultNexusGameDomain;
            }

            string folderName = Path.GetFileName(mod.FolderPath) ?? string.Empty;
            List<string> queries = new List<string>();
            if (!string.IsNullOrWhiteSpace(mod.Title))
            {
                queries.Add(mod.Title);
            }
            if (!string.IsNullOrWhiteSpace(folderName) && !string.Equals(folderName, mod.Title, StringComparison.OrdinalIgnoreCase))
            {
                queries.Add(folderName);
            }
            string compactTitle = Regex.Replace(mod.Title ?? string.Empty, @"[^A-Za-z0-9 ]", " ").Trim();
            if (!string.IsNullOrWhiteSpace(compactTitle) && !queries.Any(delegate(string q) { return string.Equals(q, compactTitle, StringComparison.OrdinalIgnoreCase); }))
            {
                queries.Add(compactTitle);
            }

            foreach (string query in queries)
            {
                string foundDomain;
                string foundId;
                string foundUrl;
                if (TryFindNexusLinkViaDuckDuckGo(query, domain, out foundDomain, out foundId, out foundUrl)
                    || TryFindNexusLinkViaBing(query, domain, out foundDomain, out foundId, out foundUrl))
                {
                    mod.NexusGameDomain = foundDomain;
                    mod.NexusModId = foundId;
                    mod.NexusUrl = foundUrl;
                    LogInfo("Inferred Nexus ID via web search for '" + mod.Title + "': " + foundDomain + "/" + foundId);
                    return;
                }
            }
        }

        private bool TryFindNexusLinkViaDuckDuckGo(string query, string expectedGameDomain, out string foundDomain, out string foundId, out string foundUrl)
        {
            foundDomain = string.Empty;
            foundId = string.Empty;
            foundUrl = string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            string scoped = "site:nexusmods.com/" + expectedGameDomain + "/mods " + query;
            string url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(scoped);
            return TryExtractNexusLinkFromHtml(url, expectedGameDomain, out foundDomain, out foundId, out foundUrl);
        }

        private bool TryFindNexusLinkViaBing(string query, string expectedGameDomain, out string foundDomain, out string foundId, out string foundUrl)
        {
            foundDomain = string.Empty;
            foundId = string.Empty;
            foundUrl = string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            string scoped = "site:nexusmods.com/" + expectedGameDomain + "/mods " + query;
            string url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(scoped);
            return TryExtractNexusLinkFromHtml(url, expectedGameDomain, out foundDomain, out foundId, out foundUrl);
        }

        private bool TryExtractNexusLinkFromHtml(string requestUrl, string expectedGameDomain, out string foundDomain, out string foundId, out string foundUrl)
        {
            foundDomain = string.Empty;
            foundId = string.Empty;
            foundUrl = string.Empty;
            if (string.IsNullOrWhiteSpace(requestUrl))
            {
                return false;
            }

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestUrl);
                request.Method = "GET";
                request.Timeout = 12000;
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123 Safari/537.36";

                string html;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    html = reader.ReadToEnd();
                }

                if (string.IsNullOrWhiteSpace(html))
                {
                    return false;
                }

                string decoded = WebUtility.HtmlDecode(html);
                MatchCollection matches = Regex.Matches(decoded, @"https?:\/\/(?:www\.)?nexusmods\.com\/([^\/\s""'<>]+)\/mods\/(\d+)", RegexOptions.IgnoreCase);
                if (matches.Count == 0)
                {
                    return false;
                }

                Match preferred = matches.Cast<Match>().FirstOrDefault(delegate(Match m)
                {
                    return string.Equals(m.Groups[1].Value, expectedGameDomain, StringComparison.OrdinalIgnoreCase);
                });
                Match pick = preferred ?? matches[0];
                if (pick == null || !pick.Success)
                {
                    return false;
                }

                foundDomain = pick.Groups[1].Value.Trim();
                foundId = pick.Groups[2].Value.Trim();
                foundUrl = "https://www.nexusmods.com/" + foundDomain + "/mods/" + foundId;
                return !string.IsNullOrWhiteSpace(foundId);
            }
            catch
            {
                return false;
            }
        }

        private NexusModData GetNexusModData(string gameDomain, string modId, bool forceRefresh = false)
        {
            string cacheFile = Path.Combine(_nexusCacheDir, gameDomain + "_" + modId + ".json");
            string json = string.Empty;

            if (!forceRefresh && File.Exists(cacheFile))
            {
                DateTime age = File.GetLastWriteTimeUtc(cacheFile);
                if ((DateTime.UtcNow - age).TotalHours <= 12)
                {
                    json = File.ReadAllText(cacheFile);
                }
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                string url = "https://api.nexusmods.com/v1/games/" + gameDomain + "/mods/" + modId + ".json";
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 20000;
                request.UserAgent = "S2MM";
                request.Headers["apikey"] = _config.nexusApiKey;
                request.Headers["application-name"] = "S2MM";

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    json = reader.ReadToEnd();
                }

                File.WriteAllText(cacheFile, json);
                LogInfo("Pulled Nexus metadata for mod " + modId + ".");
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            object raw = serializer.DeserializeObject(json);
            IDictionary<string, object> map = raw as IDictionary<string, object>;
            if (map == null)
            {
                return null;
            }

            NexusModData data = new NexusModData();
            data.name = ReadKey(map, "name");
            data.summary = FirstValue(ReadKey(map, "summary"), ReadKey(map, "description"));
            data.description = ReadKey(map, "description");
            data.author = FirstValue(ReadKey(map, "author"), ReadKey(map, "uploaded_by"));
            data.version = ReadKey(map, "version");
            data.picture_url = ReadKey(map, "picture_url");
            return data;
        }

        private string EnsureNexusImageCached(string gameDomain, string modId, string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return string.Empty;
            }

            string extension = ".png";
            try
            {
                Uri uri = new Uri(imageUrl);
                string extCandidate = Path.GetExtension(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(extCandidate) && extCandidate.Length <= 5)
                {
                    extension = extCandidate;
                }
            }
            catch
            {
            }

            string localPath = Path.Combine(_nexusImageCacheDir, gameDomain + "_" + modId + extension);
            if (File.Exists(localPath))
            {
                return localPath;
            }

            try
            {
                using (WebClient wc = new WebClient())
                {
                    wc.DownloadFile(imageUrl, localPath);
                }
                LogInfo("Cached Nexus image for mod " + modId + ".");
                return localPath;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string DetermineCategory(string modFolder, string modTitle)
        {
            string folderName = Path.GetFileName(modFolder) ?? string.Empty;
            string manual = NormalizeCategoryName(GetManualCategory(folderName));
            if (!string.IsNullOrWhiteSpace(manual))
            {
                return manual;
            }

            if (IsUe4ssLibraryMod(modFolder))
            {
                return CategoryUe4ss;
            }

            if (IsSn2ModSettingsMainMod(modFolder, modTitle) || IsSmlMod(modFolder, modTitle))
            {
                return CategorySn2Settings;
            }

            return "MOD";
        }

        private bool IsUe4ssLibraryMod(string modFolder)
        {
            string folderName = Path.GetFileName(modFolder) ?? string.Empty;
            string lower = folderName.ToLowerInvariant();
            bool hasLoaderDll = File.Exists(Path.Combine(modFolder, "dwmapi.dll"))
                || File.Exists(Path.Combine(modFolder, "winmm.dll"))
                || File.Exists(Path.Combine(modFolder, "xinput1_3.dll"))
                || File.Exists(Path.Combine(modFolder, "version.dll"));
            bool hasUe4ssCore = Directory.Exists(Path.Combine(modFolder, "ue4ss"))
                && (File.Exists(Path.Combine(modFolder, "ue4ss", "UE4SS.dll"))
                    || File.Exists(Path.Combine(modFolder, "ue4ss", "UE4SS-settings.ini"))
                    || Directory.Exists(Path.Combine(modFolder, "ue4ss", "UE4SS_Signatures")));

            bool nameLooksLikeLibrary = string.Equals(lower, "ue4ss", StringComparison.OrdinalIgnoreCase)
                || lower.StartsWith("ue4ss-", StringComparison.OrdinalIgnoreCase)
                || lower.Contains("re-ue4ss");

            return (hasLoaderDll && hasUe4ssCore) || (nameLooksLikeLibrary && hasUe4ssCore);
        }

        private bool IsSn2ModSettingsMainMod(string modFolder, string modTitle)
        {
            string folderName = Path.GetFileName(modFolder) ?? string.Empty;
            string lower = (folderName + " " + (modTitle ?? string.Empty)).ToLowerInvariant();
            return lower.Contains("sn2modsettings")
                || lower.Contains("sn2 mod settings")
                || lower.Contains("modsettings");
        }

        private bool IsSmlMod(string modFolder, string modTitle)
        {
            string folderName = (Path.GetFileName(modFolder) ?? string.Empty).Trim();
            string title = (modTitle ?? string.Empty).Trim();
            string combined = (folderName + " " + title).ToLowerInvariant();
            return string.Equals(folderName, "SML", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("simple blueprint loader and console enabler")
                || combined.Contains("simple blueprint loader")
                || Regex.IsMatch(combined, @"\bsml\b", RegexOptions.IgnoreCase);
        }

        private string NormalizeCategoryName(string category)
        {
            string value = (category ?? string.Empty).Trim();
            if (string.Equals(value, "MAIN - UE4SS", StringComparison.OrdinalIgnoreCase))
            {
                return CategoryUe4ss;
            }
            if (string.Equals(value, "UE4SS", StringComparison.OrdinalIgnoreCase))
            {
                return CategoryUe4ss;
            }
            if (string.Equals(value, "SN2 Mod Settings", StringComparison.OrdinalIgnoreCase))
            {
                return CategorySn2Settings;
            }
            if (string.Equals(value, "MAIN - SN2 Mod Settings", StringComparison.OrdinalIgnoreCase))
            {
                return CategorySn2Settings;
            }
            return value;
        }

        private int GetCategoryRank(string category)
        {
            string normalized = NormalizeCategoryName(category);
            if (string.Equals(normalized, CategoryUe4ss, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
            if (string.Equals(normalized, CategorySn2Settings, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
            return 2;
        }

        private int GetCategoryPinRank(string category)
        {
            return IsCategoryPinned(category) ? 0 : 1;
        }

        private int GetModPinRank(string modFolderName)
        {
            return IsModPinned(modFolderName) ? 0 : 1;
        }

        private string ResolveCategoryIconPath(ModInfo mod)
        {
            if (mod == null)
            {
                return Path.Combine(_assetsDir, "icon_main.png");
            }

            string ue4ssIcon = Path.Combine(_assetsDir, "icon_ue4ss.png");
            string sn2SettingsIcon = Path.Combine(_assetsDir, "icon_sn2_settings.png");
            string mainIcon = Path.Combine(_assetsDir, "icon_main.png");

            if (string.Equals(NormalizeCategoryName(mod.Category), CategoryUe4ss, StringComparison.OrdinalIgnoreCase) && File.Exists(ue4ssIcon))
            {
                return ue4ssIcon;
            }
            if (string.Equals(NormalizeCategoryName(mod.Category), CategorySn2Settings, StringComparison.OrdinalIgnoreCase) && File.Exists(sn2SettingsIcon))
            {
                return sn2SettingsIcon;
            }
            if (!string.IsNullOrWhiteSpace(mod.IconPath) && File.Exists(mod.IconPath))
            {
                return mod.IconPath;
            }
            if (File.Exists(mainIcon))
            {
                return mainIcon;
            }

            return string.Empty;
        }

        private string GetManualDescription(string modFolderName)
        {
            if (_modListData == null || _modListData.modNotes == null || string.IsNullOrWhiteSpace(modFolderName))
            {
                return string.Empty;
            }

            ModNote note = _modListData.modNotes.FirstOrDefault(delegate(ModNote n)
            {
                return string.Equals(n.modFolderName, modFolderName, StringComparison.OrdinalIgnoreCase);
            });
            return note == null ? string.Empty : (note.description ?? string.Empty);
        }

        private string GetManualCategory(string modFolderName)
        {
            if (_modListData == null || _modListData.modCategories == null || string.IsNullOrWhiteSpace(modFolderName))
            {
                return string.Empty;
            }

            ModCategoryAssignment row = _modListData.modCategories.FirstOrDefault(delegate(ModCategoryAssignment n)
            {
                return string.Equals(n.modFolderName, modFolderName, StringComparison.OrdinalIgnoreCase);
            });
            return row == null ? string.Empty : NormalizeCategoryName(row.category ?? string.Empty);
        }

        private void SetManualCategory(string modFolderName, string category)
        {
            if (_modListData == null)
            {
                return;
            }
            if (_modListData.modCategories == null)
            {
                _modListData.modCategories = new List<ModCategoryAssignment>();
            }

            ModCategoryAssignment row = _modListData.modCategories.FirstOrDefault(delegate(ModCategoryAssignment n)
            {
                return string.Equals(n.modFolderName, modFolderName, StringComparison.OrdinalIgnoreCase);
            });

            if (row == null)
            {
                row = new ModCategoryAssignment
                {
                    modFolderName = modFolderName,
                    category = NormalizeCategoryName(category ?? string.Empty)
                };
                _modListData.modCategories.Add(row);
                return;
            }

            row.category = NormalizeCategoryName(category ?? string.Empty);
        }

        private bool IsCategoryPinned(string category)
        {
            if (_modListData == null || _modListData.pinnedCategories == null || string.IsNullOrWhiteSpace(category))
            {
                return false;
            }
            string normalized = NormalizeCategoryName(category);

            PinnedCategoryAssignment row = _modListData.pinnedCategories.FirstOrDefault(delegate(PinnedCategoryAssignment n)
            {
                return string.Equals(NormalizeCategoryName(n.category), normalized, StringComparison.OrdinalIgnoreCase);
            });
            return row != null && row.pinned;
        }

        private void SetCategoryPinned(string category, bool pinned)
        {
            if (_modListData == null || string.IsNullOrWhiteSpace(category))
            {
                return;
            }
            string normalized = NormalizeCategoryName(category);
            if (_modListData.pinnedCategories == null)
            {
                _modListData.pinnedCategories = new List<PinnedCategoryAssignment>();
            }

            PinnedCategoryAssignment row = _modListData.pinnedCategories.FirstOrDefault(delegate(PinnedCategoryAssignment n)
            {
                return string.Equals(NormalizeCategoryName(n.category), normalized, StringComparison.OrdinalIgnoreCase);
            });

            if (!pinned)
            {
                if (row != null)
                {
                    _modListData.pinnedCategories.Remove(row);
                }
                return;
            }

            if (row == null)
            {
                row = new PinnedCategoryAssignment
                {
                    category = normalized,
                    pinned = true
                };
                _modListData.pinnedCategories.Add(row);
                return;
            }

            row.pinned = true;
        }

        private bool IsModPinned(string modFolderName)
        {
            if (_modListData == null || _modListData.pinnedMods == null || string.IsNullOrWhiteSpace(modFolderName))
            {
                return false;
            }

            PinnedModAssignment row = _modListData.pinnedMods.FirstOrDefault(delegate(PinnedModAssignment n)
            {
                return string.Equals(n.modFolderName, modFolderName, StringComparison.OrdinalIgnoreCase);
            });
            return row != null && row.pinned;
        }

        private void SetModPinned(string modFolderName, bool pinned)
        {
            if (_modListData == null || string.IsNullOrWhiteSpace(modFolderName))
            {
                return;
            }
            if (_modListData.pinnedMods == null)
            {
                _modListData.pinnedMods = new List<PinnedModAssignment>();
            }

            PinnedModAssignment row = _modListData.pinnedMods.FirstOrDefault(delegate(PinnedModAssignment n)
            {
                return string.Equals(n.modFolderName, modFolderName, StringComparison.OrdinalIgnoreCase);
            });

            if (!pinned)
            {
                if (row != null)
                {
                    _modListData.pinnedMods.Remove(row);
                }
                return;
            }

            if (row == null)
            {
                row = new PinnedModAssignment
                {
                    modFolderName = modFolderName,
                    pinned = true
                };
                _modListData.pinnedMods.Add(row);
                return;
            }

            row.pinned = true;
        }

        private string GetManualModUrl(string modFolderName)
        {
            if (_modListData == null || _modListData.modLinks == null || string.IsNullOrWhiteSpace(modFolderName))
            {
                return string.Empty;
            }
            ModLinkAssignment row = _modListData.modLinks.FirstOrDefault(delegate(ModLinkAssignment n)
            {
                return string.Equals(n.modFolderName, modFolderName, StringComparison.OrdinalIgnoreCase);
            });
            return row == null ? string.Empty : (row.url ?? string.Empty);
        }

        private string GetManualTitle(string modFolderName)
        {
            if (_modListData == null || _modListData.modIdentityOverrides == null || string.IsNullOrWhiteSpace(modFolderName))
            {
                return string.Empty;
            }

            ModIdentityOverride row = _modListData.modIdentityOverrides.FirstOrDefault(delegate(ModIdentityOverride n)
            {
                return string.Equals(n.modFolderName, modFolderName, StringComparison.OrdinalIgnoreCase);
            });
            return row == null ? string.Empty : (row.title ?? string.Empty);
        }

        private string GetManualAuthor(string modFolderName)
        {
            if (_modListData == null || _modListData.modIdentityOverrides == null || string.IsNullOrWhiteSpace(modFolderName))
            {
                return string.Empty;
            }

            ModIdentityOverride row = _modListData.modIdentityOverrides.FirstOrDefault(delegate(ModIdentityOverride n)
            {
                return string.Equals(n.modFolderName, modFolderName, StringComparison.OrdinalIgnoreCase);
            });
            return row == null ? string.Empty : (row.author ?? string.Empty);
        }

        private void SetManualModUrl(string modFolderName, string url)
        {
            if (_modListData == null)
            {
                return;
            }
            if (_modListData.modLinks == null)
            {
                _modListData.modLinks = new List<ModLinkAssignment>();
            }

            ModLinkAssignment row = _modListData.modLinks.FirstOrDefault(delegate(ModLinkAssignment n)
            {
                return string.Equals(n.modFolderName, modFolderName, StringComparison.OrdinalIgnoreCase);
            });

            if (row == null)
            {
                row = new ModLinkAssignment
                {
                    modFolderName = modFolderName,
                    url = url ?? string.Empty
                };
                _modListData.modLinks.Add(row);
                return;
            }

            row.url = url ?? string.Empty;
        }

        private void SetManualTitle(string modFolderName, string title)
        {
            if (_modListData == null || string.IsNullOrWhiteSpace(modFolderName))
            {
                return;
            }
            if (_modListData.modIdentityOverrides == null)
            {
                _modListData.modIdentityOverrides = new List<ModIdentityOverride>();
            }

            ModIdentityOverride row = _modListData.modIdentityOverrides.FirstOrDefault(delegate(ModIdentityOverride n)
            {
                return string.Equals(n.modFolderName, modFolderName, StringComparison.OrdinalIgnoreCase);
            });

            if (row == null)
            {
                row = new ModIdentityOverride
                {
                    modFolderName = modFolderName,
                    title = title ?? string.Empty,
                    author = string.Empty
                };
                _modListData.modIdentityOverrides.Add(row);
            }
            else
            {
                row.title = title ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(row.title) && string.IsNullOrWhiteSpace(row.author))
            {
                _modListData.modIdentityOverrides.Remove(row);
            }
        }

        private void SetManualAuthor(string modFolderName, string author)
        {
            if (_modListData == null || string.IsNullOrWhiteSpace(modFolderName))
            {
                return;
            }
            if (_modListData.modIdentityOverrides == null)
            {
                _modListData.modIdentityOverrides = new List<ModIdentityOverride>();
            }

            ModIdentityOverride row = _modListData.modIdentityOverrides.FirstOrDefault(delegate(ModIdentityOverride n)
            {
                return string.Equals(n.modFolderName, modFolderName, StringComparison.OrdinalIgnoreCase);
            });

            if (row == null)
            {
                row = new ModIdentityOverride
                {
                    modFolderName = modFolderName,
                    title = string.Empty,
                    author = author ?? string.Empty
                };
                _modListData.modIdentityOverrides.Add(row);
            }
            else
            {
                row.author = author ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(row.title) && string.IsNullOrWhiteSpace(row.author))
            {
                _modListData.modIdentityOverrides.Remove(row);
            }
        }

        private void SetManualDescription(string modFolderName, string description)
        {
            if (_modListData == null)
            {
                return;
            }
            if (_modListData.modNotes == null)
            {
                _modListData.modNotes = new List<ModNote>();
            }

            ModNote note = _modListData.modNotes.FirstOrDefault(delegate(ModNote n)
            {
                return string.Equals(n.modFolderName, modFolderName, StringComparison.OrdinalIgnoreCase);
            });

            if (note == null)
            {
                note = new ModNote
                {
                    modFolderName = modFolderName,
                    description = description ?? string.Empty
                };
                _modListData.modNotes.Add(note);
                return;
            }

            note.description = description ?? string.Empty;
        }

        private void LogInfo(string message)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;
            _uiLogLines.Add(line);
            while (_uiLogLines.Count > 9)
            {
                _uiLogLines.RemoveAt(0);
            }

            try
            {
                File.AppendAllText(_currentLogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }

            if (_status != null)
            {
                RenderStatusPanel();
            }
        }

        private void RenderStatusPanel()
        {
            if (_status == null)
            {
                return;
            }

            string subnauticaLine;
            if (_subnauticaDetected)
            {
                subnauticaLine = "Subnautica 2: FOUND (" + _subnauticaWin64Path + ")";
            }
            else
            {
                subnauticaLine = "Subnautica 2: NOT FOUND";
            }

            string logFileLine = "Log File: " + _currentLogPath;
            string recent = string.Join(Environment.NewLine, _uiLogLines.ToArray());

            _status.Text =
                subnauticaLine + Environment.NewLine +
                logFileLine + Environment.NewLine +
                "---- Recent Log ----" + Environment.NewLine +
                recent;
        }
    }

    public class ModInfo
    {
        public string FolderPath;
        public string Title;
        public string Author;
        public string LocalVersion;
        public string NexusVersion;
        public string DisplayVersion;
        public string Description;
        public string Category;
        public string IconPath;
        public string NexusModId;
        public string NexusGameDomain;
        public string NexusUrl;
    }

    public class AppConfig
    {
        public string subnauticaExePath;
        public string subnauticaWin64Path;
        public string nexusApiKey;
        public string nexusGameDomain;
        public int version;
    }

    public class ModListData
    {
        public List<ManagedInstallRecord> installedMods;
        public List<ModNote> modNotes;
        public List<ModCategoryAssignment> modCategories;
        public List<ModLinkAssignment> modLinks;
        public List<ModIdentityOverride> modIdentityOverrides;
        public List<PinnedCategoryAssignment> pinnedCategories;
        public List<PinnedModAssignment> pinnedMods;
    }

    public class LegacyConfig
    {
        public List<ManagedInstallRecord> installedMods;
        public List<ModNote> modNotes;
        public List<ModCategoryAssignment> modCategories;
        public List<ModLinkAssignment> modLinks;
        public List<PinnedCategoryAssignment> pinnedCategories;
        public List<PinnedModAssignment> pinnedMods;
    }

    public class ModCategoryAssignment
    {
        public string modFolderName;
        public string category;
    }

    public class ModLinkAssignment
    {
        public string modFolderName;
        public string url;
    }

    public class ModIdentityOverride
    {
        public string modFolderName;
        public string title;
        public string author;
    }

    public class PinnedCategoryAssignment
    {
        public string category;
        public bool pinned;
    }

    public class PinnedModAssignment
    {
        public string modFolderName;
        public bool pinned;
    }

    public class ManagedInstallRecord
    {
        public string modFolderName;
        public string installedAtUtc;
        public List<string> deployedPaths;
    }

    public class NexusModData
    {
        public string name;
        public string author;
        public string version;
        public string summary;
        public string description;
        public string picture_url;
    }

    public class NexusModFile
    {
        public string file_id;
        public string name;
        public string file_name;
        public string category_name;
        public string uploaded_timestamp;
    }

    public class ModNote
    {
        public string modFolderName;
        public string description;
    }
}
