using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class CupheadOnlineLauncher
{
	private static string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CupheadOnline");

	private static string gameDirFile = Path.Combine(configDir, "pasta-do-jogo.txt");

	private static string lastIpFile = Path.Combine(configDir, "ultimo-ip.txt");

	private static string gameDir;

	private static string exe;

	private static string LogsDir => Path.Combine(configDir, "logs");

	[STAThread]
	private static void Main()
	{
		try
		{
			SetProcessDPIAware();
		}
		catch
		{
		}
		Application.EnableVisualStyles();
		if (!FindGame())
		{
			return;
		}
		Form form = new Form
		{
			Text = "Cuphead Online v6 - por Eosplash",
			FormBorderStyle = FormBorderStyle.FixedDialog,
			MaximizeBox = false,
			StartPosition = FormStartPosition.CenterScreen,
			ClientSize = new Size(340, 280),
			Font = new Font("Segoe UI", 10f)
		};
		try
		{
			form.Icon = Icon.ExtractAssociatedIcon(exe);
		}
		catch
		{
		}
		Label label = new Label();
		label.Text = "Cuphead Online";
		label.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
		label.AutoSize = false;
		label.TextAlign = ContentAlignment.MiddleCenter;
		label.Bounds = new Rectangle(10, 8, 320, 36);
		Label label2 = label;
		Label credit = new Label
		{
			Text = "por Eosplash",
			Font = new Font("Segoe UI", 9f, FontStyle.Italic),
			AutoSize = false,
			TextAlign = ContentAlignment.MiddleCenter,
			Bounds = new Rectangle(10, 42, 320, 18)
		};
		Button button = new Button();
		button.Text = "Hospedar partida";
		button.Bounds = new Rectangle(20, 70, 300, 36);
		Button button2 = button;
		Label label3 = new Label();
		label3.Text = "Codigo da sala ou IP do host:";
		label3.Bounds = new Rectangle(20, 116, 300, 20);
		Label label4 = label3;
		TextBox ip = new TextBox
		{
			Bounds = new Rectangle(20, 138, 190, 26)
		};
		Button button3 = new Button();
		button3.Text = "Entrar";
		button3.Bounds = new Rectangle(220, 136, 100, 30);
		Button button4 = button3;
		Button button5 = new Button();
		button5.Text = "Jogar sem online";
		button5.Bounds = new Rectangle(20, 190, 300, 36);
		Button button6 = button5;
		LinkLabel linkLabel = new LinkLabel();
		linkLabel.Text = "Abrir pasta de logs (mande se der erro)";
		linkLabel.AutoSize = false;
		linkLabel.TextAlign = ContentAlignment.MiddleCenter;
		linkLabel.Bounds = new Rectangle(10, 244, 320, 22);
		LinkLabel linkLabel2 = linkLabel;
		linkLabel2.LinkClicked += delegate
		{
			OpenLogs();
		};
		try
		{
			if (File.Exists(lastIpFile))
			{
				ip.Text = File.ReadAllText(lastIpFile).Trim();
			}
		}
		catch
		{
		}
		button2.Click += delegate
		{
			Launch("-host");
			form.Close();
		};
		button6.Click += delegate
		{
			Launch("");
			form.Close();
		};
		button4.Click += delegate
		{
			string text = ip.Text.Trim();
			if (text.Length == 0)
			{
				MessageBox.Show("Digite o codigo da sala ou o IP do host.", "Cuphead Online", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				ip.Focus();
			}
			else
			{
				try
				{
					Directory.CreateDirectory(configDir);
					File.WriteAllText(lastIpFile, text);
				}
				catch
				{
				}
				Launch("-join " + text);
				form.Close();
			}
		};
		form.AcceptButton = button4;
		form.Controls.AddRange(new Control[8] { label2, credit, button2, label4, ip, button4, button6, linkLabel2 });
		Application.Run(form);
	}

	private static bool FindGame()
	{
		string text = null;
		try
		{
			if (File.Exists(gameDirFile))
			{
				text = File.ReadAllText(gameDirFile).Trim();
			}
		}
		catch
		{
		}
		string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
		string[] array = new string[3]
		{
			baseDirectory,
			Path.Combine(baseDirectory, "Cuphead"),
			text
		};
		foreach (string text2 in array)
		{
			if (!string.IsNullOrEmpty(text2) && File.Exists(Path.Combine(text2, "Cuphead.exe")))
			{
				SetGame(text2);
				return true;
			}
		}
		MessageBox.Show("Nao achei o Cuphead.exe do lado do launcher.\nNa proxima janela, escolha o Cuphead.exe do jogo com o mod.", "Cuphead Online", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		OpenFileDialog openFileDialog = new OpenFileDialog();
		openFileDialog.Title = "Escolha o Cuphead.exe";
		openFileDialog.Filter = "Cuphead.exe|Cuphead.exe|Programas (*.exe)|*.exe";
		openFileDialog.FileName = "Cuphead.exe";
		OpenFileDialog openFileDialog2 = openFileDialog;
		if (openFileDialog2.ShowDialog() != DialogResult.OK || !File.Exists(openFileDialog2.FileName))
		{
			return false;
		}
		SetGame(Path.GetDirectoryName(openFileDialog2.FileName));
		try
		{
			Directory.CreateDirectory(configDir);
			File.WriteAllText(gameDirFile, gameDir);
		}
		catch
		{
		}
		return true;
	}

	private static void OpenLogs()
	{
		try
		{
			Directory.CreateDirectory(LogsDir);
			Process.Start("explorer.exe", "\"" + LogsDir + "\"");
		}
		catch
		{
		}
	}

	private static void SetGame(string dir)
	{
		gameDir = dir;
		exe = Path.Combine(dir, "Cuphead.exe");
	}

	[DllImport("user32.dll")]
	private static extern bool SetProcessDPIAware();

	private static bool InstallMod()
	{
		string text = Path.Combine(gameDir, "Cuphead_Data\\Managed\\Assembly-CSharp.dll");
		byte[] array;
		using (Stream stream = typeof(CupheadOnlineLauncher).Assembly.GetManifestResourceStream("Assembly-CSharp.dll"))
		{
			if (stream == null)
			{
				return true;
			}
			array = new byte[stream.Length];
			for (int i = 0; i < array.Length; i += stream.Read(array, i, array.Length - i))
			{
			}
		}
		try
		{
			if (File.Exists(text) && SameBytes(File.ReadAllBytes(text), array))
			{
				return true;
			}
			if (Process.GetProcessesByName("Cuphead").Length > 0)
			{
				MessageBox.Show("Feche o Cuphead antes, pra eu instalar o mod online.", "Cuphead Online", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
				return false;
			}
			string text2 = text + ".original";
			if (File.Exists(text) && !File.Exists(text2))
			{
				File.Copy(text, text2);
			}
			File.WriteAllBytes(text, array);
			return true;
		}
		catch (Exception ex)
		{
			MessageBox.Show("Nao consegui instalar o mod em:\n" + text + "\n\n" + ex.Message, "Cuphead Online", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return false;
		}
	}

	private static bool SameBytes(byte[] a, byte[] b)
	{
		if (a.Length != b.Length)
		{
			return false;
		}
		for (int i = 0; i < a.Length; i++)
		{
			if (a[i] != b[i])
			{
				return false;
			}
		}
		return true;
	}

	private static void Launch(string args)
	{
		if (!InstallMod())
		{
			return;
		}
		Rectangle bounds = Screen.PrimaryScreen.Bounds;
		string text = $"-screen-fullscreen 1 -screen-width {bounds.Width} -screen-height {bounds.Height} ";
		try
		{
			Directory.CreateDirectory(LogsDir);
			string[] files = Directory.GetFiles(LogsDir, "unity-*.log");
			Array.Sort(files);
			for (int i = 0; i < files.Length - 9; i++)
			{
				File.Delete(files[i]);
			}
			text = text + "-logFile \"" + Path.Combine(LogsDir, "unity-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log") + "\" ";
		}
		catch
		{
		}
		ProcessStartInfo processStartInfo = new ProcessStartInfo(exe, text + args);
		processStartInfo.WorkingDirectory = gameDir;
		processStartInfo.UseShellExecute = false;
		Process.Start(processStartInfo);
	}
}
