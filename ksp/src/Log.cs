using UnityEngine;

namespace KerbalCraft
{
	public static class Log
	{
		public static void Info(string fmt, params object[] args) { Debug.Log("[KerbalCraft] " + Fmt(fmt, args)); }
		public static void Warn(string fmt, params object[] args) { Debug.LogWarning("[KerbalCraft] " + Fmt(fmt, args)); }
		public static void Error(string fmt, params object[] args) { Debug.LogError("[KerbalCraft] " + Fmt(fmt, args)); }
		static string Fmt(string f, object[] a) { try { return a == null || a.Length == 0 ? f : string.Format(f, a); } catch { return f; } }
	}
}
