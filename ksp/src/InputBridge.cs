using System.Collections.Generic;
using UnityEngine;

namespace KerbalCraft
{
	/// <summary>
	/// Unity input -> Minecraft's input ring. Minecraft thinks it has the keyboard and mouse
	/// (its window is hidden); KSP only keeps the keys listed in Settings (toggle, board) and Esc,
	/// which opens Minecraft's pause menu.
	/// </summary>
	public class InputBridge
	{
		// Unity KeyCode -> SDL scancode (USB HID usage), what Minecraft's GLFW/SDL layer reports.
		static readonly Dictionary<KeyCode, ushort> Sdl = Build();
		readonly List<KeyValuePair<KeyCode, ushort>> keys = new List<KeyValuePair<KeyCode, ushort>>(Sdl);
		float lookDx, lookDy;
		int cursorX, cursorY;

		static Dictionary<KeyCode, ushort> Build()
		{
			var d = new Dictionary<KeyCode, ushort>();
			for (int i = 0; i < 26; i++) d[KeyCode.A + i] = (ushort)(4 + i);
			for (int i = 0; i < 9; i++) d[KeyCode.Alpha1 + i] = (ushort)(30 + i);
			d[KeyCode.Alpha0] = 39;
			d[KeyCode.Return] = 40; d[KeyCode.Escape] = 41; d[KeyCode.Backspace] = 42; d[KeyCode.Tab] = 43; d[KeyCode.Space] = 44;
			d[KeyCode.Minus] = 45; d[KeyCode.Equals] = 46; d[KeyCode.LeftBracket] = 47; d[KeyCode.RightBracket] = 48; d[KeyCode.Backslash] = 49;
			d[KeyCode.Semicolon] = 51; d[KeyCode.Quote] = 52; d[KeyCode.BackQuote] = 53; d[KeyCode.Comma] = 54; d[KeyCode.Period] = 55; d[KeyCode.Slash] = 56;
			d[KeyCode.CapsLock] = 57;
			for (int i = 0; i < 12; i++) d[KeyCode.F1 + i] = (ushort)(58 + i);
			d[KeyCode.Print] = 70; d[KeyCode.ScrollLock] = 71; d[KeyCode.Pause] = 72; d[KeyCode.Insert] = 73; d[KeyCode.Home] = 74; d[KeyCode.PageUp] = 75;
			d[KeyCode.Delete] = 76; d[KeyCode.End] = 77; d[KeyCode.PageDown] = 78;
			d[KeyCode.RightArrow] = 79; d[KeyCode.LeftArrow] = 80; d[KeyCode.DownArrow] = 81; d[KeyCode.UpArrow] = 82;
			d[KeyCode.Numlock] = 83; d[KeyCode.KeypadDivide] = 84; d[KeyCode.KeypadMultiply] = 85; d[KeyCode.KeypadMinus] = 86; d[KeyCode.KeypadPlus] = 87;
			d[KeyCode.KeypadEnter] = 88;
			for (int i = 0; i < 9; i++) d[KeyCode.Keypad1 + i] = (ushort)(89 + i);
			d[KeyCode.Keypad0] = 98; d[KeyCode.KeypadPeriod] = 99;
			d[KeyCode.LeftControl] = 224; d[KeyCode.LeftShift] = 225; d[KeyCode.LeftAlt] = 226; d[KeyCode.LeftWindows] = 227;
			d[KeyCode.RightControl] = 228; d[KeyCode.RightShift] = 229; d[KeyCode.RightAlt] = 230; d[KeyCode.RightWindows] = 231;
			return d;
		}

		/// <summary>Everything before the first Steve frame: stale mouse movement must not turn the view.</summary>
		public void Reset() { lookDx = lookDy = 0f; }

		/// <summary>
		/// Call once per Update while Steve mode is on. Returns the mouse movement for the look
		/// integration (zero while a Minecraft screen is open). toggle / board report the two keys KSP keeps.
		/// </summary>
		public void Poll(bool mcScreenOpen, int overlayW, int overlayH, out float dx, out float dy, out bool toggle, out bool board)
		{
			toggle = Input.GetKeyDown(Settings.ToggleKey);
			board = !mcScreenOpen && Input.GetKeyDown(Settings.BoardKey);

			if (!mcScreenOpen && Input.GetKeyDown(KeyCode.Escape))
			{
				Link.ReleaseAllInput();
				Link.PushInput(Proto.IN_OPEN_MENU, 0);
			}
			else
			{
				for (int i = 0; i < keys.Count; i++)
				{
					KeyCode k = keys[i].Key;
					if (k == Settings.ToggleKey) continue;
					if (!mcScreenOpen && k == Settings.BoardKey) continue;
					if (Input.GetKeyDown(k)) Link.PushInput(Proto.IN_KEY, keys[i].Value, 1);
					else if (Input.GetKeyUp(k)) Link.PushInput(Proto.IN_KEY, keys[i].Value, 0);
				}
			}

			ushort[] buttons = { 1, 3, 2, 4, 5 }; // Unity 0 left, 1 right, 2 middle, 3, 4 -> SDL 1 L, 3 R, 2 M, 4 X1, 5 X2
			for (int i = 0; i < buttons.Length; i++)
			{
				if (Input.GetMouseButtonDown(i)) Link.PushInput(Proto.IN_MOUSE_BUTTON, buttons[i], 1);
				else if (Input.GetMouseButtonUp(i)) Link.PushInput(Proto.IN_MOUSE_BUTTON, buttons[i], 0);
			}

			float wheel = Input.mouseScrollDelta.y;
			if (wheel > 0.01f) Link.PushInput(Proto.IN_SCROLL, 0, 120);
			else if (wheel < -0.01f) Link.PushInput(Proto.IN_SCROLL, 0, -120);

			dx = dy = 0f;
			if (mcScreenOpen)
			{
				// A screen (inventory, chat, pause menu): Minecraft wants an absolute cursor in its own pixels.
				cursorX = Mathf.Clamp((int)(Input.mousePosition.x * overlayW / Mathf.Max(Screen.width, 1)), 0, overlayW - 1);
				cursorY = Mathf.Clamp((int)((Screen.height - Input.mousePosition.y) * overlayH / Mathf.Max(Screen.height, 1)), 0, overlayH - 1);
				Link.PushInput(Proto.IN_CURSOR, 0, cursorX, cursorY);
				string typed = Input.inputString;
				for (int i = 0; typed != null && i < typed.Length; i++)
				{
					char c = typed[i];
					if (c >= ' ' && c != (char)127) Link.PushInput(Proto.IN_TEXT, 0, c);
				}
			}
			else
			{
				dx = Input.GetAxisRaw("Mouse X") * Settings.MouseScale;
				dy = -Input.GetAxisRaw("Mouse Y") * Settings.MouseScale;   // Unity's Y grows upward, Minecraft's pitch grows downward
			}
		}
	}
}
