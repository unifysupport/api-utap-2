using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace UTAP
{
    public class SMSArbiter : IHostedService, IDisposable
    {
        UTAPConfiguration _settings;

        private readonly IHubContext<ChatHub> _hubContext;
        public DBManager _DBManager;
        public BufferBlock<SingleMessage> _queue = new BufferBlock<SingleMessage>();
        private readonly Dictionary<string, BufferBlock<SingleMessage>> _simQueues = new Dictionary<string, BufferBlock<SingleMessage>>();
        public List<string> DisconnectedSIMs = new List<string>();

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!String.IsNullOrEmpty(_settings.ATSquirterIP))
                    Task.Run(MessageQueueConsumer);
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

        public SMSArbiter(IOptionsMonitor<UTAPConfiguration> options, IHubContext<ChatHub> chatHub)
        {
            _hubContext = chatHub;
            this._settings = options.CurrentValue;

            if (!String.IsNullOrEmpty(_settings.ATSquirterIP))
            {
                Log.Logger = new LoggerConfiguration()
                .WriteTo.File(_settings.LogFilePath, rollingInterval: RollingInterval.Hour)
                .WriteTo.Console()
                .MinimumLevel.Is((Serilog.Events.LogEventLevel)Enum.Parse(typeof(Serilog.Events.LogEventLevel), _settings.MinimumLogLevel))
                .CreateLogger();

                Log.Information("Starting UTAP...");

                options.OnChange(Listener);

                Log.Information("Starting DBManager with connection string: {conString}", _settings.UCCSConString);
                _DBManager = new DBManager(_settings.UCCSConString, _settings.UCCSReadInterval);

                EnqueuePendingMessages();
            }
        }

        private void Listener(UTAPConfiguration options)
        {
            Log.Information("Configuration file changed. Reloading...");

            _settings = options;

            _DBManager.conString = _settings.UCCSConString;
            _DBManager.readingInterval = _settings.UCCSReadInterval;

            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(_settings.LogFilePath, rollingInterval: RollingInterval.Hour)
                .WriteTo.Console()
                .MinimumLevel.Is((Serilog.Events.LogEventLevel)Enum.Parse(typeof(Serilog.Events.LogEventLevel), _settings.MinimumLogLevel))
                .CreateLogger();
        }

        public async Task<bool> EnqueueMessage(SingleMessage message)
        {
            string sim = await GetSim(message.PIN, message.PAN, "");
            if (sim == null) return false;

            if (!_simQueues.ContainsKey(sim)) _simQueues[sim] = new BufferBlock<SingleMessage>();
            _simQueues[sim].Post(message);
            return true;
        }

        private async Task MessageQueueConsumer()
        {
            Log.Information("Starting SMSArbiter MessageQueueConsumer...");
            while (await _queue.OutputAvailableAsync())
            {
                var buff = await _queue.ReceiveAsync();
                if (!(await EnqueueMessage(buff))) _queue.Post(buff);
            }
        }

        public void StartNewSimQueue(string sim)
        {
            if (!_simQueues.ContainsKey(sim)) _simQueues[sim] = new BufferBlock<SingleMessage>();
            Task.Run(() => SimQueueConsumer(sim));
        }

        private async Task SimQueueConsumer(string sim)
        {
            Log.Information("Starting SMSArbiter SimQueueConsumer for SIM {SIM}...", sim);
            while (await _simQueues[sim].OutputAvailableAsync())
            {
                var buff = await _simQueues[sim].ReceiveAsync();
                
                int remainingResendAttempts = _settings.MessageRetryLimit;
                bool messageResolved = false;

                while (remainingResendAttempts > 0)
                {
                    if (!DisconnectedSIMs.Contains(sim))
                    {
                        Log.Information("Attempting - From: {PIN}, To: {PAN}, Body: {Body}", buff.PIN, buff.PAN, buff.Body.Text);
                        if (ValidatePAN(buff.PIN, buff.PAN))
                        {
                            var refNumber = _DBManager.GetAndUpdateMessageReferenceNumber(buff.PIN, buff.PAN);
                            var sendResult = await PostToModem("/Modem/send", new TextMessage() { SIM = sim, PIN = buff.PIN, PAN = buff.PAN, Message = buff.Body.Text, RefNumber = refNumber });
                            if (sendResult.IsSuccessStatusCode)
                            {
                                buff.Body.Timestamp = DateTime.UtcNow;
                                SaveMessageToDisk(new SingleMessage() { PIN = buff.PIN, PAN = buff.PAN, Body = buff.Body });
                                Log.Information("Successful - From: {PIN}, To: {PAN}, Body: {Body}", buff.PIN, buff.PAN, buff.Body.Text);
                                messageResolved = true;
                                break;
                            }
                            else
                            {
                                Log.Warning("Failure - From: {PIN}, To: {PAN}, Body: {Body}", buff.PIN, buff.PAN, buff.Body.Text);
                            }
                        }
                        else Log.Warning("No PAN match - From: {PIN}, To: {PAN}", buff.PIN, buff.PAN);
                    }
                    else
                    {
                        if (_settings.ReleaseDisconnectedSims)
                        {
                            string newSim = null;
                            while (newSim == null)
                            {
                                newSim = await GetSim(buff.PIN, buff.PAN, sim);
                                await Task.Delay(1000);
                            }

                            if (!_simQueues.ContainsKey(newSim)) _simQueues[newSim] = new BufferBlock<SingleMessage>();
                            _simQueues[newSim].Post(buff);

                            if (_simQueues[sim].Count == 0)
                            {
                                Log.Information("SIM released: {SIM}", sim);
                                return;
                            }
                        }
                        else
                        {
                            _simQueues[sim].Post(buff);
                            Log.Error("!!SIM DISONNECTED!! - {SIM} - {QCount} message(s) in queue", sim, _simQueues[sim].Count);
                            await Task.Delay(10000);
                        }
                    }
                    remainingResendAttempts--;
                }
                if (!messageResolved)
                {
                    _hubContext.Clients.All.SendAsync("MessageFailure", buff.PAN, buff.Body.MessageId);
                    buff.Body.Status = MessageStatus.SEND_FAILED;
                    UpdatePendingMessage(buff);
                }
            }
        }

        private async Task<string> GetSim(int PIN, string PAN, string ReleaseSim)
        {
            string result = null;

            var response = await GetFromModem("/Modem/devices");
            var SimCards = JsonConvert.DeserializeObject<List<string>>(response);

            var pan = _DBManager.PANList.Find(p => p.PIN == PIN && p.Digits == PAN);
            if (pan != null)
            {
                if (pan.SimNumber == ReleaseSim) pan.SimNumber = null;

                if (!String.IsNullOrEmpty(pan.SimNumber))
                {
                    result = SimCards.Find(s => s == pan.SimNumber);
                }
                else
                {
                    var pans = _DBManager.PANList.Where(p => p.Digits == PAN && !String.IsNullOrEmpty(p.SimNumber));

                    if (pans.Count() > 0)
                    {
                        if (SimCards.Exists(s => pans.Where(p => p.SimNumber == s).Count() == 0))
                        {
                            var sims = SimCards.Where(s => pans.Where(p => p.SimNumber == s).Count() == 0).ToList();
                            var minCount = int.MaxValue;
                            foreach (var sim in sims)
                            {
                                if (_simQueues[sim].Count == 0)
                                {
                                    pan.SimNumber = sim;
                                    result = sim;
                                    break;
                                }
                                else if (_simQueues[sim].Count < minCount)
                                {
                                    pan.SimNumber = sim;
                                    result = sim;
                                    minCount = _simQueues[sim].Count;
                                }
                            }
                        }
                    }
                    else
                    {
                        var minCount = int.MaxValue;
                        foreach (var sim in SimCards)
                        {
                            if (_simQueues[sim].Count == 0)
                            {
                                pan.SimNumber = sim;
                                result = sim;
                                break;
                            }
                            else if (_simQueues[sim].Count < minCount)
                            {
                                pan.SimNumber = sim;
                                result = sim;
                                minCount = _simQueues[sim].Count;
                            }
                        }
                    }
                }
            }

            if (result != null)
            {
                var command = "UPDATE [sense].[dbo].[allowed_phone_numbers] SET SimNumber = '" + pan.SimNumber + "' WHERE PIN = " + PIN + " AND p_number = '" + PAN + "'";
                Log.Information(command);
                _DBManager.Enqueue(command);
            }

            return result;
        }

        public void SaveMessageToDisk(SingleMessage message)
        {
            try
            {
                message.Body.Status = MessageStatus.SEND_SUCCESSFUL;
                var jsonString = JsonConvert.SerializeObject(message.Body);
                var TargetPath = Path.Combine(Path.Combine(new string[] { Path.Combine(_settings.ConversationsPath, message.PIN.ToString(), message.PAN) }.Concat(message.Body.Timestamp.ToString("yyyy-MM-dd").Split('-')).ToArray()), ((DateTimeOffset)message.Body.Timestamp).ToUnixTimeMilliseconds() + ".utap");
                Directory.CreateDirectory(Path.GetDirectoryName(TargetPath));
                File.WriteAllText(TargetPath, jsonString);
                Log.Information("Message saved at: {path} - From: {PIN} To: {PAN}", TargetPath, message.PIN, message.PAN);
                _hubContext.Clients.All.SendAsync("ReceiveMessage", message.PIN, message.PAN, message.Body);
                DeletePendingMessage(message);
            }
            catch (Exception ex) 
            {
                Log.Error("Exception on message save - From: {PIN} To: {PAN} Body: {Body} Ex: {ex}", message.PIN, message.PAN, message.Body.Text, ex.Message);
            }
        }

        public bool ValidatePAN(int PIN, string PAN)
        {
            try
            {
                if (_DBManager.PANList.Any(p => p.Digits == PAN && p.PIN == PIN))
                {
                    Log.Information("PASS in ValidatePAN - PIN: {PIN} PAN: {PAN}", PIN, PAN);
                    return true;
                }
            }
            catch { }
            Log.Warning("FAIL in ValidatePAN - PIN: {PIN} PAN: {PAN}", PIN, PAN);
            return false;
        }

        public void StoreInboundMessage(SMS Message)
        {
            Log.Information("Message received - From: {PAN} To: {SIM} Body: {Body}", Message.From, Message.To, Message.Body);
            var PAN = _DBManager.PANList.FirstOrDefault(p => p.Digits == Message.From && p.SimNumber == Message.To);

            if (PAN == null)
            {
                Log.Warning("Unable to find PAN in StoreInboundMessages - From: {PAN} To: {SIM} Body: {Body}", Message.From, Message.To, Message.Body);
            }
            else
            {
                var PIN = PAN.PIN;
                if (ValidatePAN(PIN, Message.From))
                {
                    var sm = new SingleMessage
                    {
                        PIN = PIN,
                        PAN = Message.From,
                        Body = new Body
                        {
                            FromMe = false,
                            Timestamp = Message.Timestamp,
                            Text = Message.Body
                        }
                    };

                    var jsonString = JsonConvert.SerializeObject(sm.Body);
                    var TargetPath = Path.Combine(Path.Combine(new string[] { Path.Combine(_settings.ConversationsPath, PIN.ToString(), Message.From) }.Concat(Message.Timestamp.ToString("yyyy-MM-dd").Split('-')).ToArray()), ((DateTimeOffset)Message.Timestamp).ToUnixTimeMilliseconds() + ".utap");
                    int counter = 1;
                    while (File.Exists(TargetPath))
                    {
                        TargetPath = Path.Combine(Path.Combine(new string[] { Path.Combine(_settings.ConversationsPath, PIN.ToString(), Message.From) }.Concat(Message.Timestamp.ToString("yyyy-MM-dd").Split('-')).ToArray()), ((DateTimeOffset)Message.Timestamp).ToUnixTimeMilliseconds() + "." + counter++.ToString() + ".utap");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(TargetPath));
                    File.WriteAllText(TargetPath, jsonString + Environment.NewLine);
                    Log.Information("Message saved at: {path} - From: {PAN} To: {PIN}", TargetPath, Message.From, PIN);
                    _DBManager.Enqueue(string.Format("UPDATE allowed_phone_numbers SET ConversationRead = 0 WHERE PIN = {0} AND p_number = '{1}'", PIN, Message.From));
                    _hubContext.Clients.All.SendAsync("ReceiveMessage", sm.PIN, sm.PAN, sm.Body);
                }
            }
        }

        public MessageThread GetConversation(int PIN, string PAN, DateTime? BeforeDate)
        {
            MessageThread conversation = new MessageThread() { PIN = PIN, PAN = _DBManager.PANList.Find(pan => pan.Digits == PAN) };
            var rootPath = Path.Combine(_settings.ConversationsPath, PIN.ToString(), PAN);
            if (Directory.Exists(rootPath))
            {
                FileSystemInfo[] files;
                files = new DirectoryInfo(rootPath).GetFileSystemInfos("*.utap", SearchOption.AllDirectories).Where(f => !f.FullName.Contains("Pending"))
                    .OrderByDescending(fi => Path.GetFileNameWithoutExtension(fi.FullName))
                    .ThenBy(fi => Path.GetFileNameWithoutExtension(fi.FullName).Contains('.') ?
                        Path.GetFileNameWithoutExtension(fi.FullName).Substring(Path.GetFileNameWithoutExtension(fi.FullName).IndexOf('.') + 1, Path.GetFileNameWithoutExtension(fi.FullName).Length - Path.GetFileNameWithoutExtension(fi.FullName).IndexOf('.') - 1) :
                        Path.GetFileNameWithoutExtension(fi.FullName)).ToArray();

                var bodies = new List<Body>();
                foreach (FileInfo file in files)
                {
                    var body = JsonConvert.DeserializeObject<Body>(File.ReadAllText(file.FullName));
                    var timestamp = Path.GetFileNameWithoutExtension(file.FullName);
                    if (timestamp.Contains('.')) timestamp = timestamp.Substring(0, timestamp.IndexOf('.'));
                    if (!BeforeDate.HasValue || Convert.ToInt64(timestamp) < ((DateTimeOffset)BeforeDate.Value).ToUnixTimeMilliseconds())
                    {
                        bodies.Add(body);
                        if (bodies.Count == _settings.MessageReadLimit) break;
                    }
                }

                bodies.Reverse();
                conversation.Bodies = bodies;
            }
            return conversation;
        }

        public List<MessageThread> GetAllConversations(int PIN, DateTime? FromDate)
        {
            List<MessageThread> conversations = new List<MessageThread>();
            var rootPath = _settings.ConversationsPath + PIN;
            if (!Directory.Exists(rootPath)) Directory.CreateDirectory(rootPath);
            if (Directory.Exists(rootPath))
            {
                var PANMatches = _DBManager.PANList.Where(pan => pan.PIN == PIN);
                foreach (PAN PAN in PANMatches)
                {
                    MessageThread conversation = new MessageThread() { PIN = PIN, PAN = PAN, IsRead = _DBManager.GetConversationReadStatus(PIN, PAN.Digits) };
                    var rootPathPAN = Path.Combine(_settings.ConversationsPath + PIN.ToString(), PAN.Digits);
                    if (Directory.Exists(rootPathPAN))
                    {
                        FileSystemInfo[] files;
                        files = new DirectoryInfo(rootPathPAN).GetFileSystemInfos("*.utap", SearchOption.AllDirectories).Where(f => !f.FullName.Contains("Pending"))
                            .OrderByDescending(fi => Path.GetFileNameWithoutExtension(fi.FullName))
                            .ThenBy(fi => Path.GetFileNameWithoutExtension(fi.FullName).Contains('.') ?
                                Path.GetFileNameWithoutExtension(fi.FullName).Substring(Path.GetFileNameWithoutExtension(fi.FullName).IndexOf('.') + 1, Path.GetFileNameWithoutExtension(fi.FullName).Length - Path.GetFileNameWithoutExtension(fi.FullName).IndexOf('.') - 1) :
                                Path.GetFileNameWithoutExtension(fi.FullName)).ToArray();

                        var bodies = new List<Body>();
                        foreach (FileInfo file in files)
                        {
                            var body = JsonConvert.DeserializeObject<Body>(File.ReadAllText(file.FullName));
                            var timestamp = Path.GetFileNameWithoutExtension(file.FullName);
                            if (timestamp.Contains('.')) timestamp = timestamp.Substring(0, timestamp.IndexOf('.'));
                            if (!FromDate.HasValue || Convert.ToInt64(timestamp) > ((DateTimeOffset)FromDate.Value).ToUnixTimeMilliseconds())
                            {
                                bodies.Add(body);
                                if (!FromDate.HasValue && bodies.Count == _settings.MessageReadLimit) break;
                            }
                        }

                        bodies.Reverse();

                        var pendingPath = Path.Combine(rootPathPAN, "Pending");
                        if (Directory.Exists(pendingPath))
                        {
                            var pendingFiles = new DirectoryInfo(pendingPath).GetFileSystemInfos("*.utap", SearchOption.AllDirectories)
                                .OrderByDescending(fi => fi.CreationTimeUtc);

                            foreach (FileInfo pendingFile in pendingFiles)
                            {
                                var body = JsonConvert.DeserializeObject<Body>(File.ReadAllText(pendingFile.FullName));
                                bodies.Add(body);
                            }
                        }

                        conversation.Bodies = bodies;
                        conversations.Add(conversation);
                    }
                    else
                    {
                        conversation.Bodies = new List<Body>();
                        conversations.Add(conversation);
                    }
                }
            }
            return conversations;
        }

        public void StorePendingMessage(SingleMessage message)
        {
            var TargetDir = Path.Combine(_settings.ConversationsPath, message.PIN.ToString(), message.PAN, "Pending");
            if (!Directory.Exists(TargetDir)) Directory.CreateDirectory(TargetDir);

            var TargetFile = Path.Combine(TargetDir, message.Body.MessageId + ".utap");
            if (!File.Exists(TargetFile))
            {
                message.Body.Status = MessageStatus.SEND_PENDING;
                var json = JsonConvert.SerializeObject(message.Body);
                File.WriteAllText(TargetFile, json + Environment.NewLine);
            }
        }

        private void DeletePendingMessage(SingleMessage message)
        {
            var TargetDir = Path.Combine(_settings.ConversationsPath, message.PIN.ToString(), message.PAN, "Pending");
            if (!Directory.Exists(TargetDir)) Directory.CreateDirectory(TargetDir);

            var TargetPath = Path.Combine(TargetDir, message.Body.MessageId.ToString()) + ".utap";

            if (File.Exists(TargetPath)) File.Delete(TargetPath);
        }

        private void UpdatePendingMessage(SingleMessage message)
        {
            var TargetDir = Path.Combine(_settings.ConversationsPath, message.PIN.ToString(), message.PAN, "Pending");
            if (!Directory.Exists(TargetDir)) Directory.CreateDirectory(TargetDir);

            var TargetPath = Path.Combine(TargetDir, message.Body.MessageId.ToString()) + ".utap";

            var json = JsonConvert.SerializeObject(message.Body);
            File.WriteAllText(TargetPath, json + Environment.NewLine);
        }

        private void EnqueuePendingMessages()
        {
            var TargetDir = _settings.ConversationsPath;
            var files = Directory.EnumerateFiles(TargetDir, "*.utap", SearchOption.AllDirectories).Where(f => f.Contains("Pending"));

            foreach (var file in files)
            {
                try
                {
                    var message = new SingleMessage();

                    var foldersInRoot = _settings.ConversationsPath.Split(Path.DirectorySeparatorChar).Length;
                    var PIN = file.Split(Path.DirectorySeparatorChar)[foldersInRoot];
                    var PAN = file.Split(Path.DirectorySeparatorChar)[foldersInRoot + 1];

                    message.PIN = Convert.ToInt32(PIN);
                    message.PAN = PAN;
                    message.Body = JsonConvert.DeserializeObject<Body>(File.ReadAllText(file));

                    if (message.Body.Status == MessageStatus.SEND_PENDING) _queue.Post(message);
                }
                catch (Exception ex)
                {
                    Log.Information("EnqueuePendingMessages - Failed to enqueue message at: {File} ex: {Ex}", file, ex.Message);
                }
            }
        }

        private async Task<HttpResponseMessage> PostToModem(string route, object content)
        {
            try
            {
                var client = new HttpClient();

                var data = JsonConvert.SerializeObject(content);

                var response = await client.PostAsync(_settings.ATSquirterIP + route, new StringContent(data, Encoding.UTF8, "application/json"));

                return response;
            }
            catch { }

            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
        }

        private async Task<string> GetFromModem(string route)
        {
            try
            {
                var client = new HttpClient();

                return await client.GetStringAsync(_settings.ATSquirterIP + route);
            }
            catch { }

            return "";
        }
    }
}
