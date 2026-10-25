using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace KerbalCraft
{
	/// <summary>
	/// Starts the bundled Minecraft next to KSP. The release carries GameData/KerbalCraft/Minecraft/
	/// KerbalCraft-Minecraft.zip (a portable Prism Launcher with a ready "KerbalCraft" instance); the
	/// first start unpacks it to %LOCALAPPDATA%\KerbalCraft (PowerShell's Expand-Archive, off the
	/// main thread), after which Prism asks for a Microsoft login once and downloads Minecraft itself.
	/// </summary>
	public static class Launcher
	{
		static DateTime lastAttempt = DateTime.MinValue;
		static volatile bool working;

		static string InstallDir { get { return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "KerbalCraft"); } }
		public static string DefaultPath { get { return Path.Combine(InstallDir, "Prism", "prismlauncher.exe"); } }
		static string BundleZip { get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KerbalCraft/Minecraft/KerbalCraft-Minecraft.zip"); } }
		static string Marker { get { return Path.Combine(InstallDir, "bundle.stamp"); } }

		public static void Start()
		{
			if (working || Link.MinecraftProcessRunning() || Link.McAlive()) return;
			if ((DateTime.UtcNow - lastAttempt).TotalSeconds < 30) return;
			lastAttempt = DateTime.UtcNow;
			bool custom = !string.IsNullOrEmpty(Settings.LauncherPath);
			string exe = custom ? Settings.LauncherPath : DefaultPath;

			if (!custom && File.Exists(BundleZip) && NeedsUnpack())
			{
				working = true;
				string zip = BundleZip, dir = InstallDir, stamp = StampOf(zip), marker = Marker;
				ThreadPool.QueueUserWorkItem(_ =>
				{
					try
					{
						Directory.CreateDirectory(dir);
						var psi = new ProcessStartInfo("powershell.exe",
							"-NoProfile -ExecutionPolicy Bypass -Command \"Expand-Archive -LiteralPath '" + zip.Replace("'", "''") + "' -DestinationPath '" + dir.Replace("'", "''") + "' -Force\"")
						{ UseShellExecute = false, CreateNoWindow = true };
						using (Process p = Process.Start(psi))
						{
							p.WaitForExit(120000);
							if (p.ExitCode == 0) File.WriteAllText(marker, stamp);
							else UnityEngine.Debug.LogError("[KerbalCraft] unpacking the Minecraft bundle failed (exit " + p.ExitCode + ")");
						}
					}
					catch (Exception e) { UnityEngine.Debug.LogError("[KerbalCraft] unpacking the Minecraft bundle failed: " + e.Message); }
					finally { working = false; lastAttempt = DateTime.MinValue; }
				});
				Log.Info("unpacking the Minecraft bundle to {0}; it starts afterwards", dir);
				return;
			}

			if (!File.Exists(exe))
			{
				Log.Warn("Minecraft launcher not found at {0}; start Minecraft yourself (see README) or set launcherPath in KerbalCraft.cfg", exe);
				return;
			}
			try
			{
				var psi = new ProcessStartInfo(exe, Settings.LauncherArguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) };
				Process.Start(psi);
				Log.Info("started Minecraft: {0} {1}", exe, Settings.LauncherArguments);
			}
			catch (Exception e) { Log.Error("couldn't start Minecraft: {0}", e.Message); }
		}

		static string StampOf(string zip) { return new FileInfo(zip).LastWriteTimeUtc.Ticks.ToString(); }

		static bool NeedsUnpack()
		{
			try
			{
				if (!File.Exists(DefaultPath) || !File.Exists(Marker)) return true;
				return File.ReadAllText(Marker) != StampOf(BundleZip);
			}
			catch { return true; }
		}
	}
}
