using Microsoft.Data.SqlClient;
using Microsoft.Identity.Client;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
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
        internal const string Marker = "interactive-browser-2";

        // SqlClient's first-party public client. The system browser uses a loopback redirect.
        private const string ClientId = "2fd908ad-0664-4344-b9be-cd3e8b574c38";
        private const string LoopbackRedirect = "http://localhost";
        internal static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(3);

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

        internal static bool IsNetFrameworkDescription(string description)
        {
            return description != null
                && description.IndexOf(".NET Framework", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static Task<SqlAuthenticationToken> AcquireAsync(
            SqlAuthenticationParameters parameters,
            ActiveDirectoryAuthenticationProvider driverProvider)
        {
            // Leave the caller's thread immediately. SqlClient opens the connection with a
            // blocking wait; if that wait is on the WinForms UI thread, an interactive prompt
            // that also needs that thread never appears.
            return Task.Run(() => AcquireAwayFromUiThreadAsync(parameters, driverProvider));
        }

        private static async Task<SqlAuthenticationToken> AcquireAwayFromUiThreadAsync(
            SqlAuthenticationParameters parameters,
            ActiveDirectoryAuthenticationProvider driverProvider)
        {
            bool systemBrowser = IsNetFrameworkDescription(RuntimeInformation.FrameworkDescription)
                || capturedHandle != IntPtr.Zero;
            if (!systemBrowser)
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

            lastFlow = "msal-system-browser";
            using (var timeout = new CancellationTokenSource(SignInTimeout))
            {
                try
                {
                    AuthenticationResult result = await AcquireWithMsalAsync(parameters, timeout.Token).ConfigureAwait(false);
                    return new SqlAuthenticationToken(result.AccessToken, result.ExpiresOn);
                }
                catch (Exception ex) when (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "Sign-in timed out after 3 minutes. Finish the sign-in in the browser, then click Test again.\r\n\r\n"
                        + Diagnostics(),
                        ex);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(ex.Message + "\r\n\r\n" + Diagnostics(), ex);
                }
            }
        }

        private static async Task<AuthenticationResult> AcquireWithMsalAsync(
            SqlAuthenticationParameters parameters,
            CancellationToken cancellation)
        {
            string authority = RewriteAuthority(parameters);
            IPublicClientApplication app = Applications.GetOrAdd(authority, CreateSystemBrowserApplication);
            string[] scopes = new[] { ScopeFor(parameters.Resource) };
            string loginHint = parameters.UserId;

            IAccount account = await FindAccountAsync(app, loginHint).ConfigureAwait(false);
            if (account != null)
            {
                try
                {
                    return await app.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellation).ConfigureAwait(false);
                }
                catch (MsalUiRequiredException)
                {
                }
            }

            // System browser. http://localhost is the loopback redirect MSAL listens on.
            // The embedded web view is not used: opening it on the blocked UI thread never appears.
            AcquireTokenInteractiveParameterBuilder interactive = app.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false);
            if (capturedHandle != IntPtr.Zero)
                interactive = interactive.WithParentActivityOrWindow(capturedHandle);
            if (!string.IsNullOrEmpty(loginHint))
                interactive = interactive.WithLoginHint(loginHint);
            return await interactive.ExecuteAsync(cancellation).ConfigureAwait(false);
        }

        private static IPublicClientApplication CreateSystemBrowserApplication(string authority)
        {
            // Do not enable the Windows account broker. It is what raises 0xwindow_handle_required.
            return PublicClientApplicationBuilder
                .Create(ClientId)
                .WithAuthority(authority)
                .WithRedirectUri(LoopbackRedirect)
                .Build();
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
