using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NanoSvg;
using SkiaSharp;

namespace Cairo
{
	internal sealed class RecordedGuiPicture : IDisposable
	{
		internal readonly SKPicture Picture;
		internal readonly int X, Y, Width, Height;
		internal readonly int? Tint;
		internal readonly bool TextureColorOrder;
		internal readonly Action<Context> NativeReplay;
		readonly IDisposable owner;
		internal RecordedGuiPicture(SKPicture picture, int x, int y, int width, int height, int? tint, Action<Context> replay, bool textureColorOrder = false, IDisposable owner = null)
		{ this.owner = owner; Picture = picture; X = x; Y = y; Width = width; Height = height; Tint = tint; TextureColorOrder = textureColorOrder; NativeReplay = replay; }
		public void Dispose() { if (owner != null) owner.Dispose(); else Picture.Dispose(); }
	}

}
