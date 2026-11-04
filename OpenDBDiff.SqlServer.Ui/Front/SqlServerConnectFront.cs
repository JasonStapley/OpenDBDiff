using Microsoft.Data.SqlClient;
using OpenDBDiff.Abstractions.Ui;
using OpenDBDiff.SqlServer.Schema.Connections;
using OpenDBDiff.SqlServer.Ui.Util;
using System;
using System.Windows.Forms;

namespace OpenDBDiff.SqlServer.Ui
{
    public partial class SqlServerConnectFront : UserControl, IFront
    {
        private static readonly SqlServerAuthenticationMode[] AuthenticationModes =
        {
            SqlServerAuthenticationMode.Windows,
            SqlServerAuthenticationMode.SqlServer,
            SqlServerAuthenticationMode.ActiveDirectoryInteractive,
            SqlServerAuthenticationMode.ActiveDirectoryIntegrated,
            SqlServerAuthenticationMode.ActiveDirectoryPassword,
            SqlServerAuthenticationMode.ActiveDirectoryDefault
        };

        private Boolean isDatabaseFilled = false;
        private Boolean isServerFilled = false;
        private ToolTip authenticationToolTip;

        private delegate void clearCombo();

        private delegate void addCombo(string item);

        public SqlServerConnectFront()
        {
            InitializeComponent();
            cboAuthentication.Items.Clear();
            cboAuthentication.Items.AddRange(new object[]
            {
                "Windows Authentication",
                "SQL Server Authentication",
                "Azure DB",
                "Active Directory Integrated",
                "Active Directory Password",
                "Active Directory Default"
            });
            authenticationToolTip = new ToolTip();
            authenticationToolTip.SetToolTip(cboAuthentication,
                "Windows and SQL Server authentication are unchanged.\r\n" +
                "Azure DB signs in through the browser and supports MFA (Active Directory Interactive).\r\n" +
                "Active Directory Integrated uses the domain account (not the same as Windows authentication).\r\n" +
                "Active Directory Password is the deprecated Entra username and password flow.\r\n" +
                "Active Directory Default uses Azure CLI, Visual Studio, environment or managed identity credentials.");
            authenticationToolTip.SetToolTip(txtUsername,
                "SQL login, or a Microsoft Entra user such as user@contoso.com.\r\n" +
                "For Azure DB this is an optional sign-in hint, not a password.");
            authenticationToolTip.SetToolTip(txtTenantId,
                "Optional Entra tenant id or domain, for example contoso.onmicrosoft.com.\r\n" +
                "Leave this blank to use the tenant the server publishes. It applies to the Active Directory modes.");
            authenticationToolTip.SetToolTip(txtPassword,
                "SQL Server password, or the Entra password for Active Directory Password.\r\n" +
                "Interactive sign-in does not use this box.");
            // Index 1 is SQL Server authentication, the same initial selection as before.
            // The project handler then selects Windows authentication for a new comparison.
            cboAuthentication.SelectedIndex = 1;
        }

        private void cboAuthentication_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateAuthenticationFields();
            isDatabaseFilled = false;
            ClearDatabase();
        }

        private void UpdateAuthenticationFields()
        {
            SqlServerAuthenticationMode mode = AuthenticationMode;
            bool sqlLogin = mode == SqlServerAuthenticationMode.SqlServer;
            bool entraPassword = mode == SqlServerAuthenticationMode.ActiveDirectoryPassword;
            bool interactive = mode == SqlServerAuthenticationMode.ActiveDirectoryInteractive;
            bool adDefault = mode == SqlServerAuthenticationMode.ActiveDirectoryDefault;
            txtUsername.Enabled = sqlLogin || entraPassword || interactive || adDefault;
            txtPassword.Enabled = sqlLogin || entraPassword;
            txtTenantId.Enabled = SqlServerConnectionSettings.IsEntra(mode);
        }

        public string ErrorConnection { get; private set; }

        public SqlServerAuthenticationMode AuthenticationMode
        {
            get
            {
                int index = cboAuthentication.SelectedIndex;
                if (index < 0 || index >= AuthenticationModes.Length)
                    return SqlServerAuthenticationMode.SqlServer;
                return AuthenticationModes[index];
            }
            set
            {
                int index = 1;
                for (int i = 0; i < AuthenticationModes.Length; i++)
                {
                    if (AuthenticationModes[i] == value)
                    {
                        index = i;
                        break;
                    }
                }

                if (cboAuthentication.SelectedIndex != index)
                    cboAuthentication.SelectedIndex = index;
                else
                    UpdateAuthenticationFields();
            }
        }

        public bool UseWindowsAuthentication
        {
            get
            {
                return AuthenticationMode == SqlServerAuthenticationMode.Windows;
            }
            set
            {
                AuthenticationMode = value
                    ? SqlServerAuthenticationMode.Windows
                    : SqlServerAuthenticationMode.SqlServer;
            }
        }

        public string DatabaseName
        {
            get { return cboDatabase.Text; }
            set { cboDatabase.Text = value; }
        }

        public string UserName
        {
            get { return txtUsername.Text; }
            set { txtUsername.Text = value; }
        }

        public string Password
        {
            get { return txtPassword.Text; }
            set { txtPassword.Text = value; }
        }

        public string TenantId
        {
            get { return txtTenantId.Text; }
            set { txtTenantId.Text = value; }
        }

        public override string Text
        {
            get { return lblName.Text; }
            set { lblName.Text = value; }
        }

        public string ServerName
        {
            get { return cboServer.Text; }
            set { cboServer.Text = value; }
        }

        public Boolean TestConnection()
        {
            try
            {
                // The Test button runs on the UI thread. Capture the owner handle here,
                // before Open, so interactive sign-in does not have to query WinForms later.
                if (SqlServerConnectionSettings.IsEntra(AuthenticationMode))
                    RememberSignInWindow("Test button");

                using (SqlConnection connection = SqlConnections.Create(this.ConnectionString))
                {
                    connection.Open();
                    connection.Close();
                    return true;
                }
            }
            catch (Exception ex)
            {
                ErrorConnection = ex.Message;
                if (SqlServerConnectionSettings.IsEntra(AuthenticationMode)
                    && (ErrorConnection == null || ErrorConnection.IndexOf(InteractiveSignInMarker, StringComparison.Ordinal) < 0))
                {
                    ErrorConnection += "\r\n\r\n" + SqlConnections.InteractiveDiagnostics();
                }
                return false;
            }
        }

        private const string InteractiveSignInMarker = "interactive-browser-1";

        private void RememberSignInWindow(string source)
        {
            IntPtr handle = IntPtr.Zero;
            Form owner = FindForm();
            if (owner != null && !owner.IsDisposed && owner.IsHandleCreated)
                handle = owner.Handle;
            else if (IsHandleCreated)
                handle = Handle;
            SqlConnections.CaptureInteractiveOwner(handle, source);
        }

        private SqlServerConnectionSettings CurrentSettings(string database)
        {
            return new SqlServerConnectionSettings
            {
                Server = cboServer.Text,
                Database = database,
                Authentication = AuthenticationMode,
                UserName = txtUsername.Text,
                Password = txtPassword.Text,
                TenantId = txtTenantId.Text
            };
        }

        public string ConnectionStringToDefaultDatabase
        {
            get { return CurrentSettings(null).ToConnectionString(); }
        }

        public string ConnectionStringToMasterDatabase
        {
            get { return CurrentSettings("master").ToConnectionString(); }
        }

        public string ConnectionString
        {
            get { return CurrentSettings(cboDatabase.Text).ToConnectionString(); }
            set
            {
                if (!String.IsNullOrWhiteSpace(value))
                {
                    SqlServerConnectionSettings settings = SqlServerConnectionSettings.Parse(value);
                    ServerName = settings.Server;
                    AuthenticationMode = settings.Authentication;
                    UserName = settings.UserName ?? "";
                    Password = settings.Password ?? "";
                    TenantId = settings.TenantId ?? "";
                    DatabaseName = settings.Database ?? "";
                }
                else
                {
                    cboAuthentication.SelectedIndex = 1;
                    UserName = "";
                    Password = "";
                    TenantId = "";
                    ServerName = "(local)";
                    DatabaseName = "";
                }
            }
        }

        public Control Control
        {
            get
            {
                return this;
            }
        }

        private void btnTest_Click(object sender, EventArgs e)
        {
            if (TestConnection())
                MessageBox.Show(this, "Test successful!", "Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(this, "Test failed!\r\n" + ErrorConnection + EntraFailureHint(), "Test", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private string EntraFailureHint()
        {
            if (!SqlServerConnectionSettings.IsEntra(AuthenticationMode))
                return "";
            return "\r\n\r\nFor Azure SQL, type the database name if the list stays empty. " +
                "A contained Entra user often cannot open master, and sys.databases in a user database returns only that database. " +
                "Azure DB opens a browser window; the connection waits two minutes so MFA can finish.";
        }

        private void AddComboItem(string item)
        {
            if (!InvokeRequired)
                cboDatabase.Items.Add(item);
            else
            {
                addCombo add = new addCombo(AddComboItem);
                Invoke(add, new string[] { item });
            }
        }

        private void ClearDatabase()
        {
            if (!InvokeRequired)
                cboDatabase.Items.Clear();
            else
            {
                clearCombo clear = new clearCombo(ClearDatabase);
                Invoke(clear);
            }
        }

        private void FillDatabase()
        {
            if (!isDatabaseFilled)
            {
                String connectionString = ConnectionStringToDefaultDatabase;
                ClearDatabase();
                if (SqlServerConnectionSettings.IsEntra(AuthenticationMode))
                    RememberSignInWindow("Database list");
                using (SqlConnection conn = SqlConnections.Create(connectionString))
                {
                    conn.Open();
                    using (SqlCommand command = new SqlCommand("SELECT name,database_id FROM sys.databases ORDER BY Name", conn))
                    {
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                AddComboItem(reader["Name"].ToString());
                            }
                            isDatabaseFilled = true;
                        }
                    }
                }
            }
        }

        private void cboServer_SelectedIndexChanged(object sender, EventArgs e)
        {
            isDatabaseFilled = false;
        }

        private void txtUsername_TextChanged(object sender, EventArgs e)
        {
            isDatabaseFilled = false;
            ClearDatabase();
        }

        private void txtPassword_TextChanged(object sender, EventArgs e)
        {
            isDatabaseFilled = false;
            ClearDatabase();
        }

        private void txtTenantId_TextChanged(object sender, EventArgs e)
        {
            isDatabaseFilled = false;
            ClearDatabase();
        }

        private void cboServer_DropDown(object sender, EventArgs e)
        {
            try
            {
                if (!isServerFilled)
                {
                    this.Cursor = Cursors.WaitCursor;
                    SqlServerList.Get().ForEach(item => cboServer.Items.Add(item));
                    isServerFilled = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "ERROR", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void cboDatabase_DropDown(object sender, EventArgs e)
        {
            try
            {
                this.Cursor = Cursors.WaitCursor;
                FillDatabase();
            }
            catch (Exception ex)
            {
                this.Cursor = Cursors.Default;
                cboDatabase.Items.Clear();
                MessageBox.Show(this, ex.Message + EntraFailureHint(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }

        private void cboServer_TextChanged(object sender, EventArgs e)
        {
            isDatabaseFilled = false;
        }

        public override string ToString()
        {
            return string.Format($"Server: {ServerName}, Database: {DatabaseName}");
        }

        public object Clone()
        {
            var clone = new SqlServerConnectFront
            {
                ServerName = this.ServerName,
                AuthenticationMode = this.AuthenticationMode,
                UserName = this.UserName,
                Password = this.Password,
                TenantId = this.TenantId,
                DatabaseName = this.DatabaseName
            };

            clone.Location = new System.Drawing.Point(1, 1);
            clone.Name = "SourceControl";
            clone.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            return clone;
        }

        public void SetSettingsFrom(IFront other)
        {
            if (other is SqlServerConnectFront sql)
            {
                this.ServerName = sql.ServerName;
                this.DatabaseName = sql.DatabaseName;
                this.AuthenticationMode = sql.AuthenticationMode;
                this.UserName = sql.UserName;
                this.Password = sql.Password;
                this.TenantId = sql.TenantId;
            }
        }
    }
}
