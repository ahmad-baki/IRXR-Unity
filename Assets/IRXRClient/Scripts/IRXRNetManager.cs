
using NetMQ;
using NetMQ.Sockets;
using UnityEngine;
using System;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Net.NetworkInformation;

public enum ServerPort {
  Discovery = 7720,
  Service = 7721,
  Topic = 7722,
}

public enum ClientPort {
  Discovery = 7720,
  Service = 7723,
  Topic = 7724,
}

class HostInfo {
  public string name;
  public string ip;
  public List<string> topics = new();
  public List<string> services = new();
}

public class IRXRNetManager : Singleton<IRXRNetManager> {

  [SerializeField] private string host = "UnityEditor";
  public Action OnDisconnected;
  public Action OnConnectionCompleted;
  public Action OnServerDiscovered;
  public Action ConnectionSpin;
  private UdpClient _discoveryClient;
  private HostInfo _serverInfo = null;
  private HostInfo _localInfo = new HostInfo();

  private List<NetMQSocket> _sockets;

  // subscriber socket
  private SubscriberSocket _subSocket;
  private Dictionary<string, Action<string>> _topicsCallbacks;
  // publisher socket
  private PublisherSocket _pubSocket;
  private RequestSocket _reqSocket;
  private ResponseSocket _resSocket;
  private Dictionary<string, Action<string>> _serviceCallbacks;

  private float lastTimeStamp;
  private bool isConnected = false;

  void Awake() {
    AsyncIO.ForceDotNet.Force();
    _discoveryClient = new UdpClient((int)ServerPort.Discovery);
    _sockets = new List<NetMQSocket>();
    _reqSocket = new RequestSocket();
    _resSocket = new ResponseSocket();
    // subscriber socket
    _subSocket = new SubscriberSocket();
    _topicsCallbacks = new Dictionary<string, Action<string>>();
    _pubSocket = new PublisherSocket();
    
    _sockets = new List<NetMQSocket> { _reqSocket, _resSocket, _subSocket, _pubSocket };
    _localInfo.name = host;
  }

  void Start() {
    OnServerDiscovered += ConnectService;
    OnServerDiscovered += StartSubscription;
    OnServerDiscovered += () => isConnected = true;
    OnConnectionCompleted += () => _pubSocket.Bind($"tcp://{_localInfo.ip}:{(int)ClientPort.Topic}");
    OnConnectionCompleted += RegisterInfo2Server;
    ConnectionSpin += () => {};
    OnDisconnected += () => Debug.Log("Disconnected");
    OnDisconnected += () => isConnected = false;
    OnDisconnected += StopSubscription;
    OnDisconnected += () => _pubSocket.Unbind($"tcp://{_localInfo.ip}:{(int)ClientPort.Topic}");
    lastTimeStamp = -1.0f;
  }

  void Update() {
    // TODO: Disconnect Behavior
    if (isConnected && lastTimeStamp + 1.0f < Time.realtimeSinceStartup)
    {
      OnDisconnected.Invoke();
      return;
    }
    ConnectionSpin.Invoke();
    if (_discoveryClient.Available == 0) return; // there's no message to read
    IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, 0);
    byte[] result = _discoveryClient.Receive(ref endPoint);
    string message =  Encoding.UTF8.GetString(result);

    if (!message.StartsWith("SimPub")) return; // not the right tag
    var split = message.Split(":", 2);
    string info = split[1];
    _serverInfo = JsonConvert.DeserializeObject<HostInfo>(info);
    _serverInfo.ip = endPoint.Address.ToString();
    if (lastTimeStamp + 1.0f < Time.realtimeSinceStartup) {
      _localInfo.ip = GetLocalIPsInSameSubnet(_serverInfo.ip);
      Debug.Log($"Discovered server at {_serverInfo.ip} with local IP {_localInfo.ip}");
      OnServerDiscovered.Invoke();
      OnConnectionCompleted.Invoke();
      isConnected = true; // not really elegant, just for the disconnection of subsocket
    }
    lastTimeStamp = Time.realtimeSinceStartup;
  }

  void OnApplicationQuit() {
    _discoveryClient.Dispose();
    foreach (var socket in _sockets) {
      socket.Dispose();
    }
    // This must be called after all the NetMQ sockets are disposed
    NetMQConfig.Cleanup();
  }

  public PublisherSocket GetPublisherSocket() {
    return _pubSocket;
  }

  public void ConnectService () {
    _reqSocket.Connect($"tcp://{_serverInfo.ip}:{(int)ServerPort.Service}");
    Debug.Log($"Starting service connection to {_serverInfo.ip}:{(int)ServerPort.Service}");
  }

  // Please use these two request functions to send request to the server.
  // It may stuck if the server is not responding,
  // which will cause the Unity Editor to freeze.
  public string RequestString(string service, string request = "") {
    _reqSocket.SendFrame($"{service}:{request}");
    string result = _reqSocket.ReceiveFrameString(out bool more);
    while(more) result += _reqSocket.ReceiveFrameString(out more);
    return result;
  }

  public List<byte> RequestBytes(string service, string request = "") {
    _reqSocket.SendFrame($"{service}:{request}");
    List<byte> result = new List<byte>(_reqSocket.ReceiveFrameBytes(out bool more));
    while (more) result.AddRange(_reqSocket.ReceiveFrameBytes(out more));
    return result;
  }

  public void StopSubscription() {
    if (isConnected) {
      _subSocket.Disconnect($"tcp://{_serverInfo.ip}:{(int)ServerPort.Topic}");
      while (_subSocket.HasIn) _subSocket.SkipFrame();
    }
    ConnectionSpin -= TopicUpdateSpin;
    // It is not necessary to clear the topics callbacks
    // _topicsCallbacks.Clear();
  }

  public void StartSubscription() {
    StopSubscription();
    _subSocket.Connect($"tcp://{_serverInfo.ip}:{(int)ServerPort.Topic}");
    _subSocket.Subscribe("");
    ConnectionSpin += TopicUpdateSpin;
    Debug.Log($"Connected topic to {_serverInfo.ip}:{(int)ServerPort.Topic}");
  }

  public void TopicUpdateSpin() {
    if (!_subSocket.HasIn) return;
    string messageReceived = _subSocket.ReceiveFrameString();
    string[] messageSplit = messageReceived.Split(":", 2);
    if (_topicsCallbacks.ContainsKey(messageSplit[0])) {
      _topicsCallbacks[messageSplit[0]](messageSplit[1]);
    }
  }

  public void SubscribeTopic(string topic, Action<string> callback) {
    _topicsCallbacks[topic] = callback;
    Debug.Log($"Subscribe a new topic {topic}");
  }

  public void UnsubscribeTopic(string topic) {
    if (_topicsCallbacks.ContainsKey(topic)) _topicsCallbacks.Remove(topic);
  }

  public void CreatePublishTopic(string topic) {
    if (_localInfo.topics.Contains(topic)) Debug.LogWarning($"Topic {topic} already exists");
    _localInfo.topics.Add(topic);
    RegisterInfo2Server();
  }

  public void RegisterInfo2Server() {
    if (isConnected) {
      string data = JsonConvert.SerializeObject(_localInfo);
      RequestString("Register", JsonConvert.SerializeObject(_localInfo));
    }
  }

  public void RegisterServiceCallback(string service, Action<string> callback) {
    _serviceCallbacks[service] = callback;
  }

  public string GetHostName() {
    return host;
  }

  public static string GetLocalIPsInSameSubnet(string inputIPAddress)
  {
    IPAddress inputIP;
    if (!IPAddress.TryParse(inputIPAddress, out inputIP))
    {
      throw new ArgumentException("Invalid IP address format.", nameof(inputIPAddress));
    }
    IPAddress subnetMask = IPAddress.Parse("255.255.255.0");
    // Get all network interfaces
    NetworkInterface[] networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
    foreach (NetworkInterface ni in networkInterfaces)
    {
      // Get IP properties of the network interface
      IPInterfaceProperties ipProperties = ni.GetIPProperties();
      UnicastIPAddressInformationCollection unicastIPAddresses = ipProperties.UnicastAddresses;
      foreach (UnicastIPAddressInformation ipInfo in unicastIPAddresses)
      {
        if (ipInfo.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
          IPAddress localIP = ipInfo.Address;
          Debug.Log($"Local IP: {localIP}");
          // Check if the IP is in the same subnet
          if (IsInSameSubnet(inputIP, localIP, subnetMask))
          {
            return localIP.ToString();;
          }
        }
      }
    }
    return "127.0.0.1";
  }

  private static bool IsInSameSubnet(IPAddress ip1, IPAddress ip2, IPAddress subnetMask)
  {
    byte[] ip1Bytes = ip1.GetAddressBytes();
    byte[] ip2Bytes = ip2.GetAddressBytes();
    byte[] maskBytes = subnetMask.GetAddressBytes();

    for (int i = 0; i < ip1Bytes.Length; i++)
    {
      if ((ip1Bytes[i] & maskBytes[i]) != (ip2Bytes[i] & maskBytes[i]))
      {
        return false;
      }
    }
    return true;
  }

}