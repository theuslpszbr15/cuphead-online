using System;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

public static class NetRandom
{
	public static bool Trace;

	private static System.Random effects = new System.Random(1);

	public static float value
	{
		get
		{
			float num = UnityEngine.Random.value;
			Log("value", num);
			return num;
		}
	}

	public static Vector2 insideUnitCircle
	{
		get
		{
			Vector2 result = UnityEngine.Random.insideUnitCircle;
			Log("circle", result.x);
			return result;
		}
	}

	public static Vector3 insideUnitSphere
	{
		get
		{
			Vector3 result = UnityEngine.Random.insideUnitSphere;
			Log("sphere", result.x);
			return result;
		}
	}

	public static void SeedEffects(int seed)
	{
		effects = new System.Random(seed);
	}

	public static float EffectRange(float min, float max)
	{
		return min + (float)effects.NextDouble() * (max - min);
	}

	public static float Range(float min, float max)
	{
		float num = UnityEngine.Random.Range(min, max);
		Log("range", num);
		return num;
	}

	public static int Range(int min, int max)
	{
		int num = UnityEngine.Random.Range(min, max);
		Log("int", num);
		return num;
	}

	private static void Log(string kind, float v)
	{
		if (Trace)
		{
			NetLog.Info("[Rnd] " + NetcodeSync.Frame + " " + Caller(3) + " < " + Caller(2) + " " + kind + " " + v.ToString("0.###"));
		}
	}

	private static string Caller(int depth)
	{
		MethodBase method = new StackFrame(depth).GetMethod();
		if (method == null)
		{
			return "?";
		}
		return ((method.DeclaringType != null) ? method.DeclaringType.FullName : "?") + "." + method.Name;
	}
}
