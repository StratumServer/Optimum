using System;
using System.Runtime.InteropServices;
using System.Threading;
using Cairo;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SkiaSharp;
using Vintagestory.API.Client;

namespace Vintagestory.Client.NoObf
{
	/// <summary>OpenGL and Skia implementation for the opt-in GUI renderer contract.</summary>
	public static class OptimumGuiGpuProbeBackend
	{
		private const int Width = OptimumGuiGpuProbe.ReadbackWidth;
		private const int Height = OptimumGuiGpuProbe.ReadbackHeight;
		[ThreadStatic] private static int attempted;
		[ThreadStatic] private static IntPtr contextHandle;
		[ThreadStatic] private static GRGlInterface candidateInterface;
		[ThreadStatic] private static GRContext candidateContext;
		[ThreadStatic] private static RecordedGpuBlur candidateBlur;

		/// <summary>Shader passes submitted by this thread's current GUI GPU context.</summary>
		public static long CpuRasterizationCount => SurfaceRecordingDiagnostics.CpuRasterizations;
		public static long NativeMaterializationCount => SurfaceRecordingDiagnostics.Materializations;
		public static long GpuDependencyRenderCount => SurfaceRecordingDiagnostics.DependencyRenders;
		public static long GpuDependencyCacheHitCount => SurfaceRecordingDiagnostics.DependencyCacheHits;
		private static long diagnosticReadbackCount;
		public static long DiagnosticReadbackCount => Interlocked.Read(ref diagnosticReadbackCount);
		public static void ResetRenderingDiagnostics() { SurfaceRecordingDiagnostics.Reset(); Interlocked.Exchange(ref diagnosticReadbackCount, 0); }
		public static long GpuBlurPassCount => candidateBlur?.PassCount ?? 0;

		/// <summary>Opt-in elapsed timings for CPU preparation. Disabled by default; not thread CPU or GPU time.</summary>
		public static bool RecordingProfilingEnabled { get => SurfaceRecordingDiagnostics.ProfilingEnabled; set => SurfaceRecordingDiagnostics.ProfilingEnabled = value; }
		public static long RecordingProfileTimestampFrequency => System.Diagnostics.Stopwatch.Frequency;
		public static long RecordingShadowAccessCount => SurfaceRecordingDiagnostics.ShadowAccesses;
		public static long RecordingShadowTicks => SurfaceRecordingDiagnostics.ShadowTicks;
		public static long RecordingShadowCreationCount => SurfaceRecordingDiagnostics.ShadowCreations;
		public static long RecordingShadowCommandCount => SurfaceRecordingDiagnostics.ShadowCommands;
		public static long RecordingDrawingCaptureCount => SurfaceRecordingDiagnostics.DrawingCaptures;
		public static long RecordingDrawingCaptureTicks => SurfaceRecordingDiagnostics.DrawingCaptureTicks;
		public static long RecordingPathCopyCount => SurfaceRecordingDiagnostics.PathCopies;
		public static long RecordingPathCopyTicks => SurfaceRecordingDiagnostics.PathCopyTicks;
		public static long RecordingPathConversionCount => SurfaceRecordingDiagnostics.PathConversions;
		public static long RecordingPathConversionTicks => SurfaceRecordingDiagnostics.PathConversionTicks;
		public static long RecordingStrokeOutlineCount => SurfaceRecordingDiagnostics.StrokeOutlines;
		public static long RecordingStrokeOutlineTicks => SurfaceRecordingDiagnostics.StrokeOutlineTicks;
		/// <summary>Reset between measurements, with no operation in flight. Does not change enabled state.</summary>
		public static void ResetRecordingProfiling() => SurfaceRecordingDiagnostics.ResetProfiling();
        public static System.Collections.Generic.IReadOnlyDictionary<string, long> GetRecordingStageDiagnostics() => SurfaceRecordingDiagnostics.StageDiagnostics;

		/// <summary>Reuse immutable CPU preparation; disabling keeps all legacy commands on the uncached route.</summary>
		public static bool RecordingPreparationReuseEnabled { get => RecordedPreparationCache.Enabled; set => RecordedPreparationCache.Enabled = value; }
		public static long RecordingPreparationCacheHits => RecordedPreparationCache.Hits;
		public static long RecordingPreparationCacheMisses => RecordedPreparationCache.Misses;
		public static long RecordingPreparationCacheEvictions => RecordedPreparationCache.Evictions;
		public static long RecordingPreparationTextHits => RecordedPreparationCache.TextHits;
		public static long RecordingPreparationPathHits => RecordedPreparationCache.PathHits;
		public static long RecordingPreparationStrokeHits => RecordedPreparationCache.StrokeHits;
		public static long RecordingPreparationSvgHits => RecordedPreparationCache.PictureHits;
		public static long RecordingPreparationCacheEntries => RecordedPreparationCache.Count;
		public static long RecordingPreparationCacheEstimatedBytes => RecordedPreparationCache.Bytes;
		public static void ResetRecordingPreparationCounters() => RecordedPreparationCache.ResetCounters();
		public static void ClearRecordingPreparationCache() => RecordedPreparationCache.Clear();



		// Call while the owning GLFW context is still current. On context replacement,
		// abandon the old resources without issuing GL calls into the new context.
		public static void ReleaseCurrentContext()
		{
			GRContext context = candidateContext;
			GRGlInterface glInterface = candidateInterface;
			RecordedGpuBlur blur = candidateBlur;
			IntPtr owner = contextHandle;
			candidateContext = null;
			candidateInterface = null;
			candidateBlur = null;
			contextHandle = IntPtr.Zero;
			attempted = 0;
			Vintagestory.API.Config.OptimumConfig.SetGuiGpuBackendProbePassed(false);
			try
			{
				// Abandon first if a different context is current; disposal must not touch its GL state.
				if (context != null && owner != GetCurrentContextHandle()) context.AbandonContext(false);
				blur?.Dispose();
				if (context != null && owner == GetCurrentContextHandle()) context.AbandonContext(true);
			}
			finally
			{
				try { context?.Dispose(); }
				finally { glInterface?.Dispose(); }
			}
		}

		private static unsafe IntPtr GetCurrentContextHandle() => (IntPtr)GLFW.GetCurrentContext();

		private static GRContext GetCandidateContext()
		{
			if (candidateContext != null) return candidateContext;
			GRGlInterface glInterface = GRGlInterface.Create(new GRGlGetProcedureAddressDelegate(GetGlProcedureAddress));
			if (glInterface == null) throw new InvalidOperationException("Skia could not create an OpenGL interface.");
			GRContext context = null;
			try
			{
				context = GRContext.CreateGl(glInterface);
				if (context == null) throw new InvalidOperationException("Skia could not create the current-context GPU backend.");
				context.SetResourceCacheLimit(16 * 1024 * 1024);
				candidateInterface = glInterface;
				candidateContext = context;
				return context;
			}
			catch
			{
				try { context?.Dispose(); }
				finally { glInterface.Dispose(); }
				throw;
			}
		}

		public static string TryRunOnce(int expectedContextThreadId)
				{
					Surface.AutomaticRecordingEnabled = OptimumGuiGpuProbe.RenderingRequested;
					SurfaceRecordingDiagnostics.Enabled = OptimumGuiGpuProbe.RenderingRequested;
					if (!OptimumGuiGpuProbe.Enabled && !OptimumGuiGpuProbe.RenderingRequested)
						return null;
					if (Environment.CurrentManagedThreadId != expectedContextThreadId)
						return "Optimum Skia GL probe skipped: caller is not on the context-owning thread.";
					if (!HasCurrentContext())
						return "Optimum Skia GL probe skipped: no current GLFW OpenGL context.";
					IntPtr currentHandle = GetCurrentContextHandle();
					if (contextHandle != currentHandle)
					{
						ReleaseCurrentContext();
						contextHandle = currentHandle;
					}
					if (Interlocked.CompareExchange(ref attempted, 1, 0) != 0)
						return null;
					string preexistingErrors = DrainPreexistingGlErrors();
					if (preexistingErrors != null)
					{
						Vintagestory.API.Config.OptimumConfig.SetGuiGpuBackendProbePassed(false);
						return "Optimum Skia GL probe skipped before mutation; preexisting GL errors were consumed: " + preexistingErrors;
					}

					string renderer = SafeGetString(StringName.Renderer);
					string version = SafeGetString(StringName.Version);
					if (OptimumGuiGpuProbe.IsSoftwareRenderer(renderer))
					{
						Vintagestory.API.Config.OptimumConfig.SetGuiGpuBackendProbePassed(false);
						return "Optimum Skia GL probe skipped; renderer=" + renderer + "; GL=" + version + "; reason=software OpenGL renderers are not enabled for GPU GUI rendering.";
					}
					try
					{
						string readbackFailure;
						if (!RunProbe(out readbackFailure))
						{
							Vintagestory.API.Config.OptimumConfig.SetGuiGpuBackendProbePassed(false);
							return "Optimum Skia GL probe failed; renderer=" + renderer + "; GL=" + version + "; reason=" + readbackFailure;
						}
						Vintagestory.API.Config.OptimumConfig.SetGuiGpuBackendProbePassed(true);
						return "Optimum Skia GL probe passed; renderer=" + renderer + "; GL=" + version + "; target=8x8 RGBA8; readback=orientation, RGBA channels, premultiplied alpha";
					}
					catch (Exception exception)
					{
						Vintagestory.API.Config.OptimumConfig.SetGuiGpuBackendProbePassed(false);
						return "Optimum Skia GL probe failed; renderer=" + renderer + "; GL=" + version + "; reason=" + exception.GetType().Name + ": " + exception.Message;
					}
				}

		public static System.Collections.Generic.IReadOnlyDictionary<string, long> GetMaterializationDiagnostics() => SurfaceRecordingDiagnostics.MaterializationCauses;
		public static System.Collections.Generic.IReadOnlyDictionary<string, long> GetRasterizationDiagnostics() => SurfaceRecordingDiagnostics.RasterizationCauses;
		public static System.Collections.Generic.IReadOnlyDictionary<string, long> GetGpuFallbackDiagnostics() => SurfaceRecordingDiagnostics.FallbackReasons;
		public static bool TryCreateCandidateTexture(Cairo.ImageSurface surface, bool linearMag, int expectedContextThreadId, out int candidateTexture, out string failureReason)
		{
			bool result = TryCreateCandidateTextureCore(surface, linearMag, expectedContextThreadId, out candidateTexture, out failureReason);
			if (!result) SurfaceRecordingDiagnostics.Fallback(failureReason);
			return result;
		}
		private static bool TryCreateCandidateTextureCore(Cairo.ImageSurface surface, bool linearMag, int expectedContextThreadId, out int candidateTexture, out string failureReason)
				{
					candidateTexture = 0;
					failureReason = null;
					Surface.AutomaticRecordingEnabled = OptimumGuiGpuProbe.RenderingRequested;
					SurfaceRecordingDiagnostics.Enabled = OptimumGuiGpuProbe.RenderingRequested;
					if (!OptimumGuiGpuProbe.RenderingRequested)
					{
						failureReason = "rendering-not-requested";
						return false;
					}
					if (surface == null)
					{
						failureReason = "missing-surface";
						return false;
					}
					if (Environment.CurrentManagedThreadId != expectedContextThreadId)
					{
						failureReason = "missing-context-thread";
						return false;
					}
					if (surface.RecordingState != Cairo.SurfaceRecordingState.Recording && surface.RecordingState != Cairo.SurfaceRecordingState.GpuRendered)
					{
						failureReason = "surface-not-recording:" + surface.RecordingState + ":" + surface.MaterializeCause;
						return false;
					}
					if (!HasCurrentContext())
					{
						failureReason = "missing-current-context";
						return false;
					}
					TryRunOnce(expectedContextThreadId);
					if (!OptimumGuiGpuProbe.GuiRenderingEnabled)
					{
						failureReason = "gpu-probe-not-passed";
						return false;
					}
					// Keep pre-existing engine errors out of the candidate operation. The probe's caller
					// performs the normal upload error check if this returns false.
					string preexistingErrors = DrainPreexistingGlErrors();
					if (preexistingErrors != null)
					{
						failureReason = "preexisting-gl-errors:" + preexistingErrors;
						return false;
					}

					GlStateSnapshot state = null;
					int texture = 0, framebuffer = 0;
					bool rendered = false;
					try
					{
						state = GlStateSnapshot.Capture();
						GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
						GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
						GL.ActiveTexture(TextureUnit.Texture0);
						GL.GenTextures(1, out texture);
						GL.BindTexture(TextureTarget.Texture2D, texture);
						GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
						GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, linearMag ? (int)TextureMagFilter.Linear : (int)TextureMagFilter.Nearest);
						GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, surface.Width, surface.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
						if (surface.Width == 0 || surface.Height == 0)
						{
							// Vanilla creates zero-sized textures while a scrollbar is not yet
							// sized. Preserve that storage and seal the empty recording without
							// constructing an incomplete framebuffer or rasterizing on the CPU.
							if (!surface.TrySealEmptyRecordedCommands()) throw new InvalidOperationException("The empty GUI command list could not be sealed.");
							rendered = true;
						}
						else
						{
						GL.GenFramebuffers(1, out framebuffer);
						GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
						GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
						GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
						GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
						if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
							throw new InvalidOperationException("Candidate GUI framebuffer is incomplete.");

						GL.Viewport(0, 0, surface.Width, surface.Height);
						GL.Disable(EnableCap.ScissorTest);
						GL.Disable(EnableCap.Blend);
						GL.Disable(EnableCap.DepthTest);
						GL.Disable(EnableCap.StencilTest);
						GL.Disable(EnableCap.CullFace);
						GL.Disable((EnableCap)0x8DB9);
						GL.ColorMask(true, true, true, true);
						GL.DepthMask(true);
						GL.StencilMask(0xFFFFFFFF);
						GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
						GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

						// TopLeft keeps Cairo's row order: canvas row 0 is the first row uploaded to GL.
						GRContext context = GetCandidateContext();
						// The engine and Skia share GL; invalidate Skia's cached bindings each time.
						context.ResetContext(GRGlBackendState.All);
						var info = new GRGlFramebufferInfo((uint)framebuffer, (uint)PixelInternalFormat.Rgba8);
						using (var target = new GRBackendRenderTarget(surface.Width, surface.Height, 0, 0, info))
						using (SKSurface skSurface = SKSurface.Create(context, target, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888))
						{
							if (skSurface == null) throw new InvalidOperationException("Skia rejected the candidate GUI framebuffer.");
							candidateBlur ??= new RecordedGpuBlur(context);
							if (!surface.TryDrawRecordedCommands(skSurface, candidateBlur)) throw new InvalidOperationException("The GUI command list could not be sealed.");
							using (SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.GpuSubmission)) {
                                skSurface.Flush(); context.Flush(); context.Submit(false);
                            }
							OpenTK.Graphics.OpenGL.ErrorCode error = GL.GetError();
							if (error != OpenTK.Graphics.OpenGL.ErrorCode.NoError) throw new InvalidOperationException("OpenGL candidate render error: " + error + ".");
							rendered = true;
						}
						}
					}
					catch (Exception exception)
					{
						rendered = false;
						failureReason = "candidate-render-exception:" + exception.GetType().Name + ":" + exception.Message;
					}
					finally
					{
						if (state != null)
						{
							try { state.Restore(); }
							catch (Exception exception) { rendered = false; failureReason = "gl-state-restore-exception:" + exception.GetType().Name + ":" + exception.Message; }
						}
						try { if (framebuffer != 0) GL.DeleteFramebuffers(1, ref framebuffer); }
						catch (Exception exception) { rendered = false; failureReason = "framebuffer-cleanup-exception:" + exception.GetType().Name + ":" + exception.Message; }
						try
						{
							if (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { rendered = false; failureReason = "gl-error-after-candidate"; }
							while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }
						}
						catch (Exception exception) { rendered = false; failureReason = "gl-error-query-exception:" + exception.GetType().Name + ":" + exception.Message; }
					}

					if (rendered && surface.CommitGpuRenderedCommands())
					{
						candidateTexture = texture;
						return true;
					}
					if (failureReason == null) failureReason = rendered ? "display-list-commit-failed" : "candidate-render-failed";
					try { if (texture != 0) GL.DeleteTextures(1, ref texture); } catch { }
					try { while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { } } catch { }
					return false;
				}

		private static string DrainPreexistingGlErrors()
				{
					string errors = null;
					for (int i = 0; i < 64; i++)
					{
						OpenTK.Graphics.OpenGL.ErrorCode error = GL.GetError();
						if (error == OpenTK.Graphics.OpenGL.ErrorCode.NoError)
							return errors;
						errors = errors == null ? error.ToString() : errors + ", " + error;
					}
					return (errors ?? "unknown") + ", queue did not clear after 64 queries";
				}

		private static unsafe bool HasCurrentContext() => GLFW.GetCurrentContext() != null;

		private static IntPtr GetGlProcedureAddress(string name)
		{
			// GLFW returns platform EGL entry points too, even when its current context is GLX.
			// Skia treats eglQueryString as proof of an EGL context and can dereference the
			// wrong context's dispatch table, so expose only OpenGL procedures here.
			if (name == null || name.StartsWith("egl", StringComparison.Ordinal)) return IntPtr.Zero;
			return GLFW.GetProcAddress(name);
		}

		private static bool RunProbe(out string failure)
				{
					failure = null;
					GlStateSnapshot state = null;
					int texture = 0;
					int framebuffer = 0;
					byte[] pixels = new byte[Width * Height * 4];
					string probeFailure = null;
					try
					{
						state = GlStateSnapshot.Capture();
						GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
						GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
						GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
						GL.PixelStore((PixelStoreParameter)0x0D02, 0); // PACK_ROW_LENGTH
						GL.PixelStore((PixelStoreParameter)0x0D03, 0); // PACK_SKIP_ROWS
						GL.PixelStore((PixelStoreParameter)0x0D04, 0); // PACK_SKIP_PIXELS
						GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
						GL.PixelStore((PixelStoreParameter)0x0CF2, 0); // UNPACK_ROW_LENGTH
						GL.PixelStore((PixelStoreParameter)0x0CF3, 0); // UNPACK_SKIP_ROWS
						GL.PixelStore((PixelStoreParameter)0x0CF4, 0); // UNPACK_SKIP_PIXELS
						GL.ActiveTexture(TextureUnit.Texture0);
						GL.GenTextures(1, out texture);
						GL.BindTexture(TextureTarget.Texture2D, texture);
						GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
						GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
						GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, Width, Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

						GL.GenFramebuffers(1, out framebuffer);
						GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
						GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
						GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
						GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
						FramebufferErrorCode framebufferStatus = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
						if (framebufferStatus != FramebufferErrorCode.FramebufferComplete)
							throw new InvalidOperationException("Probe framebuffer is incomplete: " + framebufferStatus + ".");

						GL.Viewport(0, 0, Width, Height);
						GL.Disable(EnableCap.ScissorTest);
						GL.Disable(EnableCap.Blend);
						GL.Disable(EnableCap.DepthTest);
						GL.Disable(EnableCap.StencilTest);
						GL.Disable(EnableCap.CullFace);
						GL.Disable((EnableCap)0x8DB9); // FRAMEBUFFER_SRGB
						GL.ColorMask(true, true, true, true);
						GL.DepthMask(true);
						GL.StencilMask(0xFFFFFFFF);
						GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
						GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
						GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
						GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);

						var getProc = new GRGlGetProcedureAddressDelegate(GetGlProcedureAddress);
						using (GRGlInterface glInterface = GRGlInterface.Create(getProc))
						{
							if (glInterface == null)
								throw new InvalidOperationException("Skia could not assemble an OpenGL interface using GLFW.GetProcAddress.");
							using (GRContext context = GRContext.CreateGl(glInterface))
							{
								if (context == null)
									throw new InvalidOperationException("Skia could not create a GPU context for the current OpenGL context.");
								context.ResetContext(GRGlBackendState.All);
								var framebufferInfo = new GRGlFramebufferInfo((uint)framebuffer, (uint)PixelInternalFormat.Rgba8);
								using (var target = new GRBackendRenderTarget(Width, Height, 0, 0, framebufferInfo))
								using (SKSurface surface = SKSurface.Create(context, target, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888))
								{
									if (surface == null)
										throw new InvalidOperationException("Skia rejected the 8x8 RGBA8 framebuffer.");
									DrawProbePattern(surface.Canvas);
									surface.Flush();
									context.Flush();
									context.Submit(true);
									GL.Finish();
									GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, framebuffer);
									GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
									GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
									GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
									GL.PixelStore((PixelStoreParameter)0x0D02, 0); // PACK_ROW_LENGTH
									GL.PixelStore((PixelStoreParameter)0x0D03, 0); // PACK_SKIP_ROWS
									GL.PixelStore((PixelStoreParameter)0x0D04, 0); // PACK_SKIP_PIXELS
									Interlocked.Increment(ref diagnosticReadbackCount);
						GL.ReadPixels(0, 0, Width, Height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
								}
							}
						}
					}
					catch (Exception exception)
					{
						probeFailure = exception.GetType().Name + ": " + exception.Message;
					}
					finally
					{
						if (state != null)
						{
							try { state.Restore(); }
							catch (Exception exception) { probeFailure = AppendFailure(probeFailure, "GL state restore: " + exception.Message); }
						}
						try { if (framebuffer != 0) GL.DeleteFramebuffers(1, ref framebuffer); }
						catch (Exception exception) { probeFailure = AppendFailure(probeFailure, "framebuffer cleanup: " + exception.Message); }
						try { if (texture != 0) GL.DeleteTextures(1, ref texture); }
						catch (Exception exception) { probeFailure = AppendFailure(probeFailure, "texture cleanup: " + exception.Message); }

						// This method is invoked only after the engine's GL error check succeeded.
						// Consume errors produced by this probe so they cannot surface on a later upload.
						try
						{
							var firstError = GL.GetError();
							if (firstError != OpenTK.Graphics.OpenGL.ErrorCode.NoError)
								probeFailure = AppendFailure(probeFailure, "OpenGL error: " + firstError);
							while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }
						}
						catch (Exception exception) { probeFailure = AppendFailure(probeFailure, "GL error query: " + exception.Message); }
					}

					if (probeFailure != null)
					{
						failure = probeFailure;
						return false;
					}
					if (!OptimumGuiGpuProbe.ValidateReadback(pixels, out failure))
						return false;
					return true;
				}

		private static void DrawProbePattern(SKCanvas canvas)
				{
					canvas.Clear(SKColors.Transparent);
					using (var paint = new SKPaint { IsAntialias = false })
					{
						paint.Color = new SKColor(240, 20, 30, 255);
						canvas.DrawRect(new SKRect(0, 0, 4, 4), paint);
						paint.Color = new SKColor(20, 230, 40, 255);
						canvas.DrawRect(new SKRect(4, 0, 8, 4), paint);
						paint.Color = new SKColor(30, 40, 240, 255);
						canvas.DrawRect(new SKRect(0, 4, 4, 8), paint);
						paint.Color = new SKColor(240, 160, 80, 128);
						canvas.DrawRect(new SKRect(4, 4, 8, 8), paint);
					}
				}

				private static string SafeGetString(StringName name)
				{
					try { return GL.GetString(name) ?? "unknown"; }
					catch { return "unavailable"; }
				}

		private static string AppendFailure(string existing, string added) => existing == null ? added : existing + "; " + added;

				private sealed class GlStateSnapshot
				{
					private readonly int readFramebuffer;
					private readonly int drawFramebuffer;
					private readonly int renderbuffer;
					private readonly int activeTexture;
					private readonly int[] textureBindings;
					private readonly int[] samplerBindings;
					private readonly bool samplerObjectsSupported;
					private readonly int currentProgram;
					private readonly int vertexArray;
					private readonly int arrayBuffer;
					private readonly int elementArrayBuffer;
					private readonly int pixelPackBuffer;
					private readonly int pixelUnpackBuffer;
					private readonly int packAlignment;
					private readonly int unpackAlignment;
					private readonly int packRowLength;
					private readonly int packSkipRows;
					private readonly int packSkipPixels;
					private readonly int unpackRowLength;
					private readonly int unpackSkipRows;
					private readonly int unpackSkipPixels;
					private readonly int[] viewport = new int[4];
					private readonly int[] scissorBox = new int[4];
					private readonly bool[] colorMask = new bool[4];
					private readonly bool scissorEnabled;
					private readonly bool blendEnabled;
					private readonly bool depthEnabled;
					private readonly bool stencilEnabled;
					private readonly bool cullEnabled;
					private readonly bool framebufferSrgbEnabled;
					private readonly bool depthWriteEnabled;
					private readonly int depthFunction;
					private readonly int frontStencilFunction;
					private readonly int frontStencilReference;
					private readonly int frontStencilValueMask;
					private readonly int frontStencilWriteMask;
					private readonly int frontStencilFail;
					private readonly int frontStencilDepthFail;
					private readonly int frontStencilDepthPass;
					private readonly int backStencilFunction;
					private readonly int backStencilReference;
					private readonly int backStencilValueMask;
					private readonly int backStencilWriteMask;
					private readonly int backStencilFail;
					private readonly int backStencilDepthFail;
					private readonly int backStencilDepthPass;
					private readonly int cullFaceMode;
					private readonly int frontFaceDirection;
					private readonly int blendSourceRgb;
					private readonly int blendDestinationRgb;
					private readonly int blendSourceAlpha;
					private readonly int blendDestinationAlpha;
					private readonly int blendEquationRgb;
					private readonly int blendEquationAlpha;
					private readonly float[] clearColor = new float[4];

					private GlStateSnapshot()
					{
						GL.GetInteger((GetPName)0x8CAA, out readFramebuffer);
						GL.GetInteger((GetPName)0x8CA6, out drawFramebuffer);
						GL.GetInteger((GetPName)0x8CA7, out renderbuffer);
						GL.GetInteger((GetPName)0x84E0, out activeTexture);
						GL.GetInteger((GetPName)0x8B4D, out int textureUnitCount);
						// GL_MAX_COMBINED_TEXTURE_IMAGE_UNITS is driver-dependent; llvmpipe and modern
						// desktop drivers may expose more than the old 128-unit assumption.
						if (textureUnitCount < 1 || textureUnitCount > 1024)
							throw new InvalidOperationException("Unexpected GL texture unit count: " + textureUnitCount + ".");
						textureBindings = new int[textureUnitCount];
						GL.GetInteger((GetPName)0x821B, out int glMajor);
						GL.GetInteger((GetPName)0x821C, out int glMinor);
						samplerObjectsSupported = glMajor > 3 || (glMajor == 3 && glMinor >= 3);
						samplerBindings = samplerObjectsSupported ? new int[textureUnitCount] : Array.Empty<int>();
						try
						{
							for (int unit = 0; unit < textureUnitCount; unit++)
							{
								GL.ActiveTexture((TextureUnit)(0x84C0 + unit));
								GL.GetInteger((GetPName)0x8069, out textureBindings[unit]);
								if (samplerObjectsSupported) GL.GetInteger((GetPName)0x8919, out samplerBindings[unit]);
							}
						}
					finally { GL.ActiveTexture((TextureUnit)activeTexture); }

						GL.GetInteger((GetPName)0x8B8D, out currentProgram);
						GL.GetInteger((GetPName)0x85B5, out vertexArray);
						GL.GetInteger((GetPName)0x8894, out arrayBuffer);
						GL.GetInteger((GetPName)0x8895, out elementArrayBuffer);
						GL.GetInteger((GetPName)0x88ED, out pixelPackBuffer);
						GL.GetInteger((GetPName)0x88EF, out pixelUnpackBuffer);
						GL.GetInteger((GetPName)0x0D05, out packAlignment);
						GL.GetInteger((GetPName)0x0CF5, out unpackAlignment);
						GL.GetInteger((GetPName)0x0D02, out packRowLength);
						GL.GetInteger((GetPName)0x0D03, out packSkipRows);
						GL.GetInteger((GetPName)0x0D04, out packSkipPixels);
						GL.GetInteger((GetPName)0x0CF2, out unpackRowLength);
						GL.GetInteger((GetPName)0x0CF3, out unpackSkipRows);
						GL.GetInteger((GetPName)0x0CF4, out unpackSkipPixels);
						GL.GetInteger(GetPName.Viewport, viewport);
						GL.GetInteger(GetPName.ScissorBox, scissorBox);
						GL.GetBoolean((GetPName)0x0C23, colorMask);
						GL.GetFloat((GetPName)0x0C22, clearColor);
						scissorEnabled = GL.IsEnabled(EnableCap.ScissorTest);
						blendEnabled = GL.IsEnabled(EnableCap.Blend);
						depthEnabled = GL.IsEnabled(EnableCap.DepthTest);
						stencilEnabled = GL.IsEnabled(EnableCap.StencilTest);
						cullEnabled = GL.IsEnabled(EnableCap.CullFace);
						framebufferSrgbEnabled = GL.IsEnabled((EnableCap)0x8DB9);
						depthWriteEnabled = GL.GetBoolean((GetPName)0x0B72);
						GL.GetInteger((GetPName)0x0B74, out depthFunction);
						GL.GetInteger((GetPName)0x0B45, out cullFaceMode);
						GL.GetInteger((GetPName)0x0B46, out frontFaceDirection);
						GL.GetInteger((GetPName)0x0B92, out frontStencilFunction);
						GL.GetInteger((GetPName)0x0B97, out frontStencilReference);
						GL.GetInteger((GetPName)0x0B93, out frontStencilValueMask);
						GL.GetInteger((GetPName)0x0B98, out frontStencilWriteMask);
						GL.GetInteger((GetPName)0x0B94, out frontStencilFail);
						GL.GetInteger((GetPName)0x0B95, out frontStencilDepthFail);
						GL.GetInteger((GetPName)0x0B96, out frontStencilDepthPass);
						GL.GetInteger((GetPName)0x8800, out backStencilFunction);
						GL.GetInteger((GetPName)0x8CA3, out backStencilReference);
						GL.GetInteger((GetPName)0x8CA4, out backStencilValueMask);
						GL.GetInteger((GetPName)0x8CA5, out backStencilWriteMask);
						GL.GetInteger((GetPName)0x8801, out backStencilFail);
						GL.GetInteger((GetPName)0x8802, out backStencilDepthFail);
						GL.GetInteger((GetPName)0x8803, out backStencilDepthPass);
						GL.GetInteger((GetPName)0x80C9, out blendSourceRgb);
						GL.GetInteger((GetPName)0x80C8, out blendDestinationRgb);
						GL.GetInteger((GetPName)0x80CB, out blendSourceAlpha);
						GL.GetInteger((GetPName)0x80CA, out blendDestinationAlpha);
						GL.GetInteger((GetPName)0x8009, out blendEquationRgb);
						GL.GetInteger((GetPName)0x883D, out blendEquationAlpha);
					}

					internal static GlStateSnapshot Capture() => new GlStateSnapshot();

					internal void Restore()
					{
						GL.BindFramebuffer((FramebufferTarget)0x8CA8, readFramebuffer);
						GL.BindFramebuffer((FramebufferTarget)0x8CA9, drawFramebuffer);
						GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, renderbuffer);
						GL.UseProgram(currentProgram);
						GL.BindVertexArray(vertexArray);
						GL.BindBuffer(BufferTarget.ArrayBuffer, arrayBuffer);
						if (vertexArray != 0) GL.BindBuffer(BufferTarget.ElementArrayBuffer, elementArrayBuffer);
						GL.BindBuffer(BufferTarget.PixelPackBuffer, pixelPackBuffer);
						GL.BindBuffer(BufferTarget.PixelUnpackBuffer, pixelUnpackBuffer);
						GL.PixelStore(PixelStoreParameter.PackAlignment, packAlignment);
						GL.PixelStore(PixelStoreParameter.UnpackAlignment, unpackAlignment);
						GL.PixelStore((PixelStoreParameter)0x0D02, packRowLength);
						GL.PixelStore((PixelStoreParameter)0x0D03, packSkipRows);
						GL.PixelStore((PixelStoreParameter)0x0D04, packSkipPixels);
						GL.PixelStore((PixelStoreParameter)0x0CF2, unpackRowLength);
						GL.PixelStore((PixelStoreParameter)0x0CF3, unpackSkipRows);
						GL.PixelStore((PixelStoreParameter)0x0CF4, unpackSkipPixels);
						for (int unit = 0; unit < textureBindings.Length; unit++)
						{
							GL.ActiveTexture((TextureUnit)(0x84C0 + unit));
							GL.BindTexture(TextureTarget.Texture2D, textureBindings[unit]);
							if (samplerObjectsSupported) GL.BindSampler(unit, samplerBindings[unit]);
						}
						GL.ActiveTexture((TextureUnit)activeTexture);
						GL.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
						GL.Scissor(scissorBox[0], scissorBox[1], scissorBox[2], scissorBox[3]);
						GL.ColorMask(colorMask[0], colorMask[1], colorMask[2], colorMask[3]);
						GL.ClearColor(clearColor[0], clearColor[1], clearColor[2], clearColor[3]);
						SetEnabled(EnableCap.ScissorTest, scissorEnabled);
						SetEnabled(EnableCap.Blend, blendEnabled);
						SetEnabled(EnableCap.DepthTest, depthEnabled);
						SetEnabled(EnableCap.StencilTest, stencilEnabled);
						SetEnabled(EnableCap.CullFace, cullEnabled);
						SetEnabled((EnableCap)0x8DB9, framebufferSrgbEnabled);
						GL.DepthMask(depthWriteEnabled);
						GL.DepthFunc((DepthFunction)depthFunction);
						GL.StencilFuncSeparate(StencilFace.Front, (StencilFunction)frontStencilFunction, frontStencilReference, frontStencilValueMask);
						GL.StencilMaskSeparate(StencilFace.Front, frontStencilWriteMask);
						GL.StencilOpSeparate(StencilFace.Front, (StencilOp)frontStencilFail, (StencilOp)frontStencilDepthFail, (StencilOp)frontStencilDepthPass);
						GL.StencilFuncSeparate(StencilFace.Back, (StencilFunction)backStencilFunction, backStencilReference, backStencilValueMask);
						GL.StencilMaskSeparate(StencilFace.Back, backStencilWriteMask);
						GL.StencilOpSeparate(StencilFace.Back, (StencilOp)backStencilFail, (StencilOp)backStencilDepthFail, (StencilOp)backStencilDepthPass);
						GL.CullFace((TriangleFace)cullFaceMode);
						GL.FrontFace((FrontFaceDirection)frontFaceDirection);
						GL.BlendFuncSeparate((BlendingFactorSrc)blendSourceRgb, (BlendingFactorDest)blendDestinationRgb,
							(BlendingFactorSrc)blendSourceAlpha, (BlendingFactorDest)blendDestinationAlpha);
						GL.BlendEquationSeparate((BlendEquationMode)blendEquationRgb, (BlendEquationMode)blendEquationAlpha);
					}

					private static void SetEnabled(EnableCap cap, bool enabled)
					{
						if (enabled) GL.Enable(cap);
						else GL.Disable(cap);
					}
				}


	}
}
