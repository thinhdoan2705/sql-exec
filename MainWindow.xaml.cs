using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace SQLExec;

public class FileNode
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsFile { get; set; }
    public string Icon => IsFile ? "📄" : "📁";
    public ObservableCollection<FileNode> Children { get; } = new();
}

public partial class MainWindow : Window
{
    readonly AppSettings settings = AppSettings.Load();
    string? currentFile;
    bool dirty, loading;
    CancellationTokenSource? cts;
    Encoding fileEncoding = new UTF8Encoding(false);

    public MainWindow()
    {
        InitializeComponent();
    }

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(settings.LastFolder)) LoadTree();
        await LoadDatabases(silent: true);
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
    }

    async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { e.Handled = true; await ExecuteAsync(); }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; SaveFile(); }
    }

    // ---------- Folder tree ----------
    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Chọn thư mục chứa file SQL" };
        if (Directory.Exists(settings.LastFolder)) d.InitialDirectory = settings.LastFolder;
        if (d.ShowDialog(this) != true) return;
        settings.LastFolder = d.FolderName; settings.Save();
        LoadTree();
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => LoadTree();

    void LoadTree()
    {
        var root = settings.LastFolder;
        if (!Directory.Exists(root)) { lblStatus.Text = "Chưa chọn folder"; return; }
        var rn = new FileNode { Name = root, Path = root };
        int count = Populate(rn, root);
        tree.ItemsSource = new[] { rn };
        lblStatus.Text = $"{count} file SQL trong {root}";
    }

    static int Populate(FileNode parent, string dir)
    {
        int count = 0;
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var n = new FileNode { Name = System.IO.Path.GetFileName(d), Path = d };
                int c = Populate(n, d);
                if (c > 0) { parent.Children.Add(n); count += c; }
            }
            foreach (var f in Directory.EnumerateFiles(dir, "*.sql").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                parent.Children.Add(new FileNode { Name = System.IO.Path.GetFileName(f), Path = f, IsFile = true });
                count++;
            }
        }
        catch (UnauthorizedAccessException) { }
        return count;
    }

    void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FileNode { IsFile: true } n) OpenFile(n.Path);
    }

    // ---------- File ----------
    bool ConfirmDiscard()
    {
        if (!dirty || currentFile == null) return true;
        var r = MessageBox.Show(this, $"Lưu thay đổi vào {System.IO.Path.GetFileName(currentFile)}?", "SQLExec", MessageBoxButton.YesNoCancel);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) SaveFile();
        return true;
    }

    void OpenFile(string path)
    {
        if (path == currentFile) return;
        if (!ConfirmDiscard())
        {
            // giữ nguyên file hiện tại; chọn lại node sẽ kích hoạt lại sự kiện nên chỉ báo trạng thái
            lblStatus.Text = "Giữ nguyên file đang mở: " + System.IO.Path.GetFileName(currentFile);
            return;
        }
        try
        {
            loading = true;
            var bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            using var sr = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), true);
            txtSql.Text = sr.ReadToEnd();
            fileEncoding = bom ? new UTF8Encoding(true) : sr.CurrentEncoding;
            currentFile = path; dirty = false;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Không đọc được file"); }
        finally { loading = false; }
        UpdateStatus();
    }

    void Save_Click(object sender, RoutedEventArgs e) => SaveFile();

    void SaveFile()
    {
        if (currentFile == null) return;
        try { File.WriteAllText(currentFile, txtSql.Text, fileEncoding); dirty = false; UpdateStatus(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Lỗi lưu file"); }
    }

    void txtSql_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (loading) return;
        dirty = true; UpdateStatus();
    }

    void UpdateStatus() =>
        lblFile.Text = currentFile == null ? "" : $"{currentFile}{(dirty ? " *" : "")}  |  DB: {cboDb.Text}";

    // ---------- Connection ----------
    async void Connection_Click(object sender, RoutedEventArgs e)
    {
        var w = new ConnectionWindow(settings) { Owner = this };
        if (w.ShowDialog() != true) return;
        settings.Save();
        await LoadDatabases();
    }

    async void LoadDbs_Click(object sender, RoutedEventArgs e) => await LoadDatabases();

    void cboDb_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (cboDb.SelectedItem is string db) { settings.LastDatabase = db; settings.Save(); }
        UpdateStatus();
    }

    async Task LoadDatabases(bool silent = false)
    {
        try
        {
            lblStatus.Text = "Đang kết nối " + settings.Server + "...";
            var dbs = new List<string>();
            await using var c = new SqlConnection(settings.BuildConnectionString());
            await c.OpenAsync();
            await using var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE HAS_DBACCESS(name)=1 AND state=0 ORDER BY name", c);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) dbs.Add(r.GetString(0));
            cboDb.ItemsSource = dbs;
            int i = dbs.IndexOf(settings.LastDatabase);
            cboDb.SelectedIndex = i >= 0 ? i : (dbs.Count > 0 ? 0 : -1);
            lblStatus.Text = $"Đã kết nối {settings.Server} ({dbs.Count} database)";
        }
        catch (Exception ex)
        {
            lblStatus.Text = "Kết nối thất bại";
            if (!silent) MessageBox.Show(this, ex.Message, "Lỗi kết nối", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- Execute ----------
    static readonly Regex GoRegex = new(@"^[ \t]*GO(?:[ \t]+(\d+))?[ \t]*(?:--.*)?\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    static List<string> SplitBatches(string sql)
    {
        var list = new List<string>();
        int last = 0;
        foreach (Match m in GoRegex.Matches(sql))
        {
            AddBatch(list, sql[last..m.Index], m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 1);
            last = m.Index + m.Length;
        }
        AddBatch(list, sql[last..], 1);
        return list;
    }

    static void AddBatch(List<string> l, string b, int times)
    {
        if (string.IsNullOrWhiteSpace(b)) return;
        for (int i = 0; i < times; i++) l.Add(b);
    }

    async void Execute_Click(object sender, RoutedEventArgs e) => await ExecuteAsync();
    void Cancel_Click(object sender, RoutedEventArgs e) => cts?.Cancel();

    async Task ExecuteAsync()
    {
        if (cts != null) return;
        var sql = txtSql.SelectionLength > 0 ? txtSql.SelectedText : txtSql.Text;
        if (string.IsNullOrWhiteSpace(sql)) return;
        if (cboDb.SelectedItem is not string db)
        {
            MessageBox.Show(this, "Hãy chọn database (hoặc cấu hình Connection rồi Load DBs).", "SQLExec");
            return;
        }

        ClearResults();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        btnExec.IsEnabled = false; btnCancel.IsEnabled = true;
        var sw = Stopwatch.StartNew();
        int sets = 0;
        try
        {
            lblStatus.Text = "Đang chạy...";
            await using var conn = new SqlConnection(settings.BuildConnectionString(db));
            conn.InfoMessage += (_, ev) => Dispatcher.BeginInvoke(() =>
            {
                foreach (SqlError er in ev.Errors)
                    Log(er.Class > 10 ? $"Msg {er.Number}, Level {er.Class}, State {er.State}, Line {er.LineNumber}: {er.Message}" : er.Message);
            });
            await conn.OpenAsync(token);
            foreach (var batch in SplitBatches(sql))
            {
                await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = settings.CommandTimeout };
                cmd.StatementCompleted += (_, ev) => Dispatcher.BeginInvoke(() => Log($"({ev.RecordCount} row(s) affected)"));
                await using var r = await cmd.ExecuteReaderAsync(token);
                do
                {
                    if (r.FieldCount == 0) continue;
                    var dt = new DataTable();
                    for (int i = 0; i < r.FieldCount; i++)
                    {
                        var name = r.GetName(i);
                        if (string.IsNullOrEmpty(name)) name = "(No column name)";
                        var baseName = name; int k = 1;
                        while (dt.Columns.Contains(name)) name = $"{baseName}_{k++}";
                        dt.Columns.Add(name, typeof(object));
                    }
                    var vals = new object[r.FieldCount];
                    dt.BeginLoadData();
                    while (await r.ReadAsync(token))
                    {
                        r.GetValues(vals);
                        dt.Rows.Add(vals);
                    }
                    dt.EndLoadData();
                    AddResultTab(dt, ++sets);
                } while (await r.NextResultAsync(token));
            }
            Log($"Hoàn tất trong {sw.Elapsed.TotalSeconds:0.00}s");
            lblStatus.Text = $"Xong ({sw.Elapsed.TotalSeconds:0.00}s, {sets} result set)";
            tabs.SelectedIndex = sets > 0 ? 1 : 0;
        }
        catch (OperationCanceledException) { Log("Đã huỷ."); lblStatus.Text = "Đã huỷ"; tabs.SelectedIndex = 0; }
        catch (SqlException ex) when (token.IsCancellationRequested) { Log("Đã huỷ. " + ex.Message); lblStatus.Text = "Đã huỷ"; tabs.SelectedIndex = 0; }
        catch (SqlException ex)
        {
            Log($"Msg {ex.Number}, Level {ex.Class}, State {ex.State}, Line {ex.LineNumber}: {ex.Message}");
            lblStatus.Text = "Lỗi SQL"; tabs.SelectedIndex = 0;
        }
        catch (Exception ex) { Log(ex.Message); lblStatus.Text = "Lỗi"; tabs.SelectedIndex = 0; }
        finally
        {
            cts?.Dispose(); cts = null;
            btnExec.IsEnabled = true; btnCancel.IsEnabled = false;
        }
    }

    void ClearResults()
    {
        while (tabs.Items.Count > 1) tabs.Items.RemoveAt(tabs.Items.Count - 1);
        txtMessages.Clear();
        tabs.SelectedIndex = 0;
    }

    void Log(string s)
    {
        txtMessages.AppendText(s + Environment.NewLine);
        txtMessages.ScrollToEnd();
    }

    void AddResultTab(DataTable dt, int n)
    {
        var grid = new DataGrid
        {
            IsReadOnly = true, AutoGenerateColumns = true, CanUserAddRows = false, CanUserDeleteRows = false,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            AlternatingRowBackground = System.Windows.Media.Brushes.WhiteSmoke,
            EnableRowVirtualization = true,
            ItemsSource = dt.DefaultView,
        };
        grid.AutoGeneratingColumn += (_, e) =>
        {
            // Cột tên có ký tự đặc biệt (., [, ]) => bind theo indexer; '_' trong header không bị coi là access key
            var name = e.PropertyName;
            if (e.Column is DataGridTextColumn tc)
            {
                tc.Binding = new Binding($"[{name}]") { Mode = BindingMode.OneWay, TargetNullValue = "NULL" };
                tc.Header = name.Replace("_", "__");
            }
        };
        grid.LoadingRow += (_, e) => e.Row.Header = (e.Row.GetIndex() + 1).ToString();

        var item = new TabItem { Header = $"Result {n} ({dt.Rows.Count})", Content = grid };
        tabs.Items.Add(item);
    }
}
