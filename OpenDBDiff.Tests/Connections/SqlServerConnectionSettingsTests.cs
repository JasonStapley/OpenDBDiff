using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenDBDiff.SqlServer.Schema.Connections;

namespace OpenDBDiff.Tests.Connections
{
    [TestClass]
    public class SqlServerConnectionSettingsTests
    {
        [TestMethod]
        public void WindowsAuthentication_MatchesTheOriginalConnectionString()
        {
            string connectionString = SqlServerConnectionSettings.BuildWindowsAuthentication("(local)", "AdventureWorks");

            Assert.AreEqual(
                "Data Source=(local);Initial Catalog=AdventureWorks;Integrated Security=True;Trust Server Certificate=True",
                connectionString);
            Assert.IsFalse(connectionString.Contains("Authentication="));
            Assert.IsFalse(connectionString.Contains("Encrypt="));
        }

        [TestMethod]
        public void WindowsAuthentication_OmitsTheCatalogWhenTheDatabaseIsBlank()
        {
            Assert.AreEqual(
                "Data Source=srv;Integrated Security=True;Trust Server Certificate=True",
                SqlServerConnectionSettings.BuildWindowsAuthentication(" srv ", null));
            Assert.AreEqual(
                "Data Source=srv;Integrated Security=True;Trust Server Certificate=True",
                SqlServerConnectionSettings.BuildWindowsAuthentication("srv", ""));
        }

        [TestMethod]
        public void SqlAuthentication_MatchesTheOriginalConnectionString()
        {
            Assert.AreEqual(
                "Data Source=(local);Initial Catalog=AdventureWorks;Integrated Security=False;User ID=sa;Password=p@ss;Trust Server Certificate=True",
                SqlServerConnectionSettings.BuildSqlAuthentication("(local)", "AdventureWorks", "sa", "p@ss"));
            Assert.AreEqual(
                "Data Source=(local);Integrated Security=False;User ID=domain\\user;Password=\"a;b\";Trust Server Certificate=True",
                SqlServerConnectionSettings.BuildSqlAuthentication("(local)", null, "domain\\user", "a;b"));
        }

        [TestMethod]
        public void WindowsAndSqlStrings_RoundTripWithoutChangingMode()
        {
            string windows = SqlServerConnectionSettings.BuildWindowsAuthentication("(local)", "AdventureWorks");
            SqlServerConnectionSettings parsedWindows = SqlServerConnectionSettings.Parse(windows);
            Assert.AreEqual(SqlServerAuthenticationMode.Windows, parsedWindows.Authentication);
            Assert.AreEqual("", parsedWindows.UserName);
            Assert.AreEqual("", parsedWindows.Password);
            Assert.AreEqual("", parsedWindows.TenantId);
            Assert.AreEqual(windows, parsedWindows.ToConnectionString());
            Assert.AreEqual(windows, SqlConnections.Prepare(windows));

            string sql = SqlServerConnectionSettings.BuildSqlAuthentication("sqlhost", "app", "sa", "");
            SqlServerConnectionSettings parsedSql = SqlServerConnectionSettings.Parse(sql);
            Assert.AreEqual(SqlServerAuthenticationMode.SqlServer, parsedSql.Authentication);
            Assert.AreEqual("sa", parsedSql.UserName);
            Assert.AreEqual(sql, parsedSql.ToConnectionString());
            Assert.AreEqual(sql, SqlConnections.Prepare(sql));
        }

        [TestMethod]
        public void SpacedEntraKeyword_FromMicrosoftDocumentation_ParsesAsInteractive()
        {
            SqlServerConnectionSettings settings = SqlServerConnectionSettings.Parse(
                "Server=tcp:contoso.database.windows.net,1433;Database=appdb;Authentication=Active Directory Interactive;User ID=user@contoso.com;Encrypt=True");

            Assert.AreEqual(SqlServerAuthenticationMode.ActiveDirectoryInteractive, settings.Authentication);
            Assert.AreEqual("user@contoso.com", settings.UserName);
            Assert.AreEqual("appdb", settings.Database);
            Assert.AreEqual("", settings.Password);
        }

        [TestMethod]
        public void SqlPasswordKeyword_StaysSqlAuthentication()
        {
            SqlServerConnectionSettings settings = SqlServerConnectionSettings.Parse(
                "Data Source=sqlhost;Initial Catalog=app;User ID=sa;Password=secret;Authentication=Sql Password");

            Assert.AreEqual(SqlServerAuthenticationMode.SqlServer, settings.Authentication);
            Assert.AreEqual("sa", settings.UserName);
            Assert.AreEqual("secret", settings.Password);
        }

        [TestMethod]
        public void Interactive_BuildsAnEntraStringAndKeepsTheTenantOutOfTheDriverString()
        {
            var settings = new SqlServerConnectionSettings
            {
                Server = "contoso.database.windows.net",
                Database = "appdb",
                Authentication = SqlServerAuthenticationMode.ActiveDirectoryInteractive,
                UserName = "user@contoso.com",
                Password = "must-not-be-sent",
                TenantId = "contoso.onmicrosoft.com"
            };

            string stored = settings.ToConnectionString();
            Assert.IsTrue(stored.Contains("Authentication=ActiveDirectoryInteractive"));
            Assert.IsTrue(stored.Contains("User ID=user@contoso.com"));
            Assert.IsTrue(stored.Contains("Encrypt=True"));
            Assert.IsTrue(stored.Contains("Connect Timeout=120"));
            Assert.IsTrue(stored.Contains("Multi Subnet Failover=True"));
            Assert.IsFalse(stored.Contains("Password="));
            Assert.IsFalse(stored.Contains("Integrated Security=True"));
            Assert.IsFalse(stored.Contains("Trust Server Certificate=True"));
            Assert.IsTrue(stored.Contains("OpenDBDiff|EntraTenant=contoso.onmicrosoft.com"));

            string driverString = SqlConnections.Prepare(stored);
            Assert.IsFalse(driverString.Contains("EntraTenant"));
            Assert.IsFalse(driverString.Contains("Application Name"));
            Assert.IsTrue(driverString.Contains("Authentication=ActiveDirectoryInteractive"));

            var withoutTenant = new SqlServerConnectionSettings
            {
                Server = settings.Server,
                Database = settings.Database,
                Authentication = settings.Authentication,
                UserName = settings.UserName
            };
            Assert.AreEqual(withoutTenant.ToConnectionString(), driverString);

            string tenant;
            Assert.IsTrue(EntraTenantHints.TryGet("tcp:contoso.database.windows.net,1433", out tenant));
            Assert.AreEqual("contoso.onmicrosoft.com", tenant);
            Assert.IsTrue(EntraAuthentication.IsTenantHintProviderInstalled);

            SqlServerConnectionSettings roundTrip = SqlServerConnectionSettings.Parse(stored);
            Assert.AreEqual(SqlServerAuthenticationMode.ActiveDirectoryInteractive, roundTrip.Authentication);
            Assert.AreEqual("user@contoso.com", roundTrip.UserName);
            Assert.AreEqual("contoso.onmicrosoft.com", roundTrip.TenantId);
            Assert.AreEqual("", roundTrip.Password);
        }

        [TestMethod]
        public void Integrated_DoesNotSendAUserName()
        {
            var settings = new SqlServerConnectionSettings
            {
                Server = "contoso.database.windows.net",
                Database = "appdb",
                Authentication = SqlServerAuthenticationMode.ActiveDirectoryIntegrated,
                UserName = "user@contoso.com",
                Password = "secret"
            };

            string connectionString = settings.ToConnectionString();
            Assert.IsTrue(connectionString.Contains("Authentication=ActiveDirectoryIntegrated"));
            Assert.IsFalse(connectionString.Contains("User ID="));
            Assert.IsFalse(connectionString.Contains("Password="));
            Assert.IsFalse(connectionString.Contains("Connect Timeout="));
        }

        [TestMethod]
        public void PasswordMode_SendsTheEntraUserAndPassword()
        {
            var settings = new SqlServerConnectionSettings
            {
                Server = "contoso.database.windows.net",
                Database = "appdb",
                Authentication = SqlServerAuthenticationMode.ActiveDirectoryPassword,
                UserName = "user@contoso.com",
                Password = "secret"
            };

            string connectionString = settings.ToConnectionString();
            Assert.IsTrue(connectionString.Contains("Authentication=ActiveDirectoryPassword"));
            Assert.IsTrue(connectionString.Contains("User ID=user@contoso.com"));
            Assert.IsTrue(connectionString.Contains("Password=secret"));
            Assert.IsFalse(connectionString.Contains("Integrated Security=True"));

            SqlServerConnectionSettings parsed = SqlServerConnectionSettings.Parse(connectionString);
            Assert.AreEqual(SqlServerAuthenticationMode.ActiveDirectoryPassword, parsed.Authentication);
            Assert.AreEqual("secret", parsed.Password);
        }

        [TestMethod]
        public void DefaultMode_IncludesOptionalUserHintAndALongerTimeout()
        {
            var settings = new SqlServerConnectionSettings
            {
                Server = "contoso.database.windows.net",
                Database = "appdb",
                Authentication = SqlServerAuthenticationMode.ActiveDirectoryDefault,
                UserName = "client-id"
            };

            string connectionString = settings.ToConnectionString();
            Assert.IsTrue(connectionString.Contains("Authentication=ActiveDirectoryDefault"));
            Assert.IsTrue(connectionString.Contains("User ID=client-id"));
            Assert.IsTrue(connectionString.Contains("Connect Timeout=120"));
            Assert.IsFalse(connectionString.Contains("Password="));
        }

        [TestMethod]
        public void ClearingTheTenant_DropsTheHintForThatServerOnly()
        {
            var withTenant = new SqlServerConnectionSettings
            {
                Server = "cleared.database.windows.net",
                Database = "appdb",
                Authentication = SqlServerAuthenticationMode.ActiveDirectoryInteractive,
                TenantId = "tenant-a"
            };
            SqlConnections.Prepare(withTenant.ToConnectionString());

            var other = new SqlServerConnectionSettings
            {
                Server = "other.database.windows.net",
                Database = "appdb",
                Authentication = SqlServerAuthenticationMode.ActiveDirectoryInteractive,
                TenantId = "tenant-b"
            };
            SqlConnections.Prepare(other.ToConnectionString());

            withTenant.TenantId = "";
            SqlConnections.Prepare(withTenant.ToConnectionString());

            string tenant;
            Assert.IsFalse(EntraTenantHints.TryGet("cleared.database.windows.net", out tenant));
            Assert.IsTrue(EntraTenantHints.TryGet("other.database.windows.net", out tenant));
            Assert.AreEqual("tenant-b", tenant);
        }

        [TestMethod]
        public void Authority_ReplacesOnlyTheTenantSegment()
        {
            Assert.AreEqual(
                "https://login.microsoftonline.com/contoso.onmicrosoft.com",
                EntraAuthority.WithTenant("https://login.microsoftonline.com/common", "contoso.onmicrosoft.com"));
            Assert.AreEqual(
                "https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/oauth2/authorize",
                EntraAuthority.WithTenant(
                    "https://login.microsoftonline.com/common/oauth2/authorize",
                    "11111111-2222-3333-4444-555555555555"));
            Assert.AreEqual(
                "https://login.microsoftonline.com/contoso.onmicrosoft.com",
                EntraAuthority.WithTenant(null, " /contoso.onmicrosoft.com/ "));
            Assert.AreEqual(
                "https://login.microsoftonline.com/common",
                EntraAuthority.WithTenant("https://login.microsoftonline.com/common", "  "));
        }
    }
}
