using System.Diagnostics;
using System.Text.Json;
using MDaemonCalendarMigration.Models;
using MDaemonCalendarMigration.Security;
using MDaemonCalendarMigration.Services;

namespace MDaemonCalendarMigration;

public class MainForm : Form
{
    // ── services ─────────────────────────────────────────────────────────────
    private readonly CalendarScanner    scanner   = new();
    private readonly CalendarExtractor  extractor = new();
    private readonly IcsBuilder         icsBuilder = new();
    private readonly CsvUserMapLoader   csvLoader  = new();
    private readonly ReportWriter       reporter   = new();
    private readonly IcsEventParser     evParser   = new();
    private readonly GraphEventConverter evConvert = new();
    private readonly GraphApiClient     graph      = new();

    // ── shared state ─────────────────────────────────────────────────────────
    private List<UserCalendarItem> userItems   = new();
    private List<ImportItem>       importItems = new();
    private List<ReportRow>        lastReport  = new();
    private CancellationTokenSource? cts;
    private AppSettings cfg = new();

    private static readonly string SettingsFile =
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    // ── Tab 1 controls ────────────────────────────────────────────────────────
    private TextBox txtSrcFolder   = null!;
    private TextBox txtMapCsv      = null!;
    private TextBox txtOutFolder   = null!;
    private TextBox txtRepFolder   = null!;
    private Button  btnScan        = null!;
    private Button  btnExpSel      = null!;
    private Button  btnExpAll      = null!;
    private Button  btnStop1       = null!;
    private DataGridView dgvUsers  = null!;

    // ── Tab 2 controls ────────────────────────────────────────────────────────
    private TextBox txtTenantId    = null!;
    private TextBox txtClientId    = null!;
    private TextBox txtClientSec   = null!;
    private ComboBox cmbTimeZone   = null!;
    private TextBox txtIcsFolder   = null!;
    private TextBox txtMapCsv2     = null!;
    private Button  btnTestConn    = null!;
    private Button  btnLoadIcs     = null!;
    private Button  btnImpSel      = null!;
    private Button  btnImpAll      = null!;
    private Button  btnStop2       = null!;
    private DataGridView dgvImport = null!;

    // ── Tab 3 controls ────────────────────────────────────────────────────────
    private Label        lblStats  = null!;
    private DataGridView dgvRep    = null!;

    // ── shared bottom ─────────────────────────────────────────────────────────
    private TabControl  tabs        = null!;
    private ProgressBar progressBar = null!;
    private Label       lblProgress = null!;
    private TextBox     txtLog      = null!;

    // ═════════════════════════════════════════════════════════════════════════
    // CTOR
    // ═════════════════════════════════════════════════════════════════════════

    public MainForm()
    {
        BuildUi();
        LoadSettings();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UI BUILDER
    // ═════════════════════════════════════════════════════════════════════════

    private void BuildUi()
    {
        Text          = "MDaemon Calendar Migration Tool";
        Size          = new System.Drawing.Size(1300, 900);
        MinimumSize   = new System.Drawing.Size(1000, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font          = new System.Drawing.Font("Segoe UI", 9f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Padding = new Padding(6)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // tabs
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));  // progress bar
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));  // progress label
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140)); // log

        tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildExtractTab());
        tabs.TabPages.Add(BuildImportTab());
        tabs.TabPages.Add(BuildReportTab());
        tabs.SelectedIndexChanged += Tabs_SelectedIndexChanged;

        progressBar = new ProgressBar { Dock = DockStyle.Fill };
        lblProgress = new Label       { Dock = DockStyle.Fill, Text = "Ready." };
        txtLog = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor  = System.Drawing.Color.FromArgb(15, 15, 30),
            ForeColor  = System.Drawing.Color.Lime,
            Font       = new System.Drawing.Font("Consolas", 9f)
        };

        root.Controls.Add(tabs,        0, 0);
        root.Controls.Add(progressBar, 0, 1);
        root.Controls.Add(lblProgress, 0, 2);
        root.Controls.Add(txtLog,      0, 3);
        Controls.Add(root);
    }

    // ── Tab 1: Extract ICS ───────────────────────────────────────────────────

    private TabPage BuildExtractTab()
    {
        var page = new TabPage("📁  Extract ICS");
        var tbl = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(4)
        };
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute,  52));
        tbl.RowStyles.Add(new RowStyle(SizeType.Percent,  100));

        // paths
        var grp = new GroupBox { Text = "Source / Output Paths", Dock = DockStyle.Fill };
        var pt  = PathTable(4);
        AddPathRow(pt, 0, "MDaemon Source Folder:", out txtSrcFolder,
            () => BrowseFolder(txtSrcFolder));
        AddPathRow(pt, 1, "User Mapping CSV:",       out txtMapCsv,
            () => BrowseFile(txtMapCsv, "CSV|*.csv|All|*.*"));
        AddPathRow(pt, 2, "Output ICS Folder:",      out txtOutFolder,
            () => BrowseFolder(txtOutFolder));
        AddPathRow(pt, 3, "Report Folder:",          out txtRepFolder,
            () => BrowseFolder(txtRepFolder));
        grp.Controls.Add(pt);

        // buttons
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0)
        };
        btnScan   = Btn("🔍 Scan",            100, BtnScan_Click);
        btnExpSel = Btn("⬇ Export Selected",  140, BtnExpSel_Click);
        btnExpAll = Btn("⬇ Export All Mapped", 150, BtnExpAll_Click);
        btnStop1  = Btn("⏹ Stop",              80,  (_, _) => cts?.Cancel());
        var btnOpenOut = Btn("📂 Open Output", 120, (_, _) => OpenFolder(txtOutFolder.Text));
        var btnOpenRep = Btn("📂 Open Report", 120, (_, _) => OpenFolder(txtRepFolder.Text));
        btnStop1.Enabled = false;
        flow.Controls.AddRange(new Control[] { btnScan, btnExpSel, btnExpAll, btnStop1, btnOpenOut, btnOpenRep });

        // grid
        dgvUsers = BuildGrid(new[]
        {
            CheckCol("colSelected", "✓", 40),
            TxtCol("colSourceEmail",    "SourceEmail",    200),
            TxtCol("colTargetMailbox",  "TargetMailbox",  200),
            TxtCol("colMappingStatus",  "Mapping",         75),
            NumCol("colFileCount",      "Files",           55),
            NumCol("colEstimatedSizeMB","MB",              55),
            TxtCol("colStatus",         "Status",          85),
            NumCol("colEventCount",     "Events",          60),
            TxtCol("colCalendarPath",   "CalendarPath",   260),
            TxtCol("colOutputIcs",      "OutputIcs",      220),
            TxtCol("colMessage",        "Message",        260)
        });
        dgvUsers.CellFormatting += (s, e) => ColorRow(e, userItems);

        tbl.Controls.Add(grp,     0, 0);
        tbl.Controls.Add(flow,    0, 1);
        tbl.Controls.Add(dgvUsers,0, 2);
        page.Controls.Add(tbl);
        return page;
    }

    // ── Tab 2: Import to Microsoft 365 ───────────────────────────────────────

    private TabPage BuildImportTab()
    {
        var page = new TabPage("☁  Import to Microsoft 365");
        var tbl = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(4)
        };
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute,  52));
        tbl.RowStyles.Add(new RowStyle(SizeType.Percent,  100));

        // Graph settings group
        var grp = new GroupBox { Text = "Microsoft Graph Settings (App-only · no Microsoft.Graph module required)", Dock = DockStyle.Fill };
        var gt  = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 6,
            Padding = new Padding(4, 2, 4, 2)
        };
        gt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        gt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  100));
        gt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        gt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));

        AddLabelTxt(gt, 0, "Tenant ID:",      out txtTenantId);
        AddLabelTxt(gt, 1, "Client ID:",      out txtClientId);

        // Secret row with extra buttons
        gt.Controls.Add(MkLabel("Client Secret:"), 0, 2);
        txtClientSec = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Margin = new Padding(2) };
        gt.Controls.Add(txtClientSec, 1, 2);
        var btnShow = new Button { Text = "👁 Show", Dock = DockStyle.Fill, Margin = new Padding(2) };
        btnShow.Click += (_, _) => txtClientSec.UseSystemPasswordChar = !txtClientSec.UseSystemPasswordChar;
        gt.Controls.Add(btnShow, 2, 2);
        var btnSaveSec = new Button { Text = "🔒 Save", Dock = DockStyle.Fill, Margin = new Padding(2) };
        btnSaveSec.Click += BtnSaveSecret_Click;
        gt.Controls.Add(btnSaveSec, 3, 2);

        // Time Zone — ComboBox populated from Windows system time zones
        gt.Controls.Add(MkLabel("Time Zone:"), 0, 3);
        cmbTimeZone = new ComboBox
        {
            Dock = DockStyle.Fill, Margin = new Padding(2),
            DropDownStyle = ComboBoxStyle.DropDown,   // allow typing custom value
            AutoCompleteMode   = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };
        foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
            cmbTimeZone.Items.Add(tz.Id);
        cmbTimeZone.Text = "SE Asia Standard Time";
        gt.SetColumnSpan(cmbTimeZone, 3);
        gt.Controls.Add(cmbTimeZone, 1, 3);

        // ICS folder row
        gt.Controls.Add(MkLabel("ICS Source Folder:"), 0, 4);
        txtIcsFolder = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(2) };
        gt.Controls.Add(txtIcsFolder, 1, 4);
        var btnBrowseIcs = new Button { Text = "Browse…", Dock = DockStyle.Fill, Margin = new Padding(2) };
        btnBrowseIcs.Click += (_, _) => BrowseFolder(txtIcsFolder);
        gt.Controls.Add(btnBrowseIcs, 2, 4);

        // Mapping CSV row
        gt.Controls.Add(MkLabel("User Mapping CSV:"), 0, 5);
        txtMapCsv2 = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(2) };
        gt.Controls.Add(txtMapCsv2, 1, 5);
        var btnBrowseMap2 = new Button { Text = "Browse…", Dock = DockStyle.Fill, Margin = new Padding(2) };
        btnBrowseMap2.Click += (_, _) => BrowseFile(txtMapCsv2, "CSV|*.csv|All|*.*");
        gt.Controls.Add(btnBrowseMap2, 2, 5);

        grp.Controls.Add(gt);

        // buttons
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0)
        };
        btnTestConn = Btn("🔗 Test Connection",   145, BtnTestConn_Click);
        btnLoadIcs  = Btn("🔄 Load ICS Files",     130, BtnLoadIcs_Click);
        btnImpSel   = Btn("☁ Import Selected",     135, BtnImpSel_Click);
        btnImpAll   = Btn("☁ Import All",           100, BtnImpAll_Click);
        btnStop2    = Btn("⏹ Stop",                  80, (_, _) => cts?.Cancel());
        btnStop2.Enabled = false;
        var btnOpenIcs = Btn("📂 Open ICS Folder", 130, (_, _) => OpenFolder(txtIcsFolder.Text));
        flow.Controls.AddRange(new Control[] { btnTestConn, btnLoadIcs, btnImpSel, btnImpAll, btnStop2, btnOpenIcs });

        // import grid
        dgvImport = BuildGrid(new[]
        {
            CheckCol("colSelected",    "✓",            40),
            TxtCol("colIcsFile",       "ICS File",     200),
            TxtCol("colSourceEmail",   "SourceEmail",  200),
            TxtCol("colTargetMailbox", "TargetMailbox",200),
            TxtCol("colMappingStatus", "Mapping",       75),
            NumCol("colTotalEvents",   "Total",         60),
            NumCol("colImported",      "Imported",      70),
            NumCol("colFailed",        "Failed",        60),
            TxtCol("colStatus",        "Status",        90),
            TxtCol("colMessage",       "Message",      300)
        });
        dgvImport.CellFormatting += (s, e) => ColorRow(e, importItems);

        tbl.Controls.Add(grp,      0, 0);
        tbl.Controls.Add(flow,     0, 1);
        tbl.Controls.Add(dgvImport,0, 2);
        page.Controls.Add(tbl);
        return page;
    }

    // ── Tab 3: Reports ────────────────────────────────────────────────────────

    private TabPage BuildReportTab()
    {
        var page = new TabPage("📊  Reports");
        var tbl = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(6)
        };
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        lblStats = new Label { Dock = DockStyle.Fill, Font = new System.Drawing.Font("Segoe UI", 10f) };

        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var btnOpenExt = Btn("Open Extract Report", 160, (_, _) =>
            OpenFile(Path.Combine(txtRepFolder.Text, "extract-report.csv")));
        var btnOpenImp = Btn("Open Import Report", 155, (_, _) =>
            OpenFile(Path.Combine(txtRepFolder.Text, "import-report.csv")));
        var btnRefresh = Btn("↻ Refresh", 90, (_, _) => RefreshReportTab());
        flow.Controls.AddRange(new Control[] { btnOpenExt, btnOpenImp, btnRefresh });

        dgvRep = BuildGrid(new[]
        {
            TxtCol("colSourceEmail",             "SourceEmail",   200),
            TxtCol("colTargetMailbox",           "TargetMailbox", 200),
            NumCol("colEventCount",              "Events",         70),
            NumCol("colFilesScanned",            "Scanned",        70),
            NumCol("colFilesWithCalendarBlocks", "WithBlocks",     80),
            TxtCol("colStatus",                  "Status",         90),
            TxtCol("colOutputIcs",               "OutputIcs",     250),
            TxtCol("colMessage",                 "Message",       250)
        });
        dgvRep.ReadOnly = true;

        tbl.Controls.Add(lblStats, 0, 0);
        tbl.Controls.Add(flow,     0, 1);
        tbl.Controls.Add(dgvRep,   0, 2);
        page.Controls.Add(tbl);
        return page;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // TAB 1: EXTRACT — EVENT HANDLERS
    // ═════════════════════════════════════════════════════════════════════════

    private async void BtnScan_Click(object? s, EventArgs e)
    {
        if (!Validate1()) return;
        SetBusy(true, 1);
        Log("Loading mapping CSV…");
        try
        {
            var map = await Task.Run(() => csvLoader.Load(txtMapCsv.Text));
            Log($"Mapping loaded: {map.Count} entries.");
            Log($"Scanning {txtSrcFolder.Text} …");
            userItems = await Task.Run(() => scanner.Scan(txtSrcFolder.Text, map));
            dgvUsers.DataSource = null;
            dgvUsers.DataSource = userItems;
            Log($"Found {userItems.Count} Calendar.IMAP folders — {userItems.Count(u => u.MappingStatus == "Mapped")} mapped.");
            SetProgress(0, 0, "Scan complete.");
        }
        catch (Exception ex) { LogErr(ex); }
        finally { SetBusy(false, 1); SaveSettings(); }
    }

    private async void BtnExpSel_Click(object? s, EventArgs e)
    {
        dgvUsers.EndEdit();
        var sel = userItems.Where(u => u.Selected).ToList();
        if (sel.Count == 0) { Info("No users selected."); return; }
        await RunExtract(sel);
    }

    private async void BtnExpAll_Click(object? s, EventArgs e)
    {
        var mapped = userItems.Where(u => u.MappingStatus == "Mapped").ToList();
        if (mapped.Count == 0) { Info("No mapped users found. Run Scan first."); return; }
        await RunExtract(mapped);
    }

    private async Task RunExtract(List<UserCalendarItem> items)
    {
        if (!Validate1()) return;
        Directory.CreateDirectory(txtOutFolder.Text);
        Directory.CreateDirectory(txtRepFolder.Text);

        SetBusy(true, 1);
        using var ts = new CancellationTokenSource(); cts = ts;
        var token = ts.Token;
        var rows  = new List<ReportRow>();
        int total = items.Count, cur = 0;
        Log($"Starting extract for {total} user(s)…");

        try
        {
            foreach (var item in items)
            {
                if (token.IsCancellationRequested) { Log("Cancelled."); break; }
                cur++;
                SetProgress(cur, total, item.SourceEmail);
                Log($"[{cur}/{total}] {item.SourceEmail}");

                if (item.MappingStatus != "Mapped")
                {
                    item.Status = "Skipped"; item.Message = "No mapping found";
                    rows.Add(ToRow(item, 0, 0, 0)); RefreshGrid(dgvUsers, item, userItems); continue;
                }
                try
                {
                    var res = await Task.Run(() => extractor.Extract(item.CalendarPath, token), token);
                    if (res.Events.Count == 0)
                    {
                        item.Status = "NoData"; item.EventCount = 0;
                        item.Message = "No BEGIN:VEVENT found";
                        Log($"  → NoData ({res.FilesScanned} files)");
                        rows.Add(ToRow(item, 0, res.FilesScanned, res.FilesWithCalendarBlocks));
                    }
                    else
                    {
                        var ics  = icsBuilder.Build(res.Events, res.Timezones);
                        var path = ResolveIcsPath(txtOutFolder.Text, item.SourceEmail);
                        icsBuilder.WriteIcs(path, ics);
                        item.Status = "Exported"; item.EventCount = res.Events.Count;
                        item.OutputIcs = path;
                        item.Message = $"{res.Events.Count} events from {res.FilesScanned} files";
                        Log($"  → Exported {res.Events.Count} events → {Path.GetFileName(path)}");
                        rows.Add(ToRow(item, res.Events.Count, res.FilesScanned, res.FilesWithCalendarBlocks));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    item.Status = "Failed"; item.Message = ex.Message;
                    Log($"  → FAILED: {ex.Message}");
                    rows.Add(ToRow(item, 0, 0, 0));
                }
                RefreshGrid(dgvUsers, item, userItems);
            }

            if (rows.Count > 0)
            {
                lastReport = rows;
                var rp = reporter.WriteExtractReport(txtRepFolder.Text, rows);
                Log($"Extract report: {rp}");
            }
            SetProgress(total, total, "Extract complete.");
            Log($"Done — {cur} user(s) processed.");
        }
        finally { SetBusy(false, 1); cts = null; }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // TAB 2: IMPORT — EVENT HANDLERS
    // ═════════════════════════════════════════════════════════════════════════

    private async void BtnTestConn_Click(object? s, EventArgs e)
    {
        if (!ValidateGraph()) return;
        SetBusy(true, 2);
        Log("Testing Graph API connection…");
        try
        {
            graph.InvalidateToken();
            await graph.TestConnectionAsync(txtTenantId.Text, txtClientId.Text, txtClientSec.Text);
            Log("✅ Graph connection OK — token acquired, users endpoint reachable.");
            Info("Graph API connection successful!");
        }
        catch (Exception ex) { LogErr(ex); MessageBox.Show(ex.Message, "Connection Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { SetBusy(false, 2); }
    }

    private async void BtnLoadIcs_Click(object? s, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(txtIcsFolder.Text) || !Directory.Exists(txtIcsFolder.Text))
        { Warn("ICS Folder does not exist."); return; }
        if (string.IsNullOrWhiteSpace(txtMapCsv2.Text) || !File.Exists(txtMapCsv2.Text))
        { Warn("User Mapping CSV does not exist."); return; }

        SetBusy(true, 2);
        Log("Loading ICS files…");
        try
        {
            var map = await Task.Run(() => csvLoader.Load(txtMapCsv2.Text));
            var files = Directory.GetFiles(txtIcsFolder.Text, "*.ics", SearchOption.TopDirectoryOnly);
            importItems = new List<ImportItem>();

            foreach (var f in files)
            {
                var fname = Path.GetFileNameWithoutExtension(f);
                // filename IS SourceEmail (may have Windows-invalid chars replaced with _)
                var email = fname.ToLowerInvariant();
                map.TryGetValue(email, out var target);

                // Count events quickly
                var content = File.ReadAllText(f, System.Text.Encoding.UTF8);
                int evCount = CountOccurrences(content, "BEGIN:VEVENT");

                importItems.Add(new ImportItem
                {
                    Selected      = target != null,
                    IcsFile       = f,
                    SourceEmail   = email,
                    TargetMailbox = target ?? "",
                    MappingStatus = target != null ? "Mapped" : "Unmapped",
                    TotalEvents   = evCount,
                    Status        = "Ready"
                });
            }

            dgvImport.DataSource = null;
            dgvImport.DataSource = importItems;
            Log($"Loaded {importItems.Count} ICS files — {importItems.Count(i => i.MappingStatus == "Mapped")} mapped.");
        }
        catch (Exception ex) { LogErr(ex); }
        finally { SetBusy(false, 2); }
    }

    private async void BtnImpSel_Click(object? s, EventArgs e)
    {
        dgvImport.EndEdit();
        var sel = importItems.Where(i => i.Selected).ToList();
        if (sel.Count == 0) { Info("No items selected."); return; }
        await RunImport(sel);
    }

    private async void BtnImpAll_Click(object? s, EventArgs e)
    {
        var mapped = importItems.Where(i => i.MappingStatus == "Mapped").ToList();
        if (mapped.Count == 0) { Info("No mapped ICS files. Run Load first."); return; }
        await RunImport(mapped);
    }

    private async Task RunImport(List<ImportItem> items)
    {
        if (!ValidateGraph()) return;
        Directory.CreateDirectory(txtRepFolder.Text);

        SetBusy(true, 2);
        using var ts = new CancellationTokenSource(); cts = ts;
        var token = ts.Token;
        var rows  = new List<ImportReportRow>();
        int total = items.Count, cur = 0;
        Log($"Starting M365 import for {total} ICS file(s)…");

        try
        {
            string? graphToken = null;
            try
            {
                graphToken = await graph.GetTokenAsync(
                    txtTenantId.Text, txtClientId.Text, txtClientSec.Text, token);
                Log("Token acquired.");
            }
            catch (Exception ex) { LogErr(ex); return; }

            foreach (var item in items)
            {
                if (token.IsCancellationRequested) { Log("Cancelled."); break; }
                if (item.MappingStatus != "Mapped")
                {
                    item.Status = "Skipped"; item.Message = "No mapping";
                    rows.Add(ImpRow(item)); RefreshGrid(dgvImport, item, importItems); continue;
                }

                cur++;
                SetProgress(cur, total, item.SourceEmail);
                Log($"[{cur}/{total}] {item.SourceEmail} → {item.TargetMailbox}");

                // Read and parse all VEVENTs from the ICS file
                List<string> blocks;
                try
                {
                    var content = File.ReadAllText(item.IcsFile, System.Text.Encoding.UTF8);
                    blocks = ExtractVEvents(content);
                }
                catch (Exception ex)
                {
                    item.Status = "Failed"; item.Message = ex.Message;
                    rows.Add(ImpRow(item)); RefreshGrid(dgvImport, item, importItems); continue;
                }

                item.TotalEvents = blocks.Count;
                int imported = 0, failed = 0;

                foreach (var block in blocks)
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        var parsed = evParser.Parse(block);
                        var json   = evConvert.ToGraphJson(parsed, cmbTimeZone.Text);
                        if (json == null) { failed++; continue; } // unparseable date

                        // Refresh token if near expiry
                        graphToken = await graph.GetTokenAsync(
                            txtTenantId.Text, txtClientId.Text, txtClientSec.Text, token);

                        var (ok, err) = await graph.ImportEventAsync(
                            graphToken, item.TargetMailbox, json, token);

                        if (ok) imported++;
                        else { failed++; Log($"    ⚠ {err}"); }

                        // 100ms pause per event to avoid throttling
                        await Task.Delay(100, token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { failed++; Log($"    ⚠ {ex.Message}"); }
                }

                item.Imported = imported;
                item.Failed   = failed;
                item.Status   = failed == 0 && imported > 0 ? "Imported"
                              : imported == 0                 ? "Failed"
                              : "Partial";
                item.Message  = $"{imported} imported, {failed} failed";
                Log($"  → {item.Message}");
                rows.Add(ImpRow(item));
                RefreshGrid(dgvImport, item, importItems);
            }

            if (rows.Count > 0)
            {
                var rp = reporter.WriteImportReport(txtRepFolder.Text, rows);
                Log($"Import report: {rp}");
            }
            SetProgress(total, total, "Import complete.");
            Log("Import done.");
        }
        finally { SetBusy(false, 2); cts = null; }
    }

    private void BtnSaveSecret_Click(object? s, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(txtClientSec.Text)) { Warn("Client Secret is empty."); return; }
        cfg.ClientSecretEncrypted = SecretStore.Protect(txtClientSec.Text);
        SaveSettings();
        Log("Client secret saved (DPAPI encrypted).");
        Info("Client secret saved to appsettings.json (encrypted with Windows DPAPI).");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // TAB 3: REPORTS
    // ═════════════════════════════════════════════════════════════════════════

    private void RefreshReportTab()
    {
        dgvRep.DataSource = null;
        if (lastReport.Count > 0)
        {
            dgvRep.DataSource = lastReport;
            var exp = lastReport.Count(r => r.Status == "Exported");
            var nd  = lastReport.Count(r => r.Status == "NoData");
            var sk  = lastReport.Count(r => r.Status == "Skipped");
            var fld = lastReport.Count(r => r.Status == "Failed");
            lblStats.Text = $"Extract — Total: {lastReport.Count}  |  Exported: {exp}  |  NoData: {nd}  |  Skipped: {sk}  |  Failed: {fld}";
        }
        else
        {
            lblStats.Text = "No extract report in memory. Run Extract first.";
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ═════════════════════════════════════════════════════════════════════════

    private string ResolveIcsPath(string folder, string email)
    {
        var p = icsBuilder.GetOutputPath(folder, email, 0);
        for (int i = 1; File.Exists(p); i++) p = icsBuilder.GetOutputPath(folder, email, i);
        return p;
    }

    private static List<string> ExtractVEvents(string content)
    {
        var list  = new List<string>();
        int from  = 0;
        const string begin = "BEGIN:VEVENT";
        const string end   = "END:VEVENT";
        while (true)
        {
            int s = content.IndexOf(begin, from, StringComparison.OrdinalIgnoreCase);
            if (s < 0) break;
            int e = content.IndexOf(end, s, StringComparison.OrdinalIgnoreCase);
            if (e < 0) break;
            e += end.Length;
            list.Add(content[s..e]);
            from = e;
        }
        return list;
    }

    private static int CountOccurrences(string text, string pattern) =>
        (text.Length - text.Replace(pattern, "").Length) / pattern.Length;

    private static ReportRow ToRow(UserCalendarItem u, int ev, int scanned, int blocks) =>
        new()
        {
            SourceEmail = u.SourceEmail, TargetMailbox = u.TargetMailbox,
            CalendarPath = u.CalendarPath, OutputIcs = u.OutputIcs,
            EventCount = ev, FilesScanned = scanned, FilesWithCalendarBlocks = blocks,
            Status = u.Status, Message = u.Message
        };

    private static ImportReportRow ImpRow(ImportItem i) =>
        new()
        {
            SourceEmail = i.SourceEmail, TargetMailbox = i.TargetMailbox,
            IcsFile = i.IcsFile, TotalEvents = i.TotalEvents,
            Imported = i.Imported, Failed = i.Failed,
            Status = i.Status, Message = i.Message
        };

    private void SetBusy(bool busy, int tab)
    {
        if (tab == 1)
        {
            btnScan.Enabled = btnExpSel.Enabled = btnExpAll.Enabled = !busy;
            btnStop1.Enabled = busy;
        }
        else
        {
            btnTestConn.Enabled = btnLoadIcs.Enabled =
                btnImpSel.Enabled = btnImpAll.Enabled = !busy;
            btnStop2.Enabled = busy;
        }
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void SetProgress(int cur, int total, string msg)
    {
        progressBar.Maximum = Math.Max(total, 1);
        progressBar.Value   = Math.Min(cur, progressBar.Maximum);
        lblProgress.Text    = total > 0 ? $"Processing {cur}/{total} — {msg}" : msg;
    }

    private void Log(string msg) =>
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");

    private void LogErr(Exception ex)
    {
        Log($"ERROR: {ex.Message}");
        MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static void RefreshGrid<T>(DataGridView dgv, T item, List<T> list)
    {
        var idx = list.IndexOf(item);
        if (idx >= 0 && idx < dgv.Rows.Count) dgv.InvalidateRow(idx);
    }

    private void ColorRow<T>(DataGridViewCellFormattingEventArgs e, List<T> list)
        where T : class
    {
        if (e.RowIndex < 0 || e.RowIndex >= list.Count || e.CellStyle == null) return;
        var status = list[e.RowIndex] is UserCalendarItem u  ? u.Status
                   : list[e.RowIndex] is ImportItem imp       ? imp.Status
                   : list[e.RowIndex] is ReportRow rr         ? rr.Status
                   : "";
        var bg = status switch
        {
            "Exported" or "Imported" => System.Drawing.Color.FromArgb(180, 240, 180),
            "Failed"                 => System.Drawing.Color.FromArgb(255, 180, 180),
            "NoData"   or "Partial"  => System.Drawing.Color.FromArgb(255, 255, 190),
            "Skipped"                => System.Drawing.Color.FromArgb(220, 220, 220),
            _                        => System.Drawing.Color.Empty
        };
        if (bg != System.Drawing.Color.Empty)
        { e.CellStyle.BackColor = bg; e.CellStyle.SelectionBackColor = bg; }
    }

    // ── validation ────────────────────────────────────────────────────────────

    private bool Validate1()
    {
        if (!Directory.Exists(txtSrcFolder.Text)) return Warn("Source Folder does not exist.");
        if (!File.Exists(txtMapCsv.Text))         return Warn("Mapping CSV does not exist.");
        return true;
    }

    private bool ValidateGraph()
    {
        if (string.IsNullOrWhiteSpace(txtTenantId.Text))  return Warn("Tenant ID is required.");
        if (string.IsNullOrWhiteSpace(txtClientId.Text))  return Warn("Client ID is required.");
        if (string.IsNullOrWhiteSpace(txtClientSec.Text)) return Warn("Client Secret is required.");
        return true;
    }

    private static bool Warn(string msg)
    { MessageBox.Show(msg, "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }

    private static void Info(string msg) =>
        MessageBox.Show(msg, "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);

    // ── folder / file dialogs ────────────────────────────────────────────────

    private void BrowseFolder(TextBox t)
    {
        using var d = new FolderBrowserDialog { ShowNewFolderButton = true };
        if (Directory.Exists(t.Text)) d.SelectedPath = t.Text;
        if (d.ShowDialog(this) == DialogResult.OK) t.Text = d.SelectedPath;
    }

    private void BrowseFile(TextBox t, string filter)
    {
        using var d = new OpenFileDialog { Filter = filter };
        if (File.Exists(t.Text)) d.InitialDirectory = Path.GetDirectoryName(t.Text);
        if (d.ShowDialog(this) == DialogResult.OK) t.Text = d.FileName;
    }

    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path)) Process.Start("explorer.exe", path);
        else MessageBox.Show("Folder not found.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static void OpenFile(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else MessageBox.Show("File not found.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ── tab switching ──────────────────────────────────────────────────────────

    private void Tabs_SelectedIndexChanged(object? s, EventArgs e)
    {
        if (tabs.SelectedIndex == 1)
        {
            // Pre-fill Tab 2 from Tab 1 paths
            if (string.IsNullOrEmpty(txtIcsFolder.Text))  txtIcsFolder.Text  = txtOutFolder.Text;
            if (string.IsNullOrEmpty(txtMapCsv2.Text))    txtMapCsv2.Text    = txtMapCsv.Text;
        }
        if (tabs.SelectedIndex == 2) RefreshReportTab();
    }

    // ── settings ──────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            cfg = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new();
            txtSrcFolder.Text  = cfg.LastSourceFolder;
            txtMapCsv.Text     = cfg.LastMappingCsv;
            txtOutFolder.Text  = cfg.LastOutputFolder;
            txtRepFolder.Text  = cfg.LastReportFolder;
            txtTenantId.Text   = cfg.TenantId;
            txtClientId.Text   = cfg.ClientId;
            cmbTimeZone.Text   = string.IsNullOrEmpty(cfg.TimeZone) ? "SE Asia Standard Time" : cfg.TimeZone;
            txtIcsFolder.Text  = cfg.LastIcsFolder;
            txtMapCsv2.Text    = cfg.LastImportMappingCsv;

            if (!string.IsNullOrEmpty(cfg.ClientSecretEncrypted))
                txtClientSec.Text = SecretStore.Unprotect(cfg.ClientSecretEncrypted);
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            cfg.LastSourceFolder    = txtSrcFolder.Text;
            cfg.LastMappingCsv      = txtMapCsv.Text;
            cfg.LastOutputFolder    = txtOutFolder.Text;
            cfg.LastReportFolder    = txtRepFolder.Text;
            cfg.TenantId            = txtTenantId.Text;
            cfg.ClientId            = txtClientId.Text;
            cfg.TimeZone            = cmbTimeZone.Text;
            cfg.LastIcsFolder       = txtIcsFolder.Text;
            cfg.LastImportMappingCsv = txtMapCsv2.Text;
            // ClientSecretEncrypted is saved explicitly by BtnSaveSecret_Click only
            File.WriteAllText(SettingsFile,
                JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        cts?.Cancel();
        SaveSettings();
        graph.Dispose();
        base.OnFormClosing(e);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // UI FACTORY HELPERS
    // ══════════════════════════════════════════════════════════════════════════

    private static TableLayoutPanel PathTable(int rows)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = rows,
            Padding = new Padding(4, 2, 4, 2)
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,  88));
        return t;
    }

    private void AddPathRow(TableLayoutPanel t, int row, string label,
        out TextBox txt, Action browse)
    {
        t.Controls.Add(MkLabel(label), 0, row);
        txt = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(2) };
        var btn = new Button { Text = "Browse…", Dock = DockStyle.Fill, Margin = new Padding(2) };
        btn.Click += (_, _) => browse();
        t.Controls.Add(txt, 1, row);
        t.Controls.Add(btn, 2, row);
    }

    private static void AddLabelTxt(TableLayoutPanel t, int row, string label, out TextBox txt)
    {
        t.Controls.Add(MkLabel(label), 0, row);
        txt = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(2) };
        t.SetColumnSpan(txt, 3);
        t.Controls.Add(txt, 1, row);
    }

    private static Label MkLabel(string text) => new()
    {
        Text = text, Anchor = AnchorStyles.Right,
        TextAlign = System.Drawing.ContentAlignment.MiddleRight
    };

    private static Button Btn(string text, int width, EventHandler handler)
    {
        var b = new Button { Text = text, Width = width, Height = 34, Margin = new Padding(0, 0, 6, 0) };
        b.Click += handler;
        return b;
    }

    private static DataGridView BuildGrid(DataGridViewColumn[] cols)
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            ReadOnly = false, AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false, ScrollBars = ScrollBars.Both,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            AlternatingRowsDefaultCellStyle = { BackColor = System.Drawing.Color.FromArgb(245, 248, 255) }
        };
        g.Columns.AddRange(cols);
        g.CellContentClick += (s, e) =>
        {
            if (g.Columns[e.ColumnIndex] is DataGridViewCheckBoxColumn && e.RowIndex >= 0)
                g.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        return g;
    }

    // Return base type DataGridViewColumn so mixed arrays can be inferred as DataGridViewColumn[]
    private static DataGridViewColumn CheckCol(string name, string header, int w) =>
        new DataGridViewCheckBoxColumn { Name = name, HeaderText = header, DataPropertyName = name[3..], Width = w };

    private static DataGridViewColumn TxtCol(string name, string header, int w) =>
        new DataGridViewTextBoxColumn { Name = name, HeaderText = header, DataPropertyName = name[3..], Width = w, ReadOnly = true };

    private static DataGridViewColumn NumCol(string name, string header, int w) =>
        new DataGridViewTextBoxColumn
        {
            Name = name, HeaderText = header, DataPropertyName = name[3..], Width = w, ReadOnly = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight }
        };
}
