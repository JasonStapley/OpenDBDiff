using OpenDBDiff.Abstractions.Schema;
using OpenDBDiff.Abstractions.Schema.Model;
using System;

namespace OpenDBDiff.SqlServer.Schema.Model
{
    public class User : SQLServerSchemaBase
    {
        public User(ISchemaBase parent)
            : base(parent, ObjectType.User)
        {
        }

        public override string FullName
        {
            get { return "[" + Name + "]"; }
        }

        public string Login { get; set; }

        /// <summary>
        /// sys.database_principals.type. E and X are Microsoft Entra users and groups.
        /// SQL and Windows users (S, U) keep the original FOR LOGIN / WITHOUT LOGIN script.
        /// </summary>
        public string PrincipalType { get; set; }

        public bool IsExternalPrincipal
        {
            get
            {
                return string.Equals(PrincipalType, "E", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(PrincipalType, "X", StringComparison.OrdinalIgnoreCase);
            }
        }

        public override string ToSql()
        {
            if (IsExternalPrincipal)
            {
                string externalSql = "CREATE USER " + FullName + " FROM EXTERNAL PROVIDER";
                if (!String.IsNullOrEmpty(Owner))
                    externalSql += " WITH DEFAULT_SCHEMA=[" + Owner + "]";
                return externalSql + "\r\nGO\r\n";
            }

            string sql = "";
            sql += "CREATE USER ";
            sql += FullName + " ";
            if (!String.IsNullOrEmpty(Login))
                sql += "FOR LOGIN [" + Login + "] ";
            else
                sql += "WITHOUT LOGIN ";
            if (!String.IsNullOrEmpty(Owner))
                sql += "WITH DEFAULT_SCHEMA=[" + Owner + "]";
            return sql.Trim() + "\r\nGO\r\n";
        }

        public override string ToSqlDrop()
        {
            return "DROP USER " + FullName + "\r\nGO\r\n";
        }

        public override string ToSqlAdd()
        {
            return ToSql();
        }

        public override SQLScriptList ToSqlDiff(System.Collections.Generic.ICollection<ISchemaBase> schemas)
        {
            SQLScriptList listDiff = new SQLScriptList();

            if (this.Status == ObjectStatus.Drop)
            {
                listDiff.Add(ToSqlDrop(), 0, ScriptAction.DropUser);
            }
            if (this.Status == ObjectStatus.Create)
            {
                listDiff.Add(ToSql(), 0, ScriptAction.AddUser);
            }
            if ((this.Status & ObjectStatus.Alter) == ObjectStatus.Alter)
            {
                listDiff.Add(ToSqlDrop(), 0, ScriptAction.DropUser);
                listDiff.Add(ToSql(), 0, ScriptAction.AddUser);
            }
            return listDiff;
        }

        public bool Compare(User obj)
        {
            if (obj == null) throw new ArgumentNullException("destination");
            if (!this.Login.Equals(obj.Login)) return false;
            if (!this.Owner.Equals(obj.Owner)) return false;
            if (!string.Equals(this.PrincipalType, obj.PrincipalType)) return false;
            return true;
        }
    }
}
