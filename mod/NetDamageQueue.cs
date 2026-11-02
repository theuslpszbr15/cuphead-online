using System;
using System.Collections.Generic;
using UnityEngine;

// The order Unity runs collision callbacks (and Updates) in can differ between two PCs, and
// several bosses change phase in the middle of a frame (e.g. the Tall frog only takes damage
// after the Short one pushes the fight into the next state). During lockstep every hit of the
// frame is queued and applied in an order that only depends on game state.
public static class NetDamageQueue
{
	private struct Entry
	{
		public DamageReceiver receiver;

		public DamageDealer.DamageInfo info;

		public string key;
	}

	private static readonly List<Entry> pending = new List<Entry>();

	private static bool flushing;


	public static int Flushed { get; private set; }

	// Test harness only (-netcode-test-reverse): feeds the batch in reverse to prove the result does not depend on arrival order.
	public static bool TestReverse;

	public static void TakeDamage(DamageReceiver receiver, DamageDealer.DamageInfo info)
	{
		if (!NetcodeSync.InLockstep || flushing)
		{
			receiver.TakeDamage(info);
			return;
		}
		Vector3 p = receiver.transform.position;
		Entry e = default(Entry);
		e.receiver = receiver;
		e.info = info;
		e.key = receiver.gameObject.name + "|" + p.x.ToString("0.00") + "|" + p.y.ToString("0.00") + "|" + ((int)info.damageSource).ToString() + "|" + info.damage.ToString("0.000") + "|" + ((int)info.direction).ToString() + "|" + info.origin.x.ToString("0.00") + "|" + info.origin.y.ToString("0.00") + "|" + info.stoneTime.ToString("0.000");
		pending.Add(e);
	}

	public static void Flush()
	{
		if (pending.Count == 0)
		{
			return;
		}
		Entry[] batch = pending.ToArray();
		pending.Clear();
		if (TestReverse)
		{
			Array.Reverse(batch);
		}
		// Identical keys mean identical hits on identical receivers, so their relative order cannot matter.
		Array.Sort(batch, (Entry a, Entry b) => string.CompareOrdinal(a.key, b.key));
		flushing = true;
		try
		{
			for (int i = 0; i < batch.Length; i++)
			{
				if (batch[i].receiver != null)
				{
					try
					{
						batch[i].receiver.TakeDamage(batch[i].info);
					}
					catch (Exception ex)
					{
						Debug.LogException(ex);
					}
				}
			}
			Flushed += batch.Length;
		}
		finally
		{
			flushing = false;
		}
	}

	public static void Clear()
	{
		pending.Clear();
	}
}
