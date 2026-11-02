using System;
using Rewired;
using UnityEngine;

public static class NetInput
{
	public const int ButtonCount = 32;

	public static readonly int[] SentAxes = new int[4] { 0, 1, 22, 23 };

	public static readonly int[] SentButtons = new int[17]
	{
		2, 3, 4, 5, 6, 7, 8, 11, 12, 13,
		14, 15, 16, 18, 19, 20, 26
	};

	private static readonly object sync = new object();

	private static uint latestButtons;

	private static readonly float[] latestAxes = new float[4];

	private static uint curButtons;

	private static uint prevButtons;

	private static readonly float[] curAxes = new float[4];

	private static readonly float[] prevAxes = new float[4];

	private static int lastFrame = -1;

	private static readonly NetProtocol.InputFrame[] lsCur = new NetProtocol.InputFrame[2];

	private static readonly NetProtocol.InputFrame[] lsPrev = new NetProtocol.InputFrame[2];

	public static bool RemoteActive { get; private set; }

	public static uint LatestButtons
	{
		get
		{
			lock (sync)
			{
				return latestButtons;
			}
		}
	}

	public static bool LockstepActive { get; private set; }

	public static void SetRemoteActive(bool active)
	{
		lock (sync)
		{
			RemoteActive = active;
			latestButtons = 0u;
			Array.Clear(latestAxes, 0, latestAxes.Length);
		}
	}

	public static void SetRemoteActiveIfChanged(bool active)
	{
		if (RemoteActive != active)
		{
			SetRemoteActive(active);
		}
	}

	public static void ReceiveRemote(uint buttons, float[] axes)
	{
		lock (sync)
		{
			latestButtons = buttons;
			for (int i = 0; i < latestAxes.Length; i++)
			{
				latestAxes[i] = axes[i];
			}
		}
	}

	public static void SetLockstepActive(bool active)
	{
		LockstepActive = active;
		lsCur[0] = (lsCur[1] = (lsPrev[0] = (lsPrev[1] = default(NetProtocol.InputFrame))));
	}

	public static void SetLockstepFrame(NetProtocol.InputFrame p1, NetProtocol.InputFrame p2)
	{
		lsPrev[0] = lsCur[0];
		lsPrev[1] = lsCur[1];
		lsCur[0] = p1;
		lsCur[1] = p2;
	}

	private static bool IsLockstep(Player p)
	{
		if (!LockstepActive || p == null || p.id < 0 || p.id > 1)
		{
			return false;
		}
		NetcodeSync.Advance();
		return LockstepActive;
	}

	private static float LsAxis(NetProtocol.InputFrame f, int action)
	{
		int num = AxisSlot(action);
		if (num < 0)
		{
			return 0f;
		}
		return f.Axis(num);
	}

	private static bool IsRemote(Player p)
	{
		if (!RemoteActive || p == null || p.id != 1)
		{
			return false;
		}
		int frameCount = Time.frameCount;
		if (frameCount != lastFrame)
		{
			lastFrame = frameCount;
			lock (sync)
			{
				prevButtons = curButtons;
				curButtons = latestButtons;
				for (int i = 0; i < curAxes.Length; i++)
				{
					prevAxes[i] = curAxes[i];
					curAxes[i] = latestAxes[i];
				}
			}
		}
		return true;
	}

	private static bool Bit(uint mask, int action)
	{
		if (action >= 0 && action < 32)
		{
			return (mask & (uint)(1 << action)) != 0;
		}
		return false;
	}

	private static int AxisSlot(int action)
	{
		for (int i = 0; i < SentAxes.Length; i++)
		{
			if (SentAxes[i] == action)
			{
				return i;
			}
		}
		return -1;
	}

	public static bool GetButton(Player p, int action)
	{
		if (IsLockstep(p))
		{
			return Bit(lsCur[p.id].buttons, action);
		}
#if HELPERBOT
		if (HelperBot.Drives(p))
		{
			return HelperBot.GetButton(action);
		}
#endif
		if (!p.GetButton(action))
		{
			if (IsRemote(p))
			{
				return Bit(curButtons, action);
			}
			return false;
		}
		return true;
	}

	public static bool GetButtonDown(Player p, int action)
	{
		if (IsLockstep(p))
		{
			if (Bit(lsCur[p.id].buttons, action))
			{
				return !Bit(lsPrev[p.id].buttons, action);
			}
			return false;
		}
#if HELPERBOT
		if (HelperBot.Drives(p))
		{
			return HelperBot.GetButtonDown(action);
		}
#endif
		if (!p.GetButtonDown(action))
		{
			if (IsRemote(p) && Bit(curButtons, action))
			{
				return !Bit(prevButtons, action);
			}
			return false;
		}
		return true;
	}

	public static bool GetButtonUp(Player p, int action)
	{
		if (IsLockstep(p))
		{
			if (!Bit(lsCur[p.id].buttons, action))
			{
				return Bit(lsPrev[p.id].buttons, action);
			}
			return false;
		}
#if HELPERBOT
		if (HelperBot.Drives(p))
		{
			return HelperBot.GetButtonUp(action);
		}
#endif
		if (!p.GetButtonUp(action))
		{
			if (IsRemote(p) && !Bit(curButtons, action))
			{
				return Bit(prevButtons, action);
			}
			return false;
		}
		return true;
	}

	public static bool GetNegativeButtonDown(Player p, int action)
	{
		if (IsLockstep(p))
		{
			if (LsAxis(lsCur[p.id], action) < -0.5f)
			{
				return LsAxis(lsPrev[p.id], action) >= -0.5f;
			}
			return false;
		}
#if HELPERBOT
		if (HelperBot.Drives(p))
		{
			return HelperBot.GetNegativeButtonDown(action);
		}
#endif
		if (p.GetNegativeButtonDown(action))
		{
			return true;
		}
		int num = AxisSlot(action);
		if (num >= 0 && IsRemote(p) && curAxes[num] < -0.5f)
		{
			return prevAxes[num] >= -0.5f;
		}
		return false;
	}

	public static float GetAxis(Player p, int action)
	{
		if (IsLockstep(p))
		{
			return LsAxis(lsCur[p.id], action);
		}
#if HELPERBOT
		if (HelperBot.Drives(p))
		{
			return HelperBot.GetAxis(action);
		}
#endif
		float axis = p.GetAxis(action);
		int num = AxisSlot(action);
		if (num >= 0 && IsRemote(p) && Mathf.Abs(curAxes[num]) > Mathf.Abs(axis))
		{
			return curAxes[num];
		}
		return axis;
	}

	public static float GetAxisRaw(Player p, int action)
	{
		if (IsLockstep(p))
		{
			return LsAxis(lsCur[p.id], action);
		}
#if HELPERBOT
		if (HelperBot.Drives(p))
		{
			return HelperBot.GetAxis(action);
		}
#endif
		float axisRaw = p.GetAxisRaw(action);
		int num = AxisSlot(action);
		if (num >= 0 && IsRemote(p) && Mathf.Abs(curAxes[num]) > Mathf.Abs(axisRaw))
		{
			return curAxes[num];
		}
		return axisRaw;
	}

	public static bool GetAnyButtonDown(Player p)
	{
		if (IsLockstep(p))
		{
			return (lsCur[p.id].buttons & ~lsPrev[p.id].buttons) != 0;
		}
#if HELPERBOT
		if (HelperBot.Drives(p))
		{
			return HelperBot.GetAnyButtonDown();
		}
#endif
		if (!p.GetAnyButtonDown())
		{
			if (IsRemote(p))
			{
				return (curButtons & ~prevButtons) != 0;
			}
			return false;
		}
		return true;
	}

	public static bool GetAnyButton(Player p)
	{
		if (IsLockstep(p))
		{
			return lsCur[p.id].buttons != 0;
		}
#if HELPERBOT
		if (HelperBot.Drives(p))
		{
			return HelperBot.GetAnyButton();
		}
#endif
		if (!p.GetAnyButton())
		{
			if (IsRemote(p))
			{
				return curButtons != 0;
			}
			return false;
		}
		return true;
	}

	public static bool GetButton(Player p, string action)
	{
		return p.GetButton(action);
	}

	public static bool GetButtonDown(Player p, string action)
	{
		return p.GetButtonDown(action);
	}

	public static bool GetButtonUp(Player p, string action)
	{
		return p.GetButtonUp(action);
	}

	public static float GetAxis(Player p, string action)
	{
		return p.GetAxis(action);
	}

	public static bool GetButton(CupheadInput.AnyPlayerInput i, CupheadButton b)
	{
		return i.GetButton(b);
	}

	public static bool GetButtonDown(CupheadInput.AnyPlayerInput i, CupheadButton b)
	{
		return i.GetButtonDown(b);
	}

	public static bool GetButtonUp(CupheadInput.AnyPlayerInput i, CupheadButton b)
	{
		return i.GetButtonUp(b);
	}

	public static bool GetAnyButtonDown(CupheadInput.AnyPlayerInput i)
	{
		return i.GetAnyButtonDown();
	}

	public static float GetAxis(PlayerInput i, PlayerInput.Axis a)
	{
		return i.GetAxis(a);
	}

	public static bool GetButton(PlayerInput i, CupheadButton b)
	{
		return i.GetButton(b);
	}

	public static void ReadLocal(Player p, out uint buttons, float[] axes)
	{
		buttons = 0u;
		int[] sentButtons = SentButtons;
		foreach (int num in sentButtons)
		{
			if (p.GetButton(num))
			{
				buttons |= (uint)(1 << num);
			}
		}
		for (int j = 0; j < SentAxes.Length; j++)
		{
			axes[j] = p.GetAxis(SentAxes[j]);
		}
	}
}
