using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading.Tasks.Dataflow;

namespace UTAP
{
    public class SMS
    {
        public string From { get; set; }
        public string To { get; set; }
        public string Body { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PIN
    {
        public int Number { get; set; }
        public string Name { get; set; }
        public string Cmsurn { get; set; }
    }

    public class PAN
    {
        [JsonIgnore]
        public int PIN { get; set; }
        public string Digits { get; set; }
        public string Forenames { get; set; }
        public string Surname { get; set; }
        public string SimNumber { get; set; }
    }

    public class Body
    {
        public string Text { get; set; }
        public bool FromMe { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid MessageId { get; set; }
        public MessageStatus Status { get; set; }
    }

    public class SingleMessage
    {
        public int PIN { get; set; }
        public string PAN { get; set; }
        public Body Body { get; set; }
    }

    public class MessageThread
    {
        [JsonIgnore]
        public int PIN { get; set; }
        public PAN PAN { get; set; }
        public IEnumerable<Body> Bodies { get; set; }
        public bool IsRead { get; set; }
    }

    public class TextMessage
    {
        public string SIM { get; set; }
        public int PIN { get; set; }
        public string PAN { get; set; }
        public string Message { get; set; }
        public int RefNumber { get; set; } 
    }

    public class SimCard : IEquatable<SimCard>
    {
        public string PhoneNumber { get; set; }
        public SerialPort COMPort { get; set; }
        public bool Connected { get; set; }
        public object locker = new object();
        public BufferBlock<SingleMessage> _queue = new BufferBlock<SingleMessage>();

        public bool Equals(SimCard Sim2)
        {
            if (Sim2.PhoneNumber == this.PhoneNumber && Sim2.COMPort == this.COMPort) return true;
            else return false;
        }
    }

    public enum MessageStatus
    {
        SEND_SUCCESSFUL = 100,
        SEND_PENDING = 101,
        SEND_FAILED = 200,
        SEND_NOT_SEND = 300
    }
}