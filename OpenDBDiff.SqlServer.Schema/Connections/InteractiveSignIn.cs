using Microsoft.Data.SqlClient;
using Microsoft.Identity.Client;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace OpenDBDiff.SqlServer.Schema.Connections
{
    /// <summary>
    /// Active Directory Interactive sign-in that does not use the Windows account broker.
    /// The broker (WAM) throws 0xwindow_handle_required unless it is given a parent window,
    /// and SqlClient forces the broker on for its built-in client id. That failed on the
    /// net48 x86 WinForms GUI, which has no console window for the driver to discover.
    /// A handle captured on the UI thread is passed to MSAL's own prompt instead.
    /// </summary>
    internal static class InteractiveSignIn
    {
        internal const string Marker = "interactive-browser-1";

        // SqlClient's first-party public client. Its registered redirect is the native-client URI.
        private const string ClientId = "2fd908ad-0664-4344-b9be-cd3e8b574c38";
        private const string NativeRedirect = "https://login.microsoftonline.com/common/oauth2/nativeclient";

        private static readonly ConcurrentDictionary<string, IPublicClientApplication> Applications =
            new ConcurrentDictionary<string, IPublicClientApplication>(StringComparer.OrdinalIgnoreCase);

        private static IntPtr capturedHandle;
        private static string capturedFrom = "none";
        private static string lastFlow = "not-reached";
        private static string lastRegisterDetail = "not-called";

        internal static void CaptureHandle(IntPtr handle, string source)
        {
            if (handle == IntPtr.Zero)
                return;
            capturedHandle = handle;
            capturedFrom = string.IsNullOrEmpty(source) ? "unspecified" : source;
        }

        internal static IntPtr CapturedHandle
        {
            get { return capturedHandle; }
        }

        internal static void NoteRegistration(bool replaced, string registeredTypeName)
        {
            lastRegisterDetail = (replaced ? "replaced=true" : "replaced=false")
                + " registered=" + (string.IsNullOrEmpty(registeredTypeName) ? "null" : registeredTypeName);
        }

        internal static string Diagnostics()
        {
            string version = "unknown";
            try
            {
                var attribute = typeof(InteractiveSignIn).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (attribute != null && !string.IsNullOrEmpty(attribute.InformationalVersion))
                    version = attribute.InformationalVersion;
            }
            catch (InvalidOperationException)
            {
            }

            int bits = IntPtr.Size * 8;
            string handleText = capturedHandle == IntPtr.Zero
                ? "none"
                : "0x" + capturedHandle.ToInt64().ToString("X");

            return "OpenDBDiff " + Marker
                + " version=" + version
                + " process=" + bits + "-bit"
                + " " + lastRegisterDetail
                + " handle=" + handleText
                + " handleFrom=" + capturedFrom
                + " flow=" + lastFlow;
        }

        internal static async Task<SqlAuthenticationToken> AcquireAsync(
            SqlAuthenticationParameters parameters,
            ActiveDirectoryAuthenticationProvider driverProvider)
        {
            IntPtr handle = capturedHandle;
            if (handle == IntPtr.Zero)
            {
                lastFlow = "driver-console";
                try
                {
                    return await driverProvider.AcquireTokenAsync(Rewrite(parameters)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(ex.Message + "\r\n\r\n" + Diagnostics(), ex);
                }
            }

            try
            {
                lastFlow = "msal-embedded";
                AuthenticationResult result = await AcquireWithMsalAsync(parameters, handle, true).ConfigureAwait(false);
                return new SqlAuthenticationToken(result.AccessToken, result.ExpiresOn);
            }
            catch (Exception embeddedFailure)
            {
                if (!ShouldRetryWithSystemBrowser(embeddedFailure))
                    throw new InvalidOperationException(embeddedFailure.Message + "\r\n\r\n" + Diagnostics(), embeddedFailure);

                try
                {
                    lastFlow = "msal-system-browser";
                    AuthenticationResult result = await AcquireWithMsalAsync(parameters, handle, false).ConfigureAwait(false);
                    return new SqlAuthenticationToken(result.AccessToken, result.ExpiresOn);
                }
                catch (Exception browserFailure)
                {
                    throw new InvalidOperationException(
                        embeddedFailure.Message
                        + "\r\nSystem-browser retry: " + browserFailure.Message
                        + "\r\n\r\n" + Diagnostics(),
                        browserFailure);
                }
            }
        }

        private static bool ShouldRetryWithSystemBrowser(Exception failure)
        {
            string text = failure == null ? "" : failure.ToString();
            return text.IndexOf("WebView", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("window_handle", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("broker", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("WAM", StringComparison.Ordinal) >= 0;
        }

        private static async Task<AuthenticationResult> AcquireWithMsalAsync(
            SqlAuthenticationParameters parameters,
            IntPtr handle,
            bool embeddedWebView)
        {
            string authority = RewriteAuthority(parameters);
            string cacheKey = authority + (embeddedWebView ? "|embedded" : "|browser");
            IPublicClientApplication app = Applications.GetOrAdd(cacheKey, delegate (string key) { return CreateApplication(authority, embeddedWebView); });
            string[] scopes = new[] { ScopeFor(parameters.Resource) };
            string loginHint = parameters.UserId;

            IAccount account = await FindAccountAsync(app, loginHint).ConfigureAwait(false);
            if (account != null)
            {
                try
                {
                    return await app.AcquireTokenSilent(scopes, account).ExecuteAsync().ConfigureAwait(false);
                }
                catch (MsalUiRequiredException)
                {
                }
            }

            AcquireTokenInteractiveParameterBuilder interactive = app.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(embeddedWebView)
                .WithParentActivityOrWindow(handle);
            if (!string.IsNullOrEmpty(loginHint))
                interactive = interactive.WithLoginHint(loginHint);
            return await interactive.ExecuteAsync().ConfigureAwait(false);
        }

        private static IPublicClientApplication CreateApplication(string authority, bool embeddedWebView)
        {
            // Do not call WithBroker. The Windows account broker is opt-in, and it is what
            // raises 0xwindow_handle_required when no parent window is configured.
            PublicClientApplicationBuilder builder = PublicClientApplicationBuilder
                .Create(ClientId)
                .WithAuthority(authority);
            if (embeddedWebView)
                builder = builder.WithRedirectUri(NativeRedirect);
            return builder.Build();
        }

        private static async Task<IAccount> FindAccountAsync(IPublicClientApplication app, string loginHint)
        {
            IAccount first = null;
            foreach (IAccount account in await app.GetAccountsAsync().ConfigureAwait(false))
            {
                if (first == null)
                    first = account;
                if (!string.IsNullOrEmpty(loginHint)
                    && string.Equals(account.Username, loginHint, StringComparison.OrdinalIgnoreCase))
                {
                    return account;
                }
            }
            if (string.IsNullOrEmpty(loginHint))
                return first;
            return null;
        }

        private static string ScopeFor(string resource)
        {
            if (string.IsNullOrEmpty(resource))
                return "https://database.windows.net/.default";
            resource = resource.Trim();
            if (resource.EndsWith("/.default", StringComparison.OrdinalIgnoreCase))
                return resource;
            return resource.TrimEnd('/') + "/.default";
        }

        private static SqlAuthenticationParameters Rewrite(SqlAuthenticationParameters parameters)
        {
            string tenant;
            if (parameters == null || !EntraTenantHints.TryGet(parameters.ServerName, out tenant))
                return parameters;
            return new SqlAuthenticationParameters(
                parameters.AuthenticationMethod,
                parameters.ServerName,
                parameters.DatabaseName,
                parameters.Resource,
                EntraAuthority.WithTenant(parameters.Authority, tenant),
                parameters.UserId,
                parameters.Password,
                parameters.ConnectionId,
                parameters.ConnectionTimeout);
        }

        private static string RewriteAuthority(SqlAuthenticationParameters parameters)
        {
            string authority = parameters.Authority;
            string tenant;
            if (EntraTenantHints.TryGet(parameters.ServerName, out tenant))
                authority = EntraAuthority.WithTenant(authority, tenant);
            if (string.IsNullOrEmpty(authority))
                authority = "https://login.microsoftonline.com/organizations";
            return authority;
        }
    }
}
