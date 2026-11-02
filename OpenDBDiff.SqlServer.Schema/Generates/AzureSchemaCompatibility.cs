using Microsoft.Data.SqlClient;
using OpenDBDiff.SqlServer.Schema.Model;

namespace OpenDBDiff.SqlServer.Schema.Generates
{
    /// <summary>
    /// Azure SQL Database and Managed Instance reject some catalog queries the
    /// on-premises reader uses. Those errors are skipped so the rest of the schema
    /// is still compared. The same numbers on an on-premises instance are real failures.
    /// </summary>
    internal static class AzureSchemaCompatibility
    {
        internal static bool ShouldSkip(DatabaseInfo info, int errorNumber)
        {
            if (info == null || !info.IsAzureFamily)
                return false;

            switch (errorNumber)
            {
                case 195:   // not a recognized function or option
                case 208:   // invalid object name (catalog view missing on this offering)
                case 2812:  // could not find stored procedure
                case 40508: // USE is not supported
                case 40511: // built-in function is not supported
                case 40515: // cross-database or server reference is not supported
                case 40517: // keyword or statement option is not supported
                    return true;
                default:
                    return false;
            }
        }

        internal static bool ShouldSkip(DatabaseInfo info, SqlException exception)
        {
            if (exception == null)
                return false;

            foreach (SqlError error in exception.Errors)
            {
                if (ShouldSkip(info, error.Number))
                    return true;
            }

            return false;
        }
    }
}
