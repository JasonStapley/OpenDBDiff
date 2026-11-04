using Microsoft.Data.SqlClient;
using OpenDBDiff.SqlServer.Schema.Generates.SQLCommands;
using OpenDBDiff.SqlServer.Schema.Model;
using System;
using OpenDBDiff.SqlServer.Schema.Connections;

namespace OpenDBDiff.SqlServer.Schema.Generates
{
    public class GenerateUsers
    {
        private Generate root;

        public GenerateUsers(Generate root)
        {
            this.root = root;
        }

        public void Fill(Database database, string connectioString)
        {
            string type;
            if ((database.Options.Ignore.FilterUsers) || (database.Options.Ignore.FilterRoles))
            {
                using (SqlConnection conn = SqlConnections.Create(connectioString))
                {
                    using (SqlCommand command = new SqlCommand(UserSQLCommand.Get(database.Info.Version, database.Info.Edition), conn))
                    {
                        conn.Open();
                        command.CommandTimeout = 0;
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                type = reader["type"].ToString();
                                if (database.Options.Ignore.FilterUsers && IsDatabaseUser(type))
                                {
                                    User item = new User(database);
                                    item.Id = (int)reader["principal_id"];
                                    item.Name = reader["name"].ToString();
                                    item.Login = reader["Login"].ToString();
                                    item.Owner = reader["default_schema_name"].ToString();
                                    item.PrincipalType = type;
                                    database.Users.Add(item);
                                }
                                if (database.Options.Ignore.FilterRoles && (type.Equals("A") || type.Equals("R")))
                                {
                                    Role item = new Role(database);
                                    item.Id = (int)reader["principal_id"];
                                    item.Name = reader["name"].ToString();
                                    item.Owner = reader["default_schema_name"].ToString();
                                    item.Password = "";
                                    item.IsSystem = (Boolean)reader["is_fixed_role"];
                                    if (type.Equals("A"))
                                        item.Type = Role.RoleTypeEnum.ApplicationRole;
                                    else
                                        item.Type = Role.RoleTypeEnum.DatabaseRole;
                                    database.Roles.Add(item);
                                }
                            }
                        }
                    }
                }
            }
        }

        private static bool IsDatabaseUser(string type)
        {
            // S and U are the on-premises SQL and Windows users. E and X are Entra
            // principals; the on-premises query never returns them.
            return type.Equals("S") || type.Equals("U") || type.Equals("E") || type.Equals("X");
        }
    }
}
