using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace UTAP
{
    public class DBManager
    {
        private readonly BufferBlock<string> _queue = new BufferBlock<string>();

        private readonly SqlConnection readConnection;
        private SqlCommand readCommand;
        private SqlDataReader dbReader;

        private readonly SqlConnection writeConnection;

        public List<PAN> TempPANList = new List<PAN>();
        public List<PIN> TempPINList = new List<PIN>();

        public List<PAN> PANList = new List<PAN>();
        public List<PIN> PINList = new List<PIN>();
        
        public string conString = "server=dev002\\SENSE; database=Sense; User ID=UCCS_Client; Password=;Persist Security Info=False; Trusted_Connection=no; Connection Timeout=60";
        private const string getPANStr = "SELECT [PIN] ,[p_number], [ForeNames], [name_address], [SimNumber] FROM [allowed_phone_numbers] WHERE p_number LIKE '07%'";
        private const string getPrisonerStr = "SELECT [PIN] ,[name] ,[sentence_number] FROM [prisoner] where call_status = 1";
        public int readingInterval = 30000;

        public DBManager(string UCCSConString, int UCCSReadInterval)
        {
            try
            {
                conString = UCCSConString;
                readingInterval = UCCSReadInterval;
                readConnection = new SqlConnection(conString);
                writeConnection = new SqlConnection(conString);
                readConnection.Open();
                writeConnection.Open();
                
                Task.Run(DBReader);
                Task.Run(DBWriter);
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private async Task DBReader()
        {
            while (true)
            {
                try
                {
                    readCommand = new SqlCommand(getPANStr, readConnection);
                    dbReader = readCommand.ExecuteReader();

                    while (dbReader.Read())
                    {
                        int PIN = (int)dbReader["PIN"];
                        string Digits = dbReader["p_number"].ToString();
                        string Forenames = dbReader["ForeNames"].ToString(); 
                        string Surname = dbReader["name_address"].ToString();
                        string SimNumber = dbReader["SimNumber"].ToString();

                        TempPANList.Add(new PAN { PIN = PIN, Digits = Digits, Forenames = Forenames, Surname = Surname, SimNumber = SimNumber });
                    }
                    dbReader.Close();
                    PANList = TempPANList;
                    TempPANList = new List<PAN>();

                    readCommand = new SqlCommand(getPrisonerStr, readConnection);
                    dbReader = readCommand.ExecuteReader();

                    while (dbReader.Read())
                    {
                        int PIN = (int)dbReader["PIN"];
                        string Name = dbReader["name"].ToString();
                        string Cmsurn = dbReader["sentence_number"].ToString();

                        TempPINList.Add(new PIN { Number = PIN, Name = Name, Cmsurn = Cmsurn });
                    }
                    dbReader.Close();
                    PINList = TempPINList;
                    TempPINList = new List<PIN>();

                    await Task.Delay(readingInterval);
                }
                catch(Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
        }

        private async Task DBWriter()
        {
            while (await _queue.OutputAvailableAsync())
            {
                var buff = await _queue.ReceiveAsync();

                using var command = new SqlCommand(buff, writeConnection);
                command.ExecuteNonQuery();
            }
        }

        public void Enqueue(string command)
        {
            _queue.Post(command);
        }

        public int GetAndUpdateMessageReferenceNumber(int PIN, string PAN)
        {
            var con = new SqlConnection(conString);

            var query = @"SELECT
                            MessageReferenceNumber
                          FROM
                            allowed_phone_numbers
                          WHERE
                            PIN = @PIN
                          AND
                            p_number = @PAN;";

            var command = new SqlCommand(query, con);
            command.Parameters.AddWithValue("@PIN", PIN);
            command.Parameters.AddWithValue("@PAN", PAN);

            con.Open();
            var refNumber = (int)command.ExecuteScalar();

            if (refNumber == ushort.MaxValue)
                refNumber = 1;
            else
                ++refNumber;

            query = @"UPDATE
                        allowed_phone_numbers
                      SET
                        MessageReferenceNumber = @refNumber
                      WHERE
                        PIN = @PIN
                      AND
                        p_number = @PAN";

            command = new SqlCommand(query, con);
            command.Parameters.AddWithValue("@refNumber", refNumber);
            command.Parameters.AddWithValue("@PIN", PIN);
            command.Parameters.AddWithValue("@PAN", PAN);

            command.ExecuteNonQuery();

            con.Close();

            return refNumber;
        }

        public bool GetConversationReadStatus(int PIN, string PAN)
        {
            var con = new SqlConnection(conString);

            var query = @"SELECT
                            ConversationRead
                          FROM
                            allowed_phone_numbers
                          WHERE
                            PIN = @PIN
                          AND
                            p_number = @PAN;";

            using var command = new SqlCommand(query, con);
            command.Parameters.AddWithValue("@PIN", PIN);
            command.Parameters.AddWithValue("@PAN", PAN);

            con.Open();
            var IsRead = (bool)command.ExecuteScalar();
            con.Close();

            return IsRead;
        }
    }
}
