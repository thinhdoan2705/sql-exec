using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SQLExec;

public class AppSettings
{
    public string Server { get; set; } = "localhost";
    public bool WindowsAuth { get; set; } = true;
    public string User { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    public bool TrustServerCertificate { get; set; } = true;
    public int CommandTimeout { get; set; } = 120;
    public string LastFolder { get; set; } = "";
    public string LastDatabase { get; set; } = "";

    static string PathFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SQLExec", "settings.json");

    public string Password
    {
        get
        {
            if (string.IsNullOrEmpty(PasswordProtected)) return "";
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    Convert.FromBase64String(PasswordProtected), null, DataProtectionScope.CurrentUser));
            }
            catch { return ""; }
        }
        set => PasswordProtected = string.IsNullOrEmpty(value) ? "" :
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    public string BuildConnectionString(string? database = null)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            TrustServerCertificate = TrustServerCertificate,
            Encrypt = SqlConnectionEncryptOption.Optional,
            ConnectTimeout = 15,
            ApplicationName = "SQLExec",
        };
        if (WindowsAuth) b.IntegratedSecurity = true;
        else { b.UserID = User; b.Password = Password; }
        if (!string.IsNullOrWhiteSpace(database)) b.InitialCatalog = database;
        return b.ConnectionString;
    }

    public static AppSettings Load()
    {
        try { if (File.Exists(PathFile)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFile)) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
