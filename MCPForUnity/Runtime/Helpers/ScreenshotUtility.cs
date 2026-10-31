using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MCPForUnity.Runtime.Helpers
//The reason for having another Runtime Utilities in additional to Editor Utilities is to avoid Editor-only dependencies in this runtime code.
{
    public readonly struct ScreenshotCaptureResult
    {
        public ScreenshotCaptureResult(string fullPath, string projectRelativePath, int superSize)
            : this(fullPath, projectRelativePath, superSize, isAsync: false, imageBase64: null, imageWidth: 0, imageHeight: 0)
        {
        }

        public ScreenshotCaptureResult(string fullPath, string projectRelativePath, int superSize, bool isAsync)
            : this(fullPath, projectRelativePath, superSize, isAsync, imageBase64: null, imageWidth: 0, imageHeight: 0)
        {
        }

        public ScreenshotCaptureResult(string fullPath, string projectRelativePath, int superSize, bool isAsync,
            string imageBase64, int imageWidth, int imageHeight)
        {
            FullPath = fullPath;
            ProjectRelativePath = projectRelativePath;
            SuperSize = superSize;
            IsAsync = isAsync;
            ImageBase64 = imageBase64;
            ImageWidth = imageWidth;
            ImageHeight = imageHeight;
        }

        public string FullPath { get; }
        /// <summary>Path relative to the Unity project root (e.g. "Captures/foo.png"). Suitable for ScreenCapture.CaptureScreenshot.</summary>
        public string ProjectRelativePath { get; }
        public int SuperSize { get; }
        public bool IsAsync { get; }
        /// <summary>Base64-encoded PNG image data. Only populated when include_image is true.</summary>
        public string ImageBase64 { get; }
        public int ImageWidth { get; }
        public int ImageHeight { get; }
    }

    public static class ScreenshotUtility
    {
        /// <summary>
        /// Built-in default folder for screenshots, project-relative.
        /// Callers can override per-call via the <c>folderOverride</c> parameter on capture methods,
        /// or globally via <c>ScreenshotPreferences</c> in the Editor assembly.
        /// </summary>
        public const string DefaultFolder = "Assets/Screenshots";

        private static Camera FindAvailableCamera()
        {
            var main = Camera.main;
            if (main != null)
            {
                return main;
            }

            try
            {
                var cams = UnityFindObjectsCompat.FindAll<Camera>();
                return cams.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        public static ScreenshotCaptureResult CaptureToProjectFolder(
            string fileName = null,
            int superSize = 1,
            bool ensureUniqueFileName = true,
            string folderOverride = null)
        {
            ScreenshotCaptureResult result = PrepareCaptureResult(fileName, superSize, ensureUniqueFileName, folderOverride, isAsync: true);
            // ScreenCapture.CaptureScreenshot accepts paths relative to the project root.
            ScreenCapture.CaptureScreenshot(result.ProjectRelativePath, result.SuperSize);
            return result;
        }

        /// <summary>
        /// Captures a screenshot from a specific camera by rendering into a temporary RenderTexture (works in Edit Mode).
        /// When <paramref name="includeImage"/> is true, the result includes a base64-encoded PNG (optionally
        /// downscaled so the longest edge is at most <paramref name="maxResolution"/>).
        /// </summary>
        public static ScreenshotCaptureResult CaptureFromCameraToProjectFolder(
            Camera camera,
            string fileName = null,
            int superSize = 1,
            bool ensureUniqueFileName = true,
            bool includeImage = false,
            int maxResolution = 0,
            string folderOverride = null)
        {
            if (camera == null)
            {
                throw new ArgumentNullException(nameof(camera));
            }

            ScreenshotCaptureResult result = PrepareCaptureResult(fileName, superSize, ensureUniqueFileName, folderOverride, isAsync: false);
            int size = result.SuperSize;

            int width = Mathf.Max(1, camera.pixelWidth > 0 ? camera.pixelWidth : Screen.width);
            int height = Mathf.Max(1, camera.pixelHeight > 0 ? camera.pixelHeight : Screen.height);
            width *= size;
            height *= size;

            RenderTexture prevRT = camera.targetTexture;
            RenderTexture prevActive = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            Texture2D tex = null;
            Texture2D downscaled = null;
            string imageBase64 = null;
            int imgW = 0, imgH = 0;
            try
            {
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                byte[] png = tex.EncodeToPNG();
                File.WriteAllBytes(result.FullPath, png);

                if (includeImage)
                {
                    int targetMax = maxResolution > 0 ? maxResolution : 640;
                    if (width > targetMax || height > targetMax)
                    {
                        downscaled = DownscaleTexture(tex, targetMax);
                        byte[] smallPng = downscaled.EncodeToPNG();
                        imageBase64 = System.Convert.ToBase64String(smallPng);
                        imgW = downscaled.width;
                        imgH = downscaled.height;
                    }
                    else
                    {
                        imageBase64 = System.Convert.ToBase64String(png);
                        imgW = width;
                        imgH = height;
                    }
                }
            }
            finally
            {
                camera.targetTexture = prevRT;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                DestroyTexture(tex);
                DestroyTexture(downscaled);
            }

            if (includeImage && imageBase64 != null)
            {
                return new ScreenshotCaptureResult(
                    result.FullPath, result.ProjectRelativePath, result.SuperSize, false,
                    imageBase64, imgW, imgH);
            }
            return result;
        }

#if UNITY_EDITOR
        // Player-loop steps and real-time budget for the synchronous composited capture.
        private const int CompositedStepBudget = 5;
        private const double CompositedRealTimeBudgetSeconds = 5.0;

        // Synchronously drive a WaitForEndOfFrame ScreenshotCapturer by pumping the editor's
        // player loop. Play-mode only; EditorApplication.Step is a no-op in edit mode.
        // When no composited frame arrives within the budget (e.g. the Game view is hidden
        // behind the Scene view), the runner is cancelled immediately and this throws.
        private static Texture2D CaptureCompositedAfterFrame(int superSize)
        {
            ScreenshotCaptureOutcome outcome = null;
            ScreenshotCaptureRequest request = ScreenshotCapturer.Begin(
                superSize, CompositedRealTimeBudgetSeconds, o => outcome = o);
            // Step() pauses play mode as a side effect; restore the prior state so a screenshot
            // doesn't leave a running game paused (an already-paused game stays paused).
            bool wasPaused = UnityEditor.EditorApplication.isPaused;
            double deadline = UnityEditor.EditorApplication.timeSinceStartup + CompositedRealTimeBudgetSeconds;
            try
            {
                for (int i = 0; i < CompositedStepBudget && outcome == null
                    && UnityEditor.EditorApplication.timeSinceStartup < deadline; i++)
                {
                    UnityEditor.EditorApplication.Step();
                }
            }
            catch
            {
                // A step can throw after the callback supplied a texture; no caller will own it.
                DestroyTexture(outcome?.Texture);
                throw;
            }
            finally
            {
                // Budget spent (or Step threw): tear the runner down now so it cannot capture
                // later. Cancel completes synchronously and sets outcome.
                if (outcome == null)
                {
                    request.Cancel(ScreenshotCaptureRequest.ReasonFrameUnavailable,
                        $"No composited frame was produced within {CompositedStepBudget} player-loop steps. "
                        + ScreenshotCaptureRequest.DescribeUnavailableFrame());
                }
                if (!wasPaused)
                    UnityEditor.EditorApplication.isPaused = false;
            }

            if (outcome != null && outcome.Succeeded)
                return outcome.Texture;
            throw new ScreenshotCaptureException(
                outcome?.Reason ?? ScreenshotCaptureRequest.ReasonFrameUnavailable,
                outcome?.Message ?? "Composited capture did not complete.");
        }
#endif

        /// <summary>
        /// Captures a screenshot using ScreenCapture.CaptureScreenshotAsTexture, which captures the
        /// final composited frame including UI Toolkit overlays, post-processing, etc.
        /// In Editor Play Mode the capture waits (bounded) for the end of a rendered frame and throws
        /// <see cref="ScreenshotCaptureException"/> when no composited frame is available (Game view
        /// hidden, paused without a rendered step, batch mode); it never substitutes a camera-only image.
        /// Outside that path, falls back to camera-based capture if ScreenCapture returns null.
        /// </summary>
        public static ScreenshotCaptureResult CaptureComposited(
            string fileName = null,
            int superSize = 1,
            bool ensureUniqueFileName = true,
            bool includeImage = false,
            int maxResolution = 0,
            string folderOverride = null)
        {
            ScreenshotCaptureResult result = PrepareCaptureResult(fileName, superSize, ensureUniqueFileName, folderOverride: folderOverride, isAsync: false);
            Texture2D tex = null;
            Texture2D downscaled = null;
            string imageBase64 = null;
            int imgW = 0, imgH = 0;
            try
            {
#if UNITY_EDITOR
                // In play mode, inline ScreenCapture reads a backbuffer before UITK has
                // composited; route through WaitForEndOfFrame instead. That path throws
                // rather than returning null, so it never reaches the camera fallback.
                tex = Application.isPlaying
                    ? CaptureCompositedAfterFrame(result.SuperSize)
                    : ScreenCapture.CaptureScreenshotAsTexture(result.SuperSize);
#else
                tex = ScreenCapture.CaptureScreenshotAsTexture(result.SuperSize);
#endif
                if (tex == null)
                {
                    // Fallback to camera-based if ScreenCapture fails
                    var cam = FindAvailableCamera();
                    if (cam != null)
                        return CaptureFromCameraToProjectFolder(cam, fileName, superSize, ensureUniqueFileName,
                            includeImage, maxResolution, folderOverride: folderOverride);
                    throw new InvalidOperationException("ScreenCapture.CaptureScreenshotAsTexture returned null and no fallback camera available.");
                }

                int width = tex.width;
                int height = tex.height;

                byte[] png = tex.EncodeToPNG();
                File.WriteAllBytes(result.FullPath, png);

                if (includeImage)
                {
                    int targetMax = maxResolution > 0 ? maxResolution : 640;
                    if (width > targetMax || height > targetMax)
                    {
                        downscaled = DownscaleTexture(tex, targetMax);
                        byte[] smallPng = downscaled.EncodeToPNG();
                        imageBase64 = System.Convert.ToBase64String(smallPng);
                        imgW = downscaled.width;
                        imgH = downscaled.height;
                    }
                    else
                    {
                        imageBase64 = System.Convert.ToBase64String(png);
                        imgW = width;
                        imgH = height;
                    }
                }
            }
            finally
            {
                DestroyTexture(tex);
                DestroyTexture(downscaled);
            }

            if (includeImage && imageBase64 != null)
            {
                return new ScreenshotCaptureResult(
                    result.FullPath, result.ProjectRelativePath, result.SuperSize, false,
                    imageBase64, imgW, imgH);
            }
            return result;
        }

        /// <summary>
        /// Renders a camera to a Texture2D without saving to disk. Used for multi-angle captures.
        /// Returns the base64-encoded PNG, downscaled to fit within <paramref name="maxResolution"/>.
        /// </summary>
        public static (string base64, int width, int height) RenderCameraToBase64(Camera camera, int maxResolution = 640)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));

            int width = Mathf.Max(1, camera.pixelWidth > 0 ? camera.pixelWidth : Screen.width);
            int height = Mathf.Max(1, camera.pixelHeight > 0 ? camera.pixelHeight : Screen.height);

            RenderTexture prevRT = camera.targetTexture;
            RenderTexture prevActive = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            Texture2D tex = null;
            Texture2D downscaled = null;
            try
            {
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                int targetMax = maxResolution > 0 ? maxResolution : 640;
                if (width > targetMax || height > targetMax)
                {
                    downscaled = DownscaleTexture(tex, targetMax);
                    string b64 = System.Convert.ToBase64String(downscaled.EncodeToPNG());
                    return (b64, downscaled.width, downscaled.height);
                }
                else
                {
                    string b64 = System.Convert.ToBase64String(tex.EncodeToPNG());
                    return (b64, width, height);
                }
            }
            finally
            {
                camera.targetTexture = prevRT;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                DestroyTexture(tex);
                DestroyTexture(downscaled);
            }
        }

        /// <summary>
        /// Renders a camera to a Texture2D without saving to disk.
        /// Caller owns the returned texture and must destroy it.
        /// </summary>
        public static Texture2D RenderCameraToTexture(Camera camera, int maxResolution = 640)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));

            int width = Mathf.Max(1, camera.pixelWidth > 0 ? camera.pixelWidth : Screen.width);
            int height = Mathf.Max(1, camera.pixelHeight > 0 ? camera.pixelHeight : Screen.height);

            RenderTexture prevRT = camera.targetTexture;
            RenderTexture prevActive = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            Texture2D tex = null;
            try
            {
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                int targetMax = maxResolution > 0 ? maxResolution : 640;
                if (width > targetMax || height > targetMax)
                {
                    var downscaled = DownscaleTexture(tex, targetMax);
                    DestroyTexture(tex);
                    tex = null;
                    return downscaled;
                }
                var result = tex;
                tex = null; // transfer ownership to caller
                return result;
            }
            finally
            {
                camera.targetTexture = prevRT;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                DestroyTexture(tex);
            }
        }

        /// <summary>
        /// Composites a list of tile textures into a single contact-sheet grid image.
        /// Labels are drawn as white text on a dark banner at the bottom of each tile.
        /// Returns base64 PNG plus dimensions. Destroys all input tile textures.
        /// </summary>
        public static (string base64, int width, int height) ComposeContactSheet(
            List<Texture2D> tiles, List<string> labels, int padding = 4)
        {
            if (tiles == null || tiles.Count == 0)
                throw new ArgumentException("No tiles to compose.", nameof(tiles));

            int tileW = tiles[0].width;
            int tileH = tiles[0].height;
            int count = tiles.Count;

            // Calculate grid: prefer wider than tall (cols >= rows)
            int cols = Mathf.CeilToInt(Mathf.Sqrt(count));
            int rows = Mathf.CeilToInt((float)count / cols);

            int labelHeight = Mathf.Max(14, tileH / 12);
            int cellW = tileW + padding;
            int cellH = tileH + labelHeight + padding;

            int sheetW = cols * cellW + padding;
            int sheetH = rows * cellH + padding;

            Texture2D sheet = null;
            try
            {
                sheet = new Texture2D(sheetW, sheetH, TextureFormat.RGBA32, false);

                // Build the full sheet in a Color32[] buffer, then upload once
                var bgColor = new Color32(30, 30, 30, 255);
                Color32[] sheetPixels = new Color32[sheetW * sheetH];
                for (int i = 0; i < sheetPixels.Length; i++) sheetPixels[i] = bgColor;

                // Track label draw requests so we can apply them after the bulk upload
                var labelDraws = new List<(string text, int x, int y, int h)>();

                for (int idx = 0; idx < count; idx++)
                {
                    int col = idx % cols;
                    int row = idx / cols;
                    // Place tiles top-left to bottom-right (Unity Texture2D y=0 is bottom)
                    int x = padding + col * cellW;
                    int y = sheetH - padding - (row + 1) * cellH + padding;

                    // Copy tile pixels row-by-row using bulk operations
                    Color32[] tilePixels = tiles[idx].GetPixels32();
                    for (int ty = 0; ty < tileH; ty++)
                    {
                        int srcOffset = ty * tileW;
                        int dstOffset = (y + labelHeight + ty) * sheetW + x;
                        System.Array.Copy(tilePixels, srcOffset, sheetPixels, dstOffset, tileW);
                    }

                    // Draw label banner (dark background strip below tile)
                    var bannerColor = new Color32(20, 20, 20, 220);
                    for (int ly = 0; ly < labelHeight; ly++)
                    {
                        int dstOffset = (y + ly) * sheetW + x;
                        for (int lx = 0; lx < tileW; lx++)
                        {
                            sheetPixels[dstOffset + lx] = bannerColor;
                        }
                    }

                    // Queue label text drawing (applied after bulk pixel upload)
                    if (labels != null && idx < labels.Count && !string.IsNullOrEmpty(labels[idx]))
                    {
                        labelDraws.Add((labels[idx], x + 3, y + 2, labelHeight - 4));
                    }
                }

                // Upload all tile + banner pixels in one SetPixels32 call
                sheet.SetPixels32(sheetPixels);

                // Draw label text on top (small glyph-based writes, negligible cost)
                foreach (var (text, lx, ly, lh) in labelDraws)
                {
                    DrawText(sheet, text, lx, ly, lh, Color.white);
                }

                sheet.Apply();

                byte[] png = sheet.EncodeToPNG();
                string b64 = System.Convert.ToBase64String(png);
                return (b64, sheetW, sheetH);
            }
            finally
            {
                foreach (var tile in tiles) DestroyTexture(tile);
                DestroyTexture(sheet);
            }
        }

        private static void DrawText(Texture2D tex, string text, int startX, int startY, int charHeight, Color color)
        {
            // Simple 5x7 bitmap font for basic ASCII characters
            int charWidth = Mathf.Max(4, charHeight * 5 / 7);
            int spacing = Mathf.Max(1, charWidth / 5);
            int x = startX;

            foreach (char c in text)
            {
                if (x + charWidth > tex.width) break;
                ulong glyph = GetGlyph(c);
                if (glyph != 0)
                {
                    for (int row = 0; row < 7; row++)
                    {
                        for (int col = 0; col < 5; col++)
                        {
                            bool on = ((glyph >> ((6 - row) * 5 + (4 - col))) & 1) == 1;
                            if (!on) continue;
                            // Scale the 5x7 glyph to charWidth x charHeight
                            int px0 = x + col * charWidth / 5;
                            int px1 = x + (col + 1) * charWidth / 5;
                            int py0 = startY + (6 - row) * charHeight / 7;
                            int py1 = startY + (7 - row) * charHeight / 7;
                            for (int py = py0; py < py1 && py < tex.height; py++)
                                for (int px = px0; px < px1 && px < tex.width; px++)
                                    tex.SetPixel(px, py, color);
                        }
                    }
                }
                x += charWidth + spacing;
            }
        }

        private static ulong GetGlyph(char c)
        {
            // 5x7 pixel font stored as 35-bit values (row0=bits34-30 ... row6=bits4-0)
            // Each row is 5 wide, MSB=left. Row 0 is top.
            switch (char.ToUpperInvariant(c))
            {
                case 'A': return 0b01110_10001_10001_11111_10001_10001_10001UL;
                case 'B': return 0b11110_10001_10001_11110_10001_10001_11110UL;
                case 'C': return 0b01110_10001_10000_10000_10000_10001_01110UL;
                case 'D': return 0b11100_10010_10001_10001_10001_10010_11100UL;
                case 'E': return 0b11111_10000_10000_11110_10000_10000_11111UL;
                case 'F': return 0b11111_10000_10000_11110_10000_10000_10000UL;
                case 'G': return 0b01110_10001_10000_10111_10001_10001_01110UL;
                case 'H': return 0b10001_10001_10001_11111_10001_10001_10001UL;
                case 'I': return 0b01110_00100_00100_00100_00100_00100_01110UL;
                case 'K': return 0b10001_10010_10100_11000_10100_10010_10001UL;
                case 'L': return 0b10000_10000_10000_10000_10000_10000_11111UL;
                case 'M': return 0b10001_11011_10101_10101_10001_10001_10001UL;
                case 'N': return 0b10001_11001_10101_10011_10001_10001_10001UL;
                case 'O': return 0b01110_10001_10001_10001_10001_10001_01110UL;
                case 'R': return 0b11110_10001_10001_11110_10100_10010_10001UL;
                case 'S': return 0b01110_10001_10000_01110_00001_10001_01110UL;
                case 'T': return 0b11111_00100_00100_00100_00100_00100_00100UL;
                case 'U': return 0b10001_10001_10001_10001_10001_10001_01110UL;
                case 'V': return 0b10001_10001_10001_10001_01010_01010_00100UL;
                case 'W': return 0b10001_10001_10001_10101_10101_11011_10001UL;
                case 'Y': return 0b10001_10001_01010_00100_00100_00100_00100UL;
                case '0': return 0b01110_10011_10101_10101_10101_11001_01110UL;
                case '1': return 0b00100_01100_00100_00100_00100_00100_01110UL;
                case '2': return 0b01110_10001_00001_00010_00100_01000_11111UL;
                case '3': return 0b01110_10001_00001_00110_00001_10001_01110UL;
                case '4': return 0b00010_00110_01010_10010_11111_00010_00010UL;
                case '5': return 0b11111_10000_11110_00001_00001_10001_01110UL;
                case '6': return 0b01110_10001_10000_11110_10001_10001_01110UL;
                case '7': return 0b11111_00001_00010_00100_01000_01000_01000UL;
                case '8': return 0b01110_10001_10001_01110_10001_10001_01110UL;
                case '9': return 0b01110_10001_10001_01111_00001_10001_01110UL;
                case 'J': return 0b00111_00010_00010_00010_00010_10010_01100UL;
                case 'P': return 0b11110_10001_10001_11110_10000_10000_10000UL;
                case 'Q': return 0b01110_10001_10001_10001_10101_10010_01101UL;
                case 'X': return 0b10001_01010_00100_00100_00100_01010_10001UL;
                case 'Z': return 0b11111_00001_00010_00100_01000_10000_11111UL;
                case '-': return 0b00000_00000_00000_11111_00000_00000_00000UL;
                case '_': return 0b00000_00000_00000_00000_00000_00000_11111UL;
                case ' ': return 0UL;
                case '+': return 0b00000_00100_00100_11111_00100_00100_00000UL;
                default:  return 0UL;
            }
        }

        /// <summary>
        /// Downscales a Texture2D so that its longest edge is at most <paramref name="maxEdge"/> pixels.
        /// Uses bilinear filtering via a temporary RenderTexture blit.
        /// Caller must destroy the returned Texture2D.
        /// </summary>
        public static Texture2D DownscaleTexture(Texture2D source, int maxEdge)
        {
            if (source == null)
                throw new System.ArgumentNullException(nameof(source));
            if (maxEdge <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(maxEdge), maxEdge, "maxEdge must be > 0.");

            int srcW = source.width;
            int srcH = source.height;
            float scale = Mathf.Min((float)maxEdge / srcW, (float)maxEdge / srcH);
            scale = Mathf.Min(scale, 1f); // never upscale
            int dstW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
            int dstH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));

            // Match the temporary RT's read/write mode to the source texture rather than the
            // project default. In a Linear-colorspace project a Default (sRGB) RT applies an
            // sRGB encode on store while the blit samples a linear-flagged capture without a
            // matching decode, which washes the image out (see issue #1328).
            bool srcIsSrgb = GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat);
            RenderTextureReadWrite readWrite = srcIsSrgb
                ? RenderTextureReadWrite.sRGB
                : RenderTextureReadWrite.Linear;

            RenderTexture prevActive = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(dstW, dstH, 0, RenderTextureFormat.ARGB32, readWrite);
            rt.filterMode = FilterMode.Bilinear;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var dst = new Texture2D(dstW, dstH, TextureFormat.RGBA32, false, linear: !srcIsSrgb);
                dst.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0);
                dst.Apply();
                return dst;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static void DestroyTexture(Texture2D tex)
        {
            if (tex == null) return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(tex);
            else
                UnityEngine.Object.DestroyImmediate(tex);
        }

        private static ScreenshotCaptureResult PrepareCaptureResult(string fileName, int superSize, bool ensureUniqueFileName, string folderOverride, bool isAsync)
        {
            int size = Mathf.Max(1, superSize);
            string resolvedName = BuildFileName(fileName);
            string folderAbsolute = ResolveFolderAbsolute(folderOverride);
            Directory.CreateDirectory(folderAbsolute);

            string fullPath = Path.Combine(folderAbsolute, resolvedName);
            if (ensureUniqueFileName)
            {
                fullPath = EnsureUnique(fullPath);
            }

            string normalizedFullPath = fullPath.Replace('\\', '/');
            string projectRelativePath = ToProjectRelativePath(normalizedFullPath);

            return new ScreenshotCaptureResult(normalizedFullPath, projectRelativePath, size, isAsync);
        }

        /// <summary>
        /// Resolves a folder spec (which may be project-relative like "Assets/Screenshots",
        /// absolute, or null/empty) to an absolute filesystem path inside the project root.
        /// Throws on traversal escape or absolute paths outside the project.
        /// </summary>
        public static string ResolveFolderAbsolute(string folderOverride)
        {
            string projectRoot = GetProjectRootPath().TrimEnd('/');
            string requested = string.IsNullOrWhiteSpace(folderOverride) ? DefaultFolder : folderOverride.Trim();
            requested = requested.Replace('\\', '/').TrimEnd('/');

            string combined = Path.IsPathRooted(requested)
                ? requested
                : Path.Combine(projectRoot, requested);

            string fullFolder = Path.GetFullPath(combined).Replace('\\', '/').TrimEnd('/');
            string normalizedRoot = projectRoot;

            // Reject paths that escape the project root (case-insensitive on Windows, exact elsewhere).
            var rootComparison = Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!fullFolder.Equals(normalizedRoot, rootComparison) &&
                !fullFolder.StartsWith(normalizedRoot + "/", rootComparison))
            {
                throw new InvalidOperationException(
                    $"Screenshot folder '{folderOverride}' resolves outside the Unity project root ('{fullFolder}'). " +
                    $"Use a project-relative path (e.g. 'Assets/Screenshots' or 'Captures').");
            }

            return fullFolder;
        }

        /// <summary>
        /// Converts an absolute filesystem path inside the project to a project-relative path
        /// (forward slashes, no leading separator). Returns the input unchanged when it does
        /// not live under the project root.
        /// </summary>
        public static string ToProjectRelativePath(string normalizedFullPath)
        {
            if (string.IsNullOrEmpty(normalizedFullPath)) return normalizedFullPath;
            string projectRoot = GetProjectRootPath();
            string normalized = normalizedFullPath.Replace('\\', '/');
            if (normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return normalized.Substring(projectRoot.Length).TrimStart('/');
            }
            return normalized;
        }

        /// <summary>
        /// True when <paramref name="projectRelativePath"/> lives under the Unity Assets/ folder
        /// (and therefore should be imported via AssetDatabase).
        /// </summary>
        public static bool IsUnderAssets(string projectRelativePath)
        {
            if (string.IsNullOrEmpty(projectRelativePath)) return false;
            string norm = projectRelativePath.Replace('\\', '/').TrimStart('/');
            return norm.Equals("Assets", StringComparison.OrdinalIgnoreCase)
                || norm.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildFileName(string fileName)
        {
            string name = string.IsNullOrWhiteSpace(fileName)
                ? $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}"
                : fileName.Trim();

            name = SanitizeFileName(name);

            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                name += ".png";
            }

            return name;
        }

        private static string SanitizeFileName(string fileName)
        {
            // GetInvalidFileNameChars() doesn't include '\' or '/' on Unix, so a caller-supplied
            // name like "foo\bar" would survive and later get spliced into the directory portion.
            var invalidChars = Path.GetInvalidFileNameChars();
            string cleaned = new string(
                fileName.Select(ch => invalidChars.Contains(ch) || ch == '/' || ch == '\\' ? '_' : ch).ToArray());

            return string.IsNullOrWhiteSpace(cleaned) ? "screenshot" : cleaned;
        }

        private static string EnsureUnique(string path)
        {
            if (!File.Exists(path))
            {
                return path;
            }

            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            int counter = 1;

            string candidate;
            do
            {
                candidate = Path.Combine(directory, $"{baseName}-{counter}{extension}");
                counter++;
            } while (File.Exists(candidate));

            return candidate;
        }

        private static string GetProjectRootPath()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            root = root.Replace('\\', '/');
            if (!root.EndsWith("/", StringComparison.Ordinal))
            {
                root += "/";
            }
            return root;
        }
    }


    /// <summary>Terminal state of a <see cref="ScreenshotCaptureRequest"/>.</summary>
    public enum ScreenshotCaptureStatus
    {
        Pending,
        Succeeded,
        Failed,
        TimedOut,
        Cancelled,
    }

    /// <summary>
    /// Result delivered exactly once to a composited-capture callback. On success the receiver
    /// owns <see cref="Texture"/> and must destroy it; on any other status Texture is null.
    /// </summary>
    public sealed class ScreenshotCaptureOutcome
    {
        internal ScreenshotCaptureOutcome(ScreenshotCaptureStatus status, Texture2D texture, string reason, string message)
        {
            Status = status;
            Texture = texture;
            Reason = reason;
            Message = message;
        }

        public ScreenshotCaptureStatus Status { get; }
        public Texture2D Texture { get; }
        /// <summary>Machine-readable failure code (null on success).</summary>
        public string Reason { get; }
        /// <summary>Human-readable, actionable failure description (null on success).</summary>
        public string Message { get; }
        public bool Succeeded => Status == ScreenshotCaptureStatus.Succeeded && Texture != null;
    }

    /// <summary>Thrown when a composited (UI-including) frame could not be captured.</summary>
    public sealed class ScreenshotCaptureException : InvalidOperationException
    {
        public ScreenshotCaptureException(string reason, string message) : base(message)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    /// <summary>
    /// Handle for one in-flight end-of-frame composited capture. Owns the hidden runner
    /// GameObject and guarantees: the callback runs exactly once; the runner is destroyed on
    /// every terminal path; a texture produced after the request ended is destroyed instead of
    /// being delivered. The deadline uses the real/editor clock (not Time.timeScale or Game view
    /// redraws), so a Game view hidden behind the Scene view ends in a timeout, not a hang.
    /// </summary>
    public sealed class ScreenshotCaptureRequest
    {
        public const double DefaultTimeoutSeconds = 10.0;

        public const string ReasonTimeout = "capture_timeout";
        public const string ReasonFrameUnavailable = "composited_frame_unavailable";
        public const string ReasonNotPlaying = "not_in_play_mode";
        public const string ReasonBatchMode = "batch_mode";
        public const string ReasonPlayModeExited = "play_mode_exited";
        public const string ReasonDomainReload = "domain_reload";
        public const string ReasonRunnerDestroyed = "runner_destroyed";
        public const string ReasonCaptureFailed = "capture_failed";
        public const string ReasonCancelled = "cancelled";

        private static readonly List<ScreenshotCaptureRequest> s_active = new List<ScreenshotCaptureRequest>();
#if UNITY_EDITOR
        private static bool s_editorHooksInstalled;
#endif

        private readonly Action<ScreenshotCaptureOutcome> _onComplete;
        private readonly double _deadline;
        private GameObject _runnerGo;

        private ScreenshotCaptureRequest(int superSize, double timeoutSeconds, Action<ScreenshotCaptureOutcome> onComplete)
        {
            SuperSize = Mathf.Max(1, superSize);
            TimeoutSeconds = timeoutSeconds > 0 && !double.IsInfinity(timeoutSeconds)
                ? Math.Min(timeoutSeconds, 300.0) : DefaultTimeoutSeconds;
            _onComplete = onComplete;
            _deadline = Now() + TimeoutSeconds;
        }

        public int SuperSize { get; }
        public double TimeoutSeconds { get; }
        public ScreenshotCaptureStatus Status { get; private set; } = ScreenshotCaptureStatus.Pending;
        public bool IsCompleted => Status != ScreenshotCaptureStatus.Pending;

        internal static ScreenshotCaptureRequest Start(int superSize, double timeoutSeconds, Action<ScreenshotCaptureOutcome> onComplete)
        {
            var request = new ScreenshotCaptureRequest(superSize, timeoutSeconds, onComplete);

            // WaitForEndOfFrame is never invoked in batch mode, and runner coroutines do not run
            // outside Play Mode: fail now instead of queueing a wait that cannot finish.
            if (Application.isBatchMode)
            {
                request.Complete(ScreenshotCaptureStatus.Failed, null, ReasonBatchMode,
                    "Composited screen capture is unavailable in batch mode (WaitForEndOfFrame is never invoked). "
                    + "Use a camera-based capture instead.", destroyImmediate: false);
                return request;
            }
            if (!Application.isPlaying)
            {
                request.Complete(ScreenshotCaptureStatus.Failed, null, ReasonNotPlaying,
                    "End-of-frame composited capture requires Play Mode.", destroyImmediate: false);
                return request;
            }

            // Make the Game view visible once so the frame can actually be composited. This is
            // deliberately not repeated while waiting: if the user switches tabs afterwards the
            // request times out instead of stealing focus again.
            ScreenshotCapturer.PrepareGameViewForCapture();

            s_active.Add(request);
#if UNITY_EDITOR
            InstallEditorHooks();
#endif
            try
            {
                var go = new GameObject("__MCP_ScreenshotCapturer__") { hideFlags = HideFlags.HideAndDontSave };
                request._runnerGo = go;
                var runner = go.AddComponent<ScreenshotCapturer>();
                runner.Request = request;
            }
            catch (Exception ex)
            {
                request.Complete(ScreenshotCaptureStatus.Failed, null, ReasonCaptureFailed,
                    $"Could not start the screen capture runner: {ex.Message}", destroyImmediate: true);
            }
            return request;
        }

        /// <summary>Cancels a pending request; the callback runs synchronously with Cancelled status. No-op once completed.</summary>
        public void Cancel(string reason = ReasonCancelled, string message = null)
        {
            Complete(ScreenshotCaptureStatus.Cancelled, null, reason ?? ReasonCancelled,
                message ?? "The screen capture was cancelled.", destroyImmediate: true);
        }

        /// <summary>Explains why no composited frame arrived, based on the current editor state.</summary>
        public static string DescribeUnavailableFrame()
        {
            if (Application.isBatchMode)
                return "Unity is running in batch mode, where end-of-frame callbacks are never invoked.";
#if UNITY_EDITOR
            if (UnityEditor.EditorApplication.isPaused)
                return "Play Mode is paused, so no frame is rendered. Unpause Play Mode and retry.";
            if (!ScreenshotCapturer.IsGameViewOpen())
                return "No Game view is open (the 'MCP Game' view may have been closed). Retry to reopen it, or open a Game view.";
            if (!ScreenshotCapturer.IsAnyGameViewVisible())
                return "No Game view is visible: it is hidden behind another tab (such as the Scene view or the Inspector). "
                    + "Select a Game view or the 'MCP Game' tab so it is shown, then retry; "
                    + "or use a camera-based screenshot if UI overlays are not needed.";
            return "A Game view is open but did not render a frame (the Editor may be in the background, throttled or minimized). "
                + "Bring the Editor forward and retry, or use a camera-based screenshot if UI overlays are not needed.";
#else
            return "The player did not render a frame (the application may be unfocused or not rendering).";
#endif
        }

        internal void OnEndOfFrame()
        {
            if (IsCompleted) return;
            if (Now() >= _deadline)
            {
                TimeOut(destroyImmediate: false);
                return;
            }

            Texture2D tex = null;
            try
            {
                tex = ScreenCapture.CaptureScreenshotAsTexture(SuperSize);
            }
            catch (Exception ex)
            {
                Complete(ScreenshotCaptureStatus.Failed, null, ReasonCaptureFailed,
                    $"ScreenCapture.CaptureScreenshotAsTexture failed: {ex.Message}", destroyImmediate: false);
                return;
            }

            if (tex == null)
            {
                Complete(ScreenshotCaptureStatus.Failed, null, ReasonCaptureFailed,
                    "ScreenCapture.CaptureScreenshotAsTexture returned no image.", destroyImmediate: false);
                return;
            }
            // Called from the runner's own coroutine: defer the GameObject destroy.
            Complete(ScreenshotCaptureStatus.Succeeded, tex, null, null, destroyImmediate: false);
        }

        // fromRunner: called inside the runner's own Update, where the destroy must be deferred.
        internal void Tick(bool fromRunner)
        {
            if (!IsCompleted && Now() >= _deadline)
                TimeOut(destroyImmediate: !fromRunner);
        }

        internal void OnRunnerDestroyed()
        {
            _runnerGo = null;
            Complete(ScreenshotCaptureStatus.Failed, null, ReasonRunnerDestroyed,
                "The screen capture runner was destroyed before a frame was captured (scene change, Play Mode exit or external cleanup).",
                destroyImmediate: false);
        }

        private void TimeOut(bool destroyImmediate)
        {
            Complete(ScreenshotCaptureStatus.TimedOut, null, ReasonTimeout,
                $"No composited frame was rendered within {TimeoutSeconds:0.#} s. " + DescribeUnavailableFrame(),
                destroyImmediate);
        }

        private void Complete(ScreenshotCaptureStatus status, Texture2D tex, string reason, string message, bool destroyImmediate)
        {
            if (IsCompleted)
            {
                // A late capture for a request that already ended must not be delivered or leaked.
                DestroyObject(tex, immediate: destroyImmediate);
                return;
            }

            Status = status;
            s_active.Remove(this);
            DestroyRunner(destroyImmediate);

            var outcome = new ScreenshotCaptureOutcome(status, status == ScreenshotCaptureStatus.Succeeded ? tex : null, reason, message);
            if (_onComplete == null)
            {
                DestroyObject(outcome.Texture, immediate: destroyImmediate);
                return;
            }
            try
            {
                _onComplete(outcome);
            }
            catch (Exception ex)
            {
                DestroyObject(outcome.Texture, immediate: destroyImmediate);
                Debug.LogError($"[MCP for Unity] Screenshot capture callback threw: {ex}");
            }
        }

        private void DestroyRunner(bool immediate)
        {
            var go = _runnerGo;
            _runnerGo = null;
            if (go == null) return;

            var runner = go.GetComponent<ScreenshotCapturer>();
            if (runner != null) runner.Request = null;
            DestroyObject(go, immediate);
        }

        private static void DestroyObject(UnityEngine.Object obj, bool immediate)
        {
            if (obj == null) return;
            if (immediate || !Application.isPlaying)
                UnityEngine.Object.DestroyImmediate(obj);
            else
                UnityEngine.Object.Destroy(obj);
        }

        private static double Now()
        {
#if UNITY_EDITOR
            return UnityEditor.EditorApplication.timeSinceStartup;
#else
            return Time.realtimeSinceStartup;
#endif
        }

        private static void CancelAll(string reason, string message)
        {
            foreach (var request in s_active.ToArray())
                request.Cancel(reason, message);
        }

#if UNITY_EDITOR
        // Editor update keeps ticking while Play Mode is paused or the Game view is hidden, unlike
        // the player loop, so deadlines are enforced here rather than by the runner.
        private static void InstallEditorHooks()
        {
            if (s_editorHooksInstalled) return;
            s_editorHooksInstalled = true;
            UnityEditor.EditorApplication.update += EditorTick;
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void EditorTick()
        {
            if (s_active.Count == 0) return;
            foreach (var request in s_active.ToArray())
                request.Tick(fromRunner: false);
        }

        private static void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange change)
        {
            if (change == UnityEditor.PlayModeStateChange.ExitingPlayMode
                || change == UnityEditor.PlayModeStateChange.EnteredEditMode)
            {
                CancelAll(ReasonPlayModeExited, "Play Mode exited before a composited frame was captured.");
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            CancelAll(ReasonDomainReload, "A domain reload interrupted the screen capture.");
        }
#endif
    }

    /// <summary>
    /// Transient hidden runner that yields WaitForEndOfFrame and captures the composited frame
    /// via ScreenCapture.CaptureScreenshotAsTexture. Its lifetime is owned by a
    /// <see cref="ScreenshotCaptureRequest"/>, which bounds the wait and tears it down.
    /// </summary>
    public sealed class ScreenshotCapturer : MonoBehaviour
    {
        // Not serialized: after a domain reload this is null and the orphaned runner removes itself.
        internal ScreenshotCaptureRequest Request;

        /// <summary>
        /// Legacy entry point. The callback runs exactly once: with the texture on success
        /// (caller owns it) or with null on timeout/failure/cancellation.
        /// </summary>
        public static void Begin(int superSize, Action<Texture2D> onComplete)
        {
            Begin(superSize, ScreenshotCaptureRequest.DefaultTimeoutSeconds, outcome =>
            {
                if (onComplete != null)
                    onComplete(outcome.Texture);
                else if (outcome.Texture != null)
                    Destroy(outcome.Texture);
            });
        }

        /// <summary>
        /// Starts a bounded end-of-frame composited capture. The callback runs exactly once, possibly
        /// synchronously before this returns (e.g. batch mode or not in Play Mode).
        /// </summary>
        public static ScreenshotCaptureRequest Begin(int superSize, double timeoutSeconds, Action<ScreenshotCaptureOutcome> onComplete)
        {
            return ScreenshotCaptureRequest.Start(superSize, timeoutSeconds, onComplete);
        }

        /// <summary>
        /// Editor only, called once per capture start: makes sure some Game view is rendering without
        /// covering the Scene view the user is looking at. Reuses a Game view that is already visible;
        /// otherwise reuses or creates a dedicated "MCP Game" view docked beside the Inspector/Project
        /// window when that dock is separate from the Scene view, or floating as a fallback. The
        /// previously focused window gets focus back. Never selects a Game tab inside the Scene view's
        /// dock. No-op in players and batch mode.
        /// </summary>
        public static void PrepareGameViewForCapture()
        {
#if UNITY_EDITOR
            if (Application.isBatchMode) return;
            try
            {
                EditorGameViewTarget.Prepare();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MCP for Unity] screenshot: could not prepare a Game view: {e.Message}");
            }
            try { UnityEditor.EditorApplication.QueuePlayerLoopUpdate(); }
            catch { }
#endif
        }

#if UNITY_EDITOR
        internal static bool IsGameViewOpen()
        {
            var type = EditorGameViewTarget.GameViewType;
            if (type == null) return true;
            try { return Resources.FindObjectsOfTypeAll(type).Length > 0; }
            catch { return true; }
        }

        /// <summary>True when a Game view is known to be visible (floating, or the selected tab of its dock).</summary>
        internal static bool IsAnyGameViewVisible()
        {
            return EditorGameViewTarget.FindVisibleGameView() != null;
        }

        private static class EditorGameViewTarget
        {
            internal const string DedicatedTitle = "MCP Game";
            private const string DedicatedSessionKey = "MCPForUnity.Screenshot.DedicatedGameViewId";
            private const float DedicatedWidth = 960f;
            private const float DedicatedHeight = 540f;

            private static UnityEditor.EditorWindow s_dedicated;

            // GameView, InspectorWindow and ProjectBrowser are internal editor types; every use is
            // guarded and degrades to a floating window.
            internal static readonly Type GameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");

            private static readonly System.Reflection.FieldInfo ParentField =
                typeof(UnityEditor.EditorWindow).GetField("m_Parent",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            internal static void Prepare()
            {
                if (GameViewType == null) return;

                var visible = FindVisibleGameView();
                if (visible != null)
                {
                    visible.Repaint();
                    return;
                }

                var dedicated = FindDedicated();
                var previousFocus = UnityEditor.EditorWindow.focusedWindow;

                if (dedicated != null)
                {
                    // Bring it forward only when restoring focus cannot hide it again and
                    // selecting it will not cover any Scene view.
                    bool coversScene = WouldHideWorkspace(dedicated, previousFocus);
                    if (!coversScene)
                    {
                        dedicated.Focus();
                        RestoreFocus(previousFocus, dedicated);
                    }
                    dedicated.Repaint();
                    return;
                }

                dedicated = CreateDocked(previousFocus) ?? CreateFloating();
                if (dedicated == null) return;
                Remember(dedicated);
                RestoreFocus(previousFocus, dedicated);
                dedicated.Repaint();
            }

            internal static UnityEditor.EditorWindow FindVisibleGameView()
            {
                if (GameViewType == null) return null;
                foreach (var obj in Resources.FindObjectsOfTypeAll(GameViewType))
                {
                    if (obj is UnityEditor.EditorWindow window && IsVisible(window))
                        return window;
                }
                return null;
            }

            private static UnityEditor.EditorWindow FindDedicated()
            {
                if (s_dedicated != null) return s_dedicated;
                // After a domain reload the static is gone (and GameView may reset its title), so
                // find the window by the instance id kept in SessionState.
                int id = UnityEditor.SessionState.GetInt(DedicatedSessionKey, 0);
                if (id == 0) return null;
                foreach (var obj in Resources.FindObjectsOfTypeAll(GameViewType))
                {
                    if (obj is UnityEditor.EditorWindow window && window.GetInstanceIDCompat() == id)
                    {
                        s_dedicated = window;
                        if (window.titleContent == null || window.titleContent.text != DedicatedTitle)
                            window.titleContent = new GUIContent(DedicatedTitle);
                        return window;
                    }
                }
                UnityEditor.SessionState.EraseInt(DedicatedSessionKey);
                return null;
            }

            private static void Remember(UnityEditor.EditorWindow window)
            {
                s_dedicated = window;
                UnityEditor.SessionState.SetInt(DedicatedSessionKey, window.GetInstanceIDCompat());
            }

            private static UnityEditor.EditorWindow CreateDocked(UnityEditor.EditorWindow previousFocus)
            {
                var create = FindCreateWindowMethod();
                if (create == null) return null;

                foreach (var typeName in new[] { "UnityEditor.InspectorWindow,UnityEditor", "UnityEditor.ProjectBrowser,UnityEditor" })
                {
                    var dockType = Type.GetType(typeName);
                    if (dockType == null) continue;
                    var anchors = Resources.FindObjectsOfTypeAll(dockType);
                    if (anchors.Length == 0) continue;
                    // CreateWindow docks beside any window of that type, so every instance must
                    // be provably outside both Scene view docks and the currently focused dock.
                    bool safe = true;
                    foreach (var anchor in anchors)
                    {
                        var anchorWindow = anchor as UnityEditor.EditorWindow;
                        if (anchorWindow == null || !anchorWindow.docked
                            || WouldHideWorkspace(anchorWindow, previousFocus))
                        {
                            safe = false;
                            break;
                        }
                    }
                    if (!safe) continue;

                    UnityEditor.EditorWindow created = null;
                    try
                    {
                        created = create.MakeGenericMethod(GameViewType)
                            .Invoke(null, new object[] { DedicatedTitle, new[] { dockType } }) as UnityEditor.EditorWindow;
                    }
                    catch
                    {
                        created = null;
                    }
                    if (created == null) continue;

                    if (WouldHideWorkspace(created, previousFocus))
                    {
                        // Landed on top of the Scene view after all: undo and fall back to floating.
                        created.Close();
                        return null;
                    }
                    return created;
                }
                return null;
            }

            private static bool WouldHideWorkspace(UnityEditor.EditorWindow window, UnityEditor.EditorWindow previousFocus)
            {
                // Restoring focus to another tab in this dock would immediately hide the Game view.
                if (previousFocus != null && previousFocus != window && SharesDock(window, previousFocus) != false)
                    return true;
                foreach (var scene in Resources.FindObjectsOfTypeAll<UnityEditor.SceneView>())
                    if (SharesDock(window, scene) != false) return true;
                return false;
            }

            private static UnityEditor.EditorWindow CreateFloating()
            {
                var window = ScriptableObject.CreateInstance(GameViewType) as UnityEditor.EditorWindow;
                if (window == null) return null;
                window.titleContent = new GUIContent(DedicatedTitle);
                window.Show();
                var pos = window.position;
                window.position = new Rect(pos.x, pos.y, DedicatedWidth, DedicatedHeight);
                return window;
            }

            private static System.Reflection.MethodInfo FindCreateWindowMethod()
            {
                // public static T EditorWindow.CreateWindow<T>(string title, params Type[] desiredDockNextTo)
                foreach (var method in typeof(UnityEditor.EditorWindow).GetMethods(
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (method.Name != "CreateWindow" || !method.IsGenericMethodDefinition) continue;
                    var ps = method.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(Type[]))
                        return method;
                }
                return null;
            }

            private static void RestoreFocus(UnityEditor.EditorWindow previous, UnityEditor.EditorWindow created)
            {
                if (previous == null || previous == created) return;
                try { previous.Focus(); }
                catch { }
            }

            private static bool IsVisible(UnityEditor.EditorWindow window)
            {
                if (!window.docked) return true;
                return IsSelectedTab(window) == true;
            }

            // null = unknown (internal layout API unavailable).
            private static bool? IsSelectedTab(UnityEditor.EditorWindow window)
            {
                var parent = GetParent(window);
                if (parent == null) return null;
                try
                {
                    var prop = parent.GetType().GetProperty("actualView",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic);
                    if (prop == null) return null;
                    return ReferenceEquals(prop.GetValue(parent), window);
                }
                catch
                {
                    return null;
                }
            }

            // null = unknown; callers treat unknown as "shares" to protect the Scene view.
            private static bool? SharesDock(UnityEditor.EditorWindow a, UnityEditor.EditorWindow b)
            {
                if (!a.docked || !b.docked) return false;
                var pa = GetParent(a);
                var pb = GetParent(b);
                if (pa == null || pb == null) return null;
                return ReferenceEquals(pa, pb);
            }

            private static object GetParent(UnityEditor.EditorWindow window)
            {
                if (ParentField == null || window == null) return null;
                try { return ParentField.GetValue(window); }
                catch { return null; }
            }
        }
#endif

        private System.Collections.IEnumerator Start()
        {
            if (Request == null)
            {
                Destroy(gameObject);
                yield break;
            }

            yield return new WaitForEndOfFrame();

            // Null/completed means the request already ended and tore this runner down.
            var request = Request;
            if (request == null || request.IsCompleted) yield break;
            request.OnEndOfFrame();
        }

        private void Update()
        {
            var request = Request;
            if (request == null)
            {
                Destroy(gameObject);
                return;
            }
            request.Tick(fromRunner: true);
        }

        private void OnDestroy()
        {
            var request = Request;
            Request = null;
            request?.OnRunnerDestroyed();
        }
    }
}
