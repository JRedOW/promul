using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using Promul.Common.Structs;
using Unity.Netcode;
using UnityEngine;
namespace Promul.Runtime
{
    public class PromulTransport : NetworkTransport, INetEventListener
    {
        enum HostType
        {
            None,
            Server,
            Client
        }

        [Tooltip("The port of the relay server.")]
        public ushort Port = 7777;
        [Tooltip("The address of the relay server.")]
        public string Address = "127.0.0.1";

        [Tooltip("Interval between ping packets used for detecting latency and checking connection, in seconds")]
        public float PingInterval = 1f;
        [Tooltip("Maximum duration for a connection to survive without receiving packets, in seconds")]
        public float DisconnectTimeout = 5f;
        [Tooltip("Delay between connection attempts, in seconds")]
        public float ReconnectDelay = 0.5f;
        [Tooltip("Maximum connection attempts before client stops and reports a disconnection")]
        public int MaxConnectAttempts = 10;
        [Tooltip("Size of default buffer for decoding incoming packets, in bytes")]
        public int MessageBufferSize = 1024 * 5;
        [Tooltip("Simulated chance for a packet to be \"lost\", from 0 (no simulation) to 100 percent")]
        public int SimulatePacketLossChance = 0;
        [Tooltip("Simulated minimum additional latency for packets in milliseconds (0 for no simulation)")]
        public int SimulateMinLatency = 0;
        [Tooltip("Simulated maximum additional latency for packets in milliseconds (0 for no simulation")]
        public int SimulateMaxLatency = 0;

        NetManager m_NetManager;

        public override ulong ServerClientId => 0;
        HostType m_HostType;

        void OnValidate()
        {
            PingInterval = Math.Max(0, PingInterval);
            DisconnectTimeout = Math.Max(0, DisconnectTimeout);
            ReconnectDelay = Math.Max(0, ReconnectDelay);
            MaxConnectAttempts = Math.Max(0, MaxConnectAttempts);
            MessageBufferSize = Math.Max(0, MessageBufferSize);
            SimulatePacketLossChance = Math.Min(100, Math.Max(0, SimulatePacketLossChance));
            SimulateMinLatency = Math.Max(0, SimulateMinLatency);
            SimulateMaxLatency = Math.Max(SimulateMinLatency, SimulateMaxLatency);
        }

        void Update()
        {
            m_NetManager?.PollEvents();
        }

        public override bool IsSupported => Application.platform != RuntimePlatform.WebGLPlayer;

        NetPeer? _relayPeer;

        public void SendControl(RelayControlMessage rcm, NetworkDelivery qos)
        {
            var writer = new NetDataWriter();
            writer.Put(rcm);
            _relayPeer?.Send(writer, ConvertNetworkDelivery(qos));
        }
        
        public override void Send(ulong clientId, ArraySegment<byte> data, NetworkDelivery qos)
        {
            var cpy = new byte[data.Count];
            
            Array.Copy(data.Array, data.Offset, cpy, 0, data.Count);
            SendControl(new RelayControlMessage
            {
                Type = RelayControlMessageType.Data,
                AuthorClientId = clientId,
                Data = cpy
            }, qos);
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            var message = reader.Get();
            var author = message.AuthorClientId;
            switch (message.Type)
            {
                // Either we are host and a client has connected,
                // or we're a client and we're connected.
                case RelayControlMessageType.Connected:
                    {
                        InvokeOnTransportEvent(NetworkEvent.Connect, author, default, Time.time);
                        break;
                    }
                // A client has disconnected from the relay.
                case RelayControlMessageType.Disconnected:
                    {
                        InvokeOnTransportEvent(NetworkEvent.Disconnect, author, default, Time.time);
                        break;
                    }
                // Relayed data
                case RelayControlMessageType.Data:
                    {
                        InvokeOnTransportEvent(NetworkEvent.Data, author, new ArraySegment<byte>(message.Data), Time.time);
                        break;
                    }
                default:
                    Debug.LogError("Unknown Promul control byte " + message.Type);
                    break;
            }

            reader.Recycle();
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            clientId = 0;
            receiveTime = Time.realtimeSinceStartup;
            payload = new ArraySegment<byte>();
            return NetworkEvent.Nothing;
        }

        bool ConnectToRelayServer()
        {
            if (!m_NetManager.Start()) return false;
            var joinPacket = new NetDataWriter();
            joinPacket.Put("TEST");
            _relayPeer = m_NetManager.Connect(Address, Port, joinPacket);
            return true;
        }

        public override bool StartClient()
        {
            m_HostType = HostType.Client;
            return ConnectToRelayServer();
        }

        public override bool StartServer()
        {
            m_HostType = HostType.Server;
            return ConnectToRelayServer();
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            SendControl(new RelayControlMessage {Type = RelayControlMessageType.KickFromRelay, AuthorClientId = clientId, Data = Array.Empty<byte>() }, NetworkDelivery.Reliable);
        }

        public override void DisconnectLocalClient()
        {
            m_NetManager.DisconnectAll();
            _relayPeer = null;
        }

        public override ulong GetCurrentRtt(ulong clientId)
        {
            if (_relayPeer != null) return (ulong)_relayPeer.Ping * 2;
            return 0;
        }

        public override void Shutdown()
        {
            m_NetManager?.Stop();
            _relayPeer = null;
            m_HostType = HostType.None;
        }

        public override void Initialize(NetworkManager networkManager = null)
        {
            m_NetManager = new NetManager(this)
            {
                PingInterval = SecondsToMilliseconds(PingInterval),
                DisconnectTimeout = SecondsToMilliseconds(DisconnectTimeout),
                ReconnectDelay = SecondsToMilliseconds(ReconnectDelay),
                MaxConnectAttempts = MaxConnectAttempts,
                SimulatePacketLoss = SimulatePacketLossChance > 0,
                SimulationPacketLossChance = SimulatePacketLossChance,
                SimulateLatency = SimulateMaxLatency > 0,
                SimulationMinLatency = SimulateMinLatency,
                SimulationMaxLatency = SimulateMaxLatency
            };
        }

        static DeliveryMethod ConvertNetworkDelivery(NetworkDelivery type)
        {
            return type switch
            {
                NetworkDelivery.Unreliable => DeliveryMethod.Unreliable,
                NetworkDelivery.UnreliableSequenced => DeliveryMethod.Sequenced,
                NetworkDelivery.Reliable => DeliveryMethod.ReliableUnordered,
                NetworkDelivery.ReliableSequenced => DeliveryMethod.ReliableOrdered,
                NetworkDelivery.ReliableFragmentedSequenced => DeliveryMethod.ReliableOrdered,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };
        }
        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            request.RejectForce();
        }
        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            Debug.Log("Disconnected " + disconnectInfo.Reason.ToString() + " " + disconnectInfo.SocketErrorCode.ToString());
            if (disconnectInfo.Reason != DisconnectReason.DisconnectPeerCalled)
                InvokeOnTransportEvent(NetworkEvent.TransportFailure, 0, new ArraySegment<byte>(), Time.time);
        }

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
        }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }

        static int SecondsToMilliseconds(float seconds)
        {
            return (int)Mathf.Ceil(seconds * 1000);
        }
    }
}