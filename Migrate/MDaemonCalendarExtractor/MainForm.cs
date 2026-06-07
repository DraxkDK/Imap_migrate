using System.Diagnostics;
using System.Text.Json;
using MDaemonCalendarExtractor.Models;

namespace MDaemonCalendarExtractor;

public class MainForm : Form
{
    // ── controls ────────────────────────────────────────────────────────────
    private TextBox txtSourceFolder = null!;
    private TextBox txtMappingCsv   = null!;
    private TextBox txtOutputFolder = null!;
    private TextBox txtReportFolder = null!;
    private Button  btnBrowseSource  = null!;
    private Button  btnBrowseMapping = null!;
    private Button  btnBrowseOutput  = null!;
    private Button  btnBrowseReport  = null!;
    private Button  btnScan             = null!;
    private Button  btnExportSelected   = null!;
    private Button  btnExportAllMapped  = null!;
    private Button  btnStop             = null!;
    private Button  btnOpenOutput       = null!;
    private Button  btnOpenReport       = null!;
    private DataGridView dgvUsers = null!;
    private ProgressBar  progressBar  = null!;
    private Label        lblProgress  = null!;
    private TextBox      txtLog       = null!;

    // ── state ────────────────────────────────────────────────────────────────
    private List<UserCalendarItem> userItems = new();
    private CancellationTokenSource? cts;

    private readonly CalendarScanner   scanner    = new();
    private readonly CalendarExtractor extractor  = new();
    private readonly IcsBuilder        icsBuilder = new();
    private readonly ReportWriter      reportWriter = new();
    private readonly CsvUserMapLoader  csvLoader  = new();

    private static readonly string SettingsFile =
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    // ── ctor ─────────────────────────────────────────────────────────────────
    public MainForm()
    {
        BuildUi();
        LoadSettings();
        Text = "MDaemon Calendar Extractor GUI";

        // ensure code-page encodings (Windows-1252 etc.) are available on .NET Core
        System.Text.Encoding.RegisterProvider(
            System.Text.CodePagesEncodingProvider.Instance);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // UI BUILDER
    // ══════════════════════════════════════════════════════════════════════════

    private void BuildUi()
    {
        Size            = new System.Drawing.Size(1280, 860);
        MinimumSize     = new System.Drawing.Size(960, 640);
        StartPosition   = FormStartPosition.CenterScreen;
        Font            = new System.Drawing.Font("Segoe UI", 9f);

        var root = new TableLayoutPanel
        {
            Dock       = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 4,
            Padding     = new Padding(6)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 138)); // paths
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,  52)); // buttons
        root.RowStyles.Add(new RowStyle(SizeType.Percent,   60)); // grid
        root.RowStyles.Add(new RowStyle(SizeType.Percent,   40)); // log

        root.Controls.Add(BuildPathPanel(),   0, 0);
        root.Controls.Add(BuildButtonPanel(), 0, 1);
        root.Controls.Add(BuildGrid(),        0, 2);
        root.Controls.Add(BuildBottomPanel(), 0, 3);

        Controls.Add(root);
    }

    private GroupBox BuildPathPanel()
    {
        var grp = new GroupBox { Text = "Paths", Dock = DockStyle.Fill };

        var tbl = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 3,
            RowCount    = 4,
            Padding     = new Padding(4, 2, 4, 2)
        };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));

        AddPathRow(tbl, 0, "Source Folder:",    out txtSourceFolder,  out btnBrowseSource,
                   () => BrowseFolder(txtSourceFolder));
        AddPathRow(tbl, 1, "Mapping CSV:",      out txtMappingCsv,    out btnBrowseMapping,
                   () => BrowseFile(txtMappingCsv, "CSV files|*.csv|All files|*.*"));
        AddPathRow(tbl, 2, "Output ICS Folder:", out txtOutputFolder, out btnBrowseOutput,
                   () => BrowseFolder(txtOutputFolder));
        AddPathRow(tbl, 3, "Report Folder:",    out txtReportFolder,  out btnBrowseReport,
                   () => BrowseFolder(txtReportFolder));

        grp.Controls.Add(tbl);
        return grp;
    }

    private static void AddPathRow(TableLayoutPanel tbl, int row, string label,
        out TextBox txt, out Button btn, Action browse)
    {
        var lbl = new Label
        {
            Text      = label,
            Anchor    = AnchorStyles.Right,
            TextAlign = System.Drawing.ContentAlignment.MiddleRight
        };
        txt = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(2) };
        btn = new Button  { Text = "Browse…",      Dock = DockStyle.Fill, Margin = new Padding(2) };
        btn.Click += (_, _) => browse();

        tbl.Controls.Add(lbl, 0, row);
        tbl.Controls.Add(txt, 1, row);
        tbl.Controls.Add(btn, 2, row);
    }

    private Panel BuildButtonPanel()
    {
        var flow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding       = new Padding(0, 6, 0, 0)
        };

        btnScan            = MakeBtn("Scan",              100);
        btnExportSelected  = MakeBtn("Export Selected",   130);
        btnExportAllMapped = MakeBtn("Export All Mapped", 145);
        btnStop            = MakeBtn("Stop",               80);
        btnOpenOutput      = MakeBtn("Open Output Folder", 145);
        btnOpenReport      = MakeBtn("Open Report Folder", 145);

        btnStop.Enabled = false;

        btnScan.Click            += BtnScan_Click;
        btnExportSelected.Click  += BtnExportSelected_Click;
        btnExportAllMapped.Click += BtnExportAllMapped_Click;
        btnStop.Click            += (_, _) => cts?.Cancel();
        btnOpenOutput.Click      += (_, _) => OpenFolder(txtOutputFolder.Text);
        btnOpenReport.Click      += (_, _) => OpenFolder(txtReportFolder.Text);

        flow.Controls.AddRange(new Control[]
        {
            btnScan, btnExportSelected, btnExportAllMapped,
            btnStop, btnOpenOutput, btnOpenReport
        });
        return flow;
    }

    private static Button MakeBtn(string text, int width) =>
        new() { Text = text, Width = width, Height = 34, Margin = new Padding(0, 0, 6, 0) };

    private DataGridView BuildGrid()
    {
        dgvUsers = new DataGridView
        {
            Dock             = DockStyle.Fill,
            AllowUserToAddRows    = false,
            AllowUserToDeleteRows = false,
            ReadOnly         = false,
            AutoGenerateColumns = false,
            SelectionMode    = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            ScrollBars       = ScrollBars.Both,
            AlternatingRowsDefaultCellStyle =
            {
                BackColor = System.Drawing.Color.FromArgb(245, 248, 255)
            }
        };

        dgvUsers.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewCheckBoxColumn
            {
                Name = "colSelected", HeaderText = "✓",
                DataPropertyName = "Selected", Width = 40, ReadOnly = false
            },
            Col("colSourceEmail",       "SourceEmail",    220),
            Col("colTargetMailbox",     "TargetMailbox",  220),
            Col("colMappingStatus",     "Mapping",         80),
            ColNum("colFileCount",      "Files",           55),
            ColNum("colEstimatedSizeMB","MB",              55),
            Col("colStatus",            "Status",          90),
            ColNum("colEventCount",     "Events",          60),
            Col("colCalendarPath",      "CalendarPath",   260),
            Col("colOutputIcs",         "OutputIcs",      220),
            Col("colMessage",           "Message",        240),
        });

        dgvUsers.CellFormatting  += DgvUsers_CellFormatting;
        dgvUsers.CellContentClick += (_, e) =>
        {
            if (dgvUsers.Columns[e.ColumnIndex] is DataGridViewCheckBoxColumn && e.RowIndex >= 0)
                dgvUsers.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        return dgvUsers;
    }

    private static DataGridViewTextBoxColumn Col(string name, string header, int width) =>
        new() { Name = name, HeaderText = header, DataPropertyName = name[3..], Width = width, ReadOnly = true };

    private static DataGridViewTextBoxColumn ColNum(string name, string header, int width) =>
        new()
        {
            Name = name, HeaderText = header,
            DataPropertyName = name[3..], Width = width, ReadOnly = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight }
        };

    private Panel BuildBottomPanel()
    {
        var tbl = new TableLayoutPanel
        {
            Dock       = DockStyle.Fill,
            RowCount   = 3,
            ColumnCount = 1
        };
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        progressBar = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
        lblProgress = new Label      { Dock = DockStyle.Fill, Text = "Ready." };
        txtLog = new TextBox
        {
            Dock      = DockStyle.Fill,
            Multiline = true,
            ReadOnly  = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = System.Drawing.Color.FromArgb(15, 15, 30),
            ForeColor = System.Drawing.Color.Lime,
            Font      = new System.Drawing.Font("Consolas", 9f)
        };

        tbl.Controls.Add(progressBar, 0, 0);
        tbl.Controls.Add(lblProgress, 0, 1);
        tbl.Controls.Add(txtLog,      0, 2);
        return tbl;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // BUTTON HANDLERS
    // ══════════════════════════════════════════════════════════════════════════

    private async void BtnScan_Click(object? sender, EventArgs e)
    {
        if (!ValidateScanPaths()) return;
        SetBusy(true);
        Log("Loading user mapping CSV…");

        try
        {
            var userMap = await Task.Run(() => csvLoader.Load(txtMappingCsv.Text));
            Log($"Loaded {userMap.Count} mapping(s) from CSV.");

            Log($"Scanning {txtSourceFolder.Text} …");
            userItems = await Task.Run(() => scanner.Scan(txtSourceFolder.Text, userMap));

            BindGrid();

            var mapped   = userItems.Count(u => u.MappingStatus == "Mapped");
            var unmapped = userItems.Count - mapped;
            Log($"Found {userItems.Count} Calendar.IMAP folder(s) — {mapped} mapped, {unmapped} unmapped.");
            SetProgress(0, 0, "Scan complete.");
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            MessageBox.Show($"Scan error:\n{ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            SaveSettings();
        }
    }

    private async void BtnExportSelected_Click(object? sender, EventArgs e)
    {
        // Commit any un-committed checkbox edits
        dgvUsers.EndEdit();

        var selected = userItems.Where(u => u.Selected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("No users selected.", "Info",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        await RunExport(selected);
    }

    private async void BtnExportAllMapped_Click(object? sender, EventArgs e)
    {
        var mapped = userItems.Where(u => u.MappingStatus == "Mapped").ToList();
        if (mapped.Count == 0)
        {
            MessageBox.Show("No mapped users found. Run Scan first.", "Info",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        await RunExport(mapped);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // EXPORT CORE
    // ══════════════════════════════════════════════════════════════════════════

    private async Task RunExport(List<UserCalendarItem> items)
    {
        if (!ValidateExportPaths()) return;

        Directory.CreateDirectory(txtOutputFolder.Text);
        Directory.CreateDirectory(txtReportFolder.Text);

        SetBusy(true);
        using var tokenSource = new CancellationTokenSource();
        cts = tokenSource;
        var token = tokenSource.Token;

        var reportRows = new List<ReportRow>();
        int total   = items.Count;
        int current = 0;

        Log($"Starting export for {total} user(s)…");

        try
        {
            foreach (var item in items)
            {
                if (token.IsCancellationRequested)
                {
                    Log("Export cancelled by user.");
                    break;
                }

                current++;
                SetProgress(current, total, $"Processing {item.SourceEmail}");
                Log($"[{current}/{total}] {item.SourceEmail}");

                if (item.MappingStatus != "Mapped")
                {
                    item.Status  = "Skipped";
                    item.Message = "No mapping found";
                    Log($"  → Skipped (no mapping)");
                    reportRows.Add(MakeRow(item, 0, 0, 0));
                    RefreshRow(item);
                    continue;
                }

                try
                {
                    var res = await Task.Run(() => extractor.Extract(item.CalendarPath, token), token);

                    if (res.Events.Count == 0)
                    {
                        item.Status     = "NoData";
                        item.EventCount = 0;
                        item.Message    = "No BEGIN:VEVENT found";
                        Log($"  → NoData ({res.FilesScanned} file(s) scanned)");
                        reportRows.Add(MakeRow(item, 0, res.FilesScanned, res.FilesWithCalendarBlocks));
                    }
                    else
                    {
                        var icsContent = icsBuilder.Build(res.Events, res.Timezones);

                        var outPath = ResolveOutputPath(txtOutputFolder.Text, item.SourceEmail);
                        icsBuilder.WriteIcs(outPath, icsContent);

                        item.Status     = "Exported";
                        item.EventCount = res.Events.Count;
                        item.OutputIcs  = outPath;
                        item.Message    = $"{res.Events.Count} event(s) from {res.FilesScanned} file(s)";
                        Log($"  → Exported {res.Events.Count} event(s) → {outPath}");
                        reportRows.Add(MakeRow(item, res.Events.Count, res.FilesScanned, res.FilesWithCalendarBlocks));
                    }
                }
                catch (OperationCanceledException)
                {
                    item.Status  = "Failed";
                    item.Message = "Cancelled";
                    break;
                }
                catch (Exception ex)
                {
                    item.Status  = "Failed";
                    item.Message = ex.Message;
                    Log($"  → FAILED: {ex.Message}");
                    reportRows.Add(MakeRow(item, 0, 0, 0));
                }

                RefreshRow(item);
            }

            if (reportRows.Count > 0)
            {
                var reportPath = reportWriter.WriteReport(txtReportFolder.Text, reportRows);
                Log($"Report: {reportPath}");
            }

            SetProgress(total, total, "Export complete.");
            Log($"Done — {current} user(s) processed.");
        }
        catch (Exception ex)
        {
            Log($"Unexpected error: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            cts = null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ══════════════════════════════════════════════════════════════════════════

    private string ResolveOutputPath(string folder, string sourceEmail)
    {
        var path = icsBuilder.GetOutputPath(folder, sourceEmail, 0);
        int idx  = 1;
        while (File.Exists(path))
            path = icsBuilder.GetOutputPath(folder, sourceEmail, idx++);
        return path;
    }

    private static ReportRow MakeRow(UserCalendarItem item, int events, int scanned, int withBlocks) =>
        new()
        {
            SourceEmail            = item.SourceEmail,
            TargetMailbox          = item.TargetMailbox,
            CalendarPath           = item.CalendarPath,
            OutputIcs              = item.OutputIcs,
            EventCount             = events,
            FilesScanned           = scanned,
            FilesWithCalendarBlocks = withBlocks,
            Status                 = item.Status,
            Message                = item.Message
        };

    private void BindGrid()
    {
        dgvUsers.DataSource = null;
        dgvUsers.DataSource = userItems;
    }

    private void RefreshRow(UserCalendarItem item)
    {
        var idx = userItems.IndexOf(item);
        if (idx >= 0 && idx < dgvUsers.Rows.Count)
            dgvUsers.InvalidateRow(idx);
    }

    private void SetBusy(bool busy)
    {
        btnScan.Enabled            = !busy;
        btnExportSelected.Enabled  = !busy;
        btnExportAllMapped.Enabled = !busy;
        btnStop.Enabled            = busy;
        Cursor                     = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void SetProgress(int current, int total, string message)
    {
        progressBar.Maximum = Math.Max(total, 1);
        progressBar.Value   = Math.Min(current, progressBar.Maximum);
        lblProgress.Text    = total > 0
            ? $"Processing {current}/{total} users.  {message}"
            : message;
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        txtLog.AppendText(line);
    }

    // ── folder / file dialogs ───────────────────────────────────────────────

    private void BrowseFolder(TextBox target)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description      = "Select folder",
            ShowNewFolderButton = true
        };
        if (!string.IsNullOrEmpty(target.Text) && Directory.Exists(target.Text))
            dlg.SelectedPath = target.Text;

        if (dlg.ShowDialog(this) == DialogResult.OK)
            target.Text = dlg.SelectedPath;
    }

    private void BrowseFile(TextBox target, string filter)
    {
        using var dlg = new OpenFileDialog { Filter = filter };
        if (!string.IsNullOrEmpty(target.Text))
            dlg.InitialDirectory = Path.GetDirectoryName(target.Text) ?? "";

        if (dlg.ShowDialog(this) == DialogResult.OK)
            target.Text = dlg.FileName;
    }

    private static void OpenFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            MessageBox.Show("Folder does not exist.", "Warning",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Process.Start("explorer.exe", path);
    }

    // ── validation ──────────────────────────────────────────────────────────

    private bool ValidateScanPaths()
    {
        if (string.IsNullOrWhiteSpace(txtSourceFolder.Text) || !Directory.Exists(txtSourceFolder.Text))
            return Warn("Source Folder is missing or does not exist.");
        if (string.IsNullOrWhiteSpace(txtMappingCsv.Text) || !File.Exists(txtMappingCsv.Text))
            return Warn("Mapping CSV is missing or does not exist.");
        return true;
    }

    private bool ValidateExportPaths()
    {
        if (string.IsNullOrWhiteSpace(txtOutputFolder.Text))
            return Warn("Please set an Output ICS Folder.");
        if (string.IsNullOrWhiteSpace(txtReportFolder.Text))
            return Warn("Please set a Report Folder.");
        if (userItems.Count == 0)
            return Warn("No data found. Please run Scan first.");
        return true;
    }

    private static bool Warn(string msg)
    {
        MessageBox.Show(msg, "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    // ── cell formatting ──────────────────────────────────────────────────────

    private void DgvUsers_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= userItems.Count) return;
        var status = userItems[e.RowIndex].Status;

        var bg = status switch
        {
            "Exported" => System.Drawing.Color.FromArgb(180, 240, 180),
            "Failed"   => System.Drawing.Color.FromArgb(255, 180, 180),
            "NoData"   => System.Drawing.Color.FromArgb(255, 255, 190),
            "Skipped"  => System.Drawing.Color.FromArgb(220, 220, 220),
            _          => System.Drawing.Color.Empty
        };

        if (bg != System.Drawing.Color.Empty && e.CellStyle != null)
        {
            e.CellStyle.BackColor          = bg;
            e.CellStyle.SelectionBackColor = bg;
        }
    }

    // ── settings ─────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            var json = File.ReadAllText(SettingsFile);
            var s    = JsonSerializer.Deserialize<AppSettings>(json);
            if (s == null) return;
            txtSourceFolder.Text = s.LastSourceFolder;
            txtMappingCsv.Text   = s.LastMappingCsv;
            txtOutputFolder.Text = s.LastOutputFolder;
            txtReportFolder.Text = s.LastReportFolder;
        }
        catch { /* ignore corrupt settings */ }
    }

    private void SaveSettings()
    {
        try
        {
            var s = new AppSettings
            {
                LastSourceFolder = txtSourceFolder.Text,
                LastMappingCsv   = txtMappingCsv.Text,
                LastOutputFolder = txtOutputFolder.Text,
                LastReportFolder = txtReportFolder.Text
            };
            File.WriteAllText(SettingsFile,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        cts?.Cancel();
        SaveSettings();
        base.OnFormClosing(e);
    }

    // ── settings model ────────────────────────────────────────────────────────

    private sealed class AppSettings
    {
        public string LastSourceFolder { get; set; } = "";
        public string LastMappingCsv   { get; set; } = "";
        public string LastOutputFolder { get; set; } = "";
        public string LastReportFolder { get; set; } = "";
    }
}
