using Microsoft.Maker.Serial;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage.Streams;

namespace io.virtualbreadboard.api
{
    /**
       Implements the Firmata IStream with a VirtualBreadboard.io Lora Stream API

      //////////////////////////////////////////
       // VirtualBreadboard.IO LORA
       //////////////////////////////////////////
       Firmata requires synchronous so provides a synchronous buffer to asynchronous background WebClient Calls

       Class A Lora devices are 'sleepy' polling devices so this is not a real-time stream but is instread

       read/writing into a Queue maintained by VirtualBreadboard.io network server and accessed via the Stream API.

       Security - LoraWAN has integrated security. The AppKey is a shared secret key registered with the VirtualBreadboard.io Lora
       application server. The Stream API interface uses this AppKey to sign data sent and received to the device.

       //////////////////////////////////////////
       // ISTREAM
       //////////////////////////////////////////
       IStream is the stream interface to remote arduino : https://github.com/ms-iot/remote-wiring

       Serial is the transport layer, which provides the physical communication between applications and the Arduino device.
       IStream is the interface which defines the requirements of a communication stream between the Arduino and the application itself.
 
       @author James Caska , www.virtualbreadboard.com
       */

    public class VbbIoTLoraStream : IStream
    {
        private const int MAX_PACKET_SIZE = 10; //Maximum data payloa in a LORA packet.
        private const int GUID_LENGTH = 36;

        private Queue<byte> inputBuffer;
        private Queue<byte> outputBuffer;
        private Queue<byte> commandBuffer;
        private bool taskRunning;
        private bool _IsConnecting;

        public event IStreamConnectionCallback ConnectionEstablished;

        public event IStreamConnectionCallbackWithMessage ConnectionFailed;

        public event IStreamConnectionCallbackWithMessage ConnectionLost;

        private string _appEUI;
        private string _devEUI;
        private string _appKey;
        private int _pollPeriodSeconds;

        private int _sequenceNo; //Tracking the sequence no

        private Dictionary<string, string> _fixedResponses;

        ///Security
        private SymmetricKeyAlgorithmProvider _aesCbcPkcs7;

        private CryptographicKey _aesAppKey;

        /// <summary>
        /// The  AppEUI, AevEUI, AppKey are defined by the LoRa specification for Over-The-Air-Activation 
        /// These keys are obtained by creating an account and registering a device with virtualbreadboard.io network server 
        /// The AppKey is used to secure communications between the UWP App and the virtualbreadboard.io network server
        /// </summary>
        /// <param name="appEUI">Application Id</param>
        /// <param name="devEUI">Device Id</param>
        /// <param name="appKey">Unique Application Encryption Secret Key</param>
        public VbbIoTLoraStream(string appEUI, string devEUI, string appKey, int pollPeriodSeconds)
        {
            _appEUI = appEUI;
            _devEUI = devEUI;
            _appKey = appKey;
            _pollPeriodSeconds = pollPeriodSeconds;
            _fixedResponses = new Dictionary<string, string>();

            inputBuffer = new Queue<byte>();
            outputBuffer = new Queue<byte>();
            commandBuffer = new Queue<byte>();

            _aesCbcPkcs7 = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesCbcPkcs7);

            IBuffer keyBuf = CryptographicBuffer.DecodeFromHexString(appKey.ToUpper());

            // Create an AES 128-bit (16 byte) key
            _aesAppKey = _aesCbcPkcs7.CreateSymmetricKey(keyBuf);
        }

        /**
            Some queries have the same response..
        */

        public void AddFixedResponse(string request, string response)
        {
            _fixedResponses.Add(request, response);
        }

        public class StreamWrite
        {
            public string AppEUI;
            public string DevEUI;
            public string Data;
            public int SequenceNo;

            public StreamWrite(string appEUI, string devEUI, string data, int sequenceNo)
            {
                this.AppEUI = appEUI;
                this.DevEUI = devEUI;
                this.Data = data;
                this.SequenceNo = sequenceNo;
            }
        }

        public class StreamRead
        {
            public StreamRead()
            {
            }

            public string Data { get; set; }
            public int SequenceNo { get; set; }
        }

        /// <summary>
        /// Encrypt with the AppKey
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public byte[] Encrypt(byte[] data)
        {
            // Creata a 16 byte initialization vector
            //Empty: uses prepended guuid as initialisation vector
            IBuffer iv = new byte[_aesCbcPkcs7.BlockLength].AsBuffer();

            // Encrypt the data
            byte[] encryptedData = CryptographicEngine.Encrypt(_aesAppKey, data.AsBuffer(), iv).ToArray();

            return encryptedData;
        }

        /// <summary>
        /// Decrypt with the AppKey
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public byte[] Decrypt(byte[] data)
        {
            //Empty: uses prepended guuid as initialisation vector
            IBuffer iv = new byte[_aesCbcPkcs7.BlockLength].AsBuffer();

            byte[] decryptedData = CryptographicEngine.Decrypt(_aesAppKey, data.AsBuffer(), iv).ToArray();

            return decryptedData;
        }

        /// <summary>
        /// Returns the next paypload to send.
        /// Encrypt[appkey, guuid:data]
        /// </summary>
        private string DequeueOutgoing()
        {
            MemoryStream stream = new MemoryStream();

            string nounce = Guid.NewGuid().ToString();

            stream.Write(System.Text.Encoding.UTF8.GetBytes(nounce), 0, GUID_LENGTH);

            //The first connection will flush the queue and synchronise. Don't send data during this.
            if (!_IsConnecting)
            {
                lock (outputBuffer)
                {
                    int packetSize = Math.Min(outputBuffer.Count, MAX_PACKET_SIZE);

                    while (packetSize != 0)
                    {
                        stream.WriteByte(outputBuffer.Dequeue());
                        packetSize--;
                    }
                }
            }
            byte[] payloadBytes = stream.ToArray();

            payloadBytes = Encrypt(payloadBytes);

            return Convert.ToBase64String(payloadBytes);
        }

        /// <summary>
        /// Invokes the VirtualBreadboard.io REST API interface and exchanges data
        /// </summary>
        /// <returns></returns>
        private async Task<StreamRead> InvokeVbbIoTAPI(string payload)
        {
            //string azureCloud = "http://localhost:57334/api/Stream";
            string azureCloud = " http://vbbiotapi.azurewebsites.net/api/Stream";

            string request = JsonConvert.SerializeObject(new StreamWrite(_appEUI, _devEUI, payload, _sequenceNo));

            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            StringContent firmataMsg = new StringContent(request, Encoding.UTF8, "application/json");

            HttpResponseMessage msg = await client.PostAsync(azureCloud, firmataMsg);

            byte[] response = await msg.Content.ReadAsByteArrayAsync();

            string jsonResponse = System.Text.Encoding.UTF8.GetString(response);

            StreamRead readResponse = JsonConvert.DeserializeObject<StreamRead>(jsonResponse);

            if (readResponse == null)
            {
                throw new Exception("Invalid Server Response");
            }
            _sequenceNo = readResponse.SequenceNo;  //Update the sequence number to mark data received

            return readResponse;
        }

        /// <summary>
        /// In order to seed the CBC encryption without a IV the data prepends a guid in the form guid:data
        /// When guid prefix is verified as a valid guid the data section is enqueued into the input buffer
        /// If is not a valid guid then the server is not valid and could be security risk so the connection is closed.
        /// </summary>
        /// <param name="data"></param>
        ///
        private bool EnqueueIncoming(byte[] data)
        {
            int len = data.Length;

            if (len >= GUID_LENGTH)
            {
                string guidNounce = System.Text.Encoding.UTF8.GetString(data, 0, GUID_LENGTH);
                Guid validGuid;
                if (Guid.TryParse(guidNounce, out validGuid))
                {
                    lock (inputBuffer)
                    {
                        //Is a valid guid
                        for (int i = GUID_LENGTH; i < len; i++)
                        {
                            inputBuffer.Enqueue(data[i]);
                        }
                    }
                }

                return len > GUID_LENGTH;
            }
            else
            {
                throw new Exception("Invalid Server");
            }
        }

        /// <summary>
        /// Exchange Data with the API.
        /// </summary>
        private async Task SendReceiveTask()
        {
            _IsConnecting = true;
            taskRunning = true;

            try
            {
                while (taskRunning)
                {
                    var invokeAPI = await InvokeVbbIoTAPI(DequeueOutgoing());

                    byte[] decrypted = Decrypt(Convert.FromBase64String(invokeAPI.Data));

                    bool dataReceived = EnqueueIncoming(decrypted);

                    if (_IsConnecting)
                    {
                        RaiseConnectionEstablished();
                        _IsConnecting = false;
                    }

                    if (dataReceived)
                    {
                        //Poll faster while active to increase response time.
                        await Task.Delay(1000);
                    }
                    else
                    {
                        await Task.Delay(_pollPeriodSeconds * 1000);
                    }
                }
            }
            catch (Exception e)
            {
                if (_IsConnecting)
                {
                    RaiseConnectionFailed(e.ToString());
                }
                else
                {
                    RaiseConnectionLost();
                }
            }

            taskRunning = false;
        }

        private void RaiseConnectionFailed(string msg)
        {
            if (ConnectionFailed != null) ConnectionFailed(msg);
        }

        private void RaiseConnectionEstablished()
        {
            if (ConnectionEstablished != null) ConnectionEstablished();
        }

        private void RaiseConnectionLost()
        {
            if (ConnectionLost != null && taskRunning)
            {
                RaiseConnectionLost();
            }
            taskRunning = false;
        }

        ushort IStream.available()
        {
            return (ushort)inputBuffer.Count;
        }

        void IStream.begin(uint baud_, SerialConfig config_)
        {
            Task.Factory.StartNew(SendReceiveTask);
        }

        void IStream.end()
        {
            taskRunning = false;
        }

        ushort IStream.read()
        {
            lock (inputBuffer)
            {
                if (inputBuffer.Count == 0)
                {
                    return 0;
                }
                else
                {
                    return inputBuffer.Dequeue();
                }
            }
        }

        public bool connectionReady()
        {
            return true;
        }

        public void flush()
        {
            byte[] packet = commandBuffer.ToArray();

            commandBuffer.Clear();

            string packet64 = Convert.ToBase64String(packet);

            if (_fixedResponses.ContainsKey(packet64))
            {
                packet = Convert.FromBase64String(_fixedResponses[packet64]);
                lock (inputBuffer)
                {
                    foreach (byte b in packet)
                    {
                        inputBuffer.Enqueue(b);
                    }
                }
            }
            else
            {
                lock (outputBuffer)
                {
                    foreach (byte b in packet)
                    {
                        outputBuffer.Enqueue(b);
                    }
                }
            }
        }

        public void @lock()
        {
        }

        public ushort print(byte[] buffer_)
        {
            throw new NotImplementedException();
        }

        public ushort print(double value_, short decimal_place_)
        {
            throw new NotImplementedException();
        }

        public ushort print(double value_)
        {
            throw new NotImplementedException();
        }

        public ushort print(uint value_, Radix base_)
        {
            throw new NotImplementedException();
        }

        public ushort print(uint value_)
        {
            throw new NotImplementedException();
        }

        public ushort print(int value_, Radix base_)
        {
            throw new NotImplementedException();
        }

        public ushort print(int value_)
        {
            throw new NotImplementedException();
        }

        public ushort print(byte c_)
        {
            throw new NotImplementedException();
        }

        public ushort write(byte[] buffer_)
        {
            throw new NotImplementedException();
        }

        public ushort write(byte c_)
        {
            commandBuffer.Enqueue(c_);

            return 0;
        }

        public void unlock()
        {
        }
    }
}