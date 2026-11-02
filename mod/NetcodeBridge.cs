using System;
using UnityEngine;

public class NetcodeBridge : MonoBehaviour
{
	private class SessionTransport : INetcodeTransport
	{
		public bool Connected
		{
			get
			{
				NetSession instance = NetSession.Instance;
				if (instance != null)
				{
					return instance.PeerConnected;
				}
				return false;
			}
		}

		public void Send(byte type, byte[] payload)
		{
			NetSession instance = NetSession.Instance;
			if (instance != null)
			{
				instance.Send(type, payload);
			}
		}
	}

	private NetSession session;

	private readonly SessionTransport transport = new SessionTransport();

	private bool running;

	private void Start()
	{
		session = NetSession.Instance;
		for (int i = 32; i <= 63; i++)
		{
			byte type = (byte)i;
			session.RegisterHandler(type, delegate(int from, byte[] payload)
			{
				if (running)
				{
					NetcodeSync.OnPacket(type, payload);
				}
			}, onNetworkThread: true);
		}
		session.GameStarted += delegate
		{
			Begin();
		};
		session.PlayerReconnected += delegate
		{
			if (session.State == NetSession.SessionState.InGame)
			{
				Begin();
			}
		};
		session.PlayerDisconnected += delegate
		{
			End();
		};
		session.PlayerLeft += delegate
		{
			End();
		};
		session.SessionEnded += delegate
		{
			End();
		};
	}

	private void Begin()
	{
		if (running || session.Mode != 0)
		{
			return;
		}
		running = true;
		NetLog.Info("netcode sincronizado iniciado (host=" + session.IsHost + ")");
		try
		{
			NetcodeSync.Begin(transport, session.IsHost);
		}
		catch (Exception ex)
		{
			NetLog.Info("ERRO " + ex);
		}
	}

	private void End()
	{
		if (!running)
		{
			return;
		}
		running = false;
		try
		{
			NetcodeSync.End();
		}
		catch (Exception ex)
		{
			NetLog.Info("ERRO " + ex);
		}
	}
}
