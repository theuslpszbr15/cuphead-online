using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Xml;

public static class NatHelper
{
	private static readonly string[] StunServers = new string[3] { "stun.l.google.com:19302", "stun1.l.google.com:19302", "stun.cloudflare.com:3478" };

	private static readonly object sync = new object();

	private static Thread thread;

	private static string controlUrl;

	private static string serviceType;

	private static int mappedPort;

	public static volatile bool Busy;

	public static volatile bool PortOpened;

	public static volatile string PublicIp;

	public static volatile string Status = string.Empty;

	public static void Begin(int port)
	{
		Close();
		Busy = true;
		PortOpened = false;
		PublicIp = null;
		Status = "Abrindo a porta no roteador...";
		thread = new Thread((ThreadStart)delegate
		{
			Run(port);
		});
		thread.IsBackground = true;
		thread.Start();
	}

	public static void Close()
	{
		string url;
		string service;
		int port;
		lock (sync)
		{
			url = controlUrl;
			service = serviceType;
			port = mappedPort;
			mappedPort = 0;
		}
		PortOpened = false;
		PublicIp = null;
		Status = string.Empty;
		if (url == null || port == 0)
		{
			return;
		}
		Thread obj = new Thread((ThreadStart)delegate
		{
			try
			{
				Soap(url, service, "DeletePortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>TCP</NewProtocol>");
			}
			catch (Exception)
			{
			}
		});
		obj.IsBackground = true;
		obj.Start();
	}

	private static void Run(int port)
	{
		string text = null;
		try
		{
			text = TryUpnp(port);
		}
		catch (Exception ex)
		{
			NetLog.Info("UPnP falhou: " + ex.Message);
		}
		string text2 = null;
		try
		{
			text2 = TryStun();
		}
		catch (Exception ex2)
		{
			NetLog.Info("STUN falhou: " + ex2.Message);
		}
		string text3 = text2 ?? text;
		if (text3 != null && IsPrivate(text3))
		{
			text3 = null;
		}
		PublicIp = text3;
		if (text3 == null)
		{
			Status = "Nao achei o IP publico. Na internet use Radmin VPN/ZeroTier e o codigo de rede local.";
		}
		else if (PortOpened && text != null && text2 != null && text != text2)
		{
			Status = "Seu provedor usa CGNAT (IP compartilhado): o codigo da internet nao deve funcionar. Use Radmin VPN/ZeroTier.";
		}
		else if (PortOpened)
		{
			Status = "Porta " + port + " aberta no roteador. O codigo da internet deve funcionar.";
		}
		else
		{
			Status = "O roteador nao abriu a porta sozinho (UPnP desligado?). Abra a porta TCP " + port + " ou use Radmin VPN/ZeroTier.";
		}
		Busy = false;
	}

	private static string TryUpnp(int port)
	{
		foreach (string item in DiscoverGateways())
		{
			if (!FindWanService(item, out var url, out var service))
			{
				continue;
			}
			Uri uri = new Uri(url);
			string text = LocalAddressTowards(uri.Host, uri.Port);
			string text2 = "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>TCP</NewProtocol><NewInternalPort>" + port + "</NewInternalPort><NewInternalClient>" + text + "</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>Cuphead Online</NewPortMappingDescription><NewLeaseDuration>";
			bool flag = false;
			try
			{
				Soap(url, service, "AddPortMapping", text2 + "7200</NewLeaseDuration>");
				flag = true;
			}
			catch (Exception)
			{
				try
				{
					Soap(url, service, "AddPortMapping", text2 + "0</NewLeaseDuration>");
					flag = true;
				}
				catch (Exception ex)
				{
					NetLog.Info("AddPortMapping falhou: " + ex.Message);
				}
			}
			if (flag)
			{
				lock (sync)
				{
					controlUrl = url;
					serviceType = service;
					mappedPort = port;
				}
				PortOpened = true;
			}
			try
			{
				string text3 = Between(Soap(url, service, "GetExternalIPAddress", string.Empty), "<NewExternalIPAddress>", "</NewExternalIPAddress>");
				if (!string.IsNullOrEmpty(text3))
				{
					return text3.Trim();
				}
			}
			catch (Exception)
			{
			}
			if (flag)
			{
				return null;
			}
		}
		return null;
	}

	private static List<string> DiscoverGateways()
	{
		List<string> list = new List<string>();
		using UdpClient udpClient = new UdpClient(0);
		udpClient.Client.ReceiveTimeout = 500;
		IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
		string[] array = new string[3] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANPPPConnection:1" };
		foreach (string text in array)
		{
			byte[] bytes = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nST: " + text + "\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n\r\n");
			udpClient.Send(bytes, bytes.Length, endPoint);
		}
		DateTime dateTime = DateTime.UtcNow.AddSeconds(3.0);
		while (DateTime.UtcNow < dateTime)
		{
			byte[] bytes2;
			try
			{
				IPEndPoint remoteEP = null;
				bytes2 = udpClient.Receive(ref remoteEP);
			}
			catch (SocketException)
			{
				continue;
			}
			array = Encoding.ASCII.GetString(bytes2).Split('\n');
			foreach (string text2 in array)
			{
				if (text2.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
				{
					string item = text2.Substring(9).Trim();
					if (!list.Contains(item))
					{
						list.Add(item);
					}
				}
			}
		}
		return list;
	}

	private static bool FindWanService(string location, out string url, out string service)
	{
		url = null;
		service = null;
		string xml;
		try
		{
			HttpWebRequest obj = (HttpWebRequest)WebRequest.Create(location);
			obj.Timeout = 3000;
			obj.ReadWriteTimeout = 3000;
			using WebResponse webResponse = obj.GetResponse();
			using StreamReader streamReader = new StreamReader(webResponse.GetResponseStream());
			xml = streamReader.ReadToEnd();
		}
		catch (Exception)
		{
			return false;
		}
		XmlDocument xmlDocument = new XmlDocument();
		xmlDocument.LoadXml(xml);
		Uri baseUri = new Uri(location);
		XmlNodeList elementsByTagName = xmlDocument.GetElementsByTagName("URLBase");
		if (elementsByTagName.Count > 0 && !string.IsNullOrEmpty(elementsByTagName[0].InnerText.Trim()))
		{
			baseUri = new Uri(elementsByTagName[0].InnerText.Trim());
		}
		foreach (XmlNode item in xmlDocument.GetElementsByTagName("service"))
		{
			string text = null;
			string text2 = null;
			foreach (XmlNode childNode in item.ChildNodes)
			{
				if (childNode.LocalName == "serviceType")
				{
					text = childNode.InnerText.Trim();
				}
				else if (childNode.LocalName == "controlURL")
				{
					text2 = childNode.InnerText.Trim();
				}
			}
			if (text != null && text2 != null && (text.IndexOf("WANIPConnection") >= 0 || text.IndexOf("WANPPPConnection") >= 0))
			{
				url = new Uri(baseUri, text2).ToString();
				service = text;
				return true;
			}
		}
		return false;
	}

	private static string Soap(string url, string service, string action, string args)
	{
		string s = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:" + action + " xmlns:u=\"" + service + "\">" + args + "</u:" + action + "></s:Body></s:Envelope>";
		byte[] bytes = Encoding.UTF8.GetBytes(s);
		HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create(url);
		httpWebRequest.Method = "POST";
		httpWebRequest.Timeout = 4000;
		httpWebRequest.ReadWriteTimeout = 4000;
		httpWebRequest.ContentType = "text/xml; charset=\"utf-8\"";
		httpWebRequest.Headers.Add("SOAPACTION", "\"" + service + "#" + action + "\"");
		httpWebRequest.ServicePoint.Expect100Continue = false;
		httpWebRequest.ContentLength = bytes.Length;
		using (Stream stream = httpWebRequest.GetRequestStream())
		{
			stream.Write(bytes, 0, bytes.Length);
		}
		using WebResponse webResponse = httpWebRequest.GetResponse();
		using StreamReader streamReader = new StreamReader(webResponse.GetResponseStream());
		return streamReader.ReadToEnd();
	}

	private static string LocalAddressTowards(string host, int port)
	{
		using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
		socket.Connect(host, port);
		return ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
	}

	private static string TryStun()
	{
		string[] stunServers = StunServers;
		foreach (string text in stunServers)
		{
			try
			{
				string[] array = text.Split(':');
				IPAddress iPAddress = null;
				IPAddress[] hostAddresses = Dns.GetHostAddresses(array[0]);
				foreach (IPAddress iPAddress2 in hostAddresses)
				{
					if (iPAddress2.AddressFamily == AddressFamily.InterNetwork)
					{
						iPAddress = iPAddress2;
						break;
					}
				}
				if (iPAddress != null)
				{
					string text2 = StunQuery(new IPEndPoint(iPAddress, int.Parse(array[1])));
					if (text2 != null)
					{
						return text2;
					}
				}
			}
			catch (Exception)
			{
			}
		}
		return null;
	}

	private static string StunQuery(IPEndPoint server)
	{
		byte[] array = new byte[20]
		{
			0, 1, 0, 0, 33, 18, 164, 66, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0
		};
		byte[] array2 = new byte[12];
		new Random().NextBytes(array2);
		Array.Copy(array2, 0, array, 8, 12);
		using (UdpClient udpClient = new UdpClient(0))
		{
			udpClient.Client.ReceiveTimeout = 1500;
			for (int i = 0; i < 2; i++)
			{
				udpClient.Send(array, array.Length, server);
				byte[] array3;
				try
				{
					IPEndPoint remoteEP = null;
					array3 = udpClient.Receive(ref remoteEP);
				}
				catch (SocketException)
				{
					continue;
				}
				if (array3.Length < 20 || array3[0] != 1 || array3[1] != 1)
				{
					continue;
				}
				int num = 20;
				while (num + 4 <= array3.Length)
				{
					int num2 = (array3[num] << 8) | array3[num + 1];
					int num3 = (array3[num + 2] << 8) | array3[num + 3];
					int num4 = num + 4;
					if (num4 + num3 > array3.Length)
					{
						break;
					}
					if ((num2 == 32 || num2 == 1) && num3 >= 8 && array3[num4 + 1] == 1)
					{
						byte[] array4 = new byte[4];
						for (int j = 0; j < 4; j++)
						{
							array4[j] = array3[num4 + 4 + j];
							if (num2 == 32)
							{
								array4[j] ^= array[4 + j];
							}
						}
						return new IPAddress(array4).ToString();
					}
					num = num4 + ((num3 + 3) & -4);
				}
			}
		}
		return null;
	}

	public static bool IsPrivate(string ip)
	{
		IPAddress iPAddress;
		try
		{
			iPAddress = IPAddress.Parse(ip);
		}
		catch (Exception)
		{
			return true;
		}
		byte[] addressBytes = iPAddress.GetAddressBytes();
		if (addressBytes.Length != 4)
		{
			return true;
		}
		if (addressBytes[0] != 10 && addressBytes[0] != 127 && addressBytes[0] != 0 && (addressBytes[0] != 172 || addressBytes[1] < 16 || addressBytes[1] >= 32) && (addressBytes[0] != 192 || addressBytes[1] != 168) && (addressBytes[0] != 169 || addressBytes[1] != 254))
		{
			if (addressBytes[0] == 100 && addressBytes[1] >= 64)
			{
				return addressBytes[1] < 128;
			}
			return false;
		}
		return true;
	}

	private static string Between(string text, string start, string end)
	{
		int num = text.IndexOf(start);
		if (num < 0)
		{
			return null;
		}
		num += start.Length;
		int num2 = text.IndexOf(end, num);
		if (num2 >= 0)
		{
			return text.Substring(num, num2 - num);
		}
		return null;
	}
}
