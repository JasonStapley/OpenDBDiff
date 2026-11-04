using Microsoft.Data.SqlClient;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace OpenDBDiff.SqlServer.Schema.Connections
{
    /// <summary>
    /// Opens SQL connections and, for Entra ID, applies an optional tenant hint.
    /// SqlClient has no tenant connection-string keyword. The server normally publishes
    /// its tenant in the login token. When the dialog supplies one, it is carried in
    /// Application Name as <c>OpenDBDiff|EntraTenant=...</c> and removed again here,
    /// before SQL Server can see it, and applied by rewriting the authority URL.
    /// </summary>
    public static class SqlConnections
    {
        public static SqlConnection Create(string connectionString)
        {
            return new SqlConnection(Prepare(connectionString));
        }

        public static string Prepare(string connectionString)
        {
            return EntraConnectionStrings.Prepare(connectionString);
        }
    }

    internal static class EntraConnectionStrings
    {
        internal const string TenantMarkerPrefix = "OpenDBDiff|EntraTenant=";

        internal static string ReadTenantMarker(string applicationName)
        {
            if (string.IsNullOrEmpty(applicationName))
                return "";
            if (!applicationName.StartsWith(TenantMarkerPrefix, StringComparison.Ordinal))
                return "";
            return applicationName.Substring(TenantMarkerPrefix.Length).Trim();
        }

        internal static string Prepare(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return connectionString;

            var builder = new SqlConnectionStringBuilder(connectionString);
            string tenant = ReadTenantMarker(builder.ApplicationName);
            if (string.IsNullOrEmpty(tenant))
            {
                // Leave Windows and SQL Server strings byte-for-byte unchanged.
                // An Entra string with no tenant clears any hint stored earlier for that server.
                if (IsEntra(builder.Authentication))
                    EntraTenantHints.Clear(builder.DataSource);
                return connectionString;
            }

            builder.Remove("Application Name");
            EntraTenantHints.Set(builder.DataSource, tenant);
            EntraAuthentication.EnsureTenantHintProvider();
            return builder.ConnectionString;
        }

        private static bool IsEntra(SqlAuthenticationMethod method)
        {
            switch (method)
            {
                case SqlAuthenticationMethod.ActiveDirectoryInteractive:
                case SqlAuthenticationMethod.ActiveDirectoryIntegrated:
                case SqlAuthenticationMethod.ActiveDirectoryDefault:
                    return true;
#pragma warning disable CS0618
                case SqlAuthenticationMethod.ActiveDirectoryPassword:
                    return true;
#pragma warning restore CS0618
                default:
                    return false;
            }
        }
    }

    internal static class EntraTenantHints
    {
        private static readonly ConcurrentDictionary<string, string> Hints =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static void Set(string server, string tenant)
        {
            string key = NormalizeServer(server);
            if (key.Length == 0 || string.IsNullOrWhiteSpace(tenant))
                return;
            Hints[key] = tenant.Trim();
        }

        internal static void Clear(string server)
        {
            string removed;
            Hints.TryRemove(NormalizeServer(server), out removed);
        }

        internal static bool TryGet(string server, out string tenant)
        {
            return Hints.TryGetValue(NormalizeServer(server), out tenant);
        }

        internal static string NormalizeServer(string server)
        {
            if (string.IsNullOrWhiteSpace(server))
                return "";
            server = server.Trim();
            if (server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
                server = server.Substring(4);
            int comma = server.IndexOf(',');
            if (comma >= 0)
                server = server.Substring(0, comma);
            return server.Trim().ToLowerInvariant();
        }
    }

    internal static class EntraAuthority
    {
        internal static string WithTenant(string authority, string tenant)
        {
            if (string.IsNullOrWhiteSpace(tenant))
                return authority;

            tenant = tenant.Trim().Trim('/');
            if (tenant.Length == 0)
                return authority;

            Uri uri;
            if (string.IsNullOrWhiteSpace(authority)
                || !Uri.TryCreate(authority, UriKind.Absolute, out uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return "https://login.microsoftonline.com/" + tenant;
            }

            string[] segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                segments = new[] { tenant };
            else
                segments[0] = tenant;

            var builder = new UriBuilder(uri);
            builder.Path = "/" + string.Join("/", segments);
            string absolute = builder.Uri.AbsoluteUri;
            if (absolute.EndsWith("/", StringComparison.Ordinal) && !authority.EndsWith("/", StringComparison.Ordinal))
                absolute = absolute.TrimEnd('/');
            return absolute;
        }
    }

    internal static class EntraAuthentication
    {
        private static int installed;

        internal static bool IsTenantHintProviderInstalled
        {
            get { return installed == 1; }
        }

        internal static void EnsureTenantHintProvider()
        {
            if (System.Threading.Interlocked.Exchange(ref installed, 1) == 1)
                return;

            var provider = new TenantHintAuthenticationProvider();
            foreach (SqlAuthenticationMethod method in Enum.GetValues(typeof(SqlAuthenticationMethod)))
            {
                if (provider.IsSupported(method))
                    SqlAuthenticationProvider.SetProvider(method, provider);
            }
        }
    }

    /// <summary>
    /// Forwards Entra token requests to the driver provider. When a tenant hint was
    /// supplied for the server, the authority URL's first path segment is replaced
    /// before the driver talks to Microsoft Entra.
    /// </summary>
    internal sealed class TenantHintAuthenticationProvider : SqlAuthenticationProvider
    {
        private readonly ActiveDirectoryAuthenticationProvider inner = new ActiveDirectoryAuthenticationProvider();

        public override bool IsSupported(SqlAuthenticationMethod authenticationMethod)
        {
            return inner.IsSupported(authenticationMethod);
        }

        public override Task<SqlAuthenticationToken> AcquireTokenAsync(SqlAuthenticationParameters parameters)
        {
            string tenant;
            if (parameters != null && EntraTenantHints.TryGet(parameters.ServerName, out tenant))
            {
                var rewritten = new SqlAuthenticationParameters(
                    parameters.AuthenticationMethod,
                    parameters.ServerName,
                    parameters.DatabaseName,
                    parameters.Resource,
                    EntraAuthority.WithTenant(parameters.Authority, tenant),
                    parameters.UserId,
                    parameters.Password,
                    parameters.ConnectionId,
                    parameters.ConnectionTimeout);
                return inner.AcquireTokenAsync(rewritten);
            }

            return inner.AcquireTokenAsync(parameters);
        }
    }
}
