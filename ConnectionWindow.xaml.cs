using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.SqlClient;

namespace SQLExec;

public partial class ConnectionWindow : Window
{
    readonly AppSettings s;

    public ConnectionWindow(AppSettings settings)
    {
        InitializeComponent();
        s = settings;
        txtServer.Text = s.Server;
        cboAuth.SelectedIndex = s.WindowsAuth ? 0 : 1;
        txtUser.Text = s.User;
        txtPass.Password = s.Password;
        txtTimeout.Text = s.CommandTimeout.ToString();
        chkTrust.IsChecked = s.TrustServerCertificate;
    }

    void cboAuth_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (txtUser == null) return;
        txtUser.IsEnabled = txtPass.IsEnabled = cboAuth.SelectedIndex == 1;
    }

    void Apply()
    {
        s.Server = txtServer.Text.Trim();
        s.WindowsAuth = cboAuth.SelectedIndex == 0;
        s.User = txtUser.Text;
        s.Password = txtPass.Password;
        s.TrustServerCertificate = chkTrust.IsChecked == true;
        s.CommandTimeout = int.TryParse(txtTimeout.Text, out var t) && t >= 0 ? t : 120;
    }

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        Apply();
        lblResult.Foreground = Brushes.Black; lblResult.Text = "Connecting...";
        try
        {
            await using var c = new SqlConnection(s.BuildConnectionString());
            await c.OpenAsync();
            lblResult.Foreground = Brushes.Green; lblResult.Text = "OK - SQL Server " + c.ServerVersion;
        }
        catch (Exception ex) { lblResult.Foreground = Brushes.Firebrick; lblResult.Text = ex.Message; }
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        Apply();
        DialogResult = true;
    }
}
