using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace DpsInServerBar.UI;

/// <summary>
/// Pictures of the plugin's windows, saved as PNG in Pictures\Raid Companion. A window taller than the screen is
/// captured in slices (moved up between them) put back together.
/// </summary>
public static class WindowScreenshot
{
    /// <summary>A capture of the whole game screen, with where the window was on it (from the screen's top left).</summary>
    public sealed record Slice(IDalamudTextureWrap Screen, Vector2 WindowPos);

    // Chat link opening the screenshots folder (an id of its own, away from the pull links).
    private const uint FolderLinkId = 0x5C5C0000;
    private const int B8G8R8A8 = 87; // DXGI_FORMAT_B8G8R8A8_UNORM
    private static DalamudLinkPayload? folderLink;

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Raid Companion");

    /// <summary>The game screen with the windows drawn on it.</summary>
    public static Task<IDalamudTextureWrap> CaptureScreen(uint viewportId) =>
        Plugin.TextureProvider.CreateFromImGuiViewportAsync(new ImGuiViewportTextureArgs { ViewportId = viewportId },
                                                            "Raid Companion screenshot");

    /// <summary>Cuts the window out of each slice, puts the pieces together and saves the picture. Returns the file.</summary>
    /// <param name="rounding">Corner radius of the window (ImGui units): the corners outside it are left transparent.</param>
    public static async Task<string> StitchAndSaveAsync(List<Slice> slices, Vector2 windowSize, Vector2 screenSize, float rounding, string view)
    {
        try
        {
            byte[]? picture = null;
            int width = 0, height = 0;
            var pixelScale = 1f;
            foreach (var slice in slices)
            {
                var (spec, data) = await Plugin.TextureReadback.GetRawImageAsync(
                                       slice.Screen, new TextureModificationArgs { DxgiFormat = B8G8R8A8, MakeOpaque = true }, true);

                // Screen pixels per ImGui unit (1 unless the game renders at another resolution).
                var scale = spec.Width / screenSize.X;
                if (picture is null)
                {
                    pixelScale = scale;
                    width = (int)Math.Round(windowSize.X * scale);
                    height = (int)Math.Round(windowSize.Y * scale);
                    picture = new byte[width * height * 4];
                }

                // The rows of the window visible on this slice.
                var top = slice.WindowPos.Y;
                var first = (int)Math.Round(Math.Max(0, -top) * scale);
                var last = (int)Math.Round(Math.Min(windowSize.Y, screenSize.Y - top) * scale);
                var left = (int)Math.Round(slice.WindowPos.X * scale);
                var offset = (int)Math.Round(top * scale);
                var srcLeft = Math.Max(0, left);
                var columns = Math.Min(width - (srcLeft - left), spec.Width - srcLeft);
                if (columns <= 0)
                    continue;

                for (var row = Math.Max(0, first); row < Math.Min(height, last); row++)
                {
                    var srcRow = row + offset;
                    if (srcRow < 0 || srcRow >= spec.Height)
                        continue;
                    Buffer.BlockCopy(data, srcRow * spec.Pitch + srcLeft * 4, picture, (row * width + (srcLeft - left)) * 4, columns * 4);
                }
            }

            if (picture is null)
                throw new InvalidOperationException("nothing was captured");
            RoundCorners(picture, width, height, rounding * pixelScale);

            var texture = Plugin.TextureProvider.CreateFromRaw(RawImageSpecification.Bgra32(width, height), picture, "Raid Companion screenshot");
            var png = Plugin.TextureReadback.GetSupportedImageEncoderInfos()
                            .First(e => e.Extensions.Any(x => x.TrimStart('.').Equals("png", StringComparison.OrdinalIgnoreCase)))
                            .ContainerGuid;
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, $"Raid Companion {view} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            await Plugin.TextureReadback.SaveToFileAsync(texture, png, path); // disposes the texture
            return path;
        }
        finally
        {
            foreach (var slice in slices)
                slice.Screen.Dispose();
        }
    }

    // Clears what lies outside the window's rounded corners (the game behind), with a smooth edge.
    private static void RoundCorners(byte[] bgra, int width, int height, float radius)
    {
        var r = (int)Math.Ceiling(radius);
        if (r <= 0 || r * 2 > Math.Min(width, height))
            return;

        for (var y = 0; y < r; y++)
        {
            for (var x = 0; x < r; x++)
            {
                // Distance of the pixel's centre from the centre of the corner's circle.
                var dx = radius - (x + 0.5f);
                var dy = radius - (y + 0.5f);
                var coverage = Math.Clamp(radius - MathF.Sqrt(dx * dx + dy * dy) + 0.5f, 0f, 1f);
                if (coverage >= 1f)
                    continue;

                foreach (var (px, py) in new[] { (x, y), (width - 1 - x, y), (x, height - 1 - y), (width - 1 - x, height - 1 - y) })
                {
                    var alpha = (py * width + px) * 4 + 3;
                    bgra[alpha] = (byte)(bgra[alpha] * coverage);
                }
            }
        }
    }

    /// <summary>Tells in chat where the picture went, with a link to its folder.</summary>
    public static void Report(string path)
    {
        folderLink ??= Plugin.ChatGui.AddChatLinkHandler(FolderLinkId, (_, _) => OpenFolder());
        var message = new SeStringBuilder()
                      .AddText(Loc.F("[Raid Companion] Screenshot saved: {0} ",Path.GetFileName(path)))
                      .Add(folderLink)
                      .AddUiForeground(Loc.T("[Open folder]"), 37)
                      .Add(RawPayload.LinkTerminator)
                      .Build();
        Plugin.ChatGui.Print(message);
    }

    public static void ReportError(Exception? error)
    {
        Plugin.Log.Error(error, "Screenshot failed");
        Plugin.ChatGui.PrintError(Loc.F("[Raid Companion] Screenshot failed: {0}",error?.Message ?? Loc.T("unknown error")));
    }

    private static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo { FileName = Folder, UseShellExecute = true });
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Could not open the screenshots folder");
        }
    }
}
