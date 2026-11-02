using Microsoft.Data.SqlClient;
using OpenDBDiff.Abstractions.Schema.Errors;
using OpenDBDiff.Abstractions.Schema.Events;
using OpenDBDiff.Abstractions.Schema.Misc;
using OpenDBDiff.SqlServer.Schema.Compare;
using OpenDBDiff.SqlServer.Schema.Connections;
using OpenDBDiff.SqlServer.Schema.Generates.Util;
using OpenDBDiff.SqlServer.Schema.Model;
using OpenDBDiff.SqlServer.Schema.Options;
using System;
using System.Collections.Generic;

namespace OpenDBDiff.SqlServer.Schema.Generates
{
    public class Generate
    {
        private readonly List<MessageLog> messages;
        private ProgressEventArgs currentlyReading;

        public Generate()
        {
            messages = new List<MessageLog>();
            OnReading += Generate_OnReading;
        }

        public static int MaxValue
        {
            get { return Constants.READING_MAX; }
        }

        public string ConnectionString { get; set; }

        private string Name
        {
            get
            {
                string name;
                using (var conn = SqlConnections.Create(ConnectionString))
                {
                    name = conn.Database;
                }
                return name;
            }
        }

        public SqlOption Options { get; set; }

        private event ProgressEventHandler.ProgressHandler OnReading;

        public event ProgressEventHandler.ProgressHandler OnProgress;

        private void Generate_OnReading(ProgressEventArgs e)
        {
            if (OnProgress != null) OnProgress(e);
        }

        public void RaiseOnReading(ProgressEventArgs e)
        {
            this.currentlyReading = e;
            if (OnReading != null) OnReading(e);
        }

        public void RaiseOnReadingOne(object name)
        {
            if (name != null && this.OnReading != null && this.currentlyReading != null)
            {
                var eOne = new ProgressEventArgs(this.currentlyReading.Message, this.currentlyReading.Progress);
                eOne.Message = eOne.Message.Replace("...", String.Format(": [{0}]", name));
                this.OnReading(eOne);
            }
        }

        /// <summary>
        /// Genera el schema de la base de datos seleccionada y devuelve un objeto Database.
        /// </summary>
        public Database Process()
        {
            string error = "";
            var databaseSchema = new Database();

            //tables.OnTableProgress += new Progress.ProgressHandler(tables_OnTableProgress);
            databaseSchema.Options = Options;
            databaseSchema.Name = Name;
            databaseSchema.Info = (new GenerateDatabase(ConnectionString, Options)).Get(databaseSchema);
            /*Thread t1 = new Thread(delegate()
                {
                    try
                    {*/
            ReadAzureTolerant(databaseSchema, "rules", delegate { (new GenerateRules(this)).Fill(databaseSchema, ConnectionString); });
            (new GenerateTables(this)).Fill(databaseSchema, ConnectionString, messages);
            (new GenerateViews(this)).Fill(databaseSchema, ConnectionString, messages);

            if (Options.Ignore.FilterIndex)
            {
                ReadAzureTolerant(databaseSchema, "indexes", delegate { (new GenerateIndex(this)).Fill(databaseSchema, ConnectionString); });
                ReadAzureTolerant(databaseSchema, "full-text indexes", delegate { (new GenerateFullTextIndex(this)).Fill(databaseSchema, ConnectionString); });
            }
            (new GenerateUserDataTypes(this)).Fill(databaseSchema, ConnectionString, messages);
            ReadAzureTolerant(databaseSchema, "XML schemas", delegate { (new GenerateXMLSchemas(this)).Fill(databaseSchema, ConnectionString); });
            ReadAzureTolerant(databaseSchema, "schemas", delegate { (new GenerateSchemas(this)).Fill(databaseSchema, ConnectionString); });
            /*}
                    catch (Exception ex)
                    {
                        error = ex.StackTrace;
                    }
                });
                Thread t2 = new Thread(delegate()
                {
                    try
                    {*/

            // Azure SQL Database has no user filegroups, so partition schemes and filegroups
            // cannot be scripted. Managed Instance supports them and is not on this path.
            if (!databaseSchema.Info.IsAzureSqlDatabase)
            {
                ReadAzureTolerant(databaseSchema, "partition functions", delegate { (new GeneratePartitionFunctions(this)).Fill(databaseSchema, ConnectionString); });
                ReadAzureTolerant(databaseSchema, "partition schemes", delegate { (new GeneratePartitionScheme(this)).Fill(databaseSchema, ConnectionString); });
                ReadAzureTolerant(databaseSchema, "filegroups", delegate { (new GenerateFileGroups(this)).Fill(databaseSchema, ConnectionString); });
            }

            ReadAzureTolerant(databaseSchema, "DDL triggers", delegate { (new GenerateDDLTriggers(this)).Fill(databaseSchema, ConnectionString); });
            ReadAzureTolerant(databaseSchema, "synonyms", delegate { (new GenerateSynonyms(this)).Fill(databaseSchema, ConnectionString); });

            // CLR assemblies and full-text catalogs (filegroup path) are not available on Azure SQL Database.
            if (!databaseSchema.Info.IsAzureSqlDatabase)
            {
                ReadAzureTolerant(databaseSchema, "assemblies", delegate { (new GenerateAssemblies(this)).Fill(databaseSchema, ConnectionString); });
                ReadAzureTolerant(databaseSchema, "full-text catalogs", delegate { (new GenerateFullText(this)).Fill(databaseSchema, ConnectionString); });
            }
            /*}
                    catch (Exception ex)
                    {
                        error = ex.StackTrace;
                    }
                });
                Thread t3 = new Thread(delegate()
                {
                    try
                    {*/
            ReadAzureTolerant(databaseSchema, "stored procedures", delegate { (new GenerateStoredProcedures(this)).Fill(databaseSchema, ConnectionString); });
            ReadAzureTolerant(databaseSchema, "functions", delegate { (new GenerateFunctions(this)).Fill(databaseSchema, ConnectionString); });
            (new GenerateTriggers(this)).Fill(databaseSchema, ConnectionString, messages);
            ReadAzureTolerant(databaseSchema, "object definitions", delegate { (new GenerateTextObjects(this)).Fill(databaseSchema, ConnectionString); });
            ReadAzureTolerant(databaseSchema, "users", delegate { (new GenerateUsers(this)).Fill(databaseSchema, ConnectionString); });
            /*}
                    catch (Exception ex)
                    {
                        error = ex.StackTrace;
                    }
                });
                t1.Start();
                t2.Start();
                t3.Start();
                t1.Join();
                t2.Join();
                t3.Join();*/
            if (String.IsNullOrEmpty(error))
            {
                /*Las propiedades extendidas deben ir despues de haber capturado el resto de los objetos de la base*/
                (new GenerateExtendedProperties(this)).Fill(databaseSchema, ConnectionString, messages);
                ReadAzureTolerant(databaseSchema, "permissions", delegate { (new GeneratePermissions(this)).Fill(databaseSchema, ConnectionString); });
                databaseSchema.BuildDependency();
                return databaseSchema;
            }
            else
                throw new SchemaException(error);
        }

        private void ReadAzureTolerant(Database database, string description, Action read)
        {
            try
            {
                read();
            }
            catch (SqlException ex) when (AzureSchemaCompatibility.ShouldSkip(database.Info, ex))
            {
                messages.Add(new MessageLog(
                    "Skipped " + description + " because this Azure SQL offering rejected the query. " + ex.Message,
                    ex.ToString(),
                    MessageLog.LogType.Warning));
                RaiseOnReading(new ProgressEventArgs("Skipped " + description + " (not supported on this Azure SQL offering).", -1));
            }
        }

        private void tables_OnTableProgress(object sender, ProgressEventArgs e)
        {
            ProgressEventHandler.RaiseOnChange(e);
        }

        // TODO: Static because Compare method is static; static events are not my favorite
        public static event ProgressEventHandler.ProgressHandler OnCompareProgress;

        internal static void RaiseOnCompareProgress(string formatString, params object[] formatParams)
        {
            OnCompareProgress?.Invoke(new ProgressEventArgs(String.Format(formatString, formatParams), -1));
        }

        /// <summary>
        /// Generates the differences to migrate a schema from origin to destination
        /// </summary>
        /// <param name="origin">The Origin schema is the schema before our generated actions are applied.</param>
        /// <param name="destination">The Destination schema is the schema after our actions are applied.</param>
        /// <returns></returns>
        public static Database Compare(Database origin, Database destination)
        {
            Database merge = CompareDatabase.GenerateDifferences(origin, destination);
            return merge;
        }
    }
}
