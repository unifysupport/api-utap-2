using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UTAP
{
    public static class UTAPEncoding
    {
        private readonly static Dictionary<(string, int), string[]> multiPartMessages = new Dictionary<(string, int), string[]>();//key: (reference, numberOfParts); value: List of messages
        private readonly static List<char> NonGSMASCIIChars = new List<char>() { '$', '@', '[', '\\', ']', '^', '_', '`', '{', '|', '}', '~' };
        private readonly static string EmojiRegex = @"[\uD800-\uDBFF][\uDC00-\uDFFF]";

        public static List<string> EncodePDU(int PIN, string Destination, string Text, int RefNumber)
        {
            var messages = new List<string>();

            var UCS2Encoding = false;
            for (int i = 0; i < Text.Length; i++)
            {
                if (Text[i] > 127 || NonGSMASCIIChars.Contains(Text[i]))
                {
                    UCS2Encoding = true;

                    break;
                }
            }

            var maxLength = UCS2Encoding ? 70 : 160;
            var UDLLength = UCS2Encoding ? 4 : 8;
            int[] encodedLengths = new int[Text.Length];

            string encoded;
            //Encode in UCS2
            if (UCS2Encoding)
            {
                encoded = EncodeUCS2(Text);

                Regex messageMatcher = new Regex(EmojiRegex);
                var messageMatch = messageMatcher.Match(Text);
                while (messageMatch.Success)
                {
                    encodedLengths[messageMatch.Index] = messageMatch.Length * 4;

                    messageMatch = messageMatch.NextMatch();
                }

                for (int i = 0; i < encodedLengths.Length; i++)
                {
                    if ((i > 0 && encodedLengths[i] == 0 && encodedLengths[i - 1] != 8) || (i == 0 && encodedLengths[i] == 0)) encodedLengths[i] = 4;
                }
            }
            else
            {
                encoded = Encode7Bit(Text);
            }

            var messageParts = new List<string>();
            if (!UCS2Encoding && Text.Length < maxLength) messageParts.Add(Text);
            else if (UCS2Encoding && encodedLengths.Sum() < maxLength) messageParts.Add(Text);
            else if (!UCS2Encoding)
            {
                for (int i = 0; i < Text.Length; i += maxLength - UDLLength)
                {
                    if (Text.Length > i + maxLength - UDLLength)
                        messageParts.Add(Text.Substring(i, maxLength - UDLLength));
                    else
                        messageParts.Add(Text.Substring(i));
                }
            }
            else
            {
                var sum = 0;
                var _position = 0;
                for (int i = 0; i < Text.Length; i++)
                {
                    sum += encodedLengths[i];
                    if (sum > 66 * 4)
                    {
                        messageParts.Add(Text[_position..i]);
                        sum = encodedLengths[i];
                        _position = i;
                    }
                    else if (sum == 66 * 4)
                    {
                        messageParts.Add(Text[_position..(i + 2)]);
                        sum = 0;
                        _position = i + 2;
                    }
                }

                if (sum > 0) messageParts.Add(Text.Substring(_position));
            }

            var encodedChars = maxLength / 8.0D * 7 * 2;
            if ((int)encodedChars % 2 != 0) encodedChars += 1;
            if (messageParts.Count > 1) encodedChars -= 14;

            if (Destination.StartsWith('0')) Destination = "44" + Destination.Remove(0, 1);

            var position = 0;
            var encodedPosition = 0;
            for (int x = 0; x < messageParts.Count; x++)
            {
                //00 forces stick to use pre-assigned SMSC number
                var result = "00";

                if (messageParts.Count > 1)
                    result += "5";
                else
                    result += "1";

                //Specifies SMS-SUBMIT
                result += "1";

                //Message Reference Number - 00 indicates device should assign a number itself
                result += "00";

                //Specify length of destination number
                result += Destination.Length.ToString("X2");

                //Specifies type of destination number as being in internatiol format
                result += "91";

                //Append F to Destination if odd number of digits
                if (Destination.Length % 2 != 0) Destination += "F";

                //Reverse each pair of digits in Destination and append to result
                for (int i = 0; i < Destination.Length; i += 2)
                {
                    result += Destination.Substring(i + 1, 1) + Destination.Substring(i, 1);
                }

                //Protocol identifier
                result += "00";

                //Data coding - 08 indicates UCS2
                if (UCS2Encoding) result += "08";
                else result += "00";

                //Validity period
                result += "A7";

                var numberOfEncodedCharacters = encodedLengths.Skip(position).Take(messageParts[x].Length).Sum();
                int dataLength;
                if (UCS2Encoding)
                {
                    if (messageParts.Count > 1) dataLength = (numberOfEncodedCharacters + 14) / 2;
                    else dataLength = encodedLengths.Sum() / 2;

                    position += messageParts[x].Length;
                }
                else
                {
                    if (messageParts.Count > 1)
                        dataLength = messageParts[x].Length + 8;
                    else
                        dataLength = messageParts[x].Length;
                }

                result += dataLength.ToString("X2");

                if (messageParts.Count > 1)
                {
                    //Specifies length of header
                    result += "06";

                    //Identifier to concatenated message
                    result += "08";

                    //Specifies length of information element
                    result += "04";

                    //Message reference number
                    result += RefNumber.ToString("X4");

                    //Number of parts
                    result += messageParts.Count.ToString("X2");

                    //Current part
                    result += (x + 1).ToString("X2");
                }

                if (!UCS2Encoding)
                {
                    if (encoded.Length > (x * encodedChars) + encodedChars)
                        result += encoded.Substring(x * (int)encodedChars, (int)encodedChars);
                    else
                        result += encoded.Substring(x * (int)encodedChars);
                }
                else
                {
                    result += encoded.Substring(encodedPosition, numberOfEncodedCharacters);
                    encodedPosition += numberOfEncodedCharacters;
                }

                messages.Add(result);
            }

            return messages;
        }

        public static string EncodeUCS2(string Text)
        {
            string encoded;
            var ba = Encoding.BigEndianUnicode.GetBytes(Text);
            encoded = BitConverter.ToString(ba);
            encoded = encoded.Replace("-", "");
            return encoded;
        }

        public static string ParseHexToText(string hexString, bool multiPart)
        {
            var result = "";
            var hexArray = new string[hexString.Length / 2];

            for (int i = 0; i < hexArray.Length; i++)
            {
                hexArray[i] = hexString.Substring(i * 2, 2);
            }

            var extras = "";
            var byteList = new List<Byte>();
            for (int x = 0; x < hexArray.Length; x++)
            {
                var binary = String.Join(String.Empty, hexArray[x].Select(c => Convert.ToString(Convert.ToInt32(c.ToString(), 16), 2).PadLeft(4, '0')));
                binary += extras;

                var sevenBits = "";
                if (x == 0 && multiPart)
                    sevenBits = binary.Substring(0, 7);
                else
                {
                    sevenBits = binary.Substring(binary.Length - 7);
                    extras = binary[0..^7];
                }
                byteList.Add(Convert.ToByte(sevenBits.PadLeft(4, '0'), 2));
            }
            while (extras.Length >= 7)
            {
                var sevenBits = extras.Substring(extras.Length - 7);
                if (sevenBits == "0000000") break;
                byteList.Add(Convert.ToByte(sevenBits.PadLeft(4, '0'), 2));
                extras = extras[0..^7];
            }
            result = Encoding.ASCII.GetString(byteList.ToArray());
            return result;
        }

        public static string DecodeUCS2(string encoded)
        {
            var decoded = "";
            for (int i = 0; i < encoded.Length; i += 4)
            {
                decoded += @"\u" + encoded.Substring(i, 4);
            }
            return Regex.Unescape(decoded);
        }

        public static string Encode7Bit(string Message)
        {
            var result = "";
            var binary = string.Join(" ", Encoding.ASCII.GetBytes(Message).Select(byt => Convert.ToString(byt, 2).PadLeft(8, '0')));
            var parts = binary.Split(' ');

            var packedBinary = "";
            var numberOfDigitstoMove = 1;
            for (int i = 0; i < parts.Length; i++)
            {
                if (i == 0 || (i + 1) % 8 != 0)
                {
                    var digitsToMove = i + 1 == parts.Length ? new string('0', numberOfDigitstoMove) : parts[i + 1].Substring(8 - numberOfDigitstoMove, numberOfDigitstoMove);
                    if (i + 1 < parts.Length) parts[i + 1] = parts[i + 1].Substring(0, 8 - numberOfDigitstoMove);
                    var firstZeroRemoved = parts[i].Substring(1);

                    packedBinary += digitsToMove + firstZeroRemoved;

                    numberOfDigitstoMove++;

                    if (numberOfDigitstoMove == 8) numberOfDigitstoMove = 1;
                }
            }

            for (int i = 0; i < packedBinary.Length; i += 8)
            {
                result += Convert.ToInt32(packedBinary.Substring(i, 8), 2).ToString("X2");
            }
            return result;
        }

        public static string DecodePDUSender(string Message)
        {
            var result = "";

            var smscLength = Convert.ToInt32(Message.Substring(0, 2));
            var postSMSCIndex = (smscLength * 2) + 2;

            var _senderLength = Message.Substring(postSMSCIndex + 2, 2);
            var senderLength = int.Parse(_senderLength, System.Globalization.NumberStyles.HexNumber);

            var reversed = Message.Substring(postSMSCIndex + 6, senderLength);

            for (int i = 0; i < reversed.Length; i += 2)
            {
                result += reversed.Substring(i + 1, 1) + reversed.Substring(i, 1);
            }

            return result;
        }

        public static string DecodePDUMessage(string Message)
        {
            var smscLength = Convert.ToInt32(Message.Substring(0, 2));
            var postSMSCIndex = (smscLength * 2) + 2;

            var multiPart = Message.Substring(postSMSCIndex, 2) == "44" || Message.Substring(postSMSCIndex, 2) == "40";

            var _senderLength = Message.Substring(postSMSCIndex + 2, 2);
            var senderLength = int.Parse(_senderLength, System.Globalization.NumberStyles.HexNumber);

            var postSenderIndex = postSMSCIndex + 6 + senderLength;

            var encoding = Message.Substring(postSenderIndex + 2, 2);

            var result = Message;
            if (encoding == "00")
                result = ParseHexToText(Message.Substring(postSenderIndex + (multiPart ? 32 : 20)).Trim(), multiPart);
            else if (encoding == "08")
                result = DecodeUCS2(Message.Substring(postSenderIndex + (multiPart ? 32 : 20)).Trim());

            if (multiPart)
            {
                var messageReference = Message.Substring(postSenderIndex + 26, 2);
                var numberOfParts = Convert.ToInt32(Message.Substring(postSenderIndex + 28, 2), 16);
                var partNUmber = Convert.ToInt32(Message.Substring(postSenderIndex + 30, 2), 16);
                if (!multiPartMessages.ContainsKey((messageReference, numberOfParts)))
                    multiPartMessages[(messageReference, numberOfParts)] = new string[numberOfParts];

                multiPartMessages[(messageReference, numberOfParts)][partNUmber - 1] = result;

                if (multiPartMessages[(messageReference, numberOfParts)].Where(m => m == null).Count() == 0)
                {
                    result = String.Join(string.Empty, multiPartMessages[(messageReference, numberOfParts)]);
                    multiPartMessages.Remove((messageReference, numberOfParts));
                }
                else
                    result = null;
            }

            return result;
        }
    }
}
