using System;
using System.IO;
using System.Text;

public static class NetProtocol
{
	public struct InputFrame
	{
		public uint buttons;

		public sbyte ax0;

		public sbyte ax1;

		public sbyte ax2;

		public sbyte ax3;

		public float Axis(int i)
		{
			return i switch
			{
				0 => (float)ax0 / 127f, 
				1 => (float)ax1 / 127f, 
				2 => (float)ax2 / 127f, 
				_ => (float)ax3 / 127f, 
			};
		}

		public static sbyte Quantize(float v)
		{
			return (sbyte)Math.Round(Math.Max(-1f, Math.Min(1f, v)) * 127f);
		}

		public static InputFrame From(uint buttons, float[] axes)
		{
			InputFrame result = default(InputFrame);
			result.buttons = buttons;
			result.ax0 = Quantize(axes[0]);
			result.ax1 = Quantize(axes[1]);
			result.ax2 = Quantize(axes[2]);
			result.ax3 = Quantize(axes[3]);
			return result;
		}

		public void Write(BinaryWriter w)
		{
			w.Write(buttons);
			w.Write(ax0);
			w.Write(ax1);
			w.Write(ax2);
			w.Write(ax3);
		}

		public static InputFrame Read(BinaryReader r)
		{
			InputFrame result = default(InputFrame);
			result.buttons = r.ReadUInt32();
			result.ax0 = r.ReadSByte();
			result.ax1 = r.ReadSByte();
			result.ax2 = r.ReadSByte();
			result.ax3 = r.ReadSByte();
			return result;
		}
	}

	public struct PlayerState
	{
		public byte id;

		public bool present;

		public float x;

		public float y;

		public int hp;

		public float super;

		public void Write(BinaryWriter w)
		{
			w.Write(id);
			w.Write(present);
			w.Write(x);
			w.Write(y);
			w.Write(hp);
			w.Write(super);
		}

		public static PlayerState Read(BinaryReader r)
		{
			PlayerState result = default(PlayerState);
			result.id = r.ReadByte();
			result.present = r.ReadBoolean();
			result.x = r.ReadSingle();
			result.y = r.ReadSingle();
			result.hp = r.ReadInt32();
			result.super = r.ReadSingle();
			return result;
		}
	}

	public const ushort Version = 6;

	public const string ModName = "Cuphead Online";

	public const string Author = "Eosplash";

	public const string Credits = ModName + " v6 por " + Author;

	public const byte FirstType = 32;

	public const byte LastType = 63;

	public const byte Hello = 32;

	public const byte Config = 33;

	public const byte Input = 34;

	public const byte InputAck = 35;

	public const byte SceneLoad = 36;

	public const byte SceneReady = 37;

	public const byte SceneGo = 38;

	public const byte Checksum = 39;

	public const byte Snapshot = 40;

	public const byte Pause = 41;

	public const byte Ping = 42;

	public const byte Pong = 43;

	public const byte Bye = 44;

	public const byte Resync = 45;

	public const int DefaultInputDelay = 3;

	public static int BuildHash => typeof(NetProtocol).Assembly.ManifestModule.ModuleVersionId.GetHashCode();

	private static int dataHash;

	private static bool dataHashDone;

	// Fingerprint of the game data (boss/level properties live in the assets, not in the DLL).
	// Names + sizes of everything under Cuphead_Data except Managed (the mod itself) and logs.
	public static int DataHash
	{
		get
		{
			if (!dataHashDone)
			{
				dataHashDone = true;
				try
				{
					string root = UnityEngine.Application.dataPath;
					string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
					string[] entries = new string[files.Length];
					int n = 0;
					foreach (string file in files)
					{
						string rel = file.Substring(root.Length).Replace('\\', '/').ToLowerInvariant();
						if (rel.StartsWith("/managed/") || rel.StartsWith("/plugins/") || rel.EndsWith(".log"))
						{
							continue;
						}
						long length;
						try
						{
							length = new FileInfo(file).Length;
						}
						catch (Exception)
						{
							continue;
						}
						entries[n++] = rel + ":" + length;
					}
					Array.Sort(entries, 0, n, StringComparer.Ordinal);
					uint h = 2166136261u;
					for (int i = 0; i < n; i++)
					{
						foreach (char c in entries[i])
						{
							h ^= c;
							h *= 16777619;
						}
					}
					dataHash = (int)h;
				}
				catch (Exception ex)
				{
					dataHash = 0;
					NetLog.Info("[Netcode] falha ao calcular a impressao dos dados do jogo: " + ex.GetType().Name + ": " + ex.Message);
				}
			}
			return dataHash;
		}
	}

	public static string GameVersion => UnityEngine.Application.version;

	public static bool IsNetcodeType(byte type)
	{
		if (type >= 32)
		{
			return type <= 63;
		}
		return false;
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
