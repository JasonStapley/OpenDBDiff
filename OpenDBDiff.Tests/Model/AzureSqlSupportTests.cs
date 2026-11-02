using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenDBDiff.SqlServer.Schema.Generates;
using OpenDBDiff.SqlServer.Schema.Generates.SQLCommands;
using OpenDBDiff.SqlServer.Schema.Model;

namespace OpenDBDiff.Tests.Model.Tests
{
    [TestClass]
    public class AzureSqlSupportTests
    {
        [TestMethod]
        public void EngineEdition5_IsAzureSqlDatabase_AndNotManagedInstance()
        {
            var info = new DatabaseInfo();
            info.VersionNumber = 12.0f;
            info.SetEdition(DatabaseInfo.SQLServerEdition.Azure);

            Assert.AreEqual(DatabaseInfo.SQLServerVersion.SQLServerAzure10, info.Version);
            Assert.IsTrue(info.IsAzureSqlDatabase);
            Assert.IsFalse(info.IsAzureManagedInstance);
            Assert.IsTrue(info.IsAzureFamily);
        }

        [TestMethod]
        public void EngineEdition8_KeepsTheSqlServerVersion()
        {
            var info = new DatabaseInfo();
            info.VersionNumber = 16.0f;
            info.SetEdition(DatabaseInfo.SQLServerEdition.AzureManagedInstance);

            Assert.AreEqual(DatabaseInfo.SQLServerVersion.SQLServer2017, info.Version);
            Assert.IsTrue(info.IsAzureManagedInstance);
            Assert.IsFalse(info.IsAzureSqlDatabase);
            Assert.IsTrue(info.IsAzureFamily);
        }

        [TestMethod]
        public void OnPremisesEdition_IsNotTreatedAsAzure()
        {
            var info = new DatabaseInfo();
            info.VersionNumber = 15.0f;
            info.SetEdition(DatabaseInfo.SQLServerEdition.Enterprise);

            Assert.AreEqual(DatabaseInfo.SQLServerVersion.SQLServer2017, info.Version);
            Assert.IsFalse(info.IsAzureFamily);
            Assert.IsFalse(AzureSchemaCompatibility.ShouldSkip(info, 208));
            Assert.IsFalse(AzureSchemaCompatibility.ShouldSkip(info, 40515));
        }

        [TestMethod]
        public void UnsupportedCatalogErrors_AreSkippedOnlyForAzureOfferings()
        {
            var azure = new DatabaseInfo();
            azure.SetEdition(DatabaseInfo.SQLServerEdition.Azure);
            var managedInstance = new DatabaseInfo();
            managedInstance.VersionNumber = 16.0f;
            managedInstance.SetEdition(DatabaseInfo.SQLServerEdition.AzureManagedInstance);

            Assert.IsTrue(AzureSchemaCompatibility.ShouldSkip(azure, 208));
            Assert.IsTrue(AzureSchemaCompatibility.ShouldSkip(azure, 40515));
            Assert.IsTrue(AzureSchemaCompatibility.ShouldSkip(managedInstance, 40517));
            Assert.IsFalse(AzureSchemaCompatibility.ShouldSkip(azure, 18456));
            Assert.IsFalse(AzureSchemaCompatibility.ShouldSkip(azure, 40615));
            Assert.IsFalse(AzureSchemaCompatibility.ShouldSkip(null, 208));
        }

        [TestMethod]
        public void OnPremisesUserQuery_DoesNotReadEntraPrincipals()
        {
            string sql = UserSQLCommand.Get(
                DatabaseInfo.SQLServerVersion.SQLServer2017,
                DatabaseInfo.SQLServerEdition.Standard);

            StringAssert.Contains(sql, "type IN ('S','U','A','R')");
            StringAssert.Contains(sql, "ISNULL(suser_sname(sid),'') AS Login");
            Assert.IsFalse(sql.Contains("'E'"));
            Assert.IsFalse(sql.Contains("'X'"));
        }

        [TestMethod]
        public void AzureSqlDatabase_ReadsEntraUsersWithoutAssemblyCatalogs()
        {
            string users = UserSQLCommand.Get(
                DatabaseInfo.SQLServerVersion.SQLServerAzure10,
                DatabaseInfo.SQLServerEdition.Azure);
            StringAssert.Contains(users, "'E','X'");
            StringAssert.Contains(users, "WHEN type IN ('E','X') THEN ''");

            string types = UserDataTypeCommand.Get(
                DatabaseInfo.SQLServerVersion.SQLServerAzure10,
                DatabaseInfo.SQLServerEdition.Azure);
            Assert.IsFalse(types.Contains("sys.assemblies"));
            Assert.IsFalse(types.Contains("sys.assembly_types"));
            StringAssert.Contains(types, "is_assembly_type = 0");

            string onPremisesTypes = UserDataTypeCommand.Get(
                DatabaseInfo.SQLServerVersion.SQLServer2016,
                DatabaseInfo.SQLServerEdition.Standard);
            StringAssert.Contains(onPremisesTypes, "sys.assemblies");
        }

        [TestMethod]
        public void ManagedInstance_ReadsEntraUsersAndStillResolvesSqlLogins()
        {
            string sql = UserSQLCommand.Get(
                DatabaseInfo.SQLServerVersion.SQLServer2017,
                DatabaseInfo.SQLServerEdition.AzureManagedInstance);

            StringAssert.Contains(sql, "'E','X'");
            StringAssert.Contains(sql, "ISNULL(suser_sname(sid),'')");
            Assert.IsFalse(sql.Contains("suser_sid()=sid"));
        }

        [TestMethod]
        public void SqlUserScript_IsUnchanged()
        {
            var database = new Database();
            var user = new User(database)
            {
                Name = "app",
                Login = "app",
                Owner = "dbo",
                PrincipalType = "S"
            };

            Assert.AreEqual(
                "CREATE USER [app] FOR LOGIN [app] WITH DEFAULT_SCHEMA=[dbo]\r\nGO\r\n",
                user.ToSql());
        }

        [TestMethod]
        public void UserWithoutLogin_IsUnchanged()
        {
            var user = new User(new Database())
            {
                Name = "app",
                Login = "",
                Owner = "dbo"
            };

            Assert.AreEqual(
                "CREATE USER [app] WITHOUT LOGIN WITH DEFAULT_SCHEMA=[dbo]\r\nGO\r\n",
                user.ToSql());
        }

        [TestMethod]
        public void EntraUser_IsScriptedFromExternalProvider()
        {
            var user = new User(new Database())
            {
                Name = "user@contoso.com",
                Login = "user@contoso.com",
                Owner = "dbo",
                PrincipalType = "E"
            };
            var group = new User(new Database())
            {
                Name = "Analysts",
                Login = "",
                Owner = "",
                PrincipalType = "X"
            };

            Assert.AreEqual(
                "CREATE USER [user@contoso.com] FROM EXTERNAL PROVIDER WITH DEFAULT_SCHEMA=[dbo]\r\nGO\r\n",
                user.ToSql());
            Assert.AreEqual(
                "CREATE USER [Analysts] FROM EXTERNAL PROVIDER\r\nGO\r\n",
                group.ToSql());
            Assert.IsFalse(user.Compare(new User(new Database())
            {
                Name = "user@contoso.com",
                Login = "",
                Owner = "dbo"
            }));
            Assert.IsTrue(new User(new Database()) { Login = "app", Owner = "dbo" }
                .Compare(new User(new Database()) { Login = "app", Owner = "dbo" }));
        }
    }
}
