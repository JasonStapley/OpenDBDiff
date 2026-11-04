using Microsoft.Data.SqlClient;
using System;

namespace OpenDBDiff.SqlServer.Schema.Connections
{
    /// <summary>
    /// Builds and reads the connection strings the UI, the CLI and saved projects share.
    /// Windows and SQL Server authentication use the same builder calls the connection
    /// dialog used before Entra ID support, so those strings do not change.
    /// </summary>
    public sealed class SqlServerConnectionSettings
    {
        public string Server { get; set; }

        public string Database { get; set; }

        public SqlServerAuthenticationMode Authentication { get; set; }

        public string UserName { get; set; }

        public string Password { get; set; }

        /// <summary>
        /// Optional Entra tenant id or domain. Meaningful for the Active Directory modes.
        /// Leave empty to use the tenant the server publishes during login.
        /// </summary>
        public string TenantId { get; set; }

        public bool IsEntraAuthentication
        {
            get { return IsEntra(Authentication); }
        }

        public static bool IsEntra(SqlServerAuthenticationMode mode)
        {
            switch (mode)
            {
                case SqlServerAuthenticationMode.ActiveDirectoryInteractive:
                case SqlServerAuthenticationMode.ActiveDirectoryIntegrated:
                case SqlServerAuthenticationMode.ActiveDirectoryPassword:
                case SqlServerAuthenticationMode.ActiveDirectoryDefault:
                    return true;
                default:
                    return false;
            }
        }

        public string ToConnectionString()
        {
            return ToConnectionString(Database);
        }

        /// <param name="database">Catalog to open. Null or empty omits Initial Catalog, which is how the database list connects.</param>
        public string ToConnectionString(string database)
        {
            if (IsEntraAuthentication)
                return BuildEntra(Server, database, Authentication, UserName, Password, TenantId);

            if (Authentication == SqlServerAuthenticationMode.SqlServer)
                return BuildSqlAuthentication(Server, database, UserName, Password);

            return BuildWindowsAuthentication(Server, database);
        }

        public static SqlServerConnectionSettings Parse(string connectionString)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var settings = new SqlServerConnectionSettings();
            settings.Server = builder.DataSource;
            settings.Database = builder.InitialCatalog;
            settings.Authentication = MapAuthentication(builder);
            settings.UserName = builder.UserID ?? "";
            settings.Password = builder.Password ?? "";
            settings.TenantId = EntraConnectionStrings.ReadTenantMarker(builder.ApplicationName);
            if (settings.Authentication == SqlServerAuthenticationMode.Windows)
            {
                settings.UserName = "";
                settings.Password = "";
            }
            else if (settings.Authentication != SqlServerAuthenticationMode.SqlServer
                && settings.Authentication != SqlServerAuthenticationMode.ActiveDirectoryPassword)
            {
                settings.Password = "";
            }

            return settings;
        }

        /// <summary>
        /// Original Windows authentication string. Integrated Security and Trust Server Certificate
        /// are set explicitly; Encrypt is left at the driver default and is not written out.
        /// </summary>
        public static string BuildWindowsAuthentication(string server, string database)
        {
            var builder = new SqlConnectionStringBuilder();
            builder.DataSource = (server ?? "").Trim();
            if (!string.IsNullOrEmpty(database))
                builder.InitialCatalog = database.Trim();
            builder.IntegratedSecurity = true;
            builder.TrustServerCertificate = true;
            return builder.ConnectionString;
        }

        /// <summary>
        /// Original SQL authentication string. Starts from the Windows string, then turns
        /// integrated security off and sets the SQL login, matching the previous dialog.
        /// </summary>
        public static string BuildSqlAuthentication(string server, string database, string username, string password)
        {
            var builder = new SqlConnectionStringBuilder(BuildWindowsAuthentication(server, database));
            builder.IntegratedSecurity = false;
            builder.UserID = username;
            builder.Password = password;
            return builder.ConnectionString;
        }

        private static string BuildEntra(string server, string database, SqlServerAuthenticationMode mode, string username, string password, string tenantId)
        {
            var builder = new SqlConnectionStringBuilder();
            builder.DataSource = (server ?? "").Trim();
            if (!string.IsNullOrEmpty(database))
                builder.InitialCatalog = database.Trim();

            builder.Authentication = ToDriverMethod(mode);
            // Azure SQL rejects unencrypted connections. Certificate validation stays on
            // (TrustServerCertificate is left unset, so the driver default of false applies).
            builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
            builder.MultiSubnetFailover = true;

            // Active Directory Integrated on .NET Framework rejects a user name.
            // Interactive and Default treat it as an optional hint. Password requires it.
            if (mode != SqlServerAuthenticationMode.ActiveDirectoryIntegrated
                && !string.IsNullOrEmpty(username))
            {
                builder.UserID = username;
            }

            if (mode == SqlServerAuthenticationMode.ActiveDirectoryPassword)
                builder.Password = password ?? "";

            // Interactive sign-in and the Default credential chain both outlive the 15 second default.
            if (mode == SqlServerAuthenticationMode.ActiveDirectoryInteractive
                || mode == SqlServerAuthenticationMode.ActiveDirectoryDefault)
            {
                builder.ConnectTimeout = 120;
            }

            if (!string.IsNullOrWhiteSpace(tenantId))
                builder.ApplicationName = EntraConnectionStrings.TenantMarkerPrefix + tenantId.Trim();

            return builder.ConnectionString;
        }

        private static SqlServerAuthenticationMode MapAuthentication(SqlConnectionStringBuilder builder)
        {
            switch (builder.Authentication)
            {
                case SqlAuthenticationMethod.ActiveDirectoryInteractive:
                    return SqlServerAuthenticationMode.ActiveDirectoryInteractive;
                case SqlAuthenticationMethod.ActiveDirectoryIntegrated:
                    return SqlServerAuthenticationMode.ActiveDirectoryIntegrated;
#pragma warning disable CS0618 // Active Directory Password remains available; Microsoft marks the driver enum obsolete.
                case SqlAuthenticationMethod.ActiveDirectoryPassword:
                    return SqlServerAuthenticationMode.ActiveDirectoryPassword;
#pragma warning restore CS0618
                case SqlAuthenticationMethod.ActiveDirectoryDefault:
                    return SqlServerAuthenticationMode.ActiveDirectoryDefault;
                default:
                    if (builder.IntegratedSecurity)
                        return SqlServerAuthenticationMode.Windows;
                    return SqlServerAuthenticationMode.SqlServer;
            }
        }

        private static SqlAuthenticationMethod ToDriverMethod(SqlServerAuthenticationMode mode)
        {
            switch (mode)
            {
                case SqlServerAuthenticationMode.ActiveDirectoryInteractive:
                    return SqlAuthenticationMethod.ActiveDirectoryInteractive;
                case SqlServerAuthenticationMode.ActiveDirectoryIntegrated:
                    return SqlAuthenticationMethod.ActiveDirectoryIntegrated;
#pragma warning disable CS0618
                case SqlServerAuthenticationMode.ActiveDirectoryPassword:
                    return SqlAuthenticationMethod.ActiveDirectoryPassword;
#pragma warning restore CS0618
                case SqlServerAuthenticationMode.ActiveDirectoryDefault:
                    return SqlAuthenticationMethod.ActiveDirectoryDefault;
                default:
                    return SqlAuthenticationMethod.NotSpecified;
            }
        }
    }
}
