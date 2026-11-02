using System;
using System.IO;
using UnityEngine;

public static class NetLog
{
	private const int KeepLogs = 10;

	private static readonly object sync = new object();

	private static string path;

	private static bool initialized;

	public static string LogPath
	{
		get
		{
			Init();
			return path;
		}
	}

	public static void Init()
	{
		if (initialized)
		{
			return;
		}
		initialized = true;
		string[] commandLineArgs = Environment.GetCommandLineArgs();
		for (int i = 0; i + 1 < commandLineArgs.Length; i++)
		{
			if (commandLineArgs[i].ToLowerInvariant() == "-onlinelog")
			{
				NetLog.path = commandLineArgs[i + 1];
			}
		}
		if (NetLog.path == null)
		{
			try
			{
				string path = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CupheadOnline"), "logs");
				Directory.CreateDirectory(path);
				string[] files = Directory.GetFiles(path, "online-*.log");
				Array.Sort(files);
				for (int j = 0; j < files.Length - 9; j++)
				{
					File.Delete(files[j]);
				}
				NetLog.path = Path.Combine(path, "online-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
			}
			catch (Exception)
			{
			}
		}
		try
		{
			Application.logMessageReceivedThreaded += OnUnityLog;
		}
		catch (Exception)
		{
		}
		Write("jogo aberto: " + string.Join(" ", commandLineArgs) + " | mod " + NetProtocol.BuildHash.ToString("x8") + " protocolo " + NetProtocol.Version + " | " + NetProtocol.Credits);
	}

	private static void OnUnityLog(string condition, string stackTrace, LogType type)
	{
		if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
		{
			Write("[" + type.ToString() + "] " + condition + Environment.NewLine + stackTrace);
		}
	}

	public static void Info(string message)
	{
		Init();
		Write(message);
	}

	private static void Write(string message)
	{
		if (path == null)
		{
			return;
		}
		lock (sync)
		{
			try
			{
				File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
			}
			catch (Exception)
			{
			}
		}
	}
}
