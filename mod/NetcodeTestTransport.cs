using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class NetcodeTestTransport : INetcodeTransport
{
	public const int DefaultPort = 7778;

	private const int MaxPayload = 8388608;

	private readonly object writeLock = new object();

	private TcpClient client;

	private BinaryWriter writer;

	private volatile bool connected;

	private static int lagMs;

	private static int jitterMs;

	private readonly System.Random lagRandom = new System.Random(1234);

	private readonly Queue<KeyValuePair<long, KeyValuePair<byte, byte[]>>> lagQueue = new Queue<KeyValuePair<long, KeyValuePair<byte, byte[]>>>();

	private long lastDue;

	public bool Connected => connected;

	public static bool Started { get; private set; }

	public static void StartFromCommandLine()
	{
		string[] commandLineArgs = Environment.GetCommandLineArgs();
		int result = 7778;
		string text = null;
		bool flag = false;
		for (int i = 0; i < commandLineArgs.Length; i++)
		{
			if (commandLineArgs[i] == "-netcode-host")
			{
				flag = true;
			}
			else if (commandLineArgs[i] == "-netcode-join" && i + 1 < commandLineArgs.Length)
			{
				text = commandLineArgs[i + 1];
			}
			else if (commandLineArgs[i] == "-netcode-port" && i + 1 < commandLineArgs.Length)
			{
				int.TryParse(commandLineArgs[i + 1], out result);
			}
			else if (commandLineArgs[i] == "-netcode-lag" && i + 1 < commandLineArgs.Length)
			{
				int.TryParse(commandLineArgs[i + 1], out lagMs);
			}
			else if (commandLineArgs[i] == "-netcode-jitter" && i + 1 < commandLineArgs.Length)
			{
				int.TryParse(commandLineArgs[i + 1], out jitterMs);
			}
		}
		Started = flag || text != null;
		if (Started)
		{
			Application.runInBackground = true;
		}
		if (flag)
		{
			new NetcodeTestTransport().Listen(result);
		}
		else if (text != null)
		{
			new NetcodeTestTransport().Connect(text, result);
		}
	}

	private void Listen(int port)
	{
		Thread thread = new Thread((ThreadStart)delegate
		{
			try
			{
				TcpListener tcpListener = new TcpListener(IPAddress.Any, port);
				tcpListener.Start();
				NetLog.Info("[NetcodeTest] esperando conexao na porta " + port);
				TcpClient tcp = tcpListener.AcceptTcpClient();
				tcpListener.Stop();
				Run(tcp, isHost: true);
			}
			catch (Exception ex)
			{
				NetLog.Info("[NetcodeTest] host falhou: " + ex.Message);
			}
		});
		thread.IsBackground = true;
		thread.Start();
	}

	private void Connect(string host, int port)
	{
		Thread thread = new Thread((ThreadStart)delegate
		{
			for (int i = 0; i < 60; i++)
			{
				try
				{
					TcpClient tcpClient = new TcpClient();
					tcpClient.Connect(host, port);
					Run(tcpClient, isHost: false);
					break;
				}
				catch (Exception ex)
				{
					NetLog.Info("[NetcodeTest] tentando conectar: " + ex.Message);
					Thread.Sleep(1000);
				}
			}
		});
		thread.IsBackground = true;
		thread.Start();
	}

	private void Run(TcpClient tcp, bool isHost)
	{
		tcp.NoDelay = true;
		client = tcp;
		NetworkStream stream = tcp.GetStream();
		writer = new BinaryWriter(stream);
		connected = true;
		NetLog.Info("[NetcodeTest] conectado (" + (isHost ? "host" : "amigo") + ")" + ((lagMs > 0 || jitterMs > 0) ? (" com atraso simulado " + lagMs + "+" + jitterMs + " ms") : string.Empty));
		if (lagMs > 0 || jitterMs > 0)
		{
			Thread pump = new Thread(LagPump);
			pump.IsBackground = true;
			pump.Start();
		}
		NetcodeSync.Begin(this, isHost);
		try
		{
			BinaryReader binaryReader = new BinaryReader(stream);
			while (connected)
			{
				byte type = binaryReader.ReadByte();
				int num = binaryReader.ReadInt32();
				if (num < 0 || num > 8388608)
				{
					throw new IOException("tamanho invalido");
				}
				byte[] payload = binaryReader.ReadBytes(num);
				if (NetProtocol.IsNetcodeType(type))
				{
					NetcodeSync.OnPacket(type, payload);
				}
			}
		}
		catch (Exception ex)
		{
			NetLog.Info("[NetcodeTest] conexao encerrada: " + ex.Message);
		}
		connected = false;
		try
		{
			tcp.Close();
		}
		catch (Exception)
		{
		}
		NetcodeSync.End("conexao caiu");
	}

	public void Send(byte type, byte[] payload)
	{
		if (lagMs > 0 || jitterMs > 0)
		{
			long now = Stopwatch.GetTimestamp();
			long due;
			lock (lagQueue)
			{
				int extra = lagMs + ((jitterMs > 0) ? lagRandom.Next(jitterMs + 1) : 0);
				// TCP keeps order: a packet never overtakes the previous one.
				due = Math.Max(lastDue, now + extra * Stopwatch.Frequency / 1000);
				lastDue = due;
				lagQueue.Enqueue(new KeyValuePair<long, KeyValuePair<byte, byte[]>>(due, new KeyValuePair<byte, byte[]>(type, payload)));
				Monitor.Pulse(lagQueue);
			}
			return;
		}
		SendNow(type, payload);
	}

	private void LagPump()
	{
		while (connected)
		{
			KeyValuePair<long, KeyValuePair<byte, byte[]>> item;
			lock (lagQueue)
			{
				while (lagQueue.Count == 0 && connected)
				{
					Monitor.Wait(lagQueue, 50);
				}
				if (!connected)
				{
					return;
				}
				item = lagQueue.Peek();
				long wait = (item.Key - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
				if (wait > 0)
				{
					Monitor.Wait(lagQueue, (int)Math.Min(wait, 50L));
					continue;
				}
				lagQueue.Dequeue();
			}
			SendNow(item.Value.Key, item.Value.Value);
		}
	}

	private void SendNow(byte type, byte[] payload)
	{
		lock (writeLock)
		{
			if (!connected)
			{
				return;
			}
			try
			{
				writer.Write(type);
				writer.Write(payload.Length);
				writer.Write(payload);
				writer.Flush();
			}
			catch (Exception)
			{
				connected = false;
			}
		}
	}
}
