﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Org.Mentalis.Network.ProxySocket;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace APBWatcher
{
    enum LobbyOpCodes : uint
    {
        LS2GC_ERROR = 2000,
        LS2GC_LOGIN_PUZZLE = 2002,
        LS2GC_LOGIN_SALT = 2003,
        LS2GC_ANS_LOGIN_SUCCESS = 2004,
        LS2GC_ANS_LOGIN_FAILED = 2005,
    }

    struct ErrorData
    {
        public uint MessageId;
        public ushort QueryId;
        public uint ReturnCode;
        public uint Param1;
        public uint Param2;
        public uint Param3;
        public uint Param4;
    }

    struct LoginFailedData
    {
        public uint ReturnCode;
        public string CountryCode;
    }

    class LobbyClient
    {
        private const int RECV_BUFFER_SIZE = 65535;

        private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        ProxySocket m_socket;
        byte[] m_recvBuffer = new byte[RECV_BUFFER_SIZE];
        int m_recvOffset = 0;
        EncryptionProvider m_encryption = new EncryptionProvider();
        byte[] m_srpKey = null;

        public event EventHandler OnConnectSuccess = delegate { };
        public event EventHandler<Exception> OnConnectFailed = delegate { };
        public event EventHandler OnDisconnect = delegate { };
        public event EventHandler<ErrorData> OnError = delegate { };
        public event EventHandler<int> OnPuzzleFailed = delegate { };
        public event EventHandler<LoginFailedData> OnLoginFailed = delegate { };
        public event EventHandler OnLoginSuccess = delegate { };

        string m_username;
        string m_password;

        public LobbyClient(string username, string password)
        {
            m_username = username;
            m_password = password;
        }

        private void ConnectInternal(string host, int port)
        {
            Log.Info(String.Format("Connecting to {0}:{1}", host, port));
            m_socket.BeginConnect(host, port, new AsyncCallback(ConnectCallback), null);
        }

        public void Connect(string host, int port)
        {
            m_socket = new ProxySocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            ConnectInternal(host, port);
        }

        public void ConnectProxy(string host, int port, string proxyIP, int proxyPort, string proxyUsername, string proxyPassword)
        {
            m_socket = new ProxySocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            m_socket.ProxyEndPoint = new IPEndPoint(IPAddress.Parse(proxyIP), proxyPort);
            m_socket.ProxyType = ProxyTypes.Socks5;
            if (proxyUsername != null && proxyPassword != null)
            {
                m_socket.ProxyUser = proxyUsername;
                m_socket.ProxyPass = proxyPassword;
            }
            
            ConnectInternal(host, port);
        }

        private void ConnectCallback(IAsyncResult ar)
        {
            try
            {
                // Finish connecting
                m_socket.EndConnect(ar);
                Log.Info("Successfully connected");
                OnConnectSuccess(this, null);

                // Start receiving
                BeginReceive();
            }
            catch (Exception e)
            {
                Log.Error("Failed to connect", e);
                OnConnectFailed(this, e);
            }
        }

        private void BeginReceive()
        {
            int length = m_recvBuffer.Length;
            m_socket.BeginReceive(m_recvBuffer, m_recvOffset, length - m_recvOffset, SocketFlags.None, new AsyncCallback(ReceiveCallback), null);
        }

        private void Disconnect()
        {
            if (m_socket == null)
            {
                return;
            }

            try
            {
                m_socket.Disconnect(false);
                m_socket.Close();
                m_socket = null;
            }
            catch (Exception e)
            {
                Log.Warn("Error occurred while disconnecting from socket", e);
            }

            OnDisconnect(this, null);
        }

        private void ReceiveCallback(IAsyncResult ar)
        {
            try
            {
                int length = m_socket.EndReceive(ar);
                if (length <= 0)
                {
                    Log.Warn(String.Format("Received invalid packet length {0}, disconnecting", length));
                    Disconnect();
                    return;
                }

                Log.Debug(String.Format("Received packet, length={0}", length));

                // Decrypt packet if need be
                if (m_encryption.Initialized)
                {
                    m_encryption.DecryptServerData(m_recvBuffer, 4, length - 4);
                }

                // Construct new packet
                Log.Debug(String.Format("Size field = {0}", BitConverter.ToUInt32(m_recvBuffer, 0)));

                ServerPacket packet = new ServerPacket(m_recvBuffer, 4, m_recvOffset + length - 4);
                Log.Debug(Environment.NewLine + HexDump(packet.Data));

                if (packet.OpCode == (uint)LobbyOpCodes.LS2GC_LOGIN_PUZZLE)
                {
                    Log.Info("Receive [LS2GC_LOGIN_PUZZLE]");
                    HandleLoginPuzzle(packet);
                } 
                else if (packet.OpCode == (uint)LobbyOpCodes.LS2GC_ERROR)
                {
                    Log.Info("Receive [LS2GC_ERROR]");
                    HandleError(packet);
                }
                else if (packet.OpCode == (uint)LobbyOpCodes.LS2GC_ANS_LOGIN_FAILED)
                {
                    Log.Info("Receive [LS2GC_ANS_LOGIN_FAILED]");
                    HandleLoginFailed(packet);
                }
                else if (packet.OpCode == (uint)LobbyOpCodes.LS2GC_LOGIN_SALT)
                {
                    Log.Info("Receive [LS2GC_LOGIN_SALT]");
                    HandleLoginSalt(packet);
                }
                else if (packet.OpCode == (uint)LobbyOpCodes.LS2GC_ANS_LOGIN_SUCCESS)
                {
                    Log.Info("Receive [LS2GC_ANS_LOGIN_SUCCESS]");
                    HandleLoginSuccess(packet);
                }

                if (m_socket != null)
                {
                    BeginReceive();
                }
            }
            catch (Exception e)
            {
                Log.Warn("Exception occurred while receiving, disconnecting", e);
                Disconnect();
            }
        }

        public void HandleLoginSuccess(ServerPacket packet)
        {
            var reader = packet.Reader;

            string realTag = reader.ReadUnicodeString(50);
            uint accountPremium = reader.ReadUInt32();
            ulong timeStamp = reader.ReadUInt64();
            ulong accountPermissions = reader.ReadUInt64();

            Log.Debug(String.Format("m_szRealTag = {0}", realTag));
            Log.Debug(String.Format("m_nAccountPremium = {0}", accountPremium));
            Log.Debug(String.Format("m_nTimestamp = {0}", timeStamp));
            Log.Debug(String.Format("m_nAccountPermissions = {0}", accountPermissions));

            for (int i = 0; i < 5; i++)
            {
                Log.Debug(String.Format("m_nConfigFileVersion[{0}] = {1}", i, reader.ReadInt32()));
            }

            ushort voicePortMin = reader.ReadUInt16();
            ushort voicePortMax = reader.ReadUInt16();
            uint voiceAccountId = reader.ReadUInt32();
            string voiceUsername = reader.ReadASCIIString(17);
            string voiceKey = reader.ReadASCIIString(17);

            Log.Debug(String.Format("m_nVoicePortMin = {0}", voicePortMin));
            Log.Debug(String.Format("m_nVoicePortMax = {0}", voicePortMax));
            Log.Debug(String.Format("m_nVoiceAccountID = {0}", voiceAccountId));
            Log.Debug(String.Format("m_szVoiceUsername = {0}", voiceUsername));
            Log.Debug(String.Format("m_szUnknownVoiceKey = {0}", voiceKey));

            // Start reading the server's public key, starting with BLOBHEADER
            byte type = reader.ReadByte();
            byte version = reader.ReadByte();
            reader.ReadUInt16(); // Skip reserved word
            uint algId = reader.ReadUInt32();

            if (type != 6 || version != 2 || algId != 0x0000A400)
            {
                Log.Error(String.Format("Unexpected public key header (Type = {0}, Version = {1}, AlgId = {2})", type, version, algId));
                return; // TODO: probably disconnect or something
            }

            // Read the RSAPUBKEY part
            byte[] magic = reader.ReadBytes(4);
            if (magic[0] != 0x52 || magic[1] != 0x53 || magic[2] != 0x41 || magic[3] != 0x31)
            {
                Log.Error(String.Format("Incorrect RSAPUBKEY magic ({0}, {1}, {2}, {3})", magic[0], magic[1], magic[2], magic[3]));
                return; // TODO: probably disconnect or something
            }

            uint bitLength = reader.ReadUInt32();
            byte[] exponent = reader.ReadBytes(4);
            Array.Reverse(exponent); // MS CryptoAPI uses little endian, everything else uses big endian

            // Read the data part
            byte[] modulus = reader.ReadBytes((int)bitLength / 8);
            Array.Reverse(modulus);

            // Read the rest of the packet data
            string countryCode = reader.ReadUnicodeString();
            string voiceURL = reader.ReadASCIIString();

            Log.Debug(String.Format("m_nCountryCode = {0}", countryCode));
            Log.Debug(String.Format("m_szVoiceURL = {0}", voiceURL));

            // Create a new random RSA 1024 bit keypair for the client
            var generator = new RsaKeyPairGenerator();
            generator.Init(new KeyGenerationParameters(new SecureRandom(), 1024));
            AsymmetricCipherKeyPair clientKeyPair = generator.GenerateKeyPair();
            RsaKeyParameters clientPub = (RsaKeyParameters)clientKeyPair.Public;

            // Put the client public key into the Microsoft Crypto API format
            byte[] clientPubData = new byte[148];

            byte[] header = {
                0x06, 0x02, 0x00, 0x00, 0x00, 0xA4, 0x00, 0x00, 0x52, 0x53, 0x41, 0x31, 0x00, 0x04, 0x00, 0x00 
            };
            Array.Copy(header, clientPubData, header.Length);

            byte[] exponentData = clientPub.Exponent.ToByteArrayUnsigned();
            Array.Reverse(exponentData);
            Array.Copy(exponentData, 0, clientPubData, 16, exponentData.Length);

            byte[] modulusData = clientPub.Modulus.ToByteArrayUnsigned();
            Array.Reverse(modulusData);
            Array.Copy(modulusData, 0, clientPubData, 20, modulusData.Length);

            // Create a public key for the server
            var serverPub = new RsaKeyParameters(false, new BigInteger(1, modulus), new BigInteger(1, exponent));

            // Create encryption engine with the server's public key
            var encryptEngine = new Pkcs1Encoding(new RsaEngine());
            encryptEngine.Init(true, serverPub);

            // TODO: maybe do this properly rather than statically
            byte[] encData1 = encryptEngine.ProcessBlock(clientPubData, 0, 117);
            byte[] encData2 = encryptEngine.ProcessBlock(clientPubData, 117, 31);
            Array.Reverse(encData1);
            Array.Reverse(encData2);

            // Use the SRP key we calculated before
            m_encryption.SetKey(m_srpKey);

            byte[] fullEncData = new byte[encData1.Length + encData2.Length];
            Buffer.BlockCopy(encData1, 0, fullEncData, 0, encData1.Length);
            Buffer.BlockCopy(encData2, 0, fullEncData, encData1.Length, encData2.Length);

            var keyExchange = new Lobby.GC2LS_KEY_EXCHANGE(fullEncData);
            SendPacket(keyExchange);

            OnLoginSuccess(this, null);
        }

        public void HandleError(ServerPacket packet)
        {
            var reader = packet.Reader;

            ErrorData data = new ErrorData
            {
                MessageId = reader.ReadUInt32(),
                QueryId = reader.ReadUInt16(),
                ReturnCode = reader.ReadUInt32(),
                Param1 = reader.ReadUInt32(),
                Param2 = reader.ReadUInt32(),
                Param3 = reader.ReadUInt32(),
                Param4 = reader.ReadUInt32()
            };

            Log.Error(
                String.Format(
                    "An error occurred with interacting with the Lobby server: messageId={0}, queryId={1}, returnCode={2}, param1={3}, param2={4}, param3={5}, param4={6}",
                    data.MessageId,
                    data.QueryId,
                    data.ReturnCode,
                    data.Param1,
                    data.Param2,
                    data.Param3,
                    data.Param4
                )
            );

            OnError(this, data);
            Disconnect();
        }

        private static string APBMod = "11144252439149533417835749556168991736939157778924947037200268358613863350040339017097790259154750906072491181606044774215413467851989724116331597513345603";

        private static byte[] APBg = {
            0x02
        };

        public void HandleLoginSalt(ServerPacket packet)
        {
            var reader = packet.Reader;

            // Initialise the SRP client
            var srpClient = new WeakSrp6Client();
            var secureRandom = new SecureRandom();
            srpClient.Init(new BigInteger(APBMod), new BigInteger(APBg), new Sha1Digest(), secureRandom);

            // Read data from packet
            uint accountId = reader.ReadUInt32();
            byte[] serverB = reader.ReadBytes(64);
            ushort serverBLen = reader.ReadUInt16();
            byte[] salt = reader.ReadBytes(10);

            Log.Info(String.Format("Account ID = 0x{0:X}", accountId));

            // Transform values into types the SRP client expects
            BigInteger serverBInt = new BigInteger(1, serverB, 0, serverBLen);
            byte[] usernameBytes = Encoding.ASCII.GetBytes(accountId.ToString());
            byte[] passwordBytes = Encoding.ASCII.GetBytes(m_password);

            // Calculate the client's public value
            BigInteger clientPub = srpClient.GenerateClientCredentials(salt, usernameBytes, passwordBytes);

            srpClient.CalculateSecret(serverBInt);
            Console.WriteLine(HexDump(srpClient.CalculateSessionKey().ToByteArrayUnsigned()));
            m_srpKey = srpClient.CalculateSessionKey().ToByteArrayUnsigned();

            // Calculate the proof that the client knows the secret
            BigInteger proof = srpClient.CalculateClientEvidenceMessage();

            Console.WriteLine(HexDump(clientPub.ToByteArrayUnsigned()));
            Console.WriteLine(HexDump(proof.ToByteArrayUnsigned()));

            var loginProof = new Lobby.GC2LS_LOGIN_PROOF(clientPub.ToByteArrayUnsigned(), proof.ToByteArrayUnsigned());
            SendPacket(loginProof);
        }

        public void HandleLoginFailed(ServerPacket packet)
        {
            var reader = packet.Reader;

            LoginFailedData data = new LoginFailedData
            {
                ReturnCode = reader.ReadUInt32(),
                CountryCode = reader.ReadUnicodeString(48)
            };

            Log.Error(
                String.Format(
                    "Login failed: returnCode={0}, countryCode={1}",
                    data.ReturnCode,
                    data.CountryCode
                )
            );

            OnLoginFailed(this, data);
            Disconnect();
        }

        public void HandleLoginPuzzle(ServerPacket packet)
        {
            var reader = packet.Reader;

            int versionHigh = reader.ReadInt32();
            int versionMiddle = reader.ReadInt32();
            int versionLow = reader.ReadInt32();
            int buildNo = reader.ReadInt32();

            Log.Info(String.Format("Server Version: {0}.{1}.{2}.{3}", versionHigh, versionMiddle, versionLow, buildNo));

            byte unknown = reader.ReadByte();
            Log.Debug(String.Format("Unknown byte: {0}", unknown));

            uint puzzleSolution = 0;

            if (unknown > 0)
            {
                byte[] encryptionKey = reader.ReadBytes(8);
                m_encryption.SetKey(encryptionKey);

                uint[] uintEncryptionKey = new uint[2];
                uintEncryptionKey[0] = BitConverter.ToUInt32(encryptionKey, 0);
                uintEncryptionKey[1] = BitConverter.ToUInt32(encryptionKey, 4);

                uint[] puzzleData = new uint[4];
                puzzleData[3] = 0;
                for (int i = 0; i < 3; i++)
                {
                    puzzleData[i] = reader.ReadUInt32();
                }

                try
                {
                    SolveLoginPuzzle(uintEncryptionKey, puzzleData, unknown);
                }
                catch (Exception e)
                {
                    Log.Error(
                        String.Format(
                            "Failed to solve login puzzle: v[0]=0x{0:X}, v[1]=0x{1:X}, k[0]=0x{2:X}, k[1]=0x{3:X}, k[2]=0x{4:X}",
                            uintEncryptionKey[0],
                            uintEncryptionKey[1],
                            puzzleData[0],
                            puzzleData[1],
                            puzzleData[2]
                        )
                    );

                    OnPuzzleFailed(this, 10011);
                    Disconnect();
                    return;
                }

                puzzleSolution = puzzleData[3];
            }

            Log.Info(String.Format("Login puzzle solved: answer={0}", puzzleSolution));

            var askLogin = new Lobby.GC2LS_ASK_LOGIN(puzzleSolution, m_username, 0);
            SendPacket(askLogin);
        }

        private void SendPacket(ClientPacket packet)
        {
            byte[] data = packet.GetDataForSending();

            Log.Debug("Raw packet data:" + Environment.NewLine + HexDump(data));

            // Encrypt the packet if needed
            if (m_encryption.Initialized)
            { 
                m_encryption.EncryptClientData(data, 4, packet.TotalSize - 4); // Don't encrypt size
                Log.Debug("Encrypted packet data:" + Environment.NewLine + HexDump(data));
            }

            m_socket.Send(data, 0, packet.TotalSize, SocketFlags.None); // TODO: Make async
        }

        private void SolveLoginPuzzle(uint[] v, uint[] k, byte unknown)
        {
            // v is the 8 byte thing form the login puzzle, k is the 3 other uints + an unknown number
            // k[3] will be updated to the correct number after this is done.

            for (uint guess = 0; guess < uint.MaxValue; guess++)
            {
                k[3] = guess;

                uint[] vClone = (uint[])v.Clone();
                XXTEA.Encrypt(vClone, k, 6);
                uint solution = vClone[1];

                if (solution >> (32 - unknown) == 0 && (solution & (0x80000000 >> unknown)) != 0)
                {
                    return;
                }
            }

            throw new Exception("Failed to solve login puzzle");
        }

        public static string HexDump(byte[] bytes, int bytesPerLine = 16)
        {
            if (bytes == null) return "<null>";
            int bytesLength = bytes.Length;

            char[] HexChars = "0123456789ABCDEF".ToCharArray();

            int firstHexColumn =
                  8                   // 8 characters for the address
                + 3;                  // 3 spaces

            int firstCharColumn = firstHexColumn
                + bytesPerLine * 3       // - 2 digit for the hexadecimal value and 1 space
                + (bytesPerLine - 1) / 8 // - 1 extra space every 8 characters from the 9th
                + 2;                  // 2 spaces 

            int lineLength = firstCharColumn
                + bytesPerLine           // - characters to show the ascii value
                + Environment.NewLine.Length; // Carriage return and line feed (should normally be 2)

            char[] line = (new String(' ', lineLength - Environment.NewLine.Length) + Environment.NewLine).ToCharArray();
            int expectedLines = (bytesLength + bytesPerLine - 1) / bytesPerLine;
            StringBuilder result = new StringBuilder(expectedLines * lineLength);

            for (int i = 0; i < bytesLength; i += bytesPerLine)
            {
                line[0] = HexChars[(i >> 28) & 0xF];
                line[1] = HexChars[(i >> 24) & 0xF];
                line[2] = HexChars[(i >> 20) & 0xF];
                line[3] = HexChars[(i >> 16) & 0xF];
                line[4] = HexChars[(i >> 12) & 0xF];
                line[5] = HexChars[(i >> 8) & 0xF];
                line[6] = HexChars[(i >> 4) & 0xF];
                line[7] = HexChars[(i >> 0) & 0xF];

                int hexColumn = firstHexColumn;
                int charColumn = firstCharColumn;

                for (int j = 0; j < bytesPerLine; j++)
                {
                    if (j > 0 && (j & 7) == 0) hexColumn++;
                    if (i + j >= bytesLength)
                    {
                        line[hexColumn] = ' ';
                        line[hexColumn + 1] = ' ';
                        line[charColumn] = ' ';
                    }
                    else
                    {
                        byte b = bytes[i + j];
                        line[hexColumn] = HexChars[(b >> 4) & 0xF];
                        line[hexColumn + 1] = HexChars[b & 0xF];
                        line[charColumn] = (b < 32 ? '.' : (char)b);
                    }
                    hexColumn += 3;
                    charColumn++;
                }
                result.Append(line);
            }
            return result.ToString();
        }
    }
}

/*
 * eventOnKick (  int nReason, struct FString sInformation  )
 * DONE eventOnError(  int nMessageId, int nQueryId, int nReturnCode, int nParam1, int nParam2, int nParam3, int nParam4  )
 * DONE eventOnDisconnect
 * eventOnSaveConfigFailed ( int nError, int nIndex )
 * eventOnSaveConfigSuccess ( int nIndex )
 * eventOnLoadConfigFailed ( int nError, int nIndex )
 * eventOnLoadConfigSuccess ( int nIndex )
 * eventOnWorldEnterFailed ( int nError )
 * eventOnWorldEnterSuccess ( )
 * eventOnCharacterInfoFailed ( int nError )
 * eventOnCharacterInfoSuccess ( int nSlotNumber )
 * eventCharacterGetNumAdditionalSlots ( )
 * eventOnCharacterDeleteFailed ( int nError )
 * eventOnCharacterDeleteSuccess ( )
 * eventOnCharacterCreateFailed ( int nError )
 * eventOnCharacterCreateSuccess ( int nSlotNumber )
 * eventOnNameChangeFailed ( int nError )
 * eventOnNameChangeSuccess ( int nSlotNumber )
 * eventOnNameCheckFailed ( int nError )
 * eventOnNameCheckSuccess ( )
 * eventOnWorldStatus ( int nWorldUID, int nStatus )
 * eventOnGetWorldListFailed ( int nError )
 * eventOnGetWorldListSuccess ( )
 * eventOnCharacterList ( )
 * DONE eventOnLoginFailed ( int nError, struct FString sCountryCode )
 * eventOnLoginSuccess ( )
 * DONE eventOnPuzzleFailed ( int nError )
 * DONE eventOnConnectFailed ( )
 * DONE eventOnConnectSuccess ( )
 * eventConnectToLS ( )
*/