using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class NetSession : MonoBehaviour
{
	public enum Role
	{
		None,
		Host,
		Guest
	}

	public enum SessionState
	{
		Offline,
		Connecting,
		Lobby,
		InGame,
		Reconnecting
	}

	public enum GameMode : byte
	{
		Sync,
		Stream
	}

	public delegate void MessageHandler(int fromSlot, byte[] payload);

	public class PlayerInfo
	{
		public int Slot;

		public string Name;

		public bool Ready;

		public bool Connected;

		public int PingMs;

		public float ReserveLeft;
	}

	private class Connection
	{
		public TcpClient Client;

		public NetworkStream Stream;

		public readonly Queue<byte[]> Queue = new Queue<byte[]>();

		public readonly Dictionary<byte, byte[]> Latest = new Dictionary<byte, byte[]>();

		public readonly AutoResetEvent Signal = new AutoResetEvent(initialState: false);

		public volatile bool Welcomed;

		public volatile int Slot = -1;

		public volatile int LastReceive;

		public volatile bool CloseAfterSend;

		public int Closed;

		public int Generation;

		public float OpenedAt;

		public string Remote = "?";

		public void Enqueue(byte type, byte[] payload)
		{
			byte[] item = Frame(type, payload);
			lock (Queue)
			{
				Queue.Enqueue(item);
			}
			Signal.Set();
		}

		public void SetLatest(byte type, byte[] payload)
		{
			byte[] value = Frame(type, payload);
			lock (Queue)
			{
				Latest[type] = value;
			}
			Signal.Set();
		}
	}

	private class Slot
	{
		public int Index;

		public string Name;

		public string Token;

		public bool Ready;

		public Connection Conn;

		public float LostAt;

		public int PingMs;
	}

	private enum EventKind
	{
		Accepted,
		Connected,
		ConnectFailed,
		Message,
		Closed
	}

	private struct NetEvent
	{
		public EventKind Kind;

		public Connection Conn;

		public byte Type;

		public byte[] Payload;

		public string Error;

		public int Generation;
	}

	public const int ProtocolVersion = 1;

	public const int DefaultPort = 7777;

	public const int MaxPlayers = 2;

	public const float LobbyReserveSeconds = 20f;

	public const float GameReserveSeconds = 60f;

	public const float ReconnectSeconds = 60f;

	private const int TimeoutMs = 6000;

	private const float HelloTimeout = 5f;

	private const int MaxMessageBytes = 8388608;

	public const byte MsgHello = 1;

	public const byte MsgWelcome = 2;

	public const byte MsgLobbyState = 3;

	public const byte MsgSetReady = 4;

	public const byte MsgStartGame = 5;

	public const byte MsgPing = 6;

	public const byte MsgPong = 7;

	public const byte MsgBye = 8;

	public const byte LastSessionType = 31;

	private static readonly byte[] Empty = new byte[0];

	public string LocalName = "Jogador";

	public static GameMode DefaultMode = GameMode.Sync;

	private readonly MessageHandler[] mainHandlers = new MessageHandler[256];

	private readonly MessageHandler[] threadHandlers = new MessageHandler[256];

	private readonly object inboxLock = new object();

	private readonly Queue<NetEvent> inbox = new Queue<NetEvent>();

	private int generation;

	private TcpListener listener;

	private Thread acceptThread;

	private volatile bool listening;

	private readonly Slot[] slots = new Slot[2];

	private readonly List<Connection> pending = new List<Connection>();

	private bool lobbyDirty;

	private float nextBeacon;

	private Connection hostConn;

	private string token = string.Empty;

	private int joinPort;

	private bool connecting;

	private float reconnectDeadline;

	private float nextAttempt;

	private bool gameStartedOnce;

	private int hostPing;

	private readonly List<PlayerInfo> remotePlayers = new List<PlayerInfo>();

	private float nextHeartbeat;

	public static NetSession Instance { get; private set; }

	public Role CurrentRole { get; private set; }

	public SessionState State { get; private set; }

	public GameMode Mode { get; private set; }

	public string Status { get; private set; } = "Desconectado";


	public int LocalSlot { get; private set; }

	public int Port { get; private set; } = 7777;


	public string JoinAddress { get; private set; }

	public int ReconnectAttempt { get; private set; }

	public bool IsHost => CurrentRole == Role.Host;

	public bool IsGuest => CurrentRole == Role.Guest;

	public bool InSession => State != SessionState.Offline;

	public float ReconnectTimeLeft
	{
		get
		{
			if (State != SessionState.Reconnecting)
			{
				return 0f;
			}
			return Mathf.Max(0f, reconnectDeadline - Time.unscaledTime);
		}
	}

	public bool PeerConnected => IsConnected((LocalSlot == 0) ? 1 : 0);

	public int PingMs
	{
		get
		{
			if (CurrentRole == Role.Guest)
			{
				return hostPing;
			}
			return slots[1]?.PingMs ?? 0;
		}
	}

	public bool CanStart
	{
		get
		{
			if (CurrentRole != Role.Host || State != SessionState.Lobby)
			{
				return false;
			}
			Slot slot = slots[1];
			if (slot != null && slot.Ready && slot.Conn != null)
			{
				return slot.Conn.Welcomed;
			}
			return false;
		}
	}

	public bool LocalReady
	{
		get
		{
			if (CurrentRole == Role.Host)
			{
				return true;
			}
			foreach (PlayerInfo remotePlayer in remotePlayers)
			{
				if (remotePlayer.Slot == LocalSlot)
				{
					return remotePlayer.Ready;
				}
			}
			return false;
		}
	}

	public event Action GameStarted;

	public event Action<int> PlayerJoined;

	public event Action<int> PlayerLeft;

	public event Action<int> PlayerDisconnected;

	public event Action<int> PlayerReconnected;

	public event Action<string> SessionEnded;

	private void Awake()
	{
		Instance = this;
	}

	private void OnApplicationQuit()
	{
		Leave();
	}

	private void OnDestroy()
	{
		Leave();
	}

	public void RegisterHandler(byte type, MessageHandler handler, bool onNetworkThread = false)
	{
		if (type <= 31)
		{
			throw new ArgumentException("tipo reservado do lobby: " + type);
		}
		if (onNetworkThread)
		{
			threadHandlers[type] = handler;
			mainHandlers[type] = null;
		}
		else
		{
			mainHandlers[type] = handler;
			threadHandlers[type] = null;
		}
	}

	public void Send(byte type, byte[] payload, int toSlot = -1)
	{
		foreach (Connection item in Targets(toSlot))
		{
			item.Enqueue(type, payload ?? Empty);
		}
	}

	public void SendLatest(byte type, byte[] payload, int toSlot = -1)
	{
		foreach (Connection item in Targets(toSlot))
		{
			item.SetLatest(type, payload ?? Empty);
		}
	}

	public bool IsConnected(int slot)
	{
		if (CurrentRole == Role.Host)
		{
			object obj;
			switch (slot)
			{
			case 0:
				return true;
			default:
				obj = null;
				break;
			case 1:
				obj = slots[slot];
				break;
			}
			Slot slot2 = (Slot)obj;
			if (slot2 != null && slot2.Conn != null)
			{
				return slot2.Conn.Welcomed;
			}
			return false;
		}
		if (CurrentRole == Role.Guest)
		{
			if (slot == LocalSlot)
			{
				return true;
			}
			if (hostConn != null && hostConn.Welcomed)
			{
				return hostConn.Closed == 0;
			}
			return false;
		}
		return false;
	}

	public List<PlayerInfo> GetPlayers()
	{
		List<PlayerInfo> list = new List<PlayerInfo>();
		if (CurrentRole == Role.Host)
		{
			float num = ((State == SessionState.InGame) ? 60f : 20f);
			Slot[] array = slots;
			foreach (Slot slot in array)
			{
				if (slot != null)
				{
					list.Add(new PlayerInfo
					{
						Slot = slot.Index,
						Name = slot.Name,
						Ready = slot.Ready,
						Connected = (slot.Index == 0 || (slot.Conn != null && slot.Conn.Welcomed)),
						PingMs = slot.PingMs,
						ReserveLeft = ((slot.Index != 0 && slot.Conn == null) ? Mathf.Max(0f, num - (Time.unscaledTime - slot.LostAt)) : 0f)
					});
				}
			}
		}
		else if (CurrentRole == Role.Guest)
		{
			list.AddRange(remotePlayers);
		}
		return list;
	}

	public bool Host(int port)
	{
		Leave();
		try
		{
			listener = new TcpListener(IPAddress.Any, port);
			listener.Start();
		}
		catch (Exception ex)
		{
			listener = null;
			Status = "Nao consegui abrir a porta " + port + ": " + ex.Message;
			return false;
		}
		generation++;
		CurrentRole = Role.Host;
		State = SessionState.Lobby;
		LocalSlot = 0;
		Port = port;
		Mode = DefaultMode;
		gameStartedOnce = false;
		slots[0] = new Slot
		{
			Index = 0,
			Name = LocalName,
			Ready = true
		};
		for (int i = 1; i < 2; i++)
		{
			slots[i] = null;
		}
		listening = true;
		TcpListener mine = listener;
		int gen = generation;
		acceptThread = new Thread((ThreadStart)delegate
		{
			AcceptLoop(mine, gen);
		});
		acceptThread.IsBackground = true;
		acceptThread.Start();
		NatHelper.Begin(port);
		Status = "Sala criada. Passe o codigo para o seu amigo.";
		lobbyDirty = true;
		NetLog.Info("sala criada na porta " + port);
		return true;
	}

	public void SetMode(GameMode mode)
	{
		if (CurrentRole == Role.Host && State == SessionState.Lobby)
		{
			Mode = mode;
			lobbyDirty = true;
		}
	}

	public void StartGame()
	{
		if (!CanStart)
		{
			return;
		}
		State = SessionState.InGame;
		Status = "Partida iniciada";
		foreach (Connection item in Targets(-1))
		{
			item.Enqueue(5, new byte[1] { (byte)Mode });
		}
		lobbyDirty = true;
		FireGameStarted();
	}

	public void Kick(int slot)
	{
		if (CurrentRole == Role.Host && slot > 0 && slot < 2 && slots[slot] != null)
		{
			Slot slot2 = slots[slot];
			slots[slot] = null;
			if (slot2.Conn != null)
			{
				SendBye(slot2.Conn, "O host tirou voce da sala");
			}
			lobbyDirty = true;
			Status = slot2.Name + " foi removido da sala";
			Fire(this.PlayerLeft, slot);
		}
	}

	private void AcceptLoop(TcpListener l, int gen)
	{
		while (listening && listener == l)
		{
			TcpClient tcpClient;
			try
			{
				tcpClient = l.AcceptTcpClient();
			}
			catch (Exception)
			{
				break;
			}
			tcpClient.NoDelay = true;
			Connection connection = new Connection
			{
				Client = tcpClient,
				Stream = tcpClient.GetStream(),
				Generation = gen,
				LastReceive = Environment.TickCount
			};
			try
			{
				connection.Remote = tcpClient.Client.RemoteEndPoint.ToString();
			}
			catch (Exception)
			{
			}
			Post(new NetEvent
			{
				Kind = EventKind.Accepted,
				Conn = connection,
				Generation = gen
			});
		}
	}

	private void HostHandle(Connection c, byte type, byte[] payload)
	{
		BinaryReader binaryReader = Reader(payload);
		switch (type)
		{
		case 1:
			HostHello(c, binaryReader);
			break;
		case 4:
		{
			Slot slot2 = SlotOf(c);
			if (slot2 != null)
			{
				slot2.Ready = binaryReader.ReadBoolean();
				lobbyDirty = true;
			}
			break;
		}
		case 7:
		{
			Slot slot3 = SlotOf(c);
			if (slot3 != null)
			{
				slot3.PingMs = Math.Max(0, Environment.TickCount - binaryReader.ReadInt32());
			}
			break;
		}
		case 8:
		{
			Slot slot = SlotOf(c);
			if (slot != null)
			{
				slots[slot.Index] = null;
				lobbyDirty = true;
				Status = slot.Name + " saiu da sala";
				Fire(this.PlayerLeft, slot.Index);
			}
			Close(c);
			break;
		}
		}
	}

	private void HostHello(Connection c, BinaryReader r)
	{
		if (!pending.Contains(c))
		{
			return;
		}
		pending.Remove(c);
		int num = r.ReadInt32();
		string text = CleanName(r.ReadString());
		string text2 = r.ReadString();
		if (num != 1)
		{
			Reject(c, "Versao do mod diferente (host " + 1 + ", voce " + num + "). Usem a mesma DLL.");
			return;
		}
		Slot s = slots[1];
		bool resumed = false;
		if (s != null && text2.Length > 0 && text2 == s.Token)
		{
			if (s.Conn != null && s.Conn != c)
			{
				Close(s.Conn);
			}
			resumed = true;
		}
		else
		{
			if (s != null)
			{
				Reject(c, (s.Conn == null) ? "A vaga esta reservada para quem caiu. Tente de novo em alguns segundos." : "A sala esta cheia");
				return;
			}
			s = new Slot
			{
				Index = 1,
				Token = Guid.NewGuid().ToString("N"),
				Ready = (State == SessionState.InGame)
			};
			slots[1] = s;
		}
		s.Name = text;
		s.Conn = c;
		s.LostAt = 0f;
		c.Slot = 1;
		c.Welcomed = true;
		c.Enqueue(2, Build(delegate(BinaryWriter w)
		{
			w.Write(value: true);
			w.Write(string.Empty);
			w.Write(s.Token);
			w.Write(resumed);
			w.Write((byte)1);
		}));
		if (State == SessionState.InGame)
		{
			c.Enqueue(5, new byte[1] { (byte)Mode });
		}
		lobbyDirty = true;
		NetLog.Info(text + (resumed ? " voltou" : " entrou") + " (" + c.Remote + ")");
		if (resumed)
		{
			Status = text + " reconectou";
			Fire(this.PlayerReconnected, 1);
		}
		else
		{
			Status = text + " entrou na sala";
			Fire(this.PlayerJoined, 1);
		}
	}

	private void Reject(Connection c, string reason)
	{
		c.Enqueue(2, Build(delegate(BinaryWriter w)
		{
			w.Write(value: false);
			w.Write(reason);
			w.Write(string.Empty);
			w.Write(value: false);
			w.Write((byte)0);
		}));
		c.CloseAfterSend = true;
		c.Signal.Set();
	}

	private Slot SlotOf(Connection c)
	{
		int slot = c.Slot;
		if (slot > 0 && slot < 2 && slots[slot] != null && slots[slot].Conn == c)
		{
			return slots[slot];
		}
		return null;
	}

	private void HostClosed(Connection c)
	{
		pending.Remove(c);
		Slot slot = SlotOf(c);
		if (slot != null)
		{
			slot.Conn = null;
			slot.LostAt = Time.unscaledTime;
			lobbyDirty = true;
			Status = slot.Name + " caiu. Esperando reconectar...";
			NetLog.Info(slot.Name + " caiu");
			Fire(this.PlayerDisconnected, slot.Index);
		}
	}

	private void HostUpdate()
	{
		float unscaledTime = Time.unscaledTime;
		for (int num = pending.Count - 1; num >= 0; num--)
		{
			if (unscaledTime - pending[num].OpenedAt > 5f)
			{
				Close(pending[num]);
				pending.RemoveAt(num);
			}
		}
		float num2 = ((State == SessionState.InGame) ? 60f : 20f);
		for (int i = 1; i < 2; i++)
		{
			Slot slot = slots[i];
			if (slot != null && slot.Conn == null && unscaledTime - slot.LostAt > num2)
			{
				slots[i] = null;
				lobbyDirty = true;
				Status = slot.Name + " nao voltou; a vaga foi liberada";
				Fire(this.PlayerLeft, i);
			}
		}
		if (lobbyDirty)
		{
			lobbyDirty = false;
			byte[] payload = BuildLobbyState();
			foreach (Connection item in Targets(-1))
			{
				item.Enqueue(3, payload);
			}
		}
		if (!(unscaledTime >= nextBeacon))
		{
			return;
		}
		nextBeacon = unscaledTime + 1f;
		int num3 = 0;
		Slot[] array = slots;
		for (int j = 0; j < array.Length; j++)
		{
			if (array[j] != null)
			{
				num3++;
			}
		}
		LanDiscovery.SendBeacon(LocalName, Port, num3, 2, State == SessionState.InGame);
	}

	private byte[] BuildLobbyState()
	{
		List<PlayerInfo> players = GetPlayers();
		return Build(delegate(BinaryWriter w)
		{
			w.Write((byte)State);
			w.Write((byte)Mode);
			w.Write((byte)players.Count);
			foreach (PlayerInfo item in players)
			{
				w.Write((byte)item.Slot);
				w.Write(item.Name);
				w.Write(item.Ready);
				w.Write(item.Connected);
				w.Write((short)Mathf.Min(item.PingMs, 32767));
				w.Write(item.ReserveLeft);
			}
		});
	}

	public bool Join(string address, int defaultPort)
	{
		if (!RoomCode.TryParseAddress(address, defaultPort, out var host, out var port))
		{
			Status = "Codigo ou IP invalido";
			return false;
		}
		Leave();
		generation++;
		CurrentRole = Role.Guest;
		State = SessionState.Connecting;
		LocalSlot = 1;
		JoinAddress = host;
		joinPort = port;
		token = string.Empty;
		gameStartedOnce = false;
		ReconnectAttempt = 0;
		remotePlayers.Clear();
		Status = "Conectando em " + host + ":" + port + "...";
		BeginConnect();
		return true;
	}

	public void SetReady(bool ready)
	{
		if (CurrentRole != Role.Guest || hostConn == null || !hostConn.Welcomed)
		{
			return;
		}
		hostConn.Enqueue(4, new byte[1] { ready ? ((byte)1) : ((byte)0) });
		foreach (PlayerInfo remotePlayer in remotePlayers)
		{
			if (remotePlayer.Slot == LocalSlot)
			{
				remotePlayer.Ready = ready;
			}
		}
	}

	private void BeginConnect()
	{
		connecting = true;
		string host = JoinAddress;
		int port = joinPort;
		int gen = generation;
		Thread thread = new Thread((ThreadStart)delegate
		{
			TcpClient tcpClient = new TcpClient();
			try
			{
				IAsyncResult asyncResult = tcpClient.BeginConnect(host, port, null, null);
				if (!asyncResult.AsyncWaitHandle.WaitOne(5000))
				{
					tcpClient.Close();
					Post(new NetEvent
					{
						Kind = EventKind.ConnectFailed,
						Error = "tempo esgotado",
						Generation = gen
					});
				}
				else
				{
					tcpClient.EndConnect(asyncResult);
					tcpClient.NoDelay = true;
					Connection conn = new Connection
					{
						Client = tcpClient,
						Stream = tcpClient.GetStream(),
						Generation = gen,
						Slot = 0,
						LastReceive = Environment.TickCount,
						Remote = host + ":" + port
					};
					Post(new NetEvent
					{
						Kind = EventKind.Connected,
						Conn = conn,
						Generation = gen
					});
				}
			}
			catch (Exception ex)
			{
				tcpClient.Close();
				Post(new NetEvent
				{
					Kind = EventKind.ConnectFailed,
					Error = ex.Message,
					Generation = gen
				});
			}
		});
		thread.IsBackground = true;
		thread.Start();
	}

	private void GuestConnected(Connection c)
	{
		if (hostConn != null && hostConn.Closed == 0)
		{
			c.Client.Close();
			return;
		}
		hostConn = c;
		StartThreads(c);
		c.Enqueue(1, Build(delegate(BinaryWriter w)
		{
			w.Write(1);
			w.Write(LocalName);
			w.Write(token);
		}));
		Status = ((State == SessionState.Reconnecting) ? "Conectado de novo, voltando para a sala..." : "Entrando na sala...");
	}

	private void GuestConnectFailed(string error)
	{
		connecting = false;
		if (State == SessionState.Reconnecting)
		{
			nextAttempt = Time.unscaledTime + 2f;
			Status = "Reconectando (tentativa " + ReconnectAttempt + "): " + error;
		}
		else if (State == SessionState.Connecting)
		{
			EndSession("Nao conectou: " + error);
		}
	}

	private void GuestHandle(Connection c, byte type, byte[] payload)
	{
		if (c != hostConn)
		{
			return;
		}
		BinaryReader binaryReader = Reader(payload);
		switch (type)
		{
		case 2:
		{
			bool num2 = binaryReader.ReadBoolean();
			string reason = binaryReader.ReadString();
			string text2 = binaryReader.ReadString();
			bool flag = binaryReader.ReadBoolean();
			int localSlot = binaryReader.ReadByte();
			if (!num2)
			{
				EndSession(reason);
				break;
			}
			bool num3 = State == SessionState.Reconnecting;
			connecting = false;
			token = text2;
			LocalSlot = localSlot;
			c.Welcomed = true;
			ReconnectAttempt = 0;
			if (num3 && flag)
			{
				State = (gameStartedOnce ? SessionState.InGame : SessionState.Lobby);
				Status = "Reconectado!";
				Fire(this.PlayerReconnected, 0);
			}
			else
			{
				State = SessionState.Lobby;
				gameStartedOnce = false;
				Status = "Na sala. Marque pronto quando quiser comecar.";
			}
			break;
		}
		case 3:
		{
			SessionState sessionState = (SessionState)binaryReader.ReadByte();
			Mode = (GameMode)binaryReader.ReadByte();
			int num = binaryReader.ReadByte();
			remotePlayers.Clear();
			for (int i = 0; i < num; i++)
			{
				PlayerInfo playerInfo = new PlayerInfo();
				playerInfo.Slot = binaryReader.ReadByte();
				playerInfo.Name = binaryReader.ReadString();
				playerInfo.Ready = binaryReader.ReadBoolean();
				playerInfo.Connected = binaryReader.ReadBoolean();
				playerInfo.PingMs = binaryReader.ReadInt16();
				playerInfo.ReserveLeft = binaryReader.ReadSingle();
				if (playerInfo.Slot == 0)
				{
					playerInfo.PingMs = hostPing;
				}
				remotePlayers.Add(playerInfo);
			}
			if (sessionState == SessionState.Lobby && State == SessionState.InGame)
			{
				State = SessionState.Lobby;
			}
			break;
		}
		case 5:
			if (payload.Length != 0)
			{
				Mode = (GameMode)payload[0];
			}
			State = SessionState.InGame;
			Status = "Partida iniciada";
			if (!gameStartedOnce)
			{
				FireGameStarted();
			}
			break;
		case 7:
			hostPing = Math.Max(0, Environment.TickCount - binaryReader.ReadInt32());
			break;
		case 8:
		{
			string text = binaryReader.ReadString();
			EndSession(string.IsNullOrEmpty(text) ? "O host fechou a sala" : text);
			break;
		}
		case 4:
		case 6:
			break;
		}
	}

	private void GuestClosed(Connection c)
	{
		if (c != hostConn)
		{
			return;
		}
		hostConn = null;
		connecting = false;
		if (State != 0)
		{
			if (State == SessionState.Reconnecting)
			{
				nextAttempt = Time.unscaledTime + 2f;
				return;
			}
			if (token.Length == 0)
			{
				EndSession("A conexao com o host caiu");
				return;
			}
			State = SessionState.Reconnecting;
			reconnectDeadline = Time.unscaledTime + 60f;
			nextAttempt = Time.unscaledTime + 1f;
			Status = "A conexao caiu. Reconectando...";
			NetLog.Info("conexao com o host caiu, reconectando");
			Fire(this.PlayerDisconnected, 0);
		}
	}

	private void GuestUpdate()
	{
		if (State == SessionState.Reconnecting)
		{
			if (Time.unscaledTime > reconnectDeadline)
			{
				EndSession("Nao consegui reconectar ao host");
			}
			else if (!connecting && Time.unscaledTime >= nextAttempt)
			{
				ReconnectAttempt++;
				Status = "Reconectando (tentativa " + ReconnectAttempt + ")...";
				BeginConnect();
			}
		}
	}

	public void Leave()
	{
		if (CurrentRole == Role.None && State == SessionState.Offline)
		{
			return;
		}
		bool flag = State != SessionState.Offline;
		generation++;
		if (CurrentRole == Role.Host)
		{
			listening = false;
			if (listener != null)
			{
				try
				{
					listener.Stop();
				}
				catch (Exception)
				{
				}
				listener = null;
			}
			for (int i = 1; i < 2; i++)
			{
				if (slots[i] != null && slots[i].Conn != null)
				{
					SendBye(slots[i].Conn, "O host fechou a sala");
				}
				slots[i] = null;
			}
			slots[0] = null;
			foreach (Connection item in pending)
			{
				Close(item);
			}
			pending.Clear();
			NatHelper.Close();
			LanDiscovery.StopBeacon();
		}
		else if (CurrentRole == Role.Guest && hostConn != null)
		{
			SendBye(hostConn, string.Empty);
			hostConn = null;
		}
		CurrentRole = Role.None;
		State = SessionState.Offline;
		connecting = false;
		remotePlayers.Clear();
		if (flag)
		{
			Status = "Voce saiu do online";
			Fire(this.SessionEnded, Status);
		}
	}

	private void EndSession(string reason)
	{
		if (hostConn != null)
		{
			Close(hostConn);
			hostConn = null;
		}
		generation++;
		CurrentRole = Role.None;
		State = SessionState.Offline;
		connecting = false;
		remotePlayers.Clear();
		Status = reason;
		NetLog.Info("sessao encerrada: " + reason);
		Fire(this.SessionEnded, reason);
	}

	private void SendBye(Connection c, string reason)
	{
		c.Enqueue(8, Build(delegate(BinaryWriter w)
		{
			w.Write(reason);
		}));
		c.CloseAfterSend = true;
		c.Signal.Set();
	}

	private void Update()
	{
		ProcessInbox();
		if (CurrentRole == Role.Host)
		{
			HostUpdate();
		}
		else if (CurrentRole == Role.Guest)
		{
			GuestUpdate();
		}
		if (Time.unscaledTime >= nextHeartbeat)
		{
			nextHeartbeat = Time.unscaledTime + 1f;
			Heartbeat();
		}
	}

	private void Heartbeat()
	{
		int now = Environment.TickCount;
		byte[] payload = Build(delegate(BinaryWriter w)
		{
			w.Write(now);
		});
		foreach (Connection item in AllConnections())
		{
			if (now - item.LastReceive > 6000)
			{
				NetLog.Info("sem resposta de " + item.Remote + ", fechando");
				Close(item);
			}
			else if (item.Welcomed)
			{
				item.Enqueue(6, payload);
			}
		}
		if (CurrentRole == Role.Host && slots[1] != null)
		{
			lobbyDirty = true;
		}
	}

	private void ProcessInbox()
	{
		while (true)
		{
			NetEvent e;
			lock (inboxLock)
			{
				if (inbox.Count == 0)
				{
					break;
				}
				e = inbox.Dequeue();
			}
			try
			{
				Dispatch(e);
			}
			catch (Exception ex)
			{
				NetLog.Info("ERRO erro tratando mensagem " + e.Type + ": " + ex);
			}
		}
	}

	private void Dispatch(NetEvent e)
	{
		bool flag = e.Generation == generation;
		switch (e.Kind)
		{
		case EventKind.Accepted:
			if (!flag || CurrentRole != Role.Host)
			{
				e.Conn.Client.Close();
				break;
			}
			e.Conn.OpenedAt = Time.unscaledTime;
			pending.Add(e.Conn);
			StartThreads(e.Conn);
			break;
		case EventKind.Connected:
			if (!flag || CurrentRole != Role.Guest)
			{
				e.Conn.Client.Close();
			}
			else
			{
				GuestConnected(e.Conn);
			}
			break;
		case EventKind.ConnectFailed:
			if (flag && CurrentRole == Role.Guest)
			{
				GuestConnectFailed(e.Error);
			}
			break;
		case EventKind.Closed:
			if (CurrentRole == Role.Host)
			{
				HostClosed(e.Conn);
			}
			else if (CurrentRole == Role.Guest)
			{
				GuestClosed(e.Conn);
			}
			break;
		case EventKind.Message:
			if (e.Type <= 31)
			{
				if (CurrentRole == Role.Host)
				{
					HostHandle(e.Conn, e.Type, e.Payload);
				}
				else if (CurrentRole == Role.Guest)
				{
					GuestHandle(e.Conn, e.Type, e.Payload);
				}
			}
			else if (e.Conn.Welcomed && e.Conn.Closed == 0)
			{
				mainHandlers[e.Type]?.Invoke(e.Conn.Slot, e.Payload);
			}
			break;
		}
	}

	private void Post(NetEvent e)
	{
		lock (inboxLock)
		{
			inbox.Enqueue(e);
		}
	}

	private List<Connection> Targets(int toSlot)
	{
		List<Connection> list = new List<Connection>();
		if (CurrentRole == Role.Host)
		{
			for (int i = 1; i < 2; i++)
			{
				Connection connection = slots[i]?.Conn;
				if (connection != null && connection.Welcomed && (toSlot < 0 || toSlot == i))
				{
					list.Add(connection);
				}
			}
		}
		else if (CurrentRole == Role.Guest)
		{
			Connection connection2 = hostConn;
			if (connection2 != null && connection2.Welcomed && (toSlot < 0 || toSlot == 0))
			{
				list.Add(connection2);
			}
		}
		return list;
	}

	private List<Connection> AllConnections()
	{
		List<Connection> list = new List<Connection>(pending);
		if (CurrentRole == Role.Host)
		{
			for (int i = 1; i < 2; i++)
			{
				if (slots[i] != null && slots[i].Conn != null)
				{
					list.Add(slots[i].Conn);
				}
			}
		}
		else if (hostConn != null)
		{
			list.Add(hostConn);
		}
		return list;
	}

	private void StartThreads(Connection c)
	{
		Thread thread = new Thread((ThreadStart)delegate
		{
			ReadLoop(c);
		});
		thread.IsBackground = true;
		thread.Start();
		Thread thread2 = new Thread((ThreadStart)delegate
		{
			SendLoop(c);
		});
		thread2.IsBackground = true;
		thread2.Start();
	}

	private void ReadLoop(Connection c)
	{
		try
		{
			BinaryReader binaryReader = new BinaryReader(c.Stream);
			while (c.Closed == 0)
			{
				byte b = binaryReader.ReadByte();
				int num = binaryReader.ReadInt32();
				if (num < 0 || num > 8388608)
				{
					throw new IOException("mensagem invalida");
				}
				byte[] array = ((num == 0) ? Empty : binaryReader.ReadBytes(num));
				if (array.Length != num)
				{
					throw new EndOfStreamException();
				}
				c.LastReceive = Environment.TickCount;
				if (b == 6)
				{
					c.Enqueue(7, array);
					continue;
				}
				if (b > 31 && c.Welcomed)
				{
					MessageHandler messageHandler = threadHandlers[b];
					if (messageHandler != null)
					{
						try
						{
							messageHandler(c.Slot, array);
						}
						catch (Exception ex)
						{
							NetLog.Info("ERRO erro no handler do tipo " + b + ": " + ex);
						}
						continue;
					}
				}
				Post(new NetEvent
				{
					Kind = EventKind.Message,
					Conn = c,
					Type = b,
					Payload = array,
					Generation = c.Generation
				});
			}
		}
		catch (Exception ex2)
		{
			if (c.Closed == 0)
			{
				NetLog.Info("leitura encerrada (" + c.Remote + "): " + ex2.Message);
			}
		}
		Close(c);
	}

	private void SendLoop(Connection c)
	{
		List<byte[]> list = new List<byte[]>();
		try
		{
			while (c.Closed == 0)
			{
				c.Signal.WaitOne(100);
				lock (c.Queue)
				{
					while (c.Queue.Count > 0)
					{
						list.Add(c.Queue.Dequeue());
					}
					foreach (byte[] value in c.Latest.Values)
					{
						list.Add(value);
					}
					c.Latest.Clear();
				}
				if (list.Count > 0)
				{
					foreach (byte[] item in list)
					{
						c.Stream.Write(item, 0, item.Length);
					}
					c.Stream.Flush();
					list.Clear();
				}
				else if (c.CloseAfterSend)
				{
					break;
				}
			}
		}
		catch (Exception ex)
		{
			if (c.Closed == 0)
			{
				NetLog.Info("envio encerrado (" + c.Remote + "): " + ex.Message);
			}
		}
		Close(c);
	}

	private void Close(Connection c)
	{
		if (Interlocked.Exchange(ref c.Closed, 1) != 1)
		{
			try
			{
				c.Client.Close();
			}
			catch (Exception)
			{
			}
			c.Signal.Set();
			Post(new NetEvent
			{
				Kind = EventKind.Closed,
				Conn = c,
				Generation = c.Generation
			});
		}
	}

	private void FireGameStarted()
	{
		gameStartedOnce = true;
		NetLog.Info("partida iniciada (modo " + Mode.ToString() + ")");
		if (this.GameStarted != null)
		{
			try
			{
				this.GameStarted();
			}
			catch (Exception ex)
			{
				NetLog.Info("ERRO " + ex);
			}
		}
	}

	private static void Fire<T>(Action<T> handler, T arg)
	{
		if (handler == null)
		{
			return;
		}
		try
		{
			handler(arg);
		}
		catch (Exception ex)
		{
			NetLog.Info("ERRO " + ex);
		}
	}

	private static string CleanName(string name)
	{
		name = (name ?? string.Empty).Trim();
		if (name.Length > 20)
		{
			name = name.Substring(0, 20);
		}
		if (name.Length != 0)
		{
			return name;
		}
		return "Jogador";
	}

	private static byte[] Frame(byte type, byte[] payload)
	{
		byte[] array = new byte[5 + payload.Length];
		array[0] = type;
		array[1] = (byte)payload.Length;
		array[2] = (byte)(payload.Length >> 8);
		array[3] = (byte)(payload.Length >> 16);
		array[4] = (byte)(payload.Length >> 24);
		Buffer.BlockCopy(payload, 0, array, 5, payload.Length);
		return array;
	}

	public static byte[] Build(Action<BinaryWriter> write)
	{
		using MemoryStream memoryStream = new MemoryStream();
		BinaryWriter binaryWriter = new BinaryWriter(memoryStream, Encoding.UTF8);
		write(binaryWriter);
		binaryWriter.Flush();
		return memoryStream.ToArray();
	}

	public static BinaryReader Reader(byte[] payload)
	{
		return new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
	}
}
