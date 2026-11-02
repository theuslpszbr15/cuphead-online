using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Rewired;
using UnityEngine;
using UnityEngine.SceneManagement;

public class NetcodeSync : MonoBehaviour
{
	private struct Snap
	{
		public uint hash;

		public NetProtocol.PlayerState[] players;

		public float bossDamage;

		// Only snapshots taken in the same, fully loaded scene are comparable.
		public int epoch;

		public bool stable;
	}

	private struct PendingGo
	{
		public string key;

		public int frame;

		public int seed;
	}

	private const int BufferSize = 1024;

	private const float StallTimeout = 10f;

	private const float BarrierTimeout = 60f;

	private const int SnapshotInterval = 30;

	private const int TargetFps = 60;

	private static readonly object sync;

	private static INetcodeTransport transport;

	private static volatile bool running;

	private static bool pendingBegin;

	private static bool pendingEnd;

	private static string pendingEndReason;

	private static bool helloSent;

	private static float helloSentAt;

	private static bool peerHello;

	private static readonly Queue<KeyValuePair<byte, byte[]>> mainQueue;

	private static readonly NetProtocol.InputFrame[] localBuf;

	private static readonly NetProtocol.InputFrame[] remoteBuf;

	private static readonly int[] remoteTag;

	private static int frame;

	private static byte epoch;

	private static int inputDelay;

	private static int lastAdvanceUnityFrame;

	private static int savedVSync;

	private static int savedTargetFps;

	private static float savedFixedDelta;

	private static bool advancing;

	private static readonly HashSet<string> peerReady;

	private static PendingGo? pendingGo;

	private static string[] deferredScene;

	private static readonly Dictionary<int, Snap> localSnaps;

	private static readonly Dictionary<int, Snap> hostSnaps;

	private static readonly float[] axesTmp;

	private static long pingMs;

	private float nextPing;

	private static bool delayFromArgs;

	private static int autoSlot;

	private static bool autoTitleDone;

	private static bool autoEntered;

	private static System.Random bot;

	private static Levels? testLevel;

	private static NetProtocol.InputFrame botFrame;

	private static int botNextChange;

	private static readonly int[] BotButtons;

	private static int testWinAfter;

	private static int testDesyncAfter;

	private static bool testDesyncBoss;

	private static Level testWinLevel;

	private static int testWinStart;

	private static bool clientLoading;

	private static float lastResync;

	private const float ResyncInterval = 10f;

	private static int severeStreak;

	private static bool trace;

	private static bool deepTrace;

	private static StreamWriter deepWriter;

	private static readonly List<string> deepLines = new List<string>();

	public static NetcodeSync Instance { get; private set; }

	public static bool Active { get; private set; }

	public static bool IsHost { get; private set; }

	public static bool InLockstep => running;

	public static int Frame => frame;

	public static int InputDelay => inputDelay;

	// Test harness only (-netcode-dmgtrace): logs every hit on the boss with frame and call chain.
	public static bool DamageTrace;

	public static void LogDamage(float amount, float total)
	{
		try
		{
			StackTrace st = new StackTrace(2, fNeedFileInfo: false);
			string chain = string.Empty;
			for (int i = 0; i < Math.Min(4, st.FrameCount); i++)
			{
				MethodBase m = st.GetFrame(i).GetMethod();
				chain = chain + ((m.DeclaringType != null) ? m.DeclaringType.Name : "?") + "." + m.Name + " < ";
			}
			NetLog.Info("[Dmg] " + frame + " u" + Time.frameCount + " +" + amount.ToString("0.###") + " = " + total.ToString("0.###") + " " + chain);
		}
		catch (Exception)
		{
		}
	}

	public static int DesyncCount { get; private set; }

	// Host's rotateControlsWithCamera, applied on both PCs (it changes how input is read).
	public static bool SharedRotateControls;

	public static string Warning { get; private set; } = string.Empty;

	// Test harness only: simulates the v4 bug (local controller read during the boss intro) on this instance.
	public static bool TestOldHeldBug;

	public static string Status { get; private set; }

	static NetcodeSync()
	{
		sync = new object();
		mainQueue = new Queue<KeyValuePair<byte, byte[]>>();
		localBuf = new NetProtocol.InputFrame[1024];
		remoteBuf = new NetProtocol.InputFrame[1024];
		remoteTag = new int[1024];
		inputDelay = 3;
		lastAdvanceUnityFrame = -1;
		savedVSync = -1;
		savedTargetFps = 60;
		savedFixedDelta = 0.02f;
		peerReady = new HashSet<string>();
		localSnaps = new Dictionary<int, Snap>();
		hostSnaps = new Dictionary<int, Snap>();
		axesTmp = new float[4];
		pingMs = -1L;
		autoSlot = -1;
		BotButtons = new int[4] { 2, 3, 7, 13 };
		testWinAfter = -1;
		testDesyncAfter = -1;
		lastResync = -100f;
		Status = string.Empty;
	}

	public static void Begin(INetcodeTransport t, bool isHost)
	{
		lock (sync)
		{
			transport = t;
			IsHost = isHost;
			Active = true;
			pendingBegin = true;
			pendingEnd = running;
			pendingEndReason = "reconectando";
			mainQueue.Clear();
		}
	}

	public static void End()
	{
		End("conexao encerrada");
	}

	public static void End(string reason)
	{
		lock (sync)
		{
			if (Active)
			{
				pendingEnd = true;
				pendingEndReason = reason;
				Monitor.PulseAll(sync);
			}
		}
	}

	public static void OnPacket(byte type, byte[] payload)
	{
		try
		{
			switch (type)
			{
			case 34:
			{
				BinaryReader binaryReader = NetProtocol.Reader(payload);
				byte b = binaryReader.ReadByte();
				int num2 = binaryReader.ReadInt32();
				int num3 = binaryReader.ReadByte();
				lock (sync)
				{
					if (b == epoch)
					{
						for (int i = 0; i < num3; i++)
						{
							int num4 = num2 + i;
							remoteBuf[num4 % 1024] = NetProtocol.InputFrame.Read(binaryReader);
							remoteTag[num4 % 1024] = num4;
						}
						Monitor.PulseAll(sync);
					}
					break;
				}
			}
			case 33:
				lock (sync)
				{
					if (!IsHost && payload.Length != 0)
					{
						epoch = payload[0];
						ClearRemote();
					}
					mainQueue.Enqueue(new KeyValuePair<byte, byte[]>(type, payload));
					break;
				}
			case 42:
				Send(43, payload);
				break;
			case 43:
			{
				long num = NetProtocol.Reader(payload).ReadInt64();
				pingMs = (DateTime.UtcNow.Ticks - num) / 10000;
				AddRtt(pingMs);
				break;
			}
			default:
				lock (sync)
				{
					mainQueue.Enqueue(new KeyValuePair<byte, byte[]>(type, payload));
					break;
				}
			}
		}
		catch (Exception ex)
		{
			NetLog.Info("[Netcode] pacote invalido " + type + ": " + ex.Message);
		}
	}

	public static void OnSceneLoadStarted(Scenes scene, SceneLoader.Transition transitionStart, SceneLoader.Transition transitionEnd, SceneLoader.Icon icon)
	{
		if (!running)
		{
			return;
		}
		NetLog.Info("[Netcode] carregando " + scene.ToString() + " no frame " + frame);
		if (!IsHost)
		{
			return;
		}
		string name = scene.ToString();
		lock (sync)
		{
			peerReady.RemoveWhere((string k) => !k.EndsWith(":" + name));
		}
		int level = (int)SceneLoader.CurrentLevel;
		Send(36, NetProtocol.Build(delegate(BinaryWriter w)
		{
			w.Write(name);
			w.Write(level);
			w.Write((byte)transitionStart);
			w.Write((byte)transitionEnd);
			w.Write((byte)icon);
		}));
	}

	public static bool BlockLocalLoad(Scenes scene)
	{
		if (!running || IsHost || clientLoading)
		{
			return false;
		}
		NetLog.Info("[Netcode] amigo: ignorando troca de cena local para " + scene.ToString() + " (quem decide e o host)");
		return true;
	}

	public static IEnumerator SceneBarrier(int stage, string scene)
	{
		if (!running)
		{
			yield break;
		}
		string key = stage + ":" + scene;
		Status = "Esperando o outro jogador carregar...";
		float start = Time.realtimeSinceStartup;
		int target;
		int seed;
		if (IsHost)
		{
			while (running && Time.realtimeSinceStartup - start < 60f)
			{
				lock (sync)
				{
					if (peerReady.Remove(key))
					{
						break;
					}
				}
				ProcessQueue();
				yield return null;
			}
			if (Time.realtimeSinceStartup - start >= 60f)
			{
				NetLog.Info("[Netcode] barreira sem resposta do amigo: " + key);
			}
			target = frame + inputDelay + 2;
			seed = new System.Random().Next();
			byte stageByte = (byte)stage;
			int t = target;
			int s = seed;
			string save = ((stage == 0) ? PlayerData.NetExportCurrent() : string.Empty);
			Send(38, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write(stageByte);
				w.Write(scene);
				w.Write(t);
				w.Write(s);
				w.Write(save);
			}));
		}
		else
		{
			byte stageByte2 = (byte)stage;
			Send(37, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write(stageByte2);
				w.Write(scene);
			}));
			while (running && (!pendingGo.HasValue || pendingGo.Value.key != key) && Time.realtimeSinceStartup - start < 60f)
			{
				ProcessQueue();
				yield return null;
			}
			if (!pendingGo.HasValue || pendingGo.Value.key != key)
			{
				NetLog.Info("[Netcode] barreira sem resposta do host: " + key);
				Status = string.Empty;
				yield break;
			}
			target = pendingGo.Value.frame;
			seed = pendingGo.Value.seed;
			pendingGo = null;
		}
		while (running && frame < target)
		{
			yield return null;
		}
		UnityEngine.Random.InitState(seed);
		NetRandom.SeedEffects(seed);
		if (stage == 1)
		{
			sceneEpoch++;
			sceneReleaseFrame = target;
			if (!IsHost)
			{
				PlayerData.NetSaveSharedCopy();
			}
		}
		Status = string.Empty;
		NetLog.Info("[Netcode] barreira " + key + " liberada no frame " + frame + " (" + (Time.realtimeSinceStartup - start).ToString("0.0") + " s)");
	}

	public static void Advance()
	{
		if (!running || advancing || Time.frameCount == lastAdvanceUnityFrame)
		{
			return;
		}
		advancing = true;
		try
		{
			lastAdvanceUnityFrame = Time.frameCount;
			ProcessQueue();
			if (!running)
			{
				return;
			}
			int num = frame + 1;
			int target = num + inputDelay;
			NetProtocol.InputFrame local = SampleLocal();
			// An input is final once sent. Delay going up: repeat this sample over the gap.
			// Delay going down: targets already sent are kept, nothing is overwritten.
			if (target > lastSentTarget)
			{
				int first = lastSentTarget + 1;
				int count = target - first + 1;
				for (int k = first; k <= target; k++)
				{
					localBuf[k % 1024] = local;
				}
				lastSentTarget = target;
				byte e = epoch;
				Send(34, NetProtocol.Build(delegate(BinaryWriter w)
				{
					w.Write(e);
					w.Write(first);
					w.Write((byte)count);
					for (int k = 0; k < count; k++)
					{
						local.Write(w);
					}
				}));
			}
			if (!WaitRemote(num))
			{
				StopLockstep("o outro jogador parou de responder");
				return;
			}
			frame = num;
			AdaptDelay();
			NetProtocol.InputFrame inputFrame = localBuf[num % 1024];
			NetProtocol.InputFrame inputFrame2;
			lock (sync)
			{
				inputFrame2 = remoteBuf[num % 1024];
			}
			if (IsHost)
			{
				NetInput.SetLockstepFrame(inputFrame, inputFrame2);
			}
			else
			{
				NetInput.SetLockstepFrame(inputFrame2, inputFrame);
			}
			if (frame % 30 == 0)
			{
				TakeSnapshot();
			}
			if (trace)
			{
				Trace(inputFrame, inputFrame2);
			}
			if (deepTrace)
			{
				DeepTrace();
			}
			TestWin();
		}
		finally
		{
			advancing = false;
		}
	}

	private void Awake()
	{
		Instance = this;
		NetLog.Init();
		for (int i = 0; i < 1024; i++)
		{
			remoteTag[i] = -1;
		}
	}

	private void Start()
	{
		string[] commandLineArgs = Environment.GetCommandLineArgs();
		for (int i = 0; i < commandLineArgs.Length; i++)
		{
			int result2;
			Levels result3;
			int result4;
			int result5;
			if (commandLineArgs[i] == "-netdelay" && i + 1 < commandLineArgs.Length && int.TryParse(commandLineArgs[i + 1], out var result))
			{
				inputDelay = Mathf.Clamp(result, 1, 30);
				delayFromArgs = true;
			}
			else if (commandLineArgs[i] == "-netcode-autoload" && i + 1 < commandLineArgs.Length && int.TryParse(commandLineArgs[i + 1], out result2))
			{
				autoSlot = result2;
			}
			else if (commandLineArgs[i] == "-netcode-testlevel" && i + 1 < commandLineArgs.Length && EnumUtils.TryParse<Levels>(commandLineArgs[i + 1], out result3))
			{
				testLevel = result3;
			}
			else if (commandLineArgs[i] == "-netcode-testwin" && i + 1 < commandLineArgs.Length && int.TryParse(commandLineArgs[i + 1], out result4))
			{
				testWinAfter = result4;
			}
			else if (commandLineArgs[i] == "-netcode-testdesync" && i + 1 < commandLineArgs.Length && int.TryParse(commandLineArgs[i + 1], out result5))
			{
				testDesyncAfter = result5;
			}
			else if (commandLineArgs[i] == "-netcode-trace")
			{
				trace = true;
				NetRandom.Trace = true;
			}
			else if (commandLineArgs[i] == "-netcode-deeptrace")
			{
				deepTrace = true;
			}
			else if (commandLineArgs[i] == "-netcode-godmode")
			{
				PlayerStatsManager.NetGodMode = true;
			}
			else if (commandLineArgs[i] == "-netcode-godmode-p1")
			{
				PlayerStatsManager.NetGodModeP1 = true;
			}
			else if (commandLineArgs[i] == "-netcode-test-oldheld")
			{
				TestOldHeldBug = true;
			}
			else if (commandLineArgs[i] == "-netcode-test-bossdesync")
			{
				testDesyncBoss = true;
			}
			else if (commandLineArgs[i] == "-netcode-dmgtrace")
			{
				DamageTrace = true;
			}
			else if (commandLineArgs[i] == "-netcode-test-reverse")
			{
				NetDamageQueue.TestReverse = true;
			}
			else if (commandLineArgs[i] == "-netcode-botinput")
			{
				bot = new System.Random((i + 1) * 7919 + Environment.TickCount);
			}
		}
		NetcodeTestTransport.StartFromCommandLine();
		StartCoroutine(FlushDamageAfterPhysics());
	}

	private IEnumerator FlushDamageAfterPhysics()
	{
		WaitForFixedUpdate wait = new WaitForFixedUpdate();
		while (true)
		{
			yield return wait;
			NetDamageQueue.Flush();
		}
	}

	private void Update()
	{
		bool flag;
		bool flag2;
		lock (sync)
		{
			flag = pendingEnd;
			flag2 = pendingBegin;
			pendingEnd = false;
			pendingBegin = false;
		}
		if (flag)
		{
			StopLockstep(pendingEndReason);
			if (!flag2)
			{
				Active = false;
				transport = null;
				return;
			}
		}
		if (autoSlot >= 0 && !autoEntered)
		{
			AutoLoad();
		}
		// Offline test helper: -netcode-autoload N -netcode-testlevel X without a connection.
		if (!Active && autoEntered && testLevel.HasValue && !SceneLoader.CurrentlyLoading && SceneManager.GetActiveScene().name.StartsWith("scene_map_"))
		{
			Levels value0 = testLevel.Value;
			testLevel = null;
			SceneLoader.LoadLevel(value0, SceneLoader.Transition.Fade);
		}
		if (flag2)
		{
			running = false;
			helloSent = false;
			peerHello = false;
			DesyncCount = 0;
			Status = "Conectado. Entrem os dois em um save (mapa) para comecar.";
		}
		if (!Active)
		{
			return;
		}
		if (transport != null && !transport.Connected)
		{
			End("conexao caiu");
			return;
		}
		if (!helloSent && LocalReady())
		{
			helloSent = true;
			helloSentAt = Time.unscaledTime;
			Send(32, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write(NetProtocol.Version);
				w.Write(NetProtocol.BuildHash);
				w.Write(NetProtocol.DataHash);
				w.Write(NetProtocol.GameVersion ?? string.Empty);
			}));
		}
		if (!running)
		{
			ProcessQueue();
			// Old versions (v4 and earlier) drop out silently when they see a newer hello.
			if (helloSent && !peerHello && string.IsNullOrEmpty(Warning) && Time.unscaledTime - helloSentAt > 30f)
			{
				Warning = "O outro jogador nao respondeu em 30 s: deve estar com um Cuphead Online antigo. Mande o launcher v" + NetProtocol.Version + " pra ele.";
				NetLog.Info("[Netcode] sem resposta do outro PC 30 s apos o hello (versao antiga?)");
			}
			if (IsHost && helloSent && peerHello && LocalReady())
			{
				HostStart();
			}
		}
		else
		{
			Advance();
			if (IsHost && testLevel.HasValue && frame > 120 && !SceneLoader.CurrentlyLoading && SceneManager.GetActiveScene().name.StartsWith("scene_map_"))
			{
				Levels value = testLevel.Value;
				testLevel = null;
				SceneLoader.LoadLevel(value, SceneLoader.Transition.Fade);
			}
			if (deferredScene != null && !SceneLoader.CurrentlyLoading)
			{
				string[] msg = deferredScene;
				deferredScene = null;
				ClientLoadScene(msg);
			}
		}
		if (Time.unscaledTime >= nextPing)
		{
			nextPing = Time.unscaledTime + (running ? 2f : 0.5f);
			Send(42, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write(DateTime.UtcNow.Ticks);
			}));
		}
	}

	private void OnApplicationQuit()
	{
		if (Active)
		{
			Send(44, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write("saiu do jogo");
			}));
		}
		StopLockstep("saiu do jogo");
	}

	private void OnGUI()
	{
		if (Active || !string.IsNullOrEmpty(Warning))
		{
			GUI.depth = -1001;
			string text = NetProtocol.Credits;
			if (!Active)
			{
				text += "  |  online desligado";
			}
			else if (running)
			{
				string who = (IsHost ? "host" : "amigo") + " (" + LocalCharacter() + ")";
				string quality = (hudStallRate < 0.02f) ? "boa" : ((hudStallRate < 0.08f) ? "media" : "ruim");
				text = text + "  |  voce: " + who + "  |  conexao " + quality + ", ping " + ((pingMs >= 0) ? (pingMs + " ms") : "?") + ", atraso " + inputDelay + ((DesyncCount > 0) ? ("  |  correcoes " + DesyncCount) : string.Empty);
			}
			else
			{
				text += string.IsNullOrEmpty(Status) ? "  |  conectado, esperando os dois entrarem em um save" : string.Empty;
			}
			if (!string.IsNullOrEmpty(Status))
			{
				text = text + "  |  " + Status;
			}
			GUI.color = new Color(0f, 0f, 0f, 0.6f);
			GUI.DrawTexture(new Rect(0f, Screen.height - 20, Screen.width, 20f), Texture2D.whiteTexture);
			if (!string.IsNullOrEmpty(Warning))
			{
				GUI.DrawTexture(new Rect(0f, Screen.height - 42, Screen.width, 20f), Texture2D.whiteTexture);
				GUI.color = new Color(1f, 0.85f, 0.4f);
				GUI.Label(new Rect(10f, Screen.height - 41, Screen.width - 20, 20f), Warning);
			}
			GUI.color = Color.white;
			GUI.Label(new Rect(10f, Screen.height - 19, Screen.width - 20, 20f), text);
		}
	}

	private static float hudStallRate;

	private static int hudStalls;

	private static int hudFrames;

	private static int lastSentTarget;

	private static float peerStallRate;

	private static int calmWindows;

	private const int AdaptWindow = 120;

	private const int MaxDelay = 15;

	private static readonly long[] rttSamples = new long[8];

	private static int rttCount;

	private static void AddRtt(long ms)
	{
		lock (rttSamples)
		{
			rttSamples[rttCount % rttSamples.Length] = ms;
			rttCount++;
		}
	}

	private static long MaxRtt()
	{
		lock (rttSamples)
		{
			long max = -1L;
			int n = Math.Min(rttCount, rttSamples.Length);
			for (int i = 0; i < n; i++)
			{
				max = Math.Max(max, rttSamples[i]);
			}
			return max;
		}
	}

	// Frames of buffer needed so the other side's input arrives in time (one-way trip + 1 frame of margin).
	private static int DelayForRtt(long rtt)
	{
		if (rtt < 0)
		{
			return 3;
		}
		return Mathf.Clamp(Mathf.CeilToInt((float)rtt / 2f / 16.67f) + 1, 2, MaxDelay);
	}

	private static void AdaptDelay()
	{
		hudFrames++;
		if (hudFrames < AdaptWindow)
		{
			return;
		}
		float rate = (float)hudStalls / (float)hudFrames;
		hudStalls = 0;
		hudFrames = 0;
		// Waits during scene loads come from disk/asset hitches, not from the network.
		if (SceneLoader.CurrentlyLoading || sceneReleaseFrame == int.MaxValue || frame < sceneReleaseFrame + AdaptWindow)
		{
			return;
		}
		if (!IsHost)
		{
			hudStallRate = rate;
			Send(47, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write(rate);
			}));
			return;
		}
		rate = Mathf.Max(rate, peerStallRate);
		hudStallRate = rate;
		if (delayFromArgs)
		{
			return;
		}
		int next = inputDelay;
		if (rate > 0.05f)
		{
			next = Mathf.Min(inputDelay + ((rate > 0.2f) ? 2 : 1), MaxDelay);
			calmWindows = 0;
		}
		else if (rate == 0f)
		{
			calmWindows++;
			// Only go down after ~30 s without any wait and if the ping allows it.
			if (calmWindows >= 15 && inputDelay > DelayForRtt(MaxRtt()))
			{
				next = inputDelay - 1;
				calmWindows = 0;
			}
		}
		else
		{
			calmWindows = 0;
		}
		if (next != inputDelay)
		{
			NetLog.Info("[Netcode] atraso " + inputDelay + " -> " + next + " (espera " + (rate * 100f).ToString("0") + "% dos frames, ping " + pingMs + " ms)");
			inputDelay = next;
			byte d = (byte)next;
			Send(46, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write(d);
			}));
		}
	}

	// Host is always P1, friend P2; the game makes P2 the opposite of P1.
	private static string LocalCharacter()
	{
		bool p1Mugman = PlayerManager.player1IsMugman;
		bool mugman = IsHost ? p1Mugman : (!p1Mugman);
		return mugman ? "Mugman" : "Cuphead";
	}

	private static bool LocalReady()
	{
		if (!PlayerData.Initialized || SceneLoader.CurrentlyLoading)
		{
			return false;
		}
		string text = SceneManager.GetActiveScene().name;
		if (!text.StartsWith("scene_map_"))
		{
			return text.StartsWith("scene_level_");
		}
		return true;
	}

	private static void AutoLoad()
	{
		if (!SceneLoader.CurrentlyLoading)
		{
			string text = SceneManager.GetActiveScene().name;
			if (text == "scene_title" && !autoTitleDone)
			{
				autoTitleDone = true;
				PlayerManager.NetResetSlots();
				SceneLoader.LoadScene(Scenes.scene_slot_select, SceneLoader.Transition.Fade, SceneLoader.Transition.Fade);
			}
			else if (text == "scene_slot_select" && SlotSelectScreen.NetInstance != null)
			{
				PlayerManager.NetResetSlots();
				autoEntered = SlotSelectScreen.NetInstance.NetAutoEnter(autoSlot);
			}
			else if (text.StartsWith("scene_map_") || text.StartsWith("scene_level_"))
			{
				autoEntered = true;
			}
		}
	}

	private static void HostStart()
	{
		if (!delayFromArgs && MaxRtt() >= 0)
		{
			inputDelay = DelayForRtt(MaxRtt());
		}
		string save = PlayerData.NetExportCurrent();
		int level = (int)SceneLoader.CurrentLevel;
		int delay = inputDelay;
		SharedRotateControls = SettingsData.Data.rotateControlsWithCamera;
		bool rotate = SharedRotateControls;
		byte e;
		lock (sync)
		{
			epoch++;
			ClearRemote();
			e = epoch;
		}
		Send(33, NetProtocol.Build(delegate(BinaryWriter w)
		{
			w.Write(e);
			w.Write((byte)delay);
			w.Write(save);
			w.Write(level);
			w.Write(rotate);
		}));
		StartLockstep();
		// Restart the fight the host is in (e.g. after a reconnect) instead of dropping both on the map.
		if (Level.Current != null && SceneManager.GetActiveScene().name.StartsWith("scene_level_"))
		{
			NetLog.Info("[Netcode] recomecando a fase atual para os dois: " + Level.Current.CurrentLevel);
			SceneLoader.LoadLevel(Level.Current.CurrentLevel, SceneLoader.Transition.Fade);
		}
		else
		{
			SceneLoader.LoadScene(PlayerData.Data.CurrentMap, SceneLoader.Transition.Fade, SceneLoader.Transition.Fade);
		}
	}

	private static void ClearRemote()
	{
		for (int i = 0; i < 1024; i++)
		{
			remoteTag[i] = -1;
		}
	}

	private static void StartLockstep()
	{
		lock (sync)
		{
			frame = 0;
			for (int i = 0; i < 1024; i++)
			{
				localBuf[i] = default(NetProtocol.InputFrame);
			}
			for (int j = 1; j <= inputDelay; j++)
			{
				remoteBuf[j] = default(NetProtocol.InputFrame);
				remoteTag[j] = j;
			}
			peerReady.Clear();
			localSnaps.Clear();
			hostSnaps.Clear();
		}
		sceneEpoch = 0;
		sceneReleaseFrame = int.MaxValue;
		NetDamageQueue.Clear();
		lastSentTarget = inputDelay;
		hudStalls = 0;
		hudFrames = 0;
		hudStallRate = 0f;
		peerStallRate = 0f;
		calmWindows = 0;
		pendingGo = null;
		deferredScene = null;
		lastAdvanceUnityFrame = Time.frameCount;
		PlayerManager.NetResetSlots();
		NetInput.SetLockstepActive(active: true);
		Time.captureFramerate = 60;
		if (savedVSync < 0)
		{
			savedVSync = QualitySettings.vSyncCount;
			savedTargetFps = Application.targetFrameRate;
			savedFixedDelta = Time.fixedDeltaTime;
		}
		Time.fixedDeltaTime = 1f / 60f;
		QualitySettings.vSyncCount = 0;
		Application.targetFrameRate = 60;
		Application.runInBackground = true;
		running = true;
		Status = string.Empty;
		NetLog.Info("[Netcode] lockstep iniciado (" + (IsHost ? "host" : "amigo") + ", atraso " + inputDelay + ", ping max " + MaxRtt() + " ms) | P1 " + (PlayerManager.player1IsMugman ? "Mugman" : "Cuphead") + " (host), P2 " + (PlayerManager.player1IsMugman ? "Cuphead" : "Mugman") + " (amigo)");
	}

	private static void StopLockstep(string reason)
	{
		bool flag = running;
		running = false;
		NetDamageQueue.Flush();
		lock (sync)
		{
			Monitor.PulseAll(sync);
		}
		NetInput.SetLockstepActive(active: false);
		Time.captureFramerate = 0;
		if (savedVSync >= 0)
		{
			QualitySettings.vSyncCount = savedVSync;
			Application.targetFrameRate = savedTargetFps;
			Time.fixedDeltaTime = savedFixedDelta;
			savedVSync = -1;
		}
		pendingGo = null;
		deferredScene = null;
		Status = "Online encerrado: " + reason;
		if (flag)
		{
			NetLog.Info("[Netcode] lockstep encerrado: " + reason);
			if (!IsHost)
			{
				PlayerData.NetSaveSharedCopy();
				PlayerData.NetRestoreBackup();
				SceneLoader.LoadScene(PlayerData.Data.CurrentMap, SceneLoader.Transition.Fade, SceneLoader.Transition.Fade);
			}
		}
	}

	private static void Send(byte type, byte[] payload)
	{
		INetcodeTransport netcodeTransport = transport;
		if (netcodeTransport == null || !netcodeTransport.Connected)
		{
			return;
		}
		try
		{
			netcodeTransport.Send(type, payload);
		}
		catch (Exception ex)
		{
			NetLog.Info("[Netcode] falha ao enviar: " + ex.Message);
		}
	}

	private static void ProcessQueue()
	{
		while (true)
		{
			KeyValuePair<byte, byte[]> keyValuePair;
			lock (sync)
			{
				if (mainQueue.Count == 0)
				{
					break;
				}
				keyValuePair = mainQueue.Dequeue();
			}
			try
			{
				Handle(keyValuePair.Key, NetProtocol.Reader(keyValuePair.Value));
			}
			catch (Exception ex)
			{
				NetLog.Info("[Netcode] erro tratando pacote " + keyValuePair.Key + ": " + ex);
			}
		}
	}

	private static void Handle(byte type, BinaryReader r)
	{
		switch (type)
		{
		case 32:
		{
			ushort num4 = r.ReadUInt16();
			int num5 = r.ReadInt32();
			if (num4 != NetProtocol.Version)
			{
				Warning = "Versao do Cuphead Online diferente: este PC v" + NetProtocol.Version + ", o outro v" + num4 + ". Os dois precisam usar o mesmo launcher.";
				NetLog.Info("[Netcode] versao diferente: este PC protocolo " + NetProtocol.Version + ", o outro " + num4 + " - conexao recusada");
				End("versao diferente");
			}
			else
			{
				int peerData = r.ReadInt32();
				string peerGame = r.ReadString();
				NetLog.Info("[Netcode] outro PC: jogo " + peerGame + " dados " + peerData.ToString("x8") + " | este PC: jogo " + NetProtocol.GameVersion + " dados " + NetProtocol.DataHash.ToString("x8"));
				if (num5 != NetProtocol.BuildHash)
				{
					NetLog.Info("[Netcode] aviso: DLL diferente da do outro PC, pode dessincronizar");
					Status = "Aviso: os dois PCs devem usar a mesma Assembly-CSharp.dll";
				}
				if (peerGame != NetProtocol.GameVersion || peerData != NetProtocol.DataHash)
				{
					NetLog.Info("[Netcode] aviso: arquivos do jogo diferentes entre os PCs, os chefes vao dessincronizar");
					Warning = "AVISO: o Cuphead dos dois PCs e diferente (" + NetProtocol.GameVersion + " x " + peerGame + "). Usem a mesma copia do jogo ou os chefes vao dessincronizar.";
				}
				else
				{
					Warning = string.Empty;
				}
				peerHello = true;
			}
			break;
		}
		case 33:
			if (!IsHost)
			{
				r.ReadByte();
				inputDelay = Mathf.Clamp(r.ReadByte(), 1, 30);
				string json = r.ReadString();
				SceneLoader.SetCurrentLevel((Levels)r.ReadInt32());
				SharedRotateControls = r.ReadBoolean();
				PlayerData.NetImportCurrent(json);
				StartLockstep();
			}
			break;
		case 36:
		{
			if (IsHost || !running)
			{
				break;
			}
			string[] array = new string[5]
			{
				r.ReadString(),
				r.ReadInt32().ToString(),
				r.ReadByte().ToString(),
				r.ReadByte().ToString(),
				r.ReadByte().ToString()
			};
			if (SceneLoader.CurrentlyLoading)
			{
				if (SceneLoader.SceneName != array[0])
				{
					deferredScene = array;
				}
			}
			else
			{
				ClientLoadScene(array);
			}
			break;
		}
		case 37:
			if (IsHost)
			{
				int num = r.ReadByte();
				string text = r.ReadString();
				lock (sync)
				{
					peerReady.Add(num + ":" + text);
					break;
				}
			}
			break;
		case 38:
			if (!IsHost)
			{
				int num2 = r.ReadByte();
				string text2 = r.ReadString();
				PendingGo value = default(PendingGo);
				value.key = num2 + ":" + text2;
				value.frame = r.ReadInt32();
				value.seed = r.ReadInt32();
				string text3 = r.ReadString();
				if (text3.Length > 0)
				{
					PlayerData.NetImportCurrent(text3);
				}
				pendingGo = value;
			}
			break;
		case 40:
		{
			if (IsHost || !running)
			{
				break;
			}
			int key = r.ReadInt32();
			Snap value2 = default(Snap);
			value2.hash = r.ReadUInt32();
			int num3 = r.ReadByte();
			value2.players = new NetProtocol.PlayerState[2];
			for (int i = 0; i < num3; i++)
			{
				NetProtocol.PlayerState playerState = NetProtocol.PlayerState.Read(r);
				if (playerState.id < 2)
				{
					value2.players[playerState.id] = playerState;
				}
			}
			value2.bossDamage = r.ReadSingle();
			value2.epoch = r.ReadInt32();
			value2.stable = r.ReadBoolean();
			hostSnaps[key] = value2;
			CompareSnapshots();
			break;
		}
		case 44:
			End("o outro jogador saiu (" + r.ReadString() + ")");
			break;
		case 45:
			if (IsHost && running)
			{
				HostResync(r.ReadString());
			}
			break;
		case 46:
			if (!IsHost && running)
			{
				int d = Mathf.Clamp(r.ReadByte(), 1, MaxDelay);
				NetLog.Info("[Netcode] host mudou o atraso " + inputDelay + " -> " + d);
				inputDelay = d;
			}
			break;
		case 47:
			if (IsHost)
			{
				peerStallRate = r.ReadSingle();
			}
			break;
		case 34:
		case 35:
		case 39:
		case 41:
		case 42:
		case 43:
			break;
		}
	}

	private static void HostResync(string reason)
	{
		NetLog.Info("[Netcode] amigo pediu ressincronizar: " + reason);
		if (!SceneLoader.CurrentlyLoading && !(Time.realtimeSinceStartup - lastResync < 10f))
		{
			lastResync = Time.realtimeSinceStartup;
			string text = SceneManager.GetActiveScene().name;
			NetLog.Info("[Netcode] ressincronizando: recarregando " + text);
			Status = "Os jogos se separaram, recarregando para os dois...";
			if (Level.Current != null && text.StartsWith("scene_level_"))
			{
				SceneLoader.LoadLevel(Level.Current.CurrentLevel, SceneLoader.Transition.Fade);
			}
			else if (text.StartsWith("scene_map_"))
			{
				SceneLoader.LoadScene(PlayerData.Data.CurrentMap, SceneLoader.Transition.Fade, SceneLoader.Transition.Fade);
			}
		}
	}

	private static void RequestResync(string reason)
	{
		if (!(Time.realtimeSinceStartup - lastResync < 10f))
		{
			lastResync = Time.realtimeSinceStartup;
			NetLog.Info("[Netcode] pedindo ao host para ressincronizar: " + reason);
			Send(45, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write(reason);
			}));
		}
	}

	private static void ClientLoadScene(string[] msg)
	{
		if (!EnumUtils.TryParse<Scenes>(msg[0], out var result))
		{
			NetLog.Info("[Netcode] cena desconhecida: " + msg[0]);
			return;
		}
		SceneLoader.SetCurrentLevel((Levels)int.Parse(msg[1]));
		clientLoading = true;
		try
		{
			SceneLoader.LoadScene(result, (SceneLoader.Transition)byte.Parse(msg[2]), (SceneLoader.Transition)byte.Parse(msg[3]), (SceneLoader.Icon)byte.Parse(msg[4]));
		}
		finally
		{
			clientLoading = false;
		}
	}

	private static NetProtocol.InputFrame SampleLocal()
	{
		if (bot != null)
		{
			if (frame >= botNextChange)
			{
				botNextChange = frame + 10 + bot.Next(30);
				uint num = 0u;
				int[] botButtons = BotButtons;
				foreach (int num2 in botButtons)
				{
					if (bot.Next(3) == 0)
					{
						num |= (uint)(1 << num2);
					}
				}
				axesTmp[0] = bot.Next(3) - 1;
				axesTmp[1] = 0f;
				axesTmp[2] = 0f;
				axesTmp[3] = 0f;
				botFrame = NetProtocol.InputFrame.From(num, axesTmp);
			}
			return botFrame;
		}
		if (!Application.isFocused)
		{
			return default(NetProtocol.InputFrame);
		}
		Player player = ReInput.players.GetPlayer(0);
		if (player == null)
		{
			return default(NetProtocol.InputFrame);
		}
		PinJoysticks(player);
		NetInput.ReadLocal(player, out var buttons, axesTmp);
		return NetProtocol.InputFrame.From(buttons, axesTmp);
	}

	private static void Trace(NetProtocol.InputFrame mine, NetProtocol.InputFrame theirs)
	{
		try
		{
			string text = "[Trace] " + frame + " t " + Time.time.ToString("0.0000") + " fixo " + Time.fixedTime.ToString("0.0000") + " rnd " + JsonUtility.ToJson(UnityEngine.Random.state).GetHashCode().ToString("x8") + " in " + mine.buttons.ToString("x") + "/" + theirs.buttons.ToString("x") + " rawjump " + ((ReInput.players.GetPlayer(0) != null && ReInput.players.GetPlayer(0).GetButton(2)) ? 1 : 0);
			for (int i = 0; i < 2; i++)
			{
				AbstractPlayerController player = PlayerManager.GetPlayer((PlayerId)i);
				if (player != null)
				{
					Vector3 position = player.transform.position;
					text = text + " p" + (i + 1) + " " + position.x.ToString("0.000") + "," + position.y.ToString("0.000") + " v" + ((player.stats != null) ? player.stats.Health : (-1));
				}
			}
			NetLog.Info(text);
		}
		catch (Exception ex)
		{
			NetLog.Info("[Trace] erro: " + ex.Message);
		}
	}

	private static void DeepTrace()
	{
		try
		{
			if (deepWriter == null)
			{
				deepWriter = new StreamWriter(NetLog.LogPath + ".deep", append: false);
			}
			deepLines.Clear();
			Collider2D[] array = UnityEngine.Object.FindObjectsOfType<Collider2D>();
			foreach (Collider2D collider2D in array)
			{
				if (collider2D.enabled && collider2D.gameObject.activeInHierarchy)
				{
					Vector3 position = collider2D.transform.position;
					deepLines.Add(collider2D.gameObject.name.Replace("(Clone)", string.Empty).Replace(' ', '_') + " " + position.x.ToString("0.00") + " " + position.y.ToString("0.00"));
				}
			}
			deepLines.Sort(StringComparer.Ordinal);
			int animStart = deepLines.Count;
			Animator[] animators = UnityEngine.Object.FindObjectsOfType<Animator>();
			foreach (Animator animator in animators)
			{
				if (animator.isActiveAndEnabled && animator.runtimeAnimatorController != null && animator.layerCount > 0)
				{
					AnimatorStateInfo st = animator.GetCurrentAnimatorStateInfo(0);
					Renderer rend = animator.GetComponent<Renderer>();
					deepLines.Add("A " + animator.gameObject.name.Replace("(Clone)", string.Empty).Replace(' ', '_') + " " + st.fullPathHash.ToString("x8") + " " + st.normalizedTime.ToString("0.000") + " " + animator.cullingMode.ToString() + ((rend != null) ? (rend.isVisible ? " vis" : " invis") : " norend"));
				}
			}
			DamageReceiver[] receivers = UnityEngine.Object.FindObjectsOfType<DamageReceiver>();
			foreach (DamageReceiver dr in receivers)
			{
				deepLines.Add("R " + dr.gameObject.name.Replace("(Clone)", string.Empty).Replace(' ', '_') + " " + (dr.enabled ? 1 : 0) + (dr.gameObject.activeInHierarchy ? 1 : 0));
			}
			deepLines.Sort(animStart, deepLines.Count - animStart, StringComparer.Ordinal);
			float num = -1f;
			if (Level.Current != null && Level.Current.timeline != null)
			{
				num = Level.Current.timeline.damage;
			}
			deepWriter.WriteLine("F " + frame + " " + SceneManager.GetActiveScene().name + " boss " + num.ToString("0.00") + " n " + deepLines.Count);
			for (int j = 0; j < deepLines.Count; j++)
			{
				deepWriter.WriteLine(deepLines[j]);
			}
			if (frame % 60 == 0)
			{
				deepWriter.Flush();
			}
		}
		catch (Exception ex)
		{
			NetLog.Info("[DeepTrace] erro: " + ex.Message);
			deepTrace = false;
		}
	}

	private static void TestWin()
	{
		if ((testWinAfter >= 0 || testDesyncAfter >= 0) && !(Level.Current == null))
		{
			if (Level.Current != testWinLevel)
			{
				testWinLevel = Level.Current;
				testWinStart = frame;
			}
			else if (!IsHost && frame - testWinStart == testDesyncAfter && PlayerManager.GetPlayer(PlayerId.PlayerOne) != null)
			{
				testDesyncAfter = -1;
				if (testDesyncBoss && Level.Current.timeline != null)
				{
					NetLog.Info("[Netcode] teste: dano extra no chefe so no amigo no frame " + frame);
					Level.Current.timeline.DealDamage(25f);
					return;
				}
				NetLog.Info("[Netcode] teste: zerando a vida do player 1 so no amigo no frame " + frame);
				PlayerManager.GetPlayer(PlayerId.PlayerOne).stats.SetHealth(0);
			}
			else if (frame - testWinStart == testWinAfter && !Level.Current.Ending)
			{
				NetLog.Info("[Netcode] teste: chefe derrotado no frame " + frame);
				typeof(Level).GetMethod("zHack_OnWin", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Level.Current, null);
			}
		}
	}

	private static void PinJoysticks(Player player)
	{
		foreach (Joystick joystick in ReInput.controllers.Joysticks)
		{
			if (!player.controllers.ContainsController(joystick))
			{
				player.controllers.AddController(joystick, removeFromOtherPlayers: true);
				ReInput.userDataStore.LoadControllerData(player.id, ControllerType.Joystick, joystick.id);
				NetLog.Info("[Netcode] controle no player local: " + joystick.name);
			}
		}
	}

	private static bool WaitRemote(int f)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			lock (sync)
			{
				while (remoteTag[f % 1024] != f)
				{
					if (pendingEnd || transport == null || !transport.Connected || stopwatch.Elapsed.TotalSeconds > 10.0)
					{
						return false;
					}
					Monitor.Wait(sync, 20);
				}
			}
			return true;
		}
		finally
		{
			double ms = stopwatch.Elapsed.TotalMilliseconds;
			if (ms > 2.0)
			{
				stallFrames++;
				hudStalls++;
				stallTotalMs += ms;
				if (ms > stallMaxMs)
				{
					stallMaxMs = ms;
				}
			}
		}
	}

	private static int stallFrames;

	private static int sceneEpoch;

	private static int sceneReleaseFrame = int.MaxValue;

	private static double stallTotalMs;

	private static double stallMaxMs;

	private static string TakeStallStats()
	{
		string result = "espera " + stallFrames + "/300 frames, max " + stallMaxMs.ToString("0") + " ms, total " + stallTotalMs.ToString("0") + " ms";
		stallFrames = 0;
		stallTotalMs = 0.0;
		stallMaxMs = 0.0;
		return result;
	}

	private static Snap Capture()
	{
		Snap result = default(Snap);
		result.players = new NetProtocol.PlayerState[2];
		uint h = 2166136261u;
		for (int i = 0; i < 2; i++)
		{
			NetProtocol.PlayerState playerState = default(NetProtocol.PlayerState);
			playerState.id = (byte)i;
			try
			{
				AbstractPlayerController player = PlayerManager.GetPlayer((PlayerId)i);
				if (player != null)
				{
					playerState.present = true;
					Vector3 position = player.transform.position;
					playerState.x = position.x;
					playerState.y = position.y;
					if (player.stats != null)
					{
						playerState.hp = player.stats.Health;
						playerState.super = player.stats.SuperMeter;
					}
				}
				else
				{
					Transform transform = MapPlayer(i);
					if (transform != null)
					{
						playerState.present = true;
						playerState.x = transform.position.x;
						playerState.y = transform.position.y;
					}
				}
			}
			catch (Exception)
			{
			}
			result.players[i] = playerState;
			h = Mix(h, playerState.present ? 1 : 0);
			h = Mix(h, Mathf.RoundToInt(playerState.x));
			h = Mix(h, Mathf.RoundToInt(playerState.y));
			h = Mix(h, playerState.hp);
		}
		result.bossDamage = -1f;
		try
		{
			if (Level.Current != null && Level.Current.timeline != null)
			{
				result.bossDamage = Level.Current.timeline.damage;
			}
		}
		catch (Exception)
		{
		}
		h = Mix(h, Mathf.RoundToInt(result.bossDamage));
		result.hash = h;
		result.epoch = sceneEpoch;
		result.stable = !SceneLoader.CurrentlyLoading && sceneReleaseFrame != int.MaxValue && frame > sceneReleaseFrame + 1;
		return result;
	}

	private static Transform MapPlayer(int i)
	{
		Map current = Map.Current;
		if (current == null || current.players == null || i >= current.players.Length || current.players[i] == null)
		{
			return null;
		}
		return current.players[i].transform;
	}

	private static uint Mix(uint h, int v)
	{
		for (int i = 0; i < 4; i++)
		{
			h ^= (uint)(v >> i * 8) & 0xFFu;
			h *= 16777619;
		}
		return h;
	}

	private static void TakeSnapshot()
	{
		Snap snap = Capture();
		int f = frame;
		if (frame % 300 == 0)
		{
			NetLog.Info("[Netcode] snap frame " + f + " hash " + snap.hash.ToString("x8") + " cena " + SceneManager.GetActiveScene().name + " p1 " + snap.players[0].x.ToString("0.0") + "," + snap.players[0].y.ToString("0.0") + " p2 " + (snap.players[1].present ? (snap.players[1].x.ToString("0.0") + "," + snap.players[1].y.ToString("0.0")) : "-") + " chefe " + snap.bossDamage.ToString("0.0") + " | " + TakeStallStats() + " | ping " + pingMs + " atraso " + inputDelay);
		}
		if (IsHost)
		{
			Send(40, NetProtocol.Build(delegate(BinaryWriter w)
			{
				w.Write(f);
				w.Write(snap.hash);
				w.Write((byte)2);
				snap.players[0].Write(w);
				snap.players[1].Write(w);
				w.Write(snap.bossDamage);
				w.Write(snap.epoch);
				w.Write(snap.stable);
			}));
		}
		else
		{
			localSnaps[f] = snap;
			CompareSnapshots();
		}
	}

	private static void CompareSnapshots()
	{
		List<int> list = null;
		foreach (KeyValuePair<int, Snap> hostSnap in hostSnaps)
		{
			if (!localSnaps.TryGetValue(hostSnap.Key, out var value))
			{
				continue;
			}
			if (list == null)
			{
				list = new List<int>();
			}
			list.Add(hostSnap.Key);
			if (!value.stable || !hostSnap.Value.stable || value.epoch != hostSnap.Value.epoch)
			{
				continue;
			}
			if (value.hash != hostSnap.Value.hash)
			{
				DesyncCount++;
				NetLog.Info("[Netcode] dessincronia no frame " + hostSnap.Key + " cena " + SceneManager.GetActiveScene().name + ", corrigindo pelos dados do host" + Environment.NewLine + "    host:  " + Describe(hostSnap.Value) + Environment.NewLine + "    amigo: " + Describe(value));
				string text = Correct(hostSnap.Value, value);
				severeStreak = ((text != null && !SceneLoader.CurrentlyLoading) ? (severeStreak + 1) : 0);
				if (severeStreak >= 2)
				{
					severeStreak = 0;
					RequestResync(text);
				}
			}
			else
			{
				severeStreak = 0;
			}
		}
		if (list != null)
		{
			foreach (int item in list)
			{
				hostSnaps.Remove(item);
				localSnaps.Remove(item);
			}
		}
		if (localSnaps.Count > 20)
		{
			localSnaps.Clear();
		}
		if (hostSnaps.Count > 20)
		{
			hostSnaps.Clear();
		}
	}

	private static string Describe(Snap s)
	{
		string text = "hash " + s.hash.ToString("x8");
		for (int i = 0; i < 2; i++)
		{
			NetProtocol.PlayerState playerState = s.players[i];
			text = text + "  p" + (i + 1) + (playerState.present ? (" " + playerState.x.ToString("0.0") + "," + playerState.y.ToString("0.0") + " vida " + playerState.hp) : " ausente");
		}
		return text + "  chefe " + s.bossDamage.ToString("0.0");
	}

	private static string Correct(Snap host, Snap local)
	{
		string result = null;
		for (int i = 0; i < 2; i++)
		{
			NetProtocol.PlayerState playerState = host.players[i];
			NetProtocol.PlayerState playerState2 = local.players[i];
			if (playerState.present != playerState2.present)
			{
				result = "player " + (i + 1) + (playerState.present ? " sumiu so no amigo" : " so existe no amigo");
			}
			else
			{
				if (!playerState.present)
				{
					continue;
				}
				if (SceneManager.GetActiveScene().name.StartsWith("scene_level_") && playerState.hp > 0 != playerState2.hp > 0)
				{
					result = "player " + (i + 1) + ((playerState.hp > 0) ? " morto so no amigo" : " morto so no host");
				}
				try
				{
					AbstractPlayerController player = PlayerManager.GetPlayer((PlayerId)i);
					Transform transform = ((player != null) ? player.transform : MapPlayer(i));
					if (transform != null)
					{
						Vector3 position = transform.position;
						position.x += playerState.x - playerState2.x;
						position.y += playerState.y - playerState2.y;
						transform.position = position;
					}
					if (!(player == null) && player.stats != null && playerState.hp != playerState2.hp && playerState.hp > 0 && player.stats.Health > 0)
					{
						player.stats.SetHealth(playerState.hp);
					}
				}
				catch (Exception ex)
				{
					NetLog.Info("[Netcode] falha ao corrigir player " + i + ": " + ex.Message);
				}
			}
		}
		// Bosses can't be patched in place: if their HP diverged the two games are in
		// different fights, so the caller escalates to a resync (restart for both).
		if (result == null && host.bossDamage >= 0f && local.bossDamage >= 0f && Mathf.Abs(host.bossDamage - local.bossDamage) > 0.5f && SceneManager.GetActiveScene().name.StartsWith("scene_level_"))
		{
			result = "chefe com vida diferente (host " + host.bossDamage.ToString("0") + " x amigo " + local.bossDamage.ToString("0") + ")";
		}
		return result;
	}
}
