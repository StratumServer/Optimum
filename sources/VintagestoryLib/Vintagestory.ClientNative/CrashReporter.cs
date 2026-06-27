using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Optimum;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client;
using Vintagestory.Common;

namespace Vintagestory.ClientNative;

public class CrashReporter
{
	private string crashLogFileName = "";

	private bool launchCrashReporterGui;

	private static Logger logger;

	public Action OnCrash;

	public bool isCrashing;

	private static bool s_blnIsConsole = false;

	public static List<ModContainer> LoadedMods { get; set; } = new List<ModContainer>();

	public static void SetLogger(Logger logger)
	{
		CrashReporter.logger = logger;
	}

	public CrashReporter(EnumAppSide side)
	{
		crashLogFileName = ((side == EnumAppSide.Client) ? "client-crash.log" : "server-crash.log");
		launchCrashReporterGui = side == EnumAppSide.Client;
	}

	public static void EnableGlobalExceptionHandling(bool blnIsConsole)
	{
		s_blnIsConsole = blnIsConsole;
		AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
	}

	private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		if (s_blnIsConsole)
		{
			Console.ForegroundColor = ConsoleColor.Red;
			Console.Out.WriteLine("Unhandled Exception occurred");
		}
		Exception exCrash = e.ExceptionObject as Exception;
		new CrashReporter(Process.GetCurrentProcess().MainModule.FileName.ToLowerInvariant().Contains("server") ? EnumAppSide.Server : EnumAppSide.Client).Crash(exCrash);
	}

	public void Start(ThreadStart start)
	{
		if (!Debugger.IsAttached)
		{
			try
			{
				start();
				return;
			}
			catch (Exception exCrash)
			{
				Crash(exCrash);
				return;
			}
		}
		start();
	}

	public void Crash(Exception exCrash)
	{
		isCrashing = true;
		StringBuilder stringBuilder = new StringBuilder();
		try
		{
			GamePaths.EnsurePathExists(GamePaths.Logs);
			string text = Path.Combine(GamePaths.Logs, crashLogFileName);

			// Optimum crash header
			stringBuilder.AppendLine("================================================================");
			stringBuilder.AppendLine(OptimumInfo.DisplayTag + " (based on Vintage Story " + GameVersion.ShortGameVersion + ")");
			stringBuilder.AppendLine("This is a MODIFIED CLIENT. DO NOT report this crash to the Vintage Story team.");
			stringBuilder.AppendLine("Report issues at: " + OptimumInfo.Url + "/issues");
			stringBuilder.AppendLine("================================================================");
			stringBuilder.AppendLine();

			stringBuilder.AppendLine("Game Version: " + GameVersion.LongGameVersion);
			IEnumerable<ModContainer> source = LoadedMods.Where((ModContainer mod) => mod.Assembly != null && mod.Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright != "Copyright \u00a9 2016-2026 Anego Studios");
			StringBuilder stringBuilder2 = new StringBuilder();
			HashSet<ModContainer> hashSet = new HashSet<ModContainer>();
			HashSet<string> hashSet2 = new HashSet<string>();
			for (Exception ex = exCrash; ex != null; ex = ex.InnerException)
			{
				stringBuilder2.AppendLine(LoggerBase.CleanStackTrace(ex.ToString()));
				StackFrame[] frames = new StackTrace(ex, fNeedFileInfo: true).GetFrames();
				foreach (StackFrame stackFrame in frames)
				{
					MethodBase methodFromStackframe;
					try
					{
						methodFromStackframe = Harmony.GetMethodFromStackframe(stackFrame);
					}
					catch (Exception)
					{
						continue;
					}
					if (!(methodFromStackframe != null))
					{
						continue;
					}
					Assembly assembly = methodFromStackframe.DeclaringType?.Assembly;
					if (assembly != null)
					{
						hashSet.UnionWith(source.Where((ModContainer mod) => mod.Assembly == assembly));
					}
					if (!(methodFromStackframe is MethodInfo methodInfo))
					{
						continue;
					}
					MethodBase originalMethod = Harmony.GetOriginalMethod(methodInfo);
					if (originalMethod != null)
					{
						Patches patchInfo = Harmony.GetPatchInfo(originalMethod);
						if (patchInfo != null)
						{
							hashSet2.UnionWith(patchInfo.Owners);
						}
					}
				}
			}
			stringBuilder.Append(DateTime.Now.ToString() + ": Critical error occurred");
			if (hashSet.Count == 0)
			{
				stringBuilder.Append('\n');
			}
			else
			{
				stringBuilder.AppendFormat(" in the following mod{0}: {1}\n", (hashSet.Count > 1) ? "s" : "", string.Join(", ", hashSet.Select((ModContainer mod) => mod.Info?.ModID + "@" + mod.Info?.Version)));
			}
			stringBuilder.AppendLine("Loaded Mods: " + string.Join(", ", LoadedMods.Select((ModContainer mod) => mod.Info?.ModID + "@" + mod.Info?.Version)));
			if (hashSet2.Count > 0)
			{
				stringBuilder.Append("Involved Harmony IDs: ");
				stringBuilder.AppendLine(string.Join(", ", hashSet2));
			}
			stringBuilder.Append(stringBuilder2);
			if (launchCrashReporterGui)
			{
				try
				{
					File.WriteAllText(Path.Combine(Path.GetTempPath(), "VSLastCrash.log"), stringBuilder.ToString());
					switch (RuntimeEnv.OS)
					{
					case OS.Windows:
						Process.Start(Path.Combine(GamePaths.Binaries, "VSCrashReporter.exe"), new string[1] { GamePaths.Logs });
						break;
					case OS.Mac:
						Process.Start("open", new string[3]
						{
							Path.Combine(GamePaths.Binaries, "VSCrashReporter.app"),
							"--args",
							GamePaths.Logs
						});
						break;
					case OS.Linux:
						Process.Start(Path.Combine(GamePaths.Binaries, "VSCrashReporter"), new string[1] { GamePaths.Logs });
						break;
					}
				}
				catch (Exception ex3)
				{
					stringBuilder.Append("Failed to open crash reporter because: " + ex3.ToString());
				}
			}
			using (FileStream stream = File.Open(text, FileMode.Append))
			{
				using StreamWriter streamWriter = new StreamWriter(stream);
				streamWriter.Write(stringBuilder.ToString());
			}
			stringBuilder.AppendLine("Crash written to file at \"" + text + "\"");
			if (logger != null)
			{
				logger.Fatal("{0}", stringBuilder.ToString());
			}
			CallOnCrash();
			Console.WriteLine("{0}", stringBuilder);
		}
		catch (Exception ex4)
		{
			StringBuilder stringBuilder3 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder3);
			handler.AppendLiteral("Crashreport failed: ");
			handler.AppendFormatted(LoggerBase.CleanStackTrace(ex4.ToString()));
			stringBuilder3.AppendLine(ref handler);
			logger?.Fatal(stringBuilder.ToString());
		}
		finally
		{
			ScreenManager.Platform?.WindowExit("Game crashed", EnumExitMode.HardExit);
		}
	}

	private void CallOnCrash()
	{
		if (OnCrash != null)
		{
			try
			{
				OnCrash();
			}
			catch (Exception)
			{
			}
		}
	}
}
