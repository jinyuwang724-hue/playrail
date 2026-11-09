using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ClawScaling;

internal static class SelfTest
{
    public static void SaveBmp(string path, byte[] rgba, int width, int height)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0x4d42); writer.Write(54 + width * height * 4); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(width); writer.Write(-height); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(width * height * 4); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        for (int i = 0; i < rgba.Length; i += 4) { writer.Write(rgba[i + 2]); writer.Write(rgba[i + 1]); writer.Write(rgba[i]); writer.Write((byte)255); }
    }
    public static byte[] Read(Gpu gpu, ID3D11Texture2D texture, int width, int height)
    {
        var desc = texture.Description; desc.BindFlags = BindFlags.None; desc.Usage = ResourceUsage.Staging; desc.CPUAccessFlags = CpuAccessFlags.Read; desc.MiscFlags = ResourceOptionFlags.None;
        using var staging = gpu.Device.CreateTexture2D(desc); gpu.Context.CopyResource(staging, texture);
        var mapped = gpu.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try {
            byte[] result = new byte[width * height * 4];
            for (int y = 0; y < height; y++) Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, result, y * width * 4, width * 4);
            return result;
        } finally { gpu.Context.Unmap(staging, 0); }
    }
    public static unsafe int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        if (Geometry.Calculate(800, 600, 1920, 1200, "fit") != new Layout(1600, 1200, 160, 0)) throw new Exception("Fit geometry");
        if (Geometry.Calculate(800, 600, 1920, 1200, "fill") != new Layout(1920, 1440, 0, -120)) throw new Exception("Fill geometry");
        if (Geometry.Calculate(320, 240, 1920, 1200, "auto").Width != 640) throw new Exception("Auto 2x cap");
        if (Geometry.Calculate(2560, 1600, 1920, 1200, "integer").Width != 1920) throw new Exception("Integer downscale");
        using var gpu = new Gpu();
        const int w = 320, h = 240, ow = 960, oh = 600;
        byte[] pattern = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
            int i = (y * w + x) * 4;
            pattern[i] = (byte)((x / 5 + y / 5) % 2 == 0 ? 240 : 12);
            pattern[i + 1] = (byte)((x * 17 + y * 3) % 256);
            pattern[i + 2] = (byte)((x < 160 ? 220 : 24)); pattern[i + 3] = 255;
        }
        using var input = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, w, h, 1, 1, BindFlags.ShaderResource));
        fixed (byte* p = pattern) gpu.Context.UpdateSubresource(input, 0, null, (nint)p, w * 4, 0);
        using var view = gpu.Device.CreateShaderResourceView(input);
        using var output = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, ow, oh, 1, 1, BindFlags.RenderTarget));
        using var target = gpu.Device.CreateRenderTargetView(output);
        var results = new List<object>(); var fitHashes = new HashSet<string>();
        foreach (string mode in Geometry.Modes) foreach (string filter in Geometry.Filters) {
            var settings = new Settings(true, mode, filter, .5f);
            var layout = Geometry.Calculate(w, h, ow, oh, mode);
            var processed = gpu.Process(view, w, h, layout, settings);
            gpu.Present(processed, target, ow, oh, layout, gpu.EffectiveFilter);
            byte[] pixels = Read(gpu, output, ow, oh);
            string hash = Convert.ToHexString(SHA256.HashData(pixels));
            if (gpu.EffectiveFilter != filter) throw new Exception("Unexpected filter fallback: " + filter);
            if (pixels.Where((_, i) => i % 4 != 3).Max() < 100) throw new Exception("Blank GPU output");
            if (mode == "fit") fitHashes.Add(hash);
            if (mode == "fit" && (pixels[0] != 0 || pixels[1] != 0 || pixels[2] != 0)) throw new Exception("Black bars missing");
            SaveBmp(Path.Combine(directory, mode + "-" + filter + ".bmp"), pixels, ow, oh);
            results.Add(new { mode, filter, layout, hash, effectiveFilter = gpu.EffectiveFilter, note = gpu.Note });
        }
        if (fitHashes.Count != 4) throw new Exception("Filters did not produce distinct GPU outputs");
        using var nativeOutput = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, w, h, 1, 1, BindFlags.RenderTarget));
        using var nativeTarget = gpu.Device.CreateRenderTargetView(nativeOutput);
        var nativeHashes = new Dictionary<string, string>();
        var nativeResults = new List<object>();
        foreach (string filter in new[] { "linear", "fsr", "nis" }) foreach (float sharpness in (filter == "linear" ? new[] { .5f } : new[] { 0f, 1f })) {
            var settings = new Settings(true, "fit", filter, sharpness);
            var layout = new Layout(w, h, 0, 0);
            var processed = gpu.Process(view, w, h, layout, settings);
            gpu.Present(processed, nativeTarget, w, h, layout, gpu.EffectiveFilter);
            var pixels = Read(gpu, nativeOutput, w, h);
            if (gpu.EffectiveFilter != filter) throw new Exception("Native-size sharpening skipped");
            var hash = Convert.ToHexString(SHA256.HashData(pixels));
            string key = filter + sharpness;
            nativeHashes[key] = hash;
            nativeResults.Add(new { filter, sharpness, hash, note = gpu.Note });
            SaveBmp(Path.Combine(directory, "native-" + key + ".bmp"), pixels, w, h);
        }
        foreach (string filter in new[] { "fsr", "nis" }) {
            if (nativeHashes[filter + 1] == nativeHashes["linear" + .5f]) throw new Exception("Native sharpening did not change pixels: " + filter);
            if (nativeHashes[filter + 0] == nativeHashes[filter + 1]) throw new Exception("Sharpness slider did not change pixels: " + filter);
        }
        gpu.Prepare(w, h, new Layout(w / 2, h / 2, 0, 0), new Settings(true, "fit", "fsr"));
        if (gpu.EffectiveFilter != "linear" || gpu.Note.Length == 0) throw new Exception("Downscaling fallback missing");
        File.WriteAllText(Path.Combine(directory, "gpu-self-test.json"), JsonSerializer.Serialize(new { success = true, combinations = results, distinctFitFilters = fitHashes.Count, nativeSharpening = nativeResults, downscaleFallback = true }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("GPU scaling self-test passed: 20 combinations, 4 distinct filter outputs."); return 0;
    }
}
