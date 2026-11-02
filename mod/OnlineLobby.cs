using System;
using System.Collections.Generic;
using System.Net;
using UnityEngine;

public class OnlineLobby : MonoBehaviour
{
	private const string PrefName = "OnlineName";

	private const string PrefPort = "OnlinePort";

	private const string PrefJoin = "OnlineLastJoin";

	private NetSession session;

	private bool showWindow = true;

	private Rect windowRect = new Rect(30f, 110f, 520f, 440f);

	private Vector2 scroll;

	private string nameText = "Jogador";

	private string portText = "7777";

	private string joinText = string.Empty;

	private bool showAdvanced;

	private bool autoReady;

	private bool autoStart;

	private bool prefsFromArgs;

	private bool readySent;

	private string copiedNote = string.Empty;

	private float copiedUntil;

	private List<string> localIps = new List<string>();

	public static OnlineLobby Instance { get; private set; }

	public bool WindowVisible => showWindow;

	private void Awake()
	{
		Instance = this;
		session = base.gameObject.AddComponent<NetSession>();
		base.gameObject.AddComponent<OnlineStream>();
		base.gameObject.AddComponent<NetcodeBridge>();
		string defaultValue = "Jogador";
		try
		{
			defaultValue = Environment.UserName;
		}
		catch (Exception)
		{
		}
		nameText = PlayerPrefs.GetString("OnlineName", defaultValue);
		portText = PlayerPrefs.GetInt("OnlinePort", 7777).ToString();
		joinText = PlayerPrefs.GetString("OnlineLastJoin", string.Empty);
		localIps = LanDiscovery.LocalAddresses();
		session.GameStarted += delegate
		{
			showWindow = false;
		};
		session.SessionEnded += delegate
		{
			showWindow = true;
			readySent = false;
		};
	}

	private void Start()
	{
		string[] commandLineArgs = Environment.GetCommandLineArgs();
		bool flag = false;
		string text = null;
		for (int i = 0; i < commandLineArgs.Length; i++)
		{
			string text2 = commandLineArgs[i].ToLowerInvariant();
			string text3 = ((i + 1 < commandLineArgs.Length) ? commandLineArgs[i + 1] : null);
			switch (text2)
			{
			case "-host":
				flag = true;
				break;
			case "-join":
				if (text3 != null)
				{
					text = text3;
					i++;
				}
				break;
			case "-name":
				if (text3 != null)
				{
					prefsFromArgs = true;
					nameText = text3;
					i++;
				}
				break;
			case "-port":
				if (text3 != null)
				{
					prefsFromArgs = true;
					portText = text3;
					i++;
				}
				break;
			case "-mode":
				if (text3 != null)
				{
					NetSession.DefaultMode = ((!(text3.ToLowerInvariant() == "sync")) ? NetSession.GameMode.Stream : NetSession.GameMode.Sync);
					i++;
				}
				break;
			case "-autoready":
				autoReady = true;
				break;
			case "-autostart":
				autoStart = true;
				break;
			case "-nolobby":
				showWindow = false;
				break;
			}
		}
		if (flag)
		{
			CreateRoom();
		}
		else if (text != null)
		{
			joinText = text;
			JoinRoom();
		}
	}

	private void Update()
	{
		if (Input.GetKeyDown(KeyCode.F1))
		{
			showWindow = !showWindow;
		}
		Application.runInBackground = session.InSession || NetcodeSync.Active || NetcodeTestTransport.Started;
		bool flag = showWindow && !session.InSession;
		if (flag && !LanDiscovery.Listening)
		{
			LanDiscovery.StartListening();
		}
		else if (!flag && LanDiscovery.Listening)
		{
			LanDiscovery.StopListening();
		}
		if (session.IsGuest && session.State == NetSession.SessionState.Lobby)
		{
			if (autoReady && !readySent && session.PeerConnected)
			{
				readySent = true;
				session.SetReady(ready: true);
			}
		}
		else if (session.State != NetSession.SessionState.Reconnecting && session.State != NetSession.SessionState.InGame)
		{
			readySent = false;
		}
		if (autoStart && session.CanStart)
		{
			session.StartGame();
		}
	}

	private int ParsePort()
	{
		if (int.TryParse(portText, out var result) && result > 0 && result < 65536)
		{
			return result;
		}
		return 7777;
	}

	private void ApplyName()
	{
		string text = nameText.Trim();
		if (text.Length == 0)
		{
			text = "Jogador";
		}
		session.LocalName = text;
		if (!prefsFromArgs)
		{
			PlayerPrefs.SetString("OnlineName", text);
			PlayerPrefs.SetInt("OnlinePort", ParsePort());
			PlayerPrefs.Save();
		}
	}

	private void CreateRoom()
	{
		ApplyName();
		session.Host(ParsePort());
	}

	private void JoinRoom()
	{
		ApplyName();
		if (session.Join(joinText, ParsePort()) && !prefsFromArgs)
		{
			PlayerPrefs.SetString("OnlineLastJoin", joinText.Trim());
			PlayerPrefs.Save();
		}
	}

	private void JoinLan(LanDiscovery.Room room)
	{
		ApplyName();
		session.Join(room.Address + ":" + room.Port, room.Port);
	}

	private void Copy(string text, string what)
	{
		GUIUtility.systemCopyBuffer = text;
		copiedNote = what + " copiado!";
		copiedUntil = Time.unscaledTime + 2f;
	}

	private void OnGUI()
	{
		float num = Mathf.Max(1f, (float)Screen.height / 720f);
		Matrix4x4 matrix = GUI.matrix;
		GUI.matrix = Matrix4x4.Scale(new Vector3(num, num, 1f));
		GUI.depth = -1010;
		if (session.IsHost)
		{
			GUI.Label(text: (session.PeerConnected ? ("ONLINE: amigo conectado no player 2  |  " + session.PingMs + " ms  |  " + ((NetInput.LatestButtons != 0) ? "amigo apertando botao" : "amigo parado")) : ((session.GetPlayers().Count > 1) ? "ONLINE: amigo caiu, esperando ele voltar" : "ONLINE: esperando amigo")) + "  |  F1 = lobby" + ((session.State == NetSession.SessionState.InGame && session.Mode == NetSession.GameMode.Stream && showWindow) ? "  |  imagem pausada com o lobby aberto" : string.Empty), position: new Rect(10f, 4f, 900f, 30f));
		}
		else if (session.State == NetSession.SessionState.Reconnecting)
		{
			GUI.Label(new Rect(10f, (float)Screen.height / num - 55f, 900f, 30f), "ONLINE: reconectando ao host... " + Mathf.CeilToInt(session.ReconnectTimeLeft) + "s");
		}
		if (showWindow)
		{
			windowRect = GUI.Window(77001, windowRect, DrawWindow, NetProtocol.Credits + "  -  Lobby (F1 mostra/esconde)");
		}
		GUI.matrix = matrix;
	}

	private void DrawWindow(int id)
	{
		GUILayout.Space(4f);
		GUILayout.Label("Status: " + session.Status);
		if (Time.unscaledTime < copiedUntil)
		{
			GUILayout.Label(copiedNote);
		}
		GUILayout.Space(4f);
		scroll = GUILayout.BeginScrollView(scroll);
		if (!session.InSession)
		{
			DrawOffline();
		}
		else if (session.IsHost)
		{
			DrawHost();
		}
		else
		{
			DrawGuest();
		}
		GUILayout.EndScrollView();
		GUI.DragWindow();
	}

	private void DrawOffline()
	{
		GUILayout.BeginHorizontal();
		GUILayout.Label("Seu nome:", GUILayout.Width(80f));
		nameText = GUILayout.TextField(nameText, 20);
		GUILayout.EndHorizontal();
		GUILayout.Space(6f);
		if (GUILayout.Button("Criar sala", GUILayout.Height(32f)))
		{
			CreateRoom();
		}
		GUILayout.Label("Voce joga como player 1 e recebe um codigo para passar ao amigo.");
		GUILayout.Space(8f);
		GUILayout.Label("Salas na sua rede:");
		List<LanDiscovery.Room> rooms = LanDiscovery.GetRooms();
		if (rooms.Count == 0)
		{
			GUILayout.Label("   (nenhuma sala encontrada ainda)");
		}
		foreach (LanDiscovery.Room item in rooms)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label("   " + item.HostName + "  (" + item.Players + "/" + item.MaxPlayers + (item.InGame ? ", jogando" : string.Empty) + ")  " + item.Address);
			GUI.enabled = item.Players < item.MaxPlayers;
			if (GUILayout.Button("Entrar", GUILayout.Width(80f)))
			{
				JoinLan(item);
			}
			GUI.enabled = true;
			GUILayout.EndHorizontal();
		}
		GUILayout.Space(8f);
		GUILayout.Label("Entrar com codigo da sala (ou IP):");
		GUILayout.BeginHorizontal();
		joinText = GUILayout.TextField(joinText, 64);
		if (GUILayout.Button("Colar", GUILayout.Width(60f)))
		{
			joinText = GUIUtility.systemCopyBuffer.Trim();
		}
		GUILayout.EndHorizontal();
		if (GUILayout.Button("Entrar na sala", GUILayout.Height(30f)))
		{
			JoinRoom();
		}
		GUILayout.Space(6f);
		showAdvanced = GUILayout.Toggle(showAdvanced, " Avancado");
		if (showAdvanced)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label("Porta:", GUILayout.Width(60f));
			portText = GUILayout.TextField(portText, 6, GUILayout.Width(80f));
			GUILayout.EndHorizontal();
			GUILayout.Label("Seus IPs: " + string.Join(", ", localIps.ToArray()));
		}
	}

	private void DrawHost()
	{
		int port = session.Port;
		string publicIp = NatHelper.PublicIp;
		if (publicIp != null)
		{
			string text = RoomCode.Encode(IPAddress.Parse(publicIp), port);
			GUILayout.BeginHorizontal();
			GUILayout.Label("Codigo da sala (internet):  " + text);
			if (GUILayout.Button("Copiar", GUILayout.Width(70f)))
			{
				Copy(text, "Codigo");
			}
			GUILayout.EndHorizontal();
		}
		else if (NatHelper.Busy)
		{
			GUILayout.Label("Preparando o codigo da internet...");
		}
		if (NatHelper.Status.Length > 0)
		{
			GUILayout.Label(NatHelper.Status);
		}
		foreach (string localIp in localIps)
		{
			string text2 = RoomCode.Encode(IPAddress.Parse(localIp), port);
			GUILayout.BeginHorizontal();
			GUILayout.Label("Rede local/VPN (" + localIp + "):  " + text2);
			if (GUILayout.Button("Copiar", GUILayout.Width(70f)))
			{
				Copy(text2, "Codigo");
			}
			GUILayout.EndHorizontal();
		}
		GUILayout.Space(6f);
		if (session.State == NetSession.SessionState.Lobby)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label("Modo:", GUILayout.Width(50f));
			int num = GUILayout.Toolbar((session.Mode != NetSession.GameMode.Stream) ? 1 : 0, new string[2] { "Tela compartilhada", "Sincronizado" });
			session.SetMode((num == 0) ? NetSession.GameMode.Stream : NetSession.GameMode.Sync);
			GUILayout.EndHorizontal();
		}
		DrawPlayers(canKick: true);
		GUILayout.Space(6f);
		if (session.State == NetSession.SessionState.Lobby)
		{
			GUI.enabled = session.CanStart;
			if (GUILayout.Button(session.CanStart ? "Iniciar partida" : "Esperando o amigo ficar pronto...", GUILayout.Height(32f)))
			{
				session.StartGame();
			}
			GUI.enabled = true;
		}
		else
		{
			GUILayout.Label("Partida em andamento (modo " + ModeName(session.Mode) + ").");
		}
		if (GUILayout.Button("Fechar sala", GUILayout.Height(26f)))
		{
			session.Leave();
		}
	}

	private void DrawGuest()
	{
		switch (session.State)
		{
		case NetSession.SessionState.Connecting:
			GUILayout.Label("Conectando em " + session.JoinAddress + "...");
			break;
		case NetSession.SessionState.Reconnecting:
			GUILayout.Label("A conexao caiu. Tentando voltar sozinho por mais " + Mathf.CeilToInt(session.ReconnectTimeLeft) + "s.");
			break;
		case NetSession.SessionState.Lobby:
		{
			GUILayout.Label("Modo da partida: " + ModeName(session.Mode));
			bool localReady = session.LocalReady;
			if (GUILayout.Button(localReady ? "Pronto! (clique para cancelar)" : "Estou pronto", GUILayout.Height(32f)))
			{
				session.SetReady(!localReady);
			}
			GUILayout.Label(localReady ? "Esperando o host iniciar." : "Marque pronto quando quiser comecar.");
			break;
		}
		case NetSession.SessionState.InGame:
			GUILayout.Label("Partida em andamento (modo " + ModeName(session.Mode) + ").");
			if (session.Mode == NetSession.GameMode.Stream)
			{
				GUILayout.Label("Seus controles vao para o player 2 do host.");
			}
			break;
		}
		if (session.State != NetSession.SessionState.Connecting)
		{
			DrawPlayers(canKick: false);
		}
		GUILayout.Space(6f);
		if (GUILayout.Button((session.State == NetSession.SessionState.Reconnecting || session.State == NetSession.SessionState.Connecting) ? "Cancelar" : "Sair da sala", GUILayout.Height(26f)))
		{
			session.Leave();
		}
	}

	private void DrawPlayers(bool canKick)
	{
		GUILayout.Label("Jogadores:");
		List<NetSession.PlayerInfo> players = session.GetPlayers();
		for (int i = 0; i < 2; i++)
		{
			NetSession.PlayerInfo playerInfo = null;
			foreach (NetSession.PlayerInfo item in players)
			{
				if (item.Slot == i)
				{
					playerInfo = item;
				}
			}
			GUILayout.BeginHorizontal();
			if (playerInfo == null)
			{
				GUILayout.Label("   P" + (i + 1) + ":  (vaga livre)");
			}
			else
			{
				string text = ((!playerInfo.Connected) ? ("caiu, vaga guardada por " + Mathf.CeilToInt(playerInfo.ReserveLeft) + "s") : ((i != 0) ? ((playerInfo.Ready ? "pronto" : "nao pronto") + "  |  " + playerInfo.PingMs + " ms") : "host"));
				GUILayout.Label("   P" + (i + 1) + ":  " + playerInfo.Name + ((i == session.LocalSlot) ? " (voce)" : string.Empty) + "  -  " + text);
				if (canKick && i != 0 && GUILayout.Button("Remover", GUILayout.Width(80f)))
				{
					session.Kick(i);
				}
			}
			GUILayout.EndHorizontal();
		}
	}

	private static string ModeName(NetSession.GameMode mode)
	{
		if (mode != NetSession.GameMode.Stream)
		{
			return "sincronizado";
		}
		return "tela compartilhada";
	}
}
