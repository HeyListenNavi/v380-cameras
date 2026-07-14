using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace V380Decoder.src
{
    public class V380SpeakClient : IDisposable
    {
        private const int PORTNUM = 8800;
        private static readonly byte[] V380_KEY = Encoding.ASCII.GetBytes("macrovideo+*#!^@");

        private readonly uint _deviceId;
        private readonly string _username;
        private readonly string _password;
        private readonly string _ip;
        private readonly int _port;

        private TcpClient _streamClient;
        private NetworkStream _streamNs;
        private byte[] _aesKey;
        private byte[] _handleBytes;
        private byte _cameraVersion;
        private bool _encryptData;
        private int _packetSentCount = 0;
        private bool _connected = false;

        private readonly AudioUtils.ImaAdpcmEncoder _encoder = new AudioUtils.ImaAdpcmEncoder();

        public V380SpeakClient(uint deviceId, string username, string password, string ip, int port = PORTNUM)
        {
            _deviceId = deviceId;
            _username = username;
            _password = password;
            _ip = ip;
            _port = port;
        }

        public bool Connect()
        {
            try
            {
                // 1. Connect to fetch handle
                using (var authClient = new TcpClient())
                {
                    var authResult = authClient.BeginConnect(_ip, _port, null, null);
                    if (!authResult.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                    {
                        Console.Error.WriteLine("[SPEAK] Auth connection timeout");
                        return false;
                    }
                    authClient.EndConnect(authResult);

                    using (var authNs = authClient.GetStream())
                    {
                        byte[] loginPacket = GenerateLoginPacket();
                        authNs.Write(loginPacket, 0, loginPacket.Length);
                        authNs.Flush();

                        byte[] response = new byte[1024];
                        int bytesRead = authNs.Read(response, 0, response.Length);
                        if (bytesRead < 17 || response[0] != 0x90 || response[1] != 0x04)
                        {
                            Console.Error.WriteLine($"[SPEAK] Invalid login response. Length: {bytesRead}");
                            return false;
                        }

                        _cameraVersion = response[12];
                        _encryptData = _cameraVersion > 30;
                        _handleBytes = new byte[4];
                        Array.Copy(response, 13, _handleBytes, 0, 4);
                        Console.Error.WriteLine($"[SPEAK] Login success. Version: {_cameraVersion}, Handle: {BitConverter.ToString(_handleBytes)}, Encrypt: {_encryptData}");
                    }
                }

                if (_encryptData)
                {
                    _aesKey = GenerateMagicKey(_handleBytes);
                }

                // 2. Connect stream socket
                _streamClient = new TcpClient();
                _streamClient.NoDelay = true;
                var streamResult = _streamClient.BeginConnect(_ip, _port, null, null);
                if (!streamResult.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    Console.Error.WriteLine("[SPEAK] Stream connection timeout");
                    return false;
                }
                _streamClient.EndConnect(streamResult);
                _streamNs = _streamClient.GetStream();

                // 3. Send audio handshake
                byte[] handshake = GenerateAudioHandshake(_handleBytes);
                _streamNs.Write(handshake, 0, handshake.Length);
                _streamNs.Flush();

                // Start reader thread to keep socket alive and process incoming data
                _connected = true;
                Task.Run(() => ListenLoop());

                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SPEAK] Connect failed: {ex.Message}");
                Dispose();
                return false;
            }
        }

        private void ListenLoop()
        {
            byte[] buf = new byte[1024];
            try
            {
                while (_connected && _streamNs != null)
                {
                    int n = _streamNs.Read(buf, 0, buf.Length);
                    if (n <= 0)
                    {
                        Console.Error.WriteLine("[SPEAK] Connection closed by camera");
                        break;
                    }
                }
            }
            catch { }
            finally
            {
                _connected = false;
            }
        }

        public void SendAudioFrame(byte[] alawChunk)
        {
            if (!_connected)
            {
                Console.Error.WriteLine("[SPEAK] Not connected, attempting reconnect...");
                if (!Connect()) return;
            }

            if (alawChunk.Length != 505)
            {
                throw new ArgumentException("Audio chunk must be exactly 505 bytes of G.711 A-law.");
            }

            // Decode G.711 A-law to 16-bit PCM (505 samples, 1010 bytes)
            short[] pcm = AudioUtils.ALawToLinear(alawChunk);

            // Encode to ADPCM (256 bytes)
            byte[] adpcm = _encoder.EncodeBlock(pcm);

            // Encrypt if necessary
            byte[] payload = adpcm;
            if (_encryptData && _aesKey != null)
            {
                payload = EncryptAesEcb(payload);
            }

            // Generate header
            byte[] header = GenerateAudioPayloadHeader(_packetSentCount++);
            
            // Assemble packet: 16-byte header + 256-byte payload = 272 bytes
            byte[] packet = new byte[header.Length + payload.Length];
            Array.Copy(header, 0, packet, 0, header.Length);
            Array.Copy(payload, 0, packet, header.Length, payload.Length);

            try
            {
                _streamNs.Write(packet, 0, packet.Length);
                _streamNs.Flush();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SPEAK] Write error: {ex.Message}");
                _connected = false;
            }
        }

        private byte[] EncryptAesEcb(byte[] data)
        {
            using var aes = Aes.Create();
            aes.Key = _aesKey;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var encryptor = aes.CreateEncryptor();
            byte[] output = new byte[data.Length];
            encryptor.TransformBlock(data, 0, data.Length, output, 0);
            return output;
        }

        private byte[] GenerateLoginPacket()
        {
            string saltChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var rng = new Random();
            byte[] salt = new byte[16];
            for (int i = 0; i < 16; i++) salt[i] = (byte)saltChars[rng.Next(saltChars.Length)];

            byte[] encryptedPass = EncryptAesEcbWithPadding(V380_KEY, Encoding.ASCII.GetBytes(_password));
            byte[] finalPass = EncryptAesEcbWithPadding(salt, encryptedPass);

            string saltHex = Convert.ToHexString(salt).ToLower();
            string finalPassHex = Convert.ToHexString(finalPass).ToLower();

            byte[] camIdBytes = BitConverter.GetBytes(_deviceId);
            if (!BitConverter.IsLittleEndian) Array.Reverse(camIdBytes);
            string camHex = Convert.ToHexString(camIdBytes).ToLower();

            string date = "2025-12-08 19:44:29";
            byte[] dateBytes = Encoding.ASCII.GetBytes(date);
            string dateHex = Convert.ToHexString(dateBytes).ToLower();

            byte[] userBytes = new byte[32];
            byte[] userRaw = Encoding.ASCII.GetBytes(_username);
            Array.Copy(userRaw, userBytes, Math.Min(userRaw.Length, 32));
            string userHex = Convert.ToHexString(userBytes).ToLower();

            string loginHexStr = "8f040000780000001f0a000000";
            loginHexStr += camHex + dateHex + "00000000000000000000000000" + userHex + saltHex + finalPassHex;

            byte[] loginPacket = Convert.FromHexString(loginHexStr);
            Array.Resize(ref loginPacket, 512); // Pad to 512 bytes with 0
            return loginPacket;
        }

        private static byte[] EncryptAesEcbWithPadding(byte[] key, byte[] data)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            return encryptor.TransformFinalBlock(data, 0, data.Length);
        }

        private static byte[] GenerateMagicKey(byte[] handleBytes)
        {
            byte[] key = new byte[16];
            Array.Copy(handleBytes, key, Math.Min(handleBytes.Length, 4));

            ulong magic1 = 0x618123462C14795CUL;
            byte[] magic1Bytes = BitConverter.GetBytes(magic1);
            if (!BitConverter.IsLittleEndian) Array.Reverse(magic1Bytes);
            Array.Copy(magic1Bytes, 0, key, 4, 8);

            uint magic2 = 0x82800DF0;
            byte[] magic2Bytes = BitConverter.GetBytes(magic2);
            if (!BitConverter.IsLittleEndian) Array.Reverse(magic2Bytes);
            Array.Copy(magic2Bytes, 0, key, 12, 4);

            return key;
        }

        private byte[] GenerateAudioHandshake(byte[] handleBytes)
        {
            byte[] camIdBytes = BitConverter.GetBytes(_deviceId);
            if (!BitConverter.IsLittleEndian) Array.Reverse(camIdBytes);

            byte[] handshake = new byte[85];
            handshake[0] = 0x79;
            handshake[1] = 0x01;
            handshake[2] = 0x00;
            handshake[3] = 0x00;
            Array.Copy(camIdBytes, 0, handshake, 4, 4);
            Array.Copy(handleBytes, 0, handshake, 8, 4);
            return handshake;
        }

        private static byte[] GenerateAudioPayloadHeader(int packetSentCount)
        {
            byte seqByte = (byte)((packetSentCount + 1) % 256);
            byte[] prefix = Convert.FromHexString("b40000000100160000000000000001");
            byte[] header = new byte[16];
            Array.Copy(prefix, header, 15);
            header[15] = seqByte;
            return header;
        }

        public void Dispose()
        {
            _connected = false;
            _streamNs?.Dispose();
            _streamClient?.Dispose();
        }
    }
}
