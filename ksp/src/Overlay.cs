using System;
using UnityEngine;

namespace KerbalCraft
{
	/// <summary>Minecraft's hand, hotbar, hearts, chat and menus, drawn over KSP's frame (premultiplied alpha, RGBA8).</summary>
	public unsafe class Overlay
	{
		Texture2D tex;
		Material mat;
		byte[] straight;
		bool flip, have;
		public int Width, Height;

		public Overlay()
		{
			Shaders.Load();
			if (Shaders.Overlay != null) mat = new Material(Shaders.Overlay);
		}

		public void Destroy() { if (tex != null) UnityEngine.Object.Destroy(tex); if (mat != null) UnityEngine.Object.Destroy(mat); }

		public void Update()
		{
			if (!Link.AcquireOverlayFrame()) return;
			int w, h; bool bottomUp;
			Link.FrontHeader(out w, out h, out bottomUp);
			if (w <= 0 || h <= 0 || w > Proto.MAX_OVERLAY_W || h > Proto.MAX_OVERLAY_H) return;
			if (tex == null || tex.width != w || tex.height != h)
			{
				if (tex != null) UnityEngine.Object.Destroy(tex);
				tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
				tex.filterMode = FilterMode.Bilinear; tex.wrapMode = TextureWrapMode.Clamp;
				straight = mat == null ? new byte[w * h * 4] : null;
			}
			Width = w; Height = h;
			// Unity's first row is the bottom one, so bottom-up data needs no flip.
			flip = !bottomUp;
			if (mat != null)
			{
				tex.LoadRawTextureData((IntPtr)Link.FrontPixels, w * h * 4);
			}
			else
			{
				Unpremultiply(Link.FrontPixels, straight, w * h);
				tex.LoadRawTextureData(straight);
			}
			tex.Apply(false, false);
			have = true;
		}

		/// <summary>Fallback without the overlay shader: convert to straight alpha for IMGUI's blending.</summary>
		static void Unpremultiply(byte* src, byte[] dst, int pixels)
		{
			fixed (byte* d0 = dst)
			{
				byte* d = d0;
				for (int i = 0; i < pixels; i++, src += 4, d += 4)
				{
					int a = src[3];
					if (a == 255) { d[0] = src[0]; d[1] = src[1]; d[2] = src[2]; d[3] = 255; }
					else if (a == 0)
					{
						// the crosshair's "invert" blend: colour but no alpha
						int m = Math.Max(src[0], Math.Max(src[1], src[2]));
						d[0] = d[1] = d[2] = 255; d[3] = (byte)m;
					}
					else
					{
						d[0] = (byte)Math.Min(255, src[0] * 255 / a); d[1] = (byte)Math.Min(255, src[1] * 255 / a); d[2] = (byte)Math.Min(255, src[2] * 255 / a); d[3] = (byte)a;
					}
				}
			}
		}

		public void Draw()
		{
			if (!have || tex == null || Event.current.type != EventType.Repaint) return;
			Rect screen = new Rect(0, 0, Screen.width, Screen.height);
			Rect uv = flip ? new Rect(0, 1, 1, -1) : new Rect(0, 0, 1, 1);
			Color old = GUI.color; GUI.color = Color.white;   // без подмешанного оттенка/прозрачности
			if (mat != null) Graphics.DrawTexture(screen, tex, uv, 0, 0, 0, 0, mat);
			else GUI.DrawTextureWithTexCoords(screen, tex, uv, true);
			GUI.color = old;
		}
	}
}
