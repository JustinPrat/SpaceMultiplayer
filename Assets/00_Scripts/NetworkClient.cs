using Cinemachine;
using ENet6;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using UnityEngine;

public class PlayerData
{
    public InitData initData;
    public Transform playerTransform;
    public SpaceMovement spaceMovement;
    public List<PlayerInputData> predictedInput = new List<PlayerInputData>();
    public ShootManager shoot;
    public OtherClientUIManager otherUIManager;
    public ushort score;
}


public class NetworkClient : MonoBehaviour
{
    private ENet6.Host enetHost = null;
    private ENet6.Peer? serverPeer = null;


    PlayerData ownPlayer;
    PacketBuilder packetBuilder = null;
    uint currentId = 0;

    Dictionary<uint, PlayerData> players = new();

    [SerializeField] CinemachineVirtualCamera virtualCamera;
    [SerializeField] GameObject client;
    [SerializeField] GameObject otherClient;
    [SerializeField] ClientGlobalInfo clientInfo;
    [SerializeField] GameObject deathParticles;

    private float tickRate = 1f / 60f;
    private float previousTickTime;
    private float tickTime;

    public bool Connect(string addressString)
    {
        ENet6.Address address = new ENet6.Address();
        if (!address.SetHost(ENet6.AddressType.Any, addressString))
        {
            Debug.LogError("failed to resolve \"" + addressString + "\"");
            return false;
        }

        address.Port = 14769;
        Debug.Log("connecting to " + address.GetIP());


        // On recréé l'host à la connexion pour l'avoir en IPv4 / IPv6 selon l'adresse
        if (enetHost != null)
            enetHost.Dispose();

        enetHost = new ENet6.Host();
        enetHost.Create(address.Type, 1, 0);
        serverPeer = enetHost.Connect(address, 0);

        // On laisse la connexion se faire pendant un maximum de 50 * 100ms = 5s
        for (uint i = 0; i < 50; ++i)
        {
            ENet6.Event evt = new ENet6.Event();
            if (enetHost.Service(100, out evt) > 0)
            {
                Debug.Log("Successfully connected !");
                packetBuilder = new PacketBuilder(serverPeer.Value, 0);
                // Nous avons un événement, la connexion a soit pu s'effectuer (ENET_EVENT_TYPE_CONNECT) soit échoué (ENET_EVENT_TYPE_DISCONNECT)
                break; //< On sort de la boucle
            }
        }

        if (serverPeer.Value.State != PeerState.Connected)
        {
            Debug.LogError("connection to \"" + addressString + "\" failed");
            return false;
        }

        return true;
    }

    // Start is called before the first frame update
    void Start()
    {
        if (!ENet6.Library.Initialize())
            throw new Exception("Failed to initialize ENet");

        if (Connect(clientInfo.ip))
        {
            ownPlayer = new PlayerData() { initData = new InitData() { clientInitData = new ClientInitData() { matId = (byte)clientInfo.matId, playerName = clientInfo.playerName, skinId = (byte)clientInfo.skinId } } };
            packetBuilder.SendPacket(new ClientInitData(clientInfo.playerName, clientInfo.skinId, clientInfo.matId));
        }
        else
        {
            GameObject player = Instantiate(client, Vector3.zero, Quaternion.identity);
            player.GetComponent<ClientSkinLoader>().LoadSkin(clientInfo.skinId, clientInfo.matId);

            ownPlayer = new PlayerData();
            ownPlayer.playerTransform = player.transform;
            ownPlayer.spaceMovement = player.GetComponent<SpaceMovement>();
            ownPlayer.shoot = player.GetComponent<ShootManager>();

            virtualCamera.Follow = player.transform;
            virtualCamera.LookAt = player.transform;
        }
    }

    private void OnApplicationQuit()
    {
        packetBuilder.peer.Disconnect(0);
        ENet6.Library.Deinitialize();
    }

    private void Update()
    {
        if (Time.time >= tickTime && ownPlayer.spaceMovement != null)
        {
            //Debug.Log("tick : " + Time.time + " - tick rate : " + tickRate);
            ownPlayer.spaceMovement.AdvanceSpaceShip(tickTime - previousTickTime);
            previousTickTime = tickTime;
            tickTime = Time.time;
            tickTime += tickRate;

            if (ownPlayer.initData != null)
            {
                //tick reseau d'envoie d'inputs
                SendPlayerInputs();
            }
        }
    }

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
                        serverPeer = null;
                        break;

                    case ENet6.EventType.Receive:
                        byte[] buffer = new byte[1024];
                        evt.Packet.CopyTo(buffer);
                        HandleMessage(buffer);
                        Debug.Log("Receive");
                        break;

                    case ENet6.EventType.Timeout:
                        Debug.Log("Timeout");
                        break;
                }
            }
            while (enetHost.CheckEvents(out evt) > 0);
        }
    }

    public void SendPlayerInputs()
    {
        Debug.Log("Send player inputs");
        PlayerInputData inputData = new PlayerInputData(currentId, ownPlayer.spaceMovement.moveInput, ownPlayer.playerTransform.rotation, ownPlayer.initData.serverClientInitData.playerNum, ownPlayer.spaceMovement.MoveSpeed);
        ownPlayer.predictedInput.Add(inputData);
        packetBuilder.SendPacket(inputData);
        currentId++;
    }

    public void SendPlayerShoot()
    {
        if (ownPlayer.spaceMovement)
        {
            packetBuilder.SendPacket(new ClientSendShoot(ownPlayer.initData.serverClientInitData.playerNum));
        }
    }

    private void HandleMessage(byte[] buffer)
    {
        int offset = 0;
        Opcode opcode = (Opcode)Serialization.DeserializeU8(buffer, ref offset);
        Debug.Log("Opcode" + opcode.ToString());
        switch (opcode)
        {
            case Opcode.OnClientConnectResponse:
                {
                    ConnectServerInitData responseFromConnect = new();
                    responseFromConnect.Deserialize(buffer, ref offset);
                    GameObject player = Instantiate(client, responseFromConnect.playerStartPos, Quaternion.identity);
                    player.GetComponent<ClientSkinLoader>().LoadSkin(clientInfo.skinId, clientInfo.matId);

                    ownPlayer.initData.serverClientInitData = responseFromConnect;
                    ownPlayer.playerTransform = player.transform;
                    ownPlayer.spaceMovement = player.GetComponent<SpaceMovement>();
                    ownPlayer.shoot = player.GetComponent<ShootManager>();
                    ownPlayer.shoot.ShootEvent += SendPlayerShoot;

                    virtualCamera.Follow = player.transform;
                    virtualCamera.LookAt = player.transform;
                    UIManager.instance.UpdateLeaderBoard(ownPlayer.initData.clientInitData.playerName, 0);
                    break;
                }

            case Opcode.OnOtherClientConnect:
                {
                    InitData dataFromServer = new();
                    dataFromServer.Deserialize(buffer, ref offset);
                    GameObject player2 = Instantiate(otherClient, dataFromServer.serverClientInitData.playerStartPos, Quaternion.identity);
                    player2.GetComponent<ClientSkinLoader>().LoadSkin(dataFromServer.clientInitData.skinId, dataFromServer.clientInitData.matId);
                    OtherClientUIManager uIManager = player2.GetComponent<OtherClientUIManager>();
                    uIManager.LoadName(dataFromServer.clientInitData.playerName);
                    players.Add(dataFromServer.serverClientInitData.playerNum, new PlayerData() { playerTransform = player2.transform, initData = dataFromServer, otherUIManager = uIManager});
                    UIManager.instance.UpdateLeaderBoard(dataFromServer.clientInitData.playerName, 0);
                    break;
                }

            case Opcode.FromServerPlayerPosition:
                {
                    //Debug.Log("Receive position FROM SERVER");
                    ServerToPlayerPosition positionFromServer = new();
                    positionFromServer.Deserialize(buffer, ref offset);

                    if (positionFromServer.playerNum == ownPlayer.initData.serverClientInitData.playerNum)
                    {
                        Vector3 previousPredictedPos = ownPlayer.spaceMovement.baseTransformPos;

                        //Debug.Log("PREDICTED CURRENT POSITION : " + ownPlayer.spaceMovement.baseTransformPos + " with input ID : " + (currentId - 1));
                        //Debug.Log("ROLL BACK POSITION : " + positionFromServer.position + " with input ID : " + positionFromServer.inputId);

                        ownPlayer.spaceMovement.SetPositionRotation(positionFromServer.position, positionFromServer.rotation);
                        //ownPlayer.playerTransform.position = positionFromServer.position;
                        //ownPlayer.playerTransform.rotation = positionFromServer.rotation;

                        ownPlayer.predictedInput.RemoveAll(input => input.inputId <= positionFromServer.inputId);


                        for (int i = 0; i < ownPlayer.predictedInput.Count; i++)
                        {
                            ownPlayer.spaceMovement.AdvanceSpaceShip(ownPlayer.predictedInput[i].moveInput, ownPlayer.predictedInput[i].rotation, tickRate);
                            //Debug.Log("ADVANCE STEPS : " + ownPlayer.predictedInput[i].inputId + "To position : " + ownPlayer.spaceMovement.baseTransformPos);
                        }

                        Vector3 newPredictedPos = ownPlayer.spaceMovement.baseTransformPos;
                        ownPlayer.spaceMovement.visualError += previousPredictedPos - newPredictedPos;
                        //Debug.Log("SERV INPUT ID " + positionFromServer.inputId + " PLAYER INPUT ID " + (currentId - 1));
                        //Debug.Log("Visual ERROR : " + (previousPredictedPos - newPredictedPos));
                    }
                    else
                    {
                        players[positionFromServer.playerNum].playerTransform.position = positionFromServer.position;
                        players[positionFromServer.playerNum].playerTransform.rotation = positionFromServer.rotation;
                    }

                    break;
                }

        case Opcode.FromServerHealthUpdate:
                {
                    ServerHealthUpdate serverHealthUpdate = new ServerHealthUpdate();
                    serverHealthUpdate.Deserialize(buffer, ref offset);
                    if (serverHealthUpdate.playerNumber == ownPlayer.initData.serverClientInitData.playerNum)
                    {
                        UIManager.instance.lifeBar.size = (float)serverHealthUpdate.health / (float)serverHealthUpdate.maxHealth;
                    }
                    else
                    {
                        players[serverHealthUpdate.playerNumber].otherUIManager.UpdateHealth(serverHealthUpdate.health, serverHealthUpdate.maxHealth);
                    }
                    break;
                }

        case Opcode.LeaderBoardUpdate:
                {
                    LeaderBoardUpdate leaderBoardUpdate = new LeaderBoardUpdate();
                    leaderBoardUpdate.Deserialize(buffer, ref offset);

                    if(leaderBoardUpdate.playerNum == ownPlayer.initData.serverClientInitData.playerNum)
                    {
                        ownPlayer.score = leaderBoardUpdate.score;
                        UIManager.instance.UpdateLeaderBoard(ownPlayer.initData.clientInitData.playerName, leaderBoardUpdate.score);
                    }
                    else
                    {
                        players[leaderBoardUpdate.playerNum].score = leaderBoardUpdate.score;
                        UIManager.instance.UpdateLeaderBoard(players[leaderBoardUpdate.playerNum].initData.clientInitData.playerName, leaderBoardUpdate.score);
                    }
                    break;
                }

        case Opcode.ClientDead:
            {
                ClientDead clientDead = new ClientDead();
                clientDead.Deserialize(buffer, ref offset);

                if(clientDead.playerKilled == ownPlayer.initData.serverClientInitData.playerNum)
                {
                    ownPlayer.playerTransform.gameObject.SetActive(false);
                    virtualCamera.LookAt = players[clientDead.killedBy].playerTransform;
                    Instantiate(deathParticles, ownPlayer.playerTransform.position, Quaternion.identity);
                    UIManager.instance.ShowDeadUI();
                }
                else
                {
                    if(players.TryGetValue(clientDead.playerKilled, out PlayerData deadPlayerData))
                    {
                        deadPlayerData.playerTransform.gameObject.SetActive(false);
                        Instantiate(deathParticles, deadPlayerData.playerTransform.position, Quaternion.identity);
                    }
                }
                
                break;
            }
        case Opcode.ClientRespawn:
            {
                ClientRespawn clientRespawn = new ClientRespawn();
                clientRespawn.Deserialize(buffer, ref offset);

                if (clientRespawn.playerNum == ownPlayer.initData.serverClientInitData.playerNum)
                {
                    ownPlayer.playerTransform.gameObject.SetActive(true);
                    virtualCamera.LookAt = ownPlayer.playerTransform;
                    UIManager.instance.HideDeadUI();
                }
                else
                {
                    if (players.TryGetValue(clientRespawn.playerNum, out PlayerData deadPlayerData))
                    {
                        deadPlayerData.playerTransform.gameObject.SetActive(true);
                    }
                }

                break;
            }
        case Opcode.ClientDisconnect:
            {
                ClientDisconnect clientRespawn = new ClientDisconnect();
                clientRespawn.Deserialize(buffer, ref offset);
                UIManager.instance.RemoveFromLeaderBoard(players[clientRespawn.playerNum].initData.clientInitData.playerName);
                Destroy(players[clientRespawn.playerNum].playerTransform.gameObject);
                players.Remove(clientRespawn.playerNum);
                players.Remove(clientRespawn.playerNum);
                break;
            }
        }


    }
}