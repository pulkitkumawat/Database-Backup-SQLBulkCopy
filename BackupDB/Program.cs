using log4net;
using log4net.Config;
using Microsoft.IdentityModel.Protocols;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;


namespace BackupDB
{
    class Program
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        static void Main(string[] args)
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();

            bool isOrchestrator = ConfigurationManager.AppSettings["IsOrchestrator"].ToLower() == "true" ? true : false;

            string[] databaseArray = ConfigurationManager.AppSettings["BackupDatabaseList"].Split(',');
            string[] destinationDatabaseArray = null;
            if (!isOrchestrator)
            {
                destinationDatabaseArray = ConfigurationManager.AppSettings["DestinationDatabaseList"].Split(',');
            }


            for (int i = 0; i < databaseArray.Length; i++)
            {
                if (!isOrchestrator)
                {
                    BackupDatabase(databaseArray[i], destinationDatabaseArray[i]);
                }
                else
                {
                    BackupDatabase(databaseArray[i], null);
                }
            }

            sw.Stop();
            Console.WriteLine("Time taken: " + sw.Elapsed);
            log.Info("Time taken: " + sw.Elapsed);

            Console.ReadKey();
        }

        private static void BackupDatabase(string TenantDBName, string DestinationDBName)
        {
            string[] tablesToBackupArray = ConfigurationManager.AppSettings["BackupTableList"].Split(',');

            var SourceServerKey = "SourceServer";
            var DestinationServerKey = "DestinationServer";

            var SourceDB = TenantDBName;
            var DestinationDB = string.Empty;
            if (DestinationDBName == null)
            {
                DestinationDB = TenantDBName + "Archive";
            }
            else
            {
                DestinationDB = DestinationDBName;
            }

            using (SqlConnection conArchive = new SqlConnection(GetSqlConnectionString(DestinationServerKey, DestinationDB)))
            {
                string sqlBackupDate = null, BackupLogTable = null, CurrentDateString = null;
                DateTime CurrentDate = default(DateTime);
                try
                {
                    Console.WriteLine("Connecting to {0} DB..", DestinationDB);
                    conArchive.Open();
                    Console.WriteLine("Successfully connected..");

                    if (DestinationDBName == null)
                    {
                        BackupLogTable = ConfigurationManager.AppSettings["BackupLogTable"];
                        string sql = "Select GETDATE()";
                        SqlCommand sqlCommand = new SqlCommand(sql, conArchive);
                        object obj = sqlCommand.ExecuteScalar();
                        CurrentDate = Convert.ToDateTime(obj);
                        CurrentDateString = CurrentDate.ToString(@"yyyy-MM-dd HH\:mm\:ss.fff");
                        log.Info("Backup process Started for " + SourceDB + " started on " + CurrentDateString);
                        //This Date will not be null if Backup needs to be executed
                        sqlBackupDate = IsTimeToBackup(conArchive, BackupLogTable, CurrentDate);
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(ex.Message);
                    log.Error(ex.Message);
                    Console.ResetColor();
                    return;
                }

                if (!string.IsNullOrEmpty(sqlBackupDate) || DestinationDBName != null)
                {
                    foreach (var table in tablesToBackupArray)
                    {
                        Stopwatch sw = new Stopwatch();
                        sw.Start();
                        try
                        {
                            var SourceTable = table;
                            var DestinationTable = string.Empty;

                            if (DestinationDBName == null)
                            {
                                DestinationTable = table + "Backup"; 
                            }
                            else
                            {
                                DestinationTable = table;
                            }

                            var options = SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.UseInternalTransaction | SqlBulkCopyOptions.KeepIdentity;

                            using (SqlBulkCopy bulkCopy = new SqlBulkCopy(GetSqlConnectionString(DestinationServerKey, DestinationDB), options))
                            {
                                bulkCopy.DestinationTableName =
                                    DestinationTable;
                                bulkCopy.BatchSize = 5000;
                                bulkCopy.BulkCopyTimeout = 1800;

                                bulkCopy.SqlRowsCopied +=
                                new SqlRowsCopiedEventHandler(OnSqlRowsCopied);
                                bulkCopy.NotifyAfter = 50;

                                //SqlCommand IdentityInsertON = new SqlCommand("SET IDENTITY_INSERT dbo.ErrorLogBackup ON", con2);
                                //IdentityInsertON.ExecuteNonQuery();

                                using (SqlConnection conSource = new SqlConnection(GetSqlConnectionString(SourceServerKey, SourceDB)))
                                {
                                    Console.WriteLine("\nConnecting to the {0} Source DB..", SourceDB);
                                    conSource.Open();
                                    Console.WriteLine("Successfully connected..\n");

                                    string query = null;

                                    if (DestinationDBName == null)
                                    {
                                        if (SourceTable.ToLower().Contains("systemlog"))
                                        {
                                            query = "select * from " + SourceTable + " where Created_DateTime > '" + sqlBackupDate + "' and Created_DateTime <= '" + CurrentDateString + "'";
                                        }
                                        else
                                        {
                                            query = "select * from " + SourceTable + " where CreatedDateTime > '" + sqlBackupDate + "' and CreatedDateTime <= '" + CurrentDateString + "'";
                                        }
                                    }
                                    else
                                    {
                                        query = "select * from " + SourceTable;
                                    }

                                    SqlCommand sqlCmd = new SqlCommand(query, conSource);
                                    //SqlDataAdapter da = new SqlDataAdapter(sqlCmd);
                                    using (SqlDataReader reader = sqlCmd.ExecuteReader())
                                    {
                                        if (reader.HasRows)
                                        {
                                            Console.WriteLine("Copying of data started from {0} to {1} for {2}\n\n", SourceDB, DestinationDB, SourceTable);
                                            bulkCopy.WriteToServer(reader);
                                        }
                                        else
                                        {
                                            Console.WriteLine("No rows found.");
                                        }
                                    }
                                }
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine("Copy Done Successfully");
                                log.Info("Copying of data done successfully from " + SourceDB + " to " + DestinationDB + " for " + SourceTable);
                                Console.ResetColor();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine(ex.Message);
                            log.Error(ex.Message);
                            Console.ResetColor();
                        }
                        sw.Stop();
                        Console.WriteLine("\nTime taken to backup {0} is {1} ", table, sw.Elapsed);
                        log.Info("Time taken to backup " + table + " is " + sw.Elapsed);
                    }
                    if (CurrentDate != default(DateTime))
                    {
                        UpdateLastBackupDate(conArchive, BackupLogTable, CurrentDateString);
                    }
                }
                else
                {
                    Console.WriteLine("No need to Backup {0}", SourceDB);
                    log.Info("No need to backup " + SourceDB);
                }
            }
            log.Info("Backup Process Ended for db : " + TenantDBName);
        }

        private static string IsTimeToBackup(SqlConnection con, string backupLogTable, DateTime currentDate)
        {
            string BackupTime = null;
            try
            {
                if (con.State == ConnectionState.Closed)
                {
                    con.Open();
                }

                string sql = "Select LastBackupTime,BackupFrequency from " + backupLogTable + " where Id = 1";
                SqlCommand sqlCommand = new SqlCommand(sql, con);
                SqlDataReader reader = sqlCommand.ExecuteReader();

                int ordLastBackupTime = reader.GetOrdinal("LastBackupTime");
                int ordBackupFrequency = reader.GetOrdinal("BackupFrequency");

                if (!reader.Read())
                    throw new InvalidOperationException("No records were returned.");

                DateTime LastBackupTime = reader.GetDateTime(ordLastBackupTime);
                int BackupFrequency = reader.GetInt32(ordBackupFrequency);

                if (reader.Read())
                    throw new InvalidOperationException("Multiple records were returned.");

                reader.Close();

                int isTimeToBackup = DateTime.Compare(LastBackupTime.AddDays(BackupFrequency), currentDate);

                if (isTimeToBackup < 0)
                {
                    BackupTime = LastBackupTime.ToString(@"yyyy-MM-dd HH\:mm\:ss.fff");
                }

            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(ex.Message);
                log.Error(ex.Message);
                Console.ResetColor();
            }
            return BackupTime;
        }

        private static void UpdateLastBackupDate(SqlConnection con, string tableName, string lastBackupDate)
        {
            try
            {
                if (con.State == ConnectionState.Closed)
                {
                    con.Open();
                }
                var sql = "UPDATE " + tableName + " set LastBackupTime = '" + lastBackupDate + "' where Id = 1";
                SqlCommand sqlCommand = new SqlCommand(sql, con);
                sqlCommand.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(ex.Message);
                log.Error(ex.Message);
                Console.ResetColor();
            }

        }

        private static void OnSqlRowsCopied(
        object sender, SqlRowsCopiedEventArgs e)
        {
            Console.SetCursorPosition(0, Console.CursorTop - 1);
            ClearCurrentConsoleLine();
            Console.WriteLine("Copied {0} so far...", e.RowsCopied);
        }

        private static string GetSqlConnectionString(string ServerKey, string DatabaseName)
        {
            ConnectionStringSettings connectionStringSource = ConfigurationManager.ConnectionStrings[ServerKey];
            SqlConnectionStringBuilder connectionStringSettingSource = new SqlConnectionStringBuilder(connectionStringSource.ConnectionString);
            connectionStringSettingSource.InitialCatalog = DatabaseName;

            return connectionStringSettingSource.ConnectionString;
        }

        private static void ClearCurrentConsoleLine()
        {
            int currentLineCursor = Console.CursorTop;
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, currentLineCursor);
        }


    }
}
