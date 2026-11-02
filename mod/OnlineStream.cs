using System;
using System.Collections;
using Rewired;
using UnityEngine;

public class OnlineStream : MonoBehaviour
{
	public const byte MsgFrame = 64;

	public const byte MsgInput = 65;

	private const int StreamWidth = 640;

	private const int StreamHeight = 360;

	private const float StreamFps = 20f;

	private const int JpegQuality = 45;

	private NetSession session;

	private readonly object sync = new object();

	private RenderTexture captureRT;

	private Texture2D captureTex;

	private Texture2D screenTex;

	private float nextCaptureTime;

	private bool capturing;

	private byte[] receivedFrame;

	private Texture2D remoteTex;

	private float lastFrameTime;

	private int framesShown;

	private float fpsTimer;

	private int fpsShown;

	private readonly float[] localAxes = new float[4];

	private bool muted;

	private float savedVolume = 1f;

	public bool Active
	{
		get
		{
			if (session == null || session.Mode != NetSession.GameMode.Stream)
			{
				return false;
			}
			if (session.State != NetSession.SessionState.InGame)
			{
				if (session.State == NetSession.SessionState.Reconnecting)
				{
					return remoteTex != null;
				}
				return false;
			}
			return true;
		}
	}

	private void Start()
	{
		session = NetSession.Instance;
		session.RegisterHandler(64, delegate(int from, byte[] payload)
		{
			lock (sync)
			{
				receivedFrame = payload;
			}
		}, onNetworkThread: true);
		session.RegisterHandler(65, delegate(int from, byte[] payload)
		{
			if (payload.Length >= 20)
			{
				uint buttons = BitConverter.ToUInt32(payload, 0);
				float[] array = new float[4];
				for (int i = 0; i < 4; i++)
				{
					array[i] = BitConverter.ToSingle(payload, 4 + i * 4);
				}
				NetInput.ReceiveRemote(buttons, array);
			}
		}, onNetworkThread: true);
	}

	private void Update()
	{
		if (session == null)
		{
			return;
		}
		bool active = Active;
		if (session.IsHost)
		{
			bool num = active && session.IsConnected(1);
			NetInput.SetRemoteActiveIfChanged(num);
			bool flag = OnlineLobby.Instance != null && OnlineLobby.Instance.WindowVisible;
			if (num && !flag && !capturing && Time.unscaledTime >= nextCaptureTime)
			{
				nextCaptureTime = Time.unscaledTime + 0.05f;
				StartCoroutine(CaptureFrame());
			}
		}
		else
		{
			NetInput.SetRemoteActiveIfChanged(active: false);
		}
		if (session.IsGuest && active)
		{
			ShowRemoteFrames();
			if (session.State == NetSession.SessionState.InGame && session.PeerConnected && ReInput.isReady && ReInput.players.GetPlayer(0) != null)
			{
				NetInput.ReadLocal(ReInput.players.GetPlayer(0), out var buttons, localAxes);
				byte[] array = new byte[20];
				Array.Copy(BitConverter.GetBytes(buttons), 0, array, 0, 4);
				for (int i = 0; i < 4; i++)
				{
					Array.Copy(BitConverter.GetBytes(localAxes[i]), 0, array, 4 + i * 4, 4);
				}
				session.SendLatest(65, array);
			}
		}
		else if (!session.InSession && remoteTex != null)
		{
			UnityEngine.Object.Destroy(remoteTex);
			remoteTex = null;
		}
		bool flag2 = session.IsGuest && active;
		if (flag2 != muted)
		{
			muted = flag2;
			if (muted)
			{
				savedVolume = AudioListener.volume;
				AudioListener.volume = 0f;
			}
			else
			{
				AudioListener.volume = savedVolume;
			}
		}
	}

	private void ShowRemoteFrames()
	{
		byte[] array;
		lock (sync)
		{
			array = receivedFrame;
			receivedFrame = null;
		}
		if (array != null)
		{
			if (remoteTex == null)
			{
				remoteTex = new Texture2D(2, 2, TextureFormat.RGB24, mipmap: false);
			}
			remoteTex.LoadImage(array);
			lastFrameTime = Time.unscaledTime;
			framesShown++;
		}
		fpsTimer += Time.unscaledDeltaTime;
		if (fpsTimer >= 1f)
		{
			fpsShown = framesShown;
			framesShown = 0;
			fpsTimer = 0f;
		}
	}

	private IEnumerator CaptureFrame()
	{
		capturing = true;
		yield return new WaitForEndOfFrame();
		capturing = false;
		if (session.IsConnected(1))
		{
			if (captureRT == null)
			{
				captureRT = new RenderTexture(640, 360, 0, RenderTextureFormat.ARGB32);
				captureRT.Create();
				captureTex = new Texture2D(640, 360, TextureFormat.RGB24, mipmap: false);
			}
			if (screenTex == null || screenTex.width != Screen.width || screenTex.height != Screen.height)
			{
				screenTex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, mipmap: false);
			}
			screenTex.ReadPixels(new Rect(0f, 0f, Screen.width, Screen.height), 0, 0, recalculateMipMaps: false);
			screenTex.Apply(updateMipmaps: false);
			RenderTexture active = RenderTexture.active;
			Graphics.Blit(screenTex, captureRT);
			RenderTexture.active = captureRT;
			captureTex.ReadPixels(new Rect(0f, 0f, 640f, 360f), 0, 0, recalculateMipMaps: false);
			RenderTexture.active = active;
			session.SendLatest(64, captureTex.EncodeToJPG(45), 1);
		}
	}

	private void OnGUI()
	{
		if (!(session == null) && session.IsGuest && Active && !(remoteTex == null))
		{
			GUI.depth = -1000;
			GUI.color = Color.black;
			GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
			GUI.color = Color.white;
			GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), remoteTex, ScaleMode.ScaleToFit, alphaBlend: false);
			string text = "ONLINE: vendo a tela do host  |  " + fpsShown + " fps  |  " + session.PingMs + " ms" + ((Time.unscaledTime - lastFrameTime > 2f) ? "  |  SEM IMAGEM" : string.Empty) + "  |  F1 = lobby";
			GUI.Label(new Rect(10f, Screen.height - 30, Screen.width, 30f), text);
		}
	}
}
