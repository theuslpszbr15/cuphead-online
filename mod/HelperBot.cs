#if HELPERBOT
using System.Collections.Generic;
using Rewired;
using UnityEngine;
using UnityEngine.SceneManagement;

// Offline helper: F8 toggles a bot that plays Player 2 in local co-op. It only produces
// button/axis input through NetInput, exactly like a second controller would.
public class HelperBot : MonoBehaviour
{
	private const int BtnJump = 2;

	private const int BtnShoot = 3;

	private const int BtnSuper = 4;

	private const int BtnLock = 6;

	private const int BtnDash = 7;

	private const int BtnAccept = 13;

	// Tuning (game units: the screen is 1280 wide, the ground is around y = -245 in most fights).
	private const float KeepDistance = 380f;

	private const float TooClose = 260f;

	private const float WallMargin = 110f;

	private const float ThreatRadius = 260f;

	private const float DodgeLead = 0.35f;

	public static bool Active { get; private set; }

	private static uint curButtons;

	private static uint prevButtons;

	private static float axisX;

	private static float axisY;

	private static float prevAxisX;

	private static float prevAxisY;

	private static int lastFrame = -1;

	private static int jumpHold;

	private static int dashCooldown;

	private static int superCooldown;

	private static int joinTimer;

	private static int targetRefresh;

	private static DamageReceiver target;

	private static readonly Dictionary<int, Vector2> lastPos = new Dictionary<int, Vector2>();

	private static readonly Dictionary<int, Vector2> seenThisFrame = new Dictionary<int, Vector2>();

	private static string status = string.Empty;

	private static string lastThreat = "-";

	private static bool trace;

	public static bool Drives(Player p)
	{
		if (!Active || p == null || p.id != 1 || NetInput.LockstepActive || NetcodeSync.Active)
		{
			return false;
		}
		Tick();
		return true;
	}

	public static bool GetButton(int action)
	{
		return Bit(curButtons, action);
	}

	public static bool GetButtonDown(int action)
	{
		if (Bit(curButtons, action))
		{
			return !Bit(prevButtons, action);
		}
		return false;
	}

	public static bool GetButtonUp(int action)
	{
		if (!Bit(curButtons, action))
		{
			return Bit(prevButtons, action);
		}
		return false;
	}

	public static float GetAxis(int action)
	{
		return action switch
		{
			0 => axisX, 
			1 => axisY, 
			_ => 0f, 
		};
	}

	public static bool GetNegativeButtonDown(int action)
	{
		float now = GetAxis(action);
		float prev = (action == 0) ? prevAxisX : ((action == 1) ? prevAxisY : 0f);
		if (now < -0.5f)
		{
			return prev >= -0.5f;
		}
		return false;
	}

	public static bool GetAnyButtonDown()
	{
		return (curButtons & ~prevButtons) != 0;
	}

	public static bool GetAnyButton()
	{
		return curButtons != 0;
	}

	private static bool Bit(uint mask, int action)
	{
		if (action >= 0 && action < 32)
		{
			return (mask & (uint)(1 << action)) != 0;
		}
		return false;
	}

	private void Start()
	{
		foreach (string arg in System.Environment.GetCommandLineArgs())
		{
			if (arg == "-helperbot")
			{
				Active = true;
			}
			if (arg == "-helperbot-trace")
			{
				trace = true;
			}
		}
	}

	private static int lastHp = -1;

	private static int logTimer;

	private static void LogLife(AbstractPlayerController me)
	{
		int hp = (me.stats != null) ? me.stats.Health : (-1);
		float boss = (Level.Current.timeline != null) ? Level.Current.timeline.damage : (-1f);
		if (hp != lastHp)
		{
			NetLog.Info("[Bot] P2 vida " + lastHp + " -> " + hp + " | dano no chefe " + boss.ToString("0") + " | t " + Level.Current.LevelTime.ToString("0.0") + " s | fazendo: " + status + " | ameaca mais perto: " + lastThreat);
			lastHp = hp;
		}
		if (++logTimer % 300 == 0)
		{
			NetLog.Info("[Bot] " + status + " | vida " + hp + " | dano no chefe " + boss.ToString("0") + " | t " + Level.Current.LevelTime.ToString("0.0") + " s");
		}
	}

	private void Update()
	{
		if (Input.GetKeyDown(KeyCode.F8) && !NetcodeSync.Active)
		{
			Active = !Active;
			NetLog.Info("[Bot] P2 " + (Active ? "ligado" : "desligado"));
			if (!Active)
			{
				curButtons = (prevButtons = 0u);
				axisX = (axisY = (prevAxisX = (prevAxisY = 0f)));
			}
		}
		if (Active && NetcodeSync.Active)
		{
			Active = false;
		}
	}

	private void OnGUI()
	{
		if (Active)
		{
			GUI.depth = -1000;
			string text = "BOT P2 ligado (F8 desliga)" + (string.IsNullOrEmpty(status) ? string.Empty : ("  |  " + status));
			GUI.color = new Color(0f, 0f, 0f, 0.55f);
			GUI.DrawTexture(new Rect(8f, 8f, 9f * (float)text.Length + 16f, 22f), Texture2D.whiteTexture);
			GUI.color = new Color(0.55f, 0.8f, 1f);
			GUI.Label(new Rect(16f, 9f, 1000f, 22f), text);
			GUI.color = Color.white;
		}
	}

	private static void Tick()
	{
		if (Time.frameCount == lastFrame)
		{
			return;
		}
		lastFrame = Time.frameCount;
		prevButtons = curButtons;
		prevAxisX = axisX;
		prevAxisY = axisY;
		curButtons = 0u;
		axisX = 0f;
		axisY = 0f;
		if (jumpHold > 0)
		{
			jumpHold--;
		}
		if (dashCooldown > 0)
		{
			dashCooldown--;
		}
		if (superCooldown > 0)
		{
			superCooldown--;
		}
		string scene = SceneManager.GetActiveScene().name;
		if (scene.StartsWith("scene_map_") && !PlayerManager.Multiplayer && !SceneLoader.CurrentlyLoading)
		{
			// Join on the map, before the fight, so P2 starts with full health.
			status = "entrando como P2";
			joinTimer++;
			if (joinTimer % 20 < 2)
			{
				Press(BtnAccept);
			}
			return;
		}
		if (!scene.StartsWith("scene_level_") || Level.Current == null)
		{
			status = PlayerManager.Multiplayer ? "pronto (P2 entrou)" : "esperando uma fase";
			return;
		}
		AbstractPlayerController me = PlayerManager.GetPlayer(PlayerId.PlayerTwo);
		if (me == null)
		{
			// Ask to join: the level shows a join prompt, a press confirms it.
			status = "entrando como P2";
			joinTimer++;
			if (joinTimer % 20 < 2)
			{
				Press(BtnAccept);
			}
			return;
		}
		if (me.IsDead || !Level.Current.Started || Level.Current.Ending)
		{
			if (me.IsDead)
			{
				LogLife(me);
			}
			status = me.IsDead ? "P2 caiu" : string.Empty;
			return;
		}
		LogLife(me);
		RefreshTarget(me);
		if (trace && target != null && Time.frameCount % 6 == 0)
		{
			Collider2D tc = target.GetComponent<Collider2D>();
			Bounds b = (tc != null) ? tc.bounds : new Bounds(target.transform.position, Vector3.zero);
			Vector2 mc = me.center;
			NetLog.Info("[BotT] t " + Level.Current.LevelTime.ToString("0.00") + " eu " + mc.x.ToString("0") + "," + mc.y.ToString("0") + " chefe " + target.gameObject.name + " cx " + b.center.x.ToString("0") + " min " + b.min.x.ToString("0") + "," + b.min.y.ToString("0") + " max " + b.max.x.ToString("0") + "," + b.max.y.ToString("0") + " | " + status);
		}
		if (me is PlanePlayerController)
		{
			PlanePlay(me);
		}
		else if (me is LevelPlayerController)
		{
			GroundPlay((LevelPlayerController)me);
		}
		if (jumpHold > 0)
		{
			Press(BtnJump);
		}
	}

	private static void Press(int action)
	{
		curButtons |= (uint)(1 << action);
	}

	private static void RefreshTarget(AbstractPlayerController me)
	{
		if (target != null && target.enabled && target.gameObject.activeInHierarchy && targetRefresh-- > 0)
		{
			return;
		}
		targetRefresh = 10;
		target = null;
		float best = float.MaxValue;
		Vector2 c = me.center;
		DamageReceiver[] all = Object.FindObjectsOfType<DamageReceiver>();
		float minX = Level.Current.Left - 40f;
		float maxX = Level.Current.Right + 40f;
		foreach (DamageReceiver dr in all)
		{
			if (dr.type == DamageReceiver.Type.Enemy && dr.enabled && dr.gameObject.activeInHierarchy && dr.transform.position.x > minX && dr.transform.position.x < maxX)
			{
				float d = ((Vector2)dr.transform.position - c).sqrMagnitude;
				if (d < best)
				{
					best = d;
					target = dr;
				}
			}
		}
	}

	private static Vector2 TargetPos()
	{
		Collider2D col = target.GetComponent<Collider2D>();
		return (col != null) ? ((Vector2)col.bounds.center) : ((Vector2)target.transform.position);
	}

	// Closest thing about to hurt us: enemy projectiles and the boss body, predicted a bit ahead.
	private static bool FindThreat(Vector2 c, out Vector2 rel, out Vector2 vel)
	{
		rel = Vector2.zero;
		vel = Vector2.zero;
		bool found = false;
		float best = float.MaxValue;
		seenThisFrame.Clear();
		Collider2D[] hits = Physics2D.OverlapCircleAll(c, ThreatRadius);
		foreach (Collider2D h in hits)
		{
			if (h == null || !h.enabled)
			{
				continue;
			}
			bool danger = false;
			AbstractProjectile proj = h.GetComponentInParent<AbstractProjectile>();
			if (proj != null)
			{
				danger = proj.GetDamagesType(DamageReceiver.Type.Player);
			}
			else
			{
				DamageReceiver dr = h.GetComponentInParent<DamageReceiver>();
				danger = dr != null && dr.type == DamageReceiver.Type.Enemy && dr != target;
			}
			if (!danger)
			{
				continue;
			}
			Vector2 p = h.bounds.ClosestPoint(c);
			int id = h.GetInstanceID();
			seenThisFrame[id] = p;
			Vector2 v = lastPos.TryGetValue(id, out var old) ? ((p - old) * 60f) : Vector2.zero;
			Vector2 future = p + v * DodgeLead - c;
			float d = Mathf.Min((p - c).sqrMagnitude, future.sqrMagnitude);
			if (d < best)
			{
				best = d;
				rel = p - c;
				vel = v;
				found = true;
				lastThreat = h.gameObject.name.Replace("(Clone)", string.Empty) + " rel " + rel.x.ToString("0") + "," + rel.y.ToString("0") + " vel " + v.x.ToString("0") + "," + v.y.ToString("0");
			}
		}
		lastPos.Clear();
		foreach (KeyValuePair<int, Vector2> kv in seenThisFrame)
		{
			lastPos[kv.Key] = kv.Value;
		}
		return found;
	}

	private static void GroundPlay(LevelPlayerController me)
	{
		Vector2 c = me.center;
		bool grounded = me.motor != null && me.motor.Grounded;
		float left = Level.Current.Left;
		float right = Level.Current.Right;
		Press(BtnShoot);
		Vector2 tp = (target != null) ? TargetPos() : new Vector2(c.x + 300f, c.y);
		float dx = tp.x - c.x;
		float dir = Mathf.Sign((dx == 0f) ? 1f : dx);
		if (target != null && DodgeBossJump(me, c, grounded, left, right))
		{
			return;
		}
		Vector2 rel;
		Vector2 vel;
		bool threat = FindThreat(c, out rel, out vel);
		// Something is coming at body height: jump it, or dash away if already in the air.
		bool incoming = threat && Mathf.Abs(rel.x + vel.x * DodgeLead) < 150f && rel.y + vel.y * DodgeLead > -60f && rel.y + vel.y * DodgeLead < 140f && (Mathf.Abs(rel.x) < 70f || Mathf.Sign(vel.x) == -Mathf.Sign(rel.x) || Mathf.Abs(vel.y) > 200f);
		float away = (c.x - left < WallMargin) ? 1f : ((right - c.x < WallMargin) ? (-1f) : (-dir));
		bool cornered = (away == dir) && Mathf.Abs(dx) < TooClose;
		if (incoming)
		{
			status = "desviando";
			if (grounded && jumpHold == 0)
			{
				jumpHold = 14;
			}
			else if (!grounded && dashCooldown == 0 && Mathf.Abs(rel.x) < 110f)
			{
				Press(BtnDash);
				dashCooldown = 40;
			}
			axisX = (Mathf.Abs(rel.x) < 40f) ? away : (-Mathf.Sign(rel.x));
			return;
		}
		if (cornered)
		{
			// Wall behind us and the boss in front: hop and dash across.
			status = "saindo do canto";
			axisX = dir;
			if (grounded && jumpHold == 0)
			{
				jumpHold = 18;
			}
			else if (!grounded && dashCooldown == 0)
			{
				Press(BtnDash);
				dashCooldown = 50;
			}
			return;
		}
		if (Mathf.Abs(dx) < TooClose)
		{
			status = "afastando";
			axisX = away;
			return;
		}
		if (Mathf.Abs(dx) > KeepDistance + 220f)
		{
			status = "chegando perto";
			axisX = dir;
			return;
		}
		// Safe spot: stand, lock and aim at the boss (diagonal when it is high up).
		status = "atirando";
		Press(BtnLock);
		axisX = dir;
		axisY = (tp.y - c.y > 160f) ? 1f : 0f;
		if (superCooldown == 0 && me.stats != null && me.stats.SuperMeter >= 10f)
		{
			Press(BtnSuper);
			superCooldown = 90;
		}
	}

	private static DamageReceiver tracked;

	private static Vector2 prevBossCenter;

	private static float prevBossBottom;

	private static float prevBossVy;

	private static float bossGround = float.MaxValue;

	private static float gravity = 5500f;

	private static int stillFrames;

	private static readonly Dictionary<int, int> restVotes = new Dictionary<int, int>();

	private const float RunSpeed = 490f;

	// Hopping bosses (Goopy, the frogs' roll, etc.): predict where the body lands and get out of
	// that zone; pass under while it is high in the air when a wall blocks the short way.
	private static bool DodgeBossJump(AbstractPlayerController me, Vector2 c, bool grounded, float left, float right)
	{
		Collider2D col = target.GetComponent<Collider2D>();
		if (col == null)
		{
			return false;
		}
		Bounds b = col.bounds;
		if (tracked != target)
		{
			tracked = target;
			prevBossCenter = b.center;
			prevBossBottom = b.min.y;
			prevBossVy = 0f;
			bossGround = float.MaxValue;
			stillFrames = 0;
			restVotes.Clear();
			return false;
		}
		float vx = (b.center.x - prevBossCenter.x) * 60f;
		float vy = (b.min.y - prevBossBottom) * 60f;
		// Rest height = the highest bottom seen while standing still; squash poses dip below it.
		if (Mathf.Abs(vy) < 1f && Mathf.Abs(vx) < 1f)
		{
			stillFrames++;
			if (stillFrames >= 8)
			{
				// Rest height = the bottom seen most often while standing still.
				int key = Mathf.RoundToInt(b.min.y / 10f);
				restVotes[key] = (restVotes.TryGetValue(key, out var n) ? n : 0) + 1;
				int bestKey = key;
				int bestN = 0;
				foreach (KeyValuePair<int, int> kv in restVotes)
				{
					if (kv.Value > bestN)
					{
						bestN = kv.Value;
						bestKey = kv.Key;
					}
				}
				bossGround = (float)bestKey * 10f;
			}
		}
		else
		{
			stillFrames = 0;
		}
		if (bossGround == float.MaxValue)
		{
			bossGround = b.min.y;
		}
		bool airborne = b.min.y > bossGround + 25f;
		bool windup = b.min.y < bossGround - 40f;
		if (airborne && vy < prevBossVy - 1f)
		{
			gravity = Mathf.Clamp(Mathf.Lerp(gravity, (prevBossVy - vy) * 60f, 0.2f), 1500f, 12000f);
		}
		prevBossCenter = b.center;
		prevBossBottom = b.min.y;
		prevBossVy = vy;
		float half = b.extents.x + 70f;
		if (airborne)
		{
			float h = b.min.y - bossGround;
			float tLand = (vy + Mathf.Sqrt(Mathf.Max(0f, vy * vy + 2f * gravity * h))) / gravity;
			float landX = Mathf.Clamp(b.center.x + vx * tLand, left, right);
			float off = c.x - landX;
			if (Mathf.Abs(off) < half + 20f)
			{
				float s = Mathf.Sign((off == 0f) ? (-vx) : off);
				float dist = half - Mathf.Abs(off) + 40f;
				float edge = c.x + s * dist;
				if (edge < left + 40f || edge > right - 40f)
				{
					// The short way is a wall: cross under the boss to the far side.
					s = 0f - s;
					dist = half + Mathf.Abs(off) + 40f;
				}
				status = "fugindo do pulo";
				axisX = s;
				if (dist / RunSpeed > tLand * 0.85f && dashCooldown == 0 && grounded)
				{
					Press(BtnDash);
					dashCooldown = 35;
				}
				return true;
			}
			return false;
		}
		float gap = Mathf.Abs(b.center.x - c.x) - b.extents.x;
		if (windup && gap < 520f)
		{
			// Squash before a lunge: open distance now; with a wall behind, stand by to pass under.
			float awayW = Mathf.Sign(c.x - b.center.x);
			float room = (awayW > 0f) ? (right - c.x) : (c.x - left);
			status = "chefe vai pular";
			axisX = (room > 200f) ? awayW : 0f;
			return true;
		}
		if (gap < 300f)
		{
			float away = Mathf.Sign(c.x - b.center.x);
			if ((away < 0f && c.x - left < WallMargin) || (away > 0f && right - c.x < WallMargin))
			{
				// Cornered next to a grounded boss: jump over it and dash across.
				status = "pulando por cima";
				axisX = 0f - away;
				if (grounded && jumpHold == 0)
				{
					jumpHold = 20;
				}
				else if (!grounded && dashCooldown == 0 && c.y - b.max.y > -40f)
				{
					Press(BtnDash);
					dashCooldown = 50;
				}
				return true;
			}
			status = "afastando";
			axisX = away;
			return true;
		}
		return false;
	}

	private static void PlanePlay(AbstractPlayerController me)
	{
		Vector2 c = me.center;
		Press(BtnShoot);
		Vector2 tp = (target != null) ? TargetPos() : c;
		float wantX = Level.Current.Left + 260f;
		float wantY = tp.y;
		Vector2 rel;
		Vector2 vel;
		if (FindThreat(c, out rel, out vel) && (rel + vel * DodgeLead).magnitude < 120f)
		{
			status = "desviando";
			wantY = c.y - Mathf.Sign((rel.y == 0f) ? 1f : rel.y) * 160f;
			wantX = c.x - Mathf.Sign((rel.x == 0f) ? 1f : rel.x) * 120f;
		}
		else
		{
			status = "atirando";
		}
		axisX = Mathf.Clamp((wantX - c.x) / 60f, -1f, 1f);
		axisY = Mathf.Clamp((wantY - c.y) / 40f, -1f, 1f);
	}
}

#endif
