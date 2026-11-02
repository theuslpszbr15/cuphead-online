using UnityEngine;

public class NetWaitForSeconds : CustomYieldInstruction
{
	private readonly float seconds;

	private float elapsed;

	private bool waiting;

	public override bool keepWaiting
	{
		get
		{
			if (!waiting)
			{
				waiting = true;
				elapsed = 0f;
			}
			elapsed += Time.deltaTime;
			if (elapsed >= seconds)
			{
				waiting = false;
				return false;
			}
			return true;
		}
	}

	public NetWaitForSeconds(float seconds)
	{
		this.seconds = seconds;
	}
}
