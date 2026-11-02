using System;
using System.Net;
using System.Text;

public static class RoomCode
{
	private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

	private const ulong Mask = 99216275897133uL;

	public const int Length = 10;

	public static string Encode(IPAddress ip, int port)
	{
		byte[] addressBytes = ip.GetAddressBytes();
		if (addressBytes.Length != 4)
		{
			throw new ArgumentException("so IPv4");
		}
		ulong num = ((ulong)addressBytes[0] << 40) | ((ulong)addressBytes[1] << 32) | ((ulong)addressBytes[2] << 24) | ((ulong)addressBytes[3] << 16) | (ushort)port;
		num ^= 0x5A3C96E17B2DuL;
		ulong num2 = (num << 2) | Checksum(num);
		char[] array = new char[10];
		for (int num3 = 9; num3 >= 0; num3--)
		{
			array[num3] = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"[(int)(num2 & 0x1F)];
			num2 >>= 5;
		}
		string text = new string(array);
		return text.Substring(0, 5) + "-" + text.Substring(5);
	}

	public static bool TryDecode(string text, out IPAddress ip, out int port)
	{
		ip = null;
		port = 0;
		if (text == null)
		{
			return false;
		}
		StringBuilder stringBuilder = new StringBuilder();
		string text2 = text.ToUpperInvariant();
		for (int i = 0; i < text2.Length; i++)
		{
			char c = text2[i];
			if (c != '-' && c != ' ' && c != '\t')
			{
				switch (c)
				{
				case 'O':
					c = '0';
					break;
				case 'I':
				case 'L':
					c = '1';
					break;
				}
				stringBuilder.Append(c);
			}
		}
		if (stringBuilder.Length != 10)
		{
			return false;
		}
		ulong num = 0uL;
		for (int j = 0; j < 10; j++)
		{
			int num2 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".IndexOf(stringBuilder[j]);
			if (num2 < 0)
			{
				return false;
			}
			num = (num << 5) | (uint)num2;
		}
		ulong num3 = num >> 2;
		if ((num & 3) != Checksum(num3))
		{
			return false;
		}
		num3 ^= 0x5A3C96E17B2DuL;
		port = (int)(num3 & 0xFFFF);
		ip = new IPAddress(new byte[4]
		{
			(byte)(num3 >> 40),
			(byte)(num3 >> 32),
			(byte)(num3 >> 24),
			(byte)(num3 >> 16)
		});
		return port > 0;
	}

	public static bool TryParseAddress(string text, int defaultPort, out string host, out int port)
	{
		host = null;
		port = defaultPort;
		if (text == null)
		{
			return false;
		}
		text = text.Trim();
		if (text.Length == 0)
		{
			return false;
		}
		if (TryDecode(text, out var ip, out var port2))
		{
			host = ip.ToString();
			port = port2;
			return true;
		}
		if (text.IndexOf('.') < 0 && !text.Equals("localhost", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		int num = text.LastIndexOf(':');
		if (num > 0)
		{
			if (!int.TryParse(text.Substring(num + 1), out port) || port <= 0 || port > 65535)
			{
				return false;
			}
			text = text.Substring(0, num);
		}
		host = text;
		return true;
	}

	private static ulong Checksum(ulong value)
	{
		ulong num = 0uL;
		while (value != 0L)
		{
			num += value & 0xFF;
			value >>= 8;
		}
		return num & 3;
	}
}
