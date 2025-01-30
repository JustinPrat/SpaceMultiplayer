using ENet6;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using UnityEngine;

class ServerClientData
{
    public PacketBuilder packetBuilder;
    public InitData initData = new InitData();
    public List<PlayerInputData> playerInputsDatas = new List<PlayerInputData>();
    public Vector3 Position;
    public int health = 1000;
}

public class NetworkServer : MonoBehaviour
{
    private ENet6.Host enetHost = null;
    Dictionary<uint, ServerClientData> players = new();

    private float tickDelay = 1/30;
    private float tickTime;
    private int damagePerShoot = 1;

    public bool CreateServer(string addressString)
    {
        ENet6.Address address = Address.BuildAny(AddressType.IPv6);
        address.Port = 14769;

        Debug.Log("Creating server : " + address.GetIP());

        // On recréé l'host à la connexion pour l'avoir en IPv4 / IPv6 selon l'adresse
        if (enetHost != null)
            enetHost.Dispose();

        enetHost = new ENet6.Host();
        enetHost.Create(AddressType.Any, address, 10, 0);

        return true;
    }

    private void Update()
    {
        if (Time.time >= tickTime)
        {
            tickTime += tickDelay;
            foreach (ServerClientData data in players.Values)
            {
                if (data.playerInputsDatas.Count <= 0)
                    continue;

                PlayerInputData lastPlayerInputs = data.playerInputsDatas[0];
                data.playerInputsDatas.RemoveAt(0);
                AdvancePhysics(lastPlayerInputs.moveInput, lastPlayerInputs.moveSpeed, ref data.Position);
                

                Debug.Log("Server send player positions");
                foreach (ServerClientData otherDatas in players.Values)
                {
                    //if (otherDatas.initData.serverClientInitData.playerNum == data.initData.serverClientInitData.playerNum) continue;

                    ServerToPlayerPosition serverPositionData = new ServerToPlayerPosition(lastPlayerInputs.inputId, lastPlayerInputs.rotation, data.initData.serverClientInitData.playerNum, data.Position);
                    otherDatas.packetBuilder.SendPacket(serverPositionData);
                }
            }
        }
    }

    private void AdvancePhysics (Vector2 moveInput, float moveSpeed, ref Vector3 position)
    {
        Vector3 movementZ = moveInput.y * transform.forward * moveSpeed * Time.deltaTime;
        Vector3 movementX = moveInput.x * transform.right * moveSpeed * Time.deltaTime;
        Vector3 movement = movementZ + movementX;

        position += movement;
    }

    // Start is called before the first frame update
    void Start()
    {
        if (!ENet6.Library.Initialize())
            throw new Exception("Failed to initialize ENet");

        CreateServer("localhost");
    }
    private void OnApplicationQuit()
    {
        ENet6.Library.Deinitialize();
    }

    // FixedUpdate est appelé à chaque Tick (réglé dans le projet)
    void FixedUpdate()
    {
        ENet6.Event evt = new ENet6.Event();
        if (enetHost.Service(0, out evt) > 0)
        {
            do
            {
                switch (evt.Type)
                {
                    case ENet6.EventType.None:
                        Debug.Log("?");
                        break;

                    case ENet6.EventType.Connect:
                        Debug.Log("Connect");
                        break;

                    case ENet6.EventType.Disconnect:
                        Debug.Log("Disconnect");
                        break;

                    case ENet6.EventType.Receive:
                        Debug.Log("Receive");
                        byte[] buffer = new byte[1024];
                        evt.Packet.CopyTo(buffer);
                        HandleMessage(evt.Peer,buffer);
                        break;

                    case ENet6.EventType.Timeout:
                        Debug.Log("Timeout");
                        break;
                }
            }
            while (enetHost.CheckEvents(out evt) > 0);
        }
    }

    private void HandleMessage(Peer peer, byte[] buffer)
    {
        int offset = 0;
        Opcode opcode = (Opcode)Serialization.DeserializeU8(buffer, ref offset);
        switch (opcode)
        {
            case Opcode.OnClientConnect:
                ClientInitData dataFromClient = new ();
                dataFromClient.Deserialize(buffer, ref offset);
                ServerClientData serverClientData = new ServerClientData();
                serverClientData.packetBuilder = new PacketBuilder(peer, 0);
                serverClientData.initData.clientInitData = dataFromClient;
                ConnectServerInitData serverInitData = new ConnectServerInitData((byte)(players.Count + 1), new Vector3(UnityEngine.Random.Range(-5, 6), 0, UnityEngine.Random.Range(-5, 6)));
                serverClientData.initData.serverClientInitData = serverInitData;
                serverClientData.packetBuilder.SendPacket<ConnectServerInitData>(serverInitData);

                foreach (var player in players.Values)
                {
                    player.packetBuilder.SendPacket<InitData>(serverClientData.initData);
                }

                foreach (var player in players.Values)
                {
                    serverClientData.packetBuilder.SendPacket<InitData>(player.initData);
                }

                serverClientData.Position = serverInitData.playerStartPos;
                players.Add(serverInitData.playerNum, serverClientData);
                break;

            case Opcode.PlayerInputsData:
                PlayerInputData dataFromPlayer = new ();
                dataFromPlayer.Deserialize(buffer, ref offset);
                players[dataFromPlayer.playerNum].playerInputsDatas.Add(dataFromPlayer);
                break;

            case Opcode.ClientShoot:
                ClientSendShoot clientSendShoot = new ();
                clientSendShoot.Deserialize(buffer, ref offset);

                Debug.Log(clientSendShoot.ownPlayerNumber);
                break;
                
        }
    }
}
