namespace OpenDBDiff.SqlServer.Schema.Connections
{
    /// <summary>
    /// How OpenDBDiff authenticates to SQL Server or Azure SQL.
    /// Windows and SQL Server are the original on-premises modes.
    /// The Active Directory modes are Microsoft Entra ID and require
    /// Microsoft.Data.SqlClient.Extensions.Azure.
    /// </summary>
    public enum SqlServerAuthenticationMode
    {
        Windows = 0,
        SqlServer = 1,
        ActiveDirectoryInteractive = 2,
        ActiveDirectoryIntegrated = 3,
        ActiveDirectoryPassword = 4,
        ActiveDirectoryDefault = 5
    }
}
