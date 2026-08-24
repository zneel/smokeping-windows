using System.Drawing;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows.Forms;
using SmokePing.Net.Graphing;

namespace SmokePing.Net.Desktop;

/// <summary>
/// The desktop client: a target tree on the left, and graphs, charts and alerts on
/// the right. Everything is drawn natively - there is no embedded browser.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MainForm : Form
{
    private const string ChartRange = "10h";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly SmokePingClient _client;
    private readonly TreeView _menu = new();
    private readonly TabControl _tabs = new();
    private readonly Panel _graphHost = new();
    private readonly TableLayoutPanel _graphStack = new();
    private readonly Label _targetHeading = new();
    private readonly Label _targetStats = new();
    private readonly List<GraphControl> _graphs = [];
    private readonly TableLayoutPanel _chartsPanel = new();
    private readonly ListView _activeAlerts = new();
    private readonly ListView _recentAlerts = new();
    private readonly ListView _alertRules = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _connectionLabel = new();
    private readonly ToolStripStatusLabel _refreshedLabel = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();

    private SiteConfigDto? _config;
    private string? _selectedTargetId;
    private CancellationTokenSource _loading = new();
    private GraphTheme _theme = GraphTheme.Dark;
    private bool _darkMode = true;

    public MainForm(SmokePingClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));

        Text = "SmokePing.NET";
        ClientSize = new Size(1180, 820);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        ApplyTheme();

        _refreshTimer.Interval = (int)RefreshInterval.TotalMilliseconds;
        _refreshTimer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        _refreshTimer.Start();

        Shown += async (_, _) => await ConnectAsync().ConfigureAwait(true);
    }

    private void BuildLayout()
    {
        var menuStrip = BuildMenuStrip();

        _menu.Dock = DockStyle.Fill;
        _menu.HideSelection = false;
        _menu.BorderStyle = BorderStyle.None;
        _menu.AfterSelect += async (_, e) => await OnTargetSelectedAsync(e.Node).ConfigureAwait(true);

        BuildGraphsTab();
        BuildChartsTab();
        BuildAlertsTab();

        _tabs.Dock = DockStyle.Fill;
        _tabs.SelectedIndexChanged += async (_, _) => await RefreshAsync().ConfigureAwait(true);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 250,
            FixedPanel = FixedPanel.Panel1,
        };

        split.Panel1.Controls.Add(_menu);
        split.Panel2.Controls.Add(_tabs);

        _connectionLabel.Text = $"Connecting to {_client.BaseAddress}";
        _connectionLabel.Spring = true;
        _connectionLabel.TextAlign = ContentAlignment.MiddleLeft;
        _status.Items.Add(_connectionLabel);
        _status.Items.Add(_refreshedLabel);

        Controls.Add(split);
        Controls.Add(_status);
        Controls.Add(menuStrip);
        MainMenuStrip = menuStrip;
    }

    private MenuStrip BuildMenuStrip()
    {
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Refresh now", null, async (_, _) =>
            await RefreshAsync().ConfigureAwait(true))
        {
            ShortcutKeys = Keys.F5,
        });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));

        var themeItem = new ToolStripMenuItem("&Dark theme", null, async (_, _) =>
        {
            _darkMode = !_darkMode;
            _theme = _darkMode ? GraphTheme.Dark : GraphTheme.Light;
            ApplyTheme();
            await RefreshAsync().ConfigureAwait(true);
        })
        {
            Checked = true,
            CheckOnClick = true,
        };

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(themeItem);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&About", null, (_, _) => MessageBox.Show(
            this,
            $"SmokePing.NET desktop client{Environment.NewLine}Connected to {_client.BaseAddress}",
            "About",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information)));

        var strip = new MenuStrip();
        strip.Items.Add(file);
        strip.Items.Add(view);
        strip.Items.Add(help);
        return strip;
    }

    private void BuildGraphsTab()
    {
        _targetHeading.AutoSize = true;
        _targetHeading.Font = new Font(Font.FontFamily, 12, FontStyle.Bold);
        _targetHeading.Padding = new Padding(4, 4, 4, 0);
        _targetHeading.Text = "Select a target";

        _targetStats.AutoSize = true;
        _targetStats.Padding = new Padding(4, 2, 4, 6);

        _graphStack.Dock = DockStyle.Top;
        _graphStack.ColumnCount = 1;
        _graphStack.AutoSize = true;
        _graphStack.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _graphStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _graphHost.Dock = DockStyle.Fill;
        _graphHost.AutoScroll = true;
        _graphHost.Controls.Add(_graphStack);

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
        };

        header.Controls.Add(_targetHeading);
        header.Controls.Add(_targetStats);

        var page = new TabPage("Graphs");
        page.Controls.Add(_graphHost);
        page.Controls.Add(header);
        _tabs.TabPages.Add(page);
    }

    private void BuildChartsTab()
    {
        _chartsPanel.Dock = DockStyle.Fill;
        _chartsPanel.ColumnCount = 2;
        _chartsPanel.RowCount = 2;
        _chartsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _chartsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _chartsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _chartsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var page = new TabPage("Charts");
        page.Controls.Add(_chartsPanel);
        _tabs.TabPages.Add(page);
    }

    private void BuildAlertsTab()
    {
        ConfigureListView(_activeAlerts, "When", "State", "Alert", "Target", "Comment");
        ConfigureListView(_recentAlerts, "When", "State", "Alert", "Target", "Comment");
        ConfigureListView(_alertRules, "Name", "Type", "Pattern", "Edge", "Comment");

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33));

        layout.Controls.Add(WithCaption("Active", _activeAlerts), 0, 0);
        layout.Controls.Add(WithCaption("Recent notifications", _recentAlerts), 0, 1);
        layout.Controls.Add(WithCaption("Rules", _alertRules), 0, 2);

        var page = new TabPage("Alerts");
        page.Controls.Add(layout);
        _tabs.TabPages.Add(page);
    }

    private static Control WithCaption(string caption, Control content)
    {
        var label = new Label
        {
            Text = caption,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 22,
            Padding = new Padding(4, 4, 0, 0),
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        };

        var panel = new Panel { Dock = DockStyle.Fill };
        content.Dock = DockStyle.Fill;
        panel.Controls.Add(content);
        panel.Controls.Add(label);
        return panel;
    }

    private static void ConfigureListView(ListView listView, params string[] columns)
    {
        listView.View = View.Details;
        listView.FullRowSelect = true;
        listView.GridLines = false;
        listView.BorderStyle = BorderStyle.None;

        foreach (var column in columns)
        {
            listView.Columns.Add(column, -2);
        }
    }

    /// <summary>Builds the graph rows for the periods the daemon offers.</summary>
    private void BuildGraphRows(IReadOnlyList<RangeDto> ranges)
    {
        _graphStack.SuspendLayout();
        _graphStack.Controls.Clear();
        _graphStack.RowStyles.Clear();
        _graphs.Clear();
        _graphStack.RowCount = ranges.Count;

        foreach (var range in ranges)
        {
            var graph = new GraphControl
            {
                Dock = DockStyle.Fill,
                Height = 300,
                Margin = new Padding(6, 4, 6, 8),
                GraphTitle = range.Label,
                Theme = _theme,
                Tag = range.Range,
            };

            _graphStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 320));
            _graphStack.Controls.Add(graph);
            _graphs.Add(graph);
        }

        _graphStack.ResumeLayout(performLayout: true);
    }

    /// <summary>
    /// Connects, retrying for a while first. In standalone mode the daemon is starting
    /// in this same process, so the first few attempts are expected to fail.
    /// </summary>
    private async Task ConnectAsync()
    {
        const int Attempts = 20;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (await LoadConfigAsync().ConfigureAwait(true))
            {
                return;
            }

            SetConnectionState($"Connecting to {_client.BaseAddress} ... ({attempt}/{Attempts})");
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
        }

        SetConnectionState($"Cannot reach {_client.BaseAddress} - is the daemon running?");
        foreach (var graph in _graphs)
        {
            graph.SetMessage("Not connected.");
        }
    }

    /// <summary>Loads the site configuration. Returns false when the daemon is unreachable.</summary>
    private async Task<bool> LoadConfigAsync()
    {
        try
        {
            _config = await _client.GetConfigAsync(CancellationToken.None).ConfigureAwait(true);
            if (_config is null)
            {
                SetConnectionState("The daemon returned no configuration.");
                return false;
            }

            Text = $"{_config.SiteName} - SmokePing.NET";
            SetConnectionState($"Connected to {_client.BaseAddress} - {_config.TargetCount} target(s)");

            BuildGraphRows(_config.DetailRanges);
            PopulateMenu(_config.Menu);
            await RefreshAsync().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private void PopulateMenu(IReadOnlyList<MenuNodeDto> nodes)
    {
        _menu.BeginUpdate();
        _menu.Nodes.Clear();

        foreach (var node in nodes)
        {
            _menu.Nodes.Add(BuildNode(node));
        }

        _menu.ExpandAll();
        _menu.EndUpdate();

        // Select the first measured target so the window is never empty on start-up.
        var first = FindFirstTarget(_menu.Nodes);
        if (first is not null)
        {
            _menu.SelectedNode = first;
        }
    }

    private static TreeNode BuildNode(MenuNodeDto node)
    {
        var treeNode = new TreeNode(node.Title) { Tag = node };
        if (node.IsTarget)
        {
            treeNode.ToolTipText = $"{node.Host} ({node.ProbeType})";
        }

        foreach (var child in node.Children)
        {
            treeNode.Nodes.Add(BuildNode(child));
        }

        return treeNode;
    }

    private static TreeNode? FindFirstTarget(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is MenuNodeDto { IsTarget: true })
            {
                return node;
            }

            var found = FindFirstTarget(node.Nodes);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private async Task OnTargetSelectedAsync(TreeNode? node)
    {
        if (node?.Tag is not MenuNodeDto dto || !dto.IsTarget)
        {
            return;
        }

        _selectedTargetId = dto.Id;
        _targetHeading.Text = dto.Title;
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        // Only one refresh at a time; a slow response must not overwrite a newer one.
        await _loading.CancelAsync().ConfigureAwait(true);
        _loading.Dispose();
        _loading = new CancellationTokenSource();
        var token = _loading.Token;

        try
        {
            switch (_tabs.SelectedIndex)
            {
                case 1:
                    await RefreshChartsAsync(token).ConfigureAwait(true);
                    break;
                case 2:
                    await RefreshAlertsAsync(token).ConfigureAwait(true);
                    break;
                default:
                    await RefreshGraphsAsync(token).ConfigureAwait(true);
                    break;
            }

            _refreshedLabel.Text = $"Updated {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            SetConnectionState($"Cannot reach {_client.BaseAddress} - is the daemon running?");
        }
    }

    private async Task RefreshGraphsAsync(CancellationToken token)
    {
        if (_selectedTargetId is not { } targetId)
        {
            return;
        }

        var first = true;
        foreach (var graph in _graphs)
        {
            var range = (string)(graph.Tag ?? "3h");
            var data = await _client.GetTargetAsync(targetId, range, token).ConfigureAwait(true);
            if (data is null)
            {
                graph.SetMessage("No data.");
                continue;
            }

            graph.Theme = _theme;
            graph.SetData(data);

            if (first)
            {
                _targetHeading.Text = $"{data.Title} - {data.Host}";
                _targetStats.Text = FormatStatistics(data);
                first = false;
            }
        }
    }

    private static string FormatStatistics(TargetDataDto data)
    {
        var s = data.Statistics;
        if (!s.HasData)
        {
            return $"{data.ProbeDescription}   -   no data in the last period";
        }

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{data.ProbeDescription}   -   " +
            $"median {SmokeGraphRenderer.FormatMilliseconds(s.Median)}   " +
            $"min {SmokeGraphRenderer.FormatMilliseconds(s.Minimum)}   " +
            $"max {SmokeGraphRenderer.FormatMilliseconds(s.Maximum)}   " +
            $"sd {SmokeGraphRenderer.FormatMilliseconds(s.StandardDeviation)}   " +
            $"loss {s.LossPercent:F1}%");
    }

    private async Task RefreshChartsAsync(CancellationToken token)
    {
        var charts = await _client.GetChartsAsync(ChartRange, token).ConfigureAwait(true);
        if (charts is null)
        {
            return;
        }

        _chartsPanel.SuspendLayout();
        _chartsPanel.Controls.Clear();

        for (var i = 0; i < charts.Charts.Count && i < 4; i++)
        {
            var chart = charts.Charts[i];
            var list = new ListView();
            ConfigureListView(list, "Target", "Host", "Value");

            var isLoss = chart.Title.Contains("Loss", StringComparison.OrdinalIgnoreCase);
            foreach (var item in chart.Items)
            {
                var value = isLoss
                    ? item.Value.ToString("F1", CultureInfo.CurrentCulture) + "%"
                    : SmokeGraphRenderer.FormatMilliseconds(item.Value);

                list.Items.Add(new ListViewItem([item.Title, item.Host, value]));
            }

            ApplyTheme(list);
            _chartsPanel.Controls.Add(WithCaption(chart.Title, list), i % 2, i / 2);
        }

        _chartsPanel.ResumeLayout(performLayout: true);
    }

    private async Task RefreshAlertsAsync(CancellationToken token)
    {
        var alerts = await _client.GetAlertsAsync(token).ConfigureAwait(true);
        if (alerts is null)
        {
            return;
        }

        FillEvents(_activeAlerts, alerts.Active);
        FillEvents(_recentAlerts, alerts.Recent);

        _alertRules.BeginUpdate();
        _alertRules.Items.Clear();
        foreach (var rule in alerts.Rules)
        {
            _alertRules.Items.Add(new ListViewItem([
                rule.Name,
                rule.Type,
                rule.Pattern,
                rule.EdgeTrigger ? "yes" : "no",
                rule.Comment,
            ]));
        }

        _alertRules.EndUpdate();

        _tabs.TabPages[2].Text = alerts.Active.Count > 0 ? $"Alerts ({alerts.Active.Count})" : "Alerts";
    }

    private static void FillEvents(ListView listView, IReadOnlyList<AlertEventDto> events)
    {
        listView.BeginUpdate();
        listView.Items.Clear();

        foreach (var alertEvent in events)
        {
            var item = new ListViewItem([
                alertEvent.Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                alertEvent.State,
                alertEvent.AlertName,
                alertEvent.TargetTitle,
                alertEvent.Comment,
            ]);

            item.ForeColor = alertEvent.State == "cleared" ? Color.FromArgb(61, 220, 132) : Color.FromArgb(255, 93, 93);
            listView.Items.Add(item);
        }

        listView.EndUpdate();
    }

    private void SetConnectionState(string message) => _connectionLabel.Text = message;

    private void ApplyTheme()
    {
        BackColor = ColorTranslator.FromHtml(_theme.Background);
        ForeColor = ColorTranslator.FromHtml(_theme.Text);

        foreach (Control control in Controls)
        {
            ApplyTheme(control);
        }

        foreach (var graph in _graphs)
        {
            graph.Theme = _theme;
        }
    }

    /// <summary>Pushes the palette down the control tree; WinForms has no theme inheritance.</summary>
    private void ApplyTheme(Control control)
    {
        var background = ColorTranslator.FromHtml(_theme.Background);
        var panel = ColorTranslator.FromHtml(_theme.PlotBackground);
        var text = ColorTranslator.FromHtml(_theme.Text);

        switch (control)
        {
            case GraphControl graph:
                graph.Theme = _theme;
                return;

            case TreeView or ListView:
                control.BackColor = panel;
                control.ForeColor = text;
                break;

            default:
                control.BackColor = background;
                control.ForeColor = text;
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyTheme(child);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
            _loading.Dispose();
            _client.Dispose();
        }

        base.Dispose(disposing);
    }
}
