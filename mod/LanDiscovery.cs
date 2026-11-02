using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public static class LanDiscovery
{
	public class Room
	{
		public string Address;

		public int Port;

		public string HostName;

		public int Players;

		public int MaxPlayers;

		public bool InGame;

		public float LastSeen;
	}

	public const int BeaconPort = 7778;

	private const string Magic = "CUPHEADLOBBY1";

	private static readonly string InstanceId = Guid.NewGuid().ToString("N").Substring(0, 8);

	private static readonly object sync = new object();

	private static readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>();

	private static UdpClient listener;

	private static Thread listenThread;

	private static UdpClient sender;

	private static List<IPEndPoint> targets;

	private static volatile float now;

	public static bool Listening => listener != null;

	public static void StartListening()
	{
		if (listener == null)
		{
			try
			{
				UdpClient udpClient = new UdpClient();
				udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, optionValue: true);
				udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 7778));
				listener = udpClient;
			}
			catch (Exception ex)
			{
				NetLog.Info("nao consegui escutar salas da rede local: " + ex.Message);
				return;
			}
			UdpClient mine = listener;
			listenThread = new Thread((ThreadStart)delegate
			{
				ListenLoop(mine);
			});
			listenThread.IsBackground = true;
			listenThread.Start();
		}
	}

	public static void StopListening()
	{
		UdpClient udpClient = listener;
		listener = null;
		udpClient?.Close();
		lock (sync)
		{
			rooms.Clear();
		}
	}

	public static List<Room> GetRooms()
	{
		now = Time.unscaledTime;
		List<Room> list = new List<Room>();
		lock (sync)
		{
			foreach (Room value in rooms.Values)
			{
				if (now - value.LastSeen < 3f)
				{
					list.Add(value);
				}
			}
		}
		list.Sort((Room a, Room b) => string.Compare(a.HostName, b.HostName, StringComparison.OrdinalIgnoreCase));
		return list;
	}

	private static void ListenLoop(UdpClient udp)
	{
		while (listener == udp)
		{
			IPEndPoint remoteEP = null;
			byte[] bytes;
			try
			{
				bytes = udp.Receive(ref remoteEP);
			}
			catch (Exception)
			{
				if (listener != udp)
				{
					break;
				}
				continue;
			}
			string[] array = Encoding.UTF8.GetString(bytes).Split('|');
			if (array.Length < 7 || array[0] != "CUPHEADLOBBY1" || array[1] == InstanceId || !int.TryParse(array[3], out var result) || !int.TryParse(array[4], out var result2) || !int.TryParse(array[5], out var result3))
			{
				continue;
			}
			string key = array[1] + ":" + result;
			lock (sync)
			{
				if (!rooms.TryGetValue(key, out var value))
				{
					value = new Room();
					rooms[key] = value;
				}
				if (value.Address == null || Rank(remoteEP.Address) < Rank(IPAddress.Parse(value.Address)))
				{
					value.Address = remoteEP.Address.ToString();
				}
				value.Port = result;
				value.HostName = array[2];
				value.Players = result2;
				value.MaxPlayers = result3;
				value.InGame = array[6] == "1";
				value.LastSeen = now;
			}
		}
	}

	private static int Rank(IPAddress address)
	{
		if (IPAddress.IsLoopback(address))
		{
			return 2;
		}
		byte[] addressBytes = address.GetAddressBytes();
		return (addressBytes[0] != 10 && (addressBytes[0] != 192 || addressBytes[1] != 168) && (addressBytes[0] != 172 || addressBytes[1] < 16 || addressBytes[1] >= 32)) ? 1 : 0;
	}

	public static void SendBeacon(string hostName, int port, int players, int maxPlayers, bool inGame)
	{
		now = Time.unscaledTime;
		try
		{
			if (sender == null)
			{
				sender = new UdpClient();
				sender.EnableBroadcast = true;
				targets = BuildTargets();
			}
			string text = hostName.Replace("|", "/");
			byte[] bytes = Encoding.UTF8.GetBytes("CUPHEADLOBBY1|" + InstanceId + "|" + text + "|" + port + "|" + players + "|" + maxPlayers + "|" + (inGame ? "1" : "0"));
			foreach (IPEndPoint target in targets)
			{
				try
				{
					sender.Send(bytes, bytes.Length, target);
				}
				catch (Exception)
				{
				}
			}
		}
		catch (Exception ex2)
		{
			NetLog.Info("aviso de sala falhou: " + ex2.Message);
		}
	}

	public static void StopBeacon()
	{
		if (sender != null)
		{
			sender.Close();
			sender = null;
		}
	}

	private static List<IPEndPoint> BuildTargets()
	{
		List<IPEndPoint> list = new List<IPEndPoint>();
		list.Add(new IPEndPoint(IPAddress.Broadcast, 7778));
		list.Add(new IPEndPoint(IPAddress.Loopback, 7778));
		foreach (string item in LocalAddresses())
		{
			byte[] addressBytes = IPAddress.Parse(item).GetAddressBytes();
			addressBytes[3] = byte.MaxValue;
			list.Add(new IPEndPoint(new IPAddress(addressBytes), 7778));
		}
		return list;
	}

	public static List<string> LocalAddresses()
	{
		List<string> list = new List<string>();
		try
		{
			IPAddress[] hostAddresses = Dns.GetHostAddresses(Dns.GetHostName());
			foreach (IPAddress iPAddress in hostAddresses)
			{
				if (iPAddress.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(iPAddress))
				{
					list.Add(iPAddress.ToString());
				}
			}
		}
		catch (Exception)
		{
		}
		return list;
	}
}
