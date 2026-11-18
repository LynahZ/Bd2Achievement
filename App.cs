using System.Text.Json;

namespace Bd2Achievement;

internal static class App
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--export", .. var options])
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return TableExportEntry.Main(options);
        }
        if (args is ["--verify", var query, var output])
        {
            try
            {
                var catalog = Catalog.Load();
                File.WriteAllText(output, JsonSerializer.Serialize(catalog.Search(query)));
                return 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(output, error.ToString());
                return 1;
            }
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainWindow());
        return 0;
    }
}

internal sealed class MainWindow : Form
{
    private readonly TextBox query = new() { Dock = DockStyle.Fill, PlaceholderText = "例如 退下12", AccessibleName = "成就名称或达成条件" };
    private readonly Button search = new() { Text = "查询", Dock = DockStyle.Fill };
    private readonly Button update = new() { Text = "更新表", Dock = DockStyle.Fill };
    private readonly TextBox results = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, AccessibleName = "查询结果", Margin = new Padding(0, 10, 0, 0)
    };
    private readonly Label status = new() { Text = "等待查询", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };

    public MainWindow()
    {
        Text = "棕色尘埃2 · 成就查询";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(810, 630);
        MinimumSize = new Size(490, 390);
        BackColor = Color.FromArgb(242, 244, 248);
        ForeColor = Color.FromArgb(31, 40, 61);
        Font = new Font("Microsoft YaHei UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.Controls.Add(new Label { Text = "棕色尘埃2  成就查询", Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 19, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(new Label { Text = query.AccessibleName, Dock = DockStyle.Fill }, 0, 1);
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        query.Margin = new Padding(0, 7, 7, 4);
        search.BackColor = Color.FromArgb(48, 66, 127);
        search.ForeColor = Color.White;
        search.FlatStyle = FlatStyle.Flat;
        bar.Controls.Add(query, 0, 0);
        bar.Controls.Add(search, 1, 0);
        bar.Controls.Add(update, 2, 0);
        layout.Controls.Add(bar, 0, 2);
        layout.Controls.Add(results, 0, 3);
        layout.Controls.Add(status, 0, 4);
        Controls.Add(layout);
        AcceptButton = search;
        ActiveControl = query;
        search.Click += async (_, _) => await RunAsync(refreshOnly: false);
        update.Click += async (_, _) => await RunAsync(refreshOnly: true);
    }

    private async Task RunAsync(bool refreshOnly)
    {
        if (!search.Enabled) return;
        var input = query.Text.Trim();
        if (!refreshOnly && input.Length == 0)
        {
            status.Text = "请输入成就名称或达成条件。";
            query.Focus();
            return;
        }
        search.Enabled = update.Enabled = false;
        status.Text = refreshOnly ? "正在检查本机游戏数据…" : "正在查询…";
        try
        {
            var result = await Task.Run(() =>
            {
                var catalog = Catalog.Load();
                if (refreshOnly)
                    return (Status: "本机成就表已就绪", Text: $"成就定义 {catalog.AchievementCount} 条，文本 {catalog.TextCount} 条。");
                var series = catalog.Search(input);
                return series.Length == 0
                    ? (Status: "未找到匹配的成就", Text: "可以尝试缩短成就名称或达成条件。")
                    : (Status: $"找到 {series.Length} 个成就系列", Text: FormatResults(series));
            });
            status.Text = result.Status;
            results.Text = result.Text;
            results.Select(0, 0);
            results.ScrollToCaret();
        }
        catch (Exception error)
        {
            status.Text = "读取失败";
            results.Text = error.Message;
        }
        finally
        {
            search.Enabled = update.Enabled = true;
        }
    }

    private static string FormatResults(IEnumerable<Series> series) => string.Join("\r\n\r\n", series.Select(group =>
        $"{group.Tiers[^1].Name}  ·  共 {group.Tiers.Length} 档  ·  最终门槛 {Catalog.Format(group.FinalTarget)}\r\n\r\n"
        + string.Join("\r\n", group.Tiers.Select(tier =>
            $"{tier.Id}. {tier.Name}  ·  {Catalog.Format(tier.Target)}  ·  {tier.Description}"))));
}
