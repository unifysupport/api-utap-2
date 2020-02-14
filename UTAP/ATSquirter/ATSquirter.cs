using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace UTAP
{
    public class ATSquirter : IHostedService, IDisposable
    {
        UTAPConfiguration _settings;

        public List<SimCard> SimCards = new List<SimCard>();
        public BufferBlock<SMS> _queue = new BufferBlock<SMS>();

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!String.IsNullOrEmpty(_settings.SMSArbiterIP))
                {
                    Log.Information("Starting ATSquirter.NumberDiscoverer...");
                    Task.Run(NumberDiscoverer);

                    Log.Information("Starting ATSquirter.InboundMessageReader...");
                    Task.Run(InboundMessageReader);

                    Log.Information("Starting ATSquirter.InboundMessageSender...");
                    Task.Run(InboundMessageSender);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Exception on SMSArbiter.StartAsync: {ex}", ex.Message);
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {

        }

        public ATSquirter(IOptionsMonitor<UTAPConfiguration> options)
        {
            try
            {
                this._settings = options.CurrentValue;

                options.OnChange(Listener);
            }
            catch (Exception ex)
            {
                Log.Error("Exception on ATSquirter constructor: {ex}", ex.Message);
            }
        }

        private void Listener(UTAPConfiguration options)
        {
            _settings = options;
        }

        private async Task NumberDiscoverer()
        {
            while (true)
            {
                try
                {
                    string[] ports = SerialPort.GetPortNames();
                    foreach (string sPort in ports)
                    {
                        if (!SimCards.Exists(sim => sim.COMPort.PortName == sPort))
                        {
                            SerialPort port = null;
                            try
                            {
                                port = OpenPort(sPort);

                                SendCommand(port, ATCommands._CONFIRM_AT_DEVICE, 3000);
                                SendCommand(port, ATCommands._STOP_RSSI_FEEDBACK, 3000);
                                SendCommand(port, ATCommands._SET_MESSAGE_FORMAT, 3000);
                                SendCommand(port, ATCommands._SET_MESSAGE_STORAGE, 3000);
                                SendCommand(port, ATCommands._SET_MESSAGE_REPORTING, 3000);

                                var response = SendCommand(port, ATCommands._GET_PHONE_NUMBER, 3000);
                                var regex = new Regex(RegexStrings._MATCH_PHONE_NUMBER);
                                var match = regex.Match(response);
                                var phoneNumber = match.Value;

                                if (phoneNumber != "")
                                {
                                    var sim = new SimCard() { PhoneNumber = phoneNumber, COMPort = port, Connected = true };

                                    if (!SimCards.Exists(s => s.PhoneNumber == phoneNumber))
                                    {
                                        Log.Information("New SIM added to AvailableNumbers - Number: {SimNumber} Port: {Port}", phoneNumber, sPort);
                                        SimCards.Add(sim);
                                        PostToArbiter("/Modem/devices/" + sim.PhoneNumber, null);
                                    }
                                }
                            }
                            catch (UnauthorizedAccessException) { }
                            catch (ApplicationException) { }
                            catch (Exception ex)
                            {
                                Log.Debug("Exception in NumberDiscoverer - Port: {Port} Ex: {ex}", sPort, ex.Message);
                            }
                        }
                        else
                        {
                            var sim = SimCards.Find(sim => sim.COMPort.PortName == sPort);

                            lock (sim.locker)
                            {
                                try
                                {
                                    if (!sim.COMPort.IsOpen) sim.COMPort = OpenPort(sim.COMPort.PortName);
                                    SendCommand(sim.COMPort, ATCommands._CONFIRM_AT_DEVICE, 3000);
                                    SendCommand(sim.COMPort, ATCommands._SET_MESSAGE_FORMAT, 3000);
                                    SendCommand(sim.COMPort, ATCommands._SET_MESSAGE_STORAGE, 3000);
                                    SendCommand(sim.COMPort, ATCommands._SET_MESSAGE_REPORTING, 3000);

                                    var response = SendCommand(sim.COMPort, ATCommands._GET_PHONE_NUMBER, 3000);
                                    var regex = new Regex(RegexStrings._MATCH_PHONE_NUMBER);
                                    var match = regex.Match(response);
                                    var phoneNumber = match.Value;

                                    if (phoneNumber != sim.PhoneNumber)
                                    {
                                        if (SimCards.Exists(s => s.PhoneNumber == phoneNumber))
                                        {
                                            SimCards.Find(s => s.PhoneNumber == phoneNumber).COMPort = sim.COMPort;
                                        }
                                        else
                                        {
                                            var newSim = new SimCard() { PhoneNumber = phoneNumber, COMPort = sim.COMPort, Connected = true };
                                            Log.Information("New SIM added to AvailableNumbers - Number: {SimNumber} Port: {Port}", phoneNumber, sPort);
                                            SimCards.Add(newSim);
                                            PostToArbiter("/Modem/devices/" + newSim.PhoneNumber, null);
                                        }
                                        sim.COMPort = null;
                                        sim.Connected = false;
                                        DeleteToArbiter("/Modem/devices/" + sim.PhoneNumber);
                                    }
                                    else if (!sim.Connected)
                                    {
                                        sim.Connected = true;
                                        PutToArbiter("/Modem/devices/" + sim.PhoneNumber);
                                    }
                                }
                                catch
                                {
                                    if (sim.Connected)
                                    {
                                        Log.Warning("SIM disconnected: {SimNumber}", sim.PhoneNumber);
                                        sim.Connected = false;
                                        sim.COMPort.Close();
                                        sim.COMPort = null;
                                        DeleteToArbiter("/Modem/devices/" + sim.PhoneNumber);
                                    }
                                }
                            }
                        }
                    }

                    while (SimCards.Exists(s => !ports.Contains(s.COMPort.PortName) && s.Connected))
                    {
                        var disconnectedSim = SimCards.Find(s => !ports.Contains(s.COMPort.PortName));
                        Log.Warning("SIM disconnected: {SimNumber}", disconnectedSim.PhoneNumber);
                        disconnectedSim.Connected = false;
                        disconnectedSim.COMPort.Close();
                        disconnectedSim.COMPort = null;
                        DeleteToArbiter("/Modem/devices/" + disconnectedSim.PhoneNumber);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Exception in NumberDiscoverer: {ex}", ex.Message);
                }
                finally
                {
                    await Task.Delay(10000);
                }
            }
        }

        private async Task InboundMessageReader()
        {
            while (true)
            {
                try
                {
                    var messages = new List<SMS>();
                    foreach (SimCard sim in SimCards)
                    {
                        if (sim.Connected)
                        {
                            try
                            {
                                lock (sim.locker)
                                {
                                    var allMessages = SendCommand(sim.COMPort, ATCommands._GET_ALL_MESSAGES, 3000);
                                    Regex r = new Regex(RegexStrings._MATCH_MESSAGES);
                                    Match m = r.Match(allMessages);
                                    var indexes = new List<string>();
                                    while (m.Success)
                                    {
                                        indexes.Add(m.Groups["Index"].Value);

                                        m = m.NextMatch();
                                    }

                                    foreach (string index in indexes)
                                    {
                                        var message = SendCommand(sim.COMPort, string.Format(ATCommands._GET_MESSAGE, index), 3000);
                                        Regex messageMatcher = new Regex(RegexStrings._MATCH_MESSAGE);
                                        Match messageMatch = messageMatcher.Match(message);
                                        while (messageMatch.Success)
                                        {
                                            try
                                            {
                                                SMS msg = new SMS
                                                {
                                                    From = UTAPEncoding.DecodePDUSender(messageMatch.Groups["Body"].Value)
                                                };
                                                if (msg.From.StartsWith("44")) msg.From = "0" + msg.From.Remove(0, 2);
                                                else if (msg.From.StartsWith("+44")) msg.From = "0" + msg.From.Remove(0, 3);
                                                msg.To = sim.PhoneNumber;
                                                msg.Timestamp = DateTime.UtcNow;

                                                msg.Body = UTAPEncoding.DecodePDUMessage(messageMatch.Groups["Body"].Value);

                                                if (msg.Body != null)
                                                    _queue.Post(msg);
                                            }
                                            catch (Exception ex)
                                            {
                                                Log.Information("Exception on decode message - To: {To} Ex: {ex}", sim.PhoneNumber, ex.Message);
                                            }

                                            SendCommand(sim.COMPort, string.Format(ATCommands._DELETE_MESSAGE, index), 3000);

                                            messageMatch = m.NextMatch();
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Error("Exception in InboundMessageReader on message parse - SimNumber: {SimNumber} Port: {Port} Ex: {ex}", sim.PhoneNumber, sim.COMPort.PortName, ex.Message);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Exception in InboundMessageReader: {ex}", ex.Message);
                }
                finally
                {
                    await Task.Delay(10000);
                }
            }
        }

        private async Task InboundMessageSender()
        {
            while (await _queue.OutputAvailableAsync())
            {
                try
                {
                    var buff = await _queue.ReceiveAsync();

                    var response = await PostToArbiter("/Modem/receive", buff);

                    if (!response.IsSuccessStatusCode) _queue.Post(buff);
                }
                catch (Exception ex)
                {
                    Log.Error("Exception in InboundMessageSender: {ex}", ex.Message);
                }
            }
        }

        public bool SendSMS(TextMessage TextMessage)
        {
            if (SimCards.Exists(s => s.PhoneNumber == TextMessage.PAN))
            {
                Log.Information("!!Attempt to send to UTAP SIM!! - PIN: {PIN} SIM: {SIM}", TextMessage.PIN, TextMessage.PAN);
                return false;
            }
            var sim = SimCards.Find(s => s.PhoneNumber == TextMessage.SIM);
            var success = false;
            try
            {
                lock (sim.locker)
                {
                    SendCommand(sim.COMPort, ATCommands._CONFIRM_AT_DEVICE, 3000);
                    SendCommand(sim.COMPort, ATCommands._SET_MESSAGE_FORMAT, 3000);
                    SendCommand(sim.COMPort, ATCommands._SET_REPORTING_STATUS, 3000);

                    var Messages = UTAPEncoding.EncodePDU(TextMessage.PIN, TextMessage.PAN, TextMessage.Message, TextMessage.RefNumber);
                    foreach (var message in Messages)
                    {
                        SendCommand(sim.COMPort, string.Format(ATCommands._START_SEND_MESSAGE, (message.Length - 2) / 2), 3000);
                        SendCommand(sim.COMPort, message + char.ConvertFromUtf32(26) + "\r", 10000);
                    }
                    success = true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Exception in SendSMS - From: {PIN} To: {PAN} Ex: {ex}", TextMessage.PIN, TextMessage.PAN, ex.Message);
            }
            return success;
        }

        private SerialPort OpenPort(string PortName)
        {
            SerialPort port = new SerialPort();

            try
            {
                port.PortName = PortName;
                port.BaudRate = 115200;
                port.DataBits = 8;
                port.StopBits = StopBits.One;
                port.Parity = Parity.None;
                port.ReadTimeout = 3000;
                port.WriteTimeout = 3000;
                port.Encoding = Encoding.GetEncoding("iso-8859-1");
                port.Open();
                port.DtrEnable = true;
                port.RtsEnable = true;
            }
            catch (Exception ex)
            {
                throw ex;
            }

            return port;
        }

        private string SendCommand(SerialPort port, string command, int responseTimeout)
        {
            try
            {
                port.DiscardOutBuffer();
                port.DiscardInBuffer();
                port.Write(command + "\r");
                var stopwatch = new Stopwatch();
                stopwatch.Start();

                string result = "";
                while (!IsSuccess(result) && !IsCommError(result) && !IsMessageServiceError(result) && !IsMobileEquipmentError(result) && stopwatch.ElapsedMilliseconds < responseTimeout)
                {
                    try { result += port.ReadExisting(); }
                    catch (TimeoutException) { }
                }

                stopwatch.Stop();

                if (IsCommError(result) || IsMessageServiceError(result) || IsMobileEquipmentError(result)) throw new ApplicationException(result);
                else if (!IsSuccess(result)) throw new ApplicationException("Command timed out");
                else return result;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        private bool IsSuccess(string input) => input.IndexOf("\r\nOK\r\n") >= 0 || input.EndsWith("\r\n> ");

        private bool IsCommError(string input) => input.IndexOf("\r\nERROR\r\n") >= 0;

        private bool IsMessageServiceError(string input) => Regex.IsMatch(input, "\\r\\n\\+CMS ERROR: (.+)\\r\\n");

        private bool IsMobileEquipmentError(string input) => Regex.IsMatch(input, "\\r\\n\\+CME ERROR: (.+)\\r\\n");

        private async Task<HttpResponseMessage> PostToArbiter(string route, object content)
        {
            try
            {
                var client = new HttpClient();

                var data = JsonConvert.SerializeObject(content);

                var response = await client.PostAsync(_settings.SMSArbiterIP + route, new StringContent(data, Encoding.UTF8, "application/json"));

                return response;
            }
            catch { }

            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
        }

        private async Task<HttpResponseMessage> DeleteToArbiter(string route)
        {
            try
            {
                var client = new HttpClient();

                var response = await client.DeleteAsync(_settings.SMSArbiterIP + route);

                return response;
            }
            catch { }

            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
        }

        private async Task<HttpResponseMessage> PutToArbiter(string route)
        {
            try
            {
                var client = new HttpClient();

                var response = await client.DeleteAsync(_settings.SMSArbiterIP + route);

                return response;
            }
            catch { }

            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
        }
    }

    static class ATCommands
    {
        public const string _CONFIRM_AT_DEVICE = "AT";
        public const string _SET_MESSAGE_FORMAT = "AT+CMGF=0";
        public const string _STOP_RSSI_FEEDBACK = "AT^CURC=0";
        public const string _SET_CHAR_SET = "AT+CSCS=\"IRA\"";
        public const string _SET_MESSAGE_STORAGE = "AT+CPMS=\"MT\",\"MT\",\"MT\"";
        public const string _START_SEND_MESSAGE = "AT+CMGS={0}";
        public const string _SET_REPORTING_STATUS = "AT+CMEE=1";
        public const string _GET_PHONE_NUMBER = "AT+CNUM";
        public const string _GET_ALL_MESSAGES = "AT+CMGL=4";
        public const string _DELETE_MESSAGE = "AT+CMGD={0}";
        public const string _GET_MESSAGE = "AT+CMGR={0}";
        public const string _SET_MESSAGE_REPORTING = "AT+CSDH=1";
    }

    static class RegexStrings
    {
        public const string _MATCH_PHONE_NUMBER = "(0|44)[0-9]{10}";
        public const string _MATCH_MESSAGES = @"\+CMGL: (?<Index>\d+),(\s*)(\d+),(.*),(\s*)(\d+)";
        public const string _MATCH_MESSAGE = @"\+CMGR: (\d+),(.*),(\s*)(\d+)(\s*)(?<Body>.+)";
    }

    public class MessagesReceivedArgs : EventArgs
    {
        public List<SMS> Messages { get; set; }
    }

    public class NewSimAddedArgs : EventArgs
    {
        public SimCard Sim { get; set; }
    }
}
