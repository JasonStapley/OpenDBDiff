using OpenDBDiff.SqlServer.Schema.Model;
using System.Text;

namespace OpenDBDiff.SqlServer.Schema.Generates.SQLCommands
{
    internal static class UserSQLCommand
    {
        public static string Get(DatabaseInfo.SQLServerVersion version, DatabaseInfo.SQLServerEdition edition)
        {
            switch (version)
            {
                case DatabaseInfo.SQLServerVersion.SQLServer2000:
                case DatabaseInfo.SQLServerVersion.SQLServer2005:
                case DatabaseInfo.SQLServerVersion.SQLServer2008:
                case DatabaseInfo.SQLServerVersion.SQLServer2008R2:
                    return Get2008();

                case DatabaseInfo.SQLServerVersion.SQLServerAzure10:
                    return GetAzure();

                default:
                    if (edition == DatabaseInfo.SQLServerEdition.Azure)
                        return GetAzure();
                    if (edition == DatabaseInfo.SQLServerEdition.AzureManagedInstance)
                        return GetManagedInstance();
                    return Get2008();
            }
        }

        private static string Get2008()
        {
            var sql = new StringBuilder();
            sql.AppendLine("SELECT is_fixed_role, type, ISNULL(suser_sname(sid),'') AS Login,Name,principal_id, ISNULL(default_schema_name,'') AS default_schema_name ");
            sql.AppendLine("FROM sys.database_principals ");
            sql.AppendLine("WHERE type IN ('S','U','A','R') ");
            sql.AppendLine("ORDER BY Name");
            return sql.ToString();
        }

        private static string GetAzure()
        {
            var sql = new StringBuilder();
            //to get LoginName in Azure (asside for the current login) you would have to link to master and query sys.sysusers or sys.sql_users
            //the CASE test below will at least get you the Current login
            // E = EXTERNAL_USER, X = EXTERNAL_GROUP (CREATE USER ... FROM EXTERNAL PROVIDER).
            // Other logins stay blank unless they are the current login; suser_sname(sid) cannot see them.
            sql.AppendLine("SELECT is_fixed_role, type, CASE WHEN type IN ('E','X') THEN '' WHEN suser_sid()=sid THEN suser_sname() ELSE '' END  AS Login,Name,principal_id, ISNULL(default_schema_name,'') AS default_schema_name ");
            sql.AppendLine("FROM sys.database_principals ");
            sql.AppendLine("WHERE type IN ('S','U','A','R','E','X') ");
            sql.AppendLine("ORDER BY Name");
            return sql.ToString();
        }

        private static string GetManagedInstance()
        {
            var sql = new StringBuilder();
            // Managed Instance resolves SQL logins like on-premises SQL Server.
            // Entra principals still have no server login, so their Login column stays empty
            // and the user is scripted FROM EXTERNAL PROVIDER.
            sql.AppendLine("SELECT is_fixed_role, type, CASE WHEN type IN ('E','X') THEN '' ELSE ISNULL(suser_sname(sid),'') END AS Login,Name,principal_id, ISNULL(default_schema_name,'') AS default_schema_name ");
            sql.AppendLine("FROM sys.database_principals ");
            sql.AppendLine("WHERE type IN ('S','U','A','R','E','X') ");
            sql.AppendLine("ORDER BY Name");
            return sql.ToString();
        }
    }
}
