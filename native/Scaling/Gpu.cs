using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace ClawScaling;

internal sealed class Gpu : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    readonly ID3D11VertexShader vertex;
    readonly ID3D11PixelShader pixel;
    readonly ID3D11ComputeShader easu, rcas, nis;
    readonly ID3D11SamplerState linear, nearest;
    readonly ID3D11Buffer constants;
    readonly ID3D11RasterizerState rasterizer;
    readonly ID3D11ShaderResourceView coefScale, coefUsm;
    readonly List<Stage> stages = [];
    string pipeline = "";
    public string EffectiveFilter { get; private set; } = "linear";
    public string Note { get; private set; } = "";
    public record Stage(ID3D11Texture2D Texture, ID3D11ShaderResourceView View, ID3D11UnorderedAccessView Output, int Width, int Height, bool Rcas, bool Final) : IDisposable
    { public void Dispose() { Output.Dispose(); View.Dispose(); Texture.Dispose(); } }

    public Gpu()
    {
        try {
        D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device device, out _, out ID3D11DeviceContext context).CheckError();
        Device = device; Context = context;
        vertex = Device.CreateVertexShader(Compile("Basic.hlsl", "VS", "vs_5_0"));
        pixel = Device.CreatePixelShader(Compile("Basic.hlsl", "PS", "ps_5_0"));
        easu = Device.CreateComputeShader(Compile("FSR.hlsl", "main", "cs_5_0"));
        rcas = Device.CreateComputeShader(Compile("FSR.hlsl", "main", "cs_5_0", "#define RCAS 1\n"));
        nis = Device.CreateComputeShader(Compile("NIS_Main.hlsl", "main", "cs_5_0", "#define NIS_VIEWPORT_SUPPORT 1\n#define NIS_USE_HALF_PRECISION 0\n"));
        linear = Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
        nearest = Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
        constants = Device.CreateBuffer(new BufferDescription(256, BindFlags.ConstantBuffer, ResourceUsage.Default));
        rasterizer = Device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        coefScale = Coefficients("coef_scale"); coefUsm = Coefficients("coef_usm");
        } catch { Dispose(); throw; }
    }
    public static string ShaderRoot => Path.Combine(AppContext.BaseDirectory, "Shaders");
    static string Expand(string file, HashSet<string>? stack = null)
    {
        stack ??= [];
        if (!stack.Add(file)) throw new InvalidDataException("Shader include cycle");
        string result = Regex.Replace(File.ReadAllText(Path.Combine(ShaderRoot, file)), "^\\s*#include\\s+\"([^\"]+)\".*$", m => Expand(m.Groups[1].Value, stack), RegexOptions.Multiline);
        stack.Remove(file); return result;
    }
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)] static extern int D3DCompile(byte[] source, nuint length, string sourceName, nint defines, nint include, string entry, string profile, uint flags, uint effects, out nint code, out nint errors);
    static unsafe byte[] Blob(nint blob)
    {
        nint* table = *(nint**)blob;
        nint pointer = ((delegate* unmanaged[Stdcall]<nint, nint>)table[3])(blob);
        nuint size = ((delegate* unmanaged[Stdcall]<nint, nuint>)table[4])(blob);
        return new ReadOnlySpan<byte>((void*)pointer, checked((int)size)).ToArray();
    }
    public static byte[] Compile(string file, string entry, string profile, string prefix = "")
    {
        byte[] source = Encoding.UTF8.GetBytes(prefix + Expand(file));
        int hr = D3DCompile(source, (nuint)source.Length, file, 0, 0, entry, profile, 0x800 | 0x8000, 0, out nint code, out nint errors);
        try {
            if (hr < 0) throw new InvalidOperationException(file + ": " + (errors != 0 ? Encoding.UTF8.GetString(Blob(errors)) : hr.ToString("X")));
            return Blob(code);
        } finally { if (code != 0) Marshal.Release(code); if (errors != 0) Marshal.Release(errors); }
    }
    unsafe ID3D11ShaderResourceView Coefficients(string name)
    {
        string header = File.ReadAllText(Path.Combine(ShaderRoot, "NIS_Config.h"));
        var array = Regex.Match(header, @"constexpr float " + name + @"\[.*?=\s*\{(.*?)\};", RegexOptions.Singleline);
        float[] values = Regex.Matches(array.Groups[1].Value, @"-?\d+\.\d+f").Select(m => float.Parse(m.Value.TrimEnd('f'), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != 512) throw new InvalidDataException("NIS coefficient table invalid: " + name);
        fixed (float* pointer = values) {
            using var texture = Device.CreateTexture2D(new Texture2DDescription(Format.R32G32B32A32_Float, 2, 64, 1, 1, BindFlags.ShaderResource, ResourceUsage.Immutable), [new SubresourceData((nint)pointer, 32, 0)]);
            return Device.CreateShaderResourceView(texture);
        }
    }
    Stage CreateStage(int width, int height, bool rcasPass, bool final)
    {
        var texture = Device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource | BindFlags.UnorderedAccess));
        return new(texture, Device.CreateShaderResourceView(texture), Device.CreateUnorderedAccessView(texture), width, height, rcasPass, final);
    }
    public void Prepare(int width, int height, Layout layout, Settings settings)
    {
        string key = $"{width}:{height}:{layout.Width}:{layout.Height}:{settings.Filter}:{settings.Sharpness}";
        if (key == pipeline) return;
        foreach (var stage in stages) stage.Dispose(); stages.Clear(); pipeline = key;
        EffectiveFilter = settings.Filter; Note = "";
        if (settings.Filter is not ("fsr" or "nis")) return;
        if (layout.Width < width || layout.Height < height) {
            EffectiveFilter = "linear"; Note = "输入画面大于输出，当前按线性缩小；FSR/NIS 放大未执行"; return;
        }
        bool nativeSize = layout.Width == width && layout.Height == height;
        if (settings.Filter == "fsr") {
            if (!nativeSize) stages.Add(CreateStage(layout.Width, layout.Height, false, false));
            stages.Add(CreateStage(layout.Width, layout.Height, true, true));
        } else {
            int w = width, h = height;
            if (nativeSize) stages.Add(CreateStage(w, h, false, true));
            while (w < layout.Width || h < layout.Height) {
                w = Math.Min(layout.Width, w * 2); h = Math.Min(layout.Height, h * 2);
                stages.Add(CreateStage(w, h, false, w == layout.Width && h == layout.Height));
            }
            if (stages.Count > 1) Note = "NIS 分阶段放大，中间阶段不叠加锐化";
        }
        if (nativeSize) Note = $"原生尺寸：缩放比例不变，已应用 {settings.Filter.ToUpperInvariant()} 锐化";
    }
    static uint F(float value) => BitConverter.SingleToUInt32Bits(value);
    uint[] FsrConstants(int width, int height, Stage output, float sharpness)
    {
        uint[] values = new uint[64];
        if (output.Rcas) values[0] = F(MathF.Pow(2, -2 * (1 - sharpness)));
        else {
            float sx = (float)width / output.Width, sy = (float)height / output.Height;
            float[] constants = [sx, sy, .5f * sx - .5f, .5f * sy - .5f,
                1f / width, 1f / height, 1f / width, -1f / height,
                -1f / width, 2f / height, 1f / width, 2f / height,
                0, 4f / height, 0, 0];
            for (int i = 0; i < constants.Length; i++) values[i] = F(constants[i]);
        }
        values[16] = (uint)output.Width; values[17] = (uint)output.Height;
        return values;
    }
    uint[] NisConstants(int width, int height, Stage output, float sharpness)
    {
        // SDR port of NVIDIA SDK NVScalerUpdateConfig; intermediate passes have zero sharpening strength.
        float slider = sharpness - .5f, maxScale = slider >= 0 ? 1.25f : 1.75f, minScale = slider >= 0 ? 1.25f : 1;
        float strengthMin = Math.Max(0, .4f + slider * minScale * 1.2f), strengthMax = 1.6f + slider * maxScale * 1.8f;
        if (!output.Final) strengthMin = strengthMax = 0;
        float limitMin = Math.Max(.1f, .14f + slider * minScale * .32f), limitMax = .5f + slider * minScale * .6f;
        float[] raw = [2 * 1127f / 1024, 64f / 1024, 2, 1f / 8,
            1, 1f / 255, .45f, 1f / .45f, strengthMin, strengthMax - strengthMin, limitMin, limitMax - limitMin,
            (float)width / output.Width, (float)height / output.Height, 1f / output.Width, 1f / output.Height, 1f / width, 1f / height];
        uint[] values = new uint[64];
        for (int i = 0; i < raw.Length; i++) values[i] = F(raw[i]);
        values[20] = (uint)width; values[21] = (uint)height;
        values[24] = (uint)output.Width; values[25] = (uint)output.Height;
        return values;
    }
    public ID3D11ShaderResourceView Process(ID3D11ShaderResourceView source, int width, int height, Layout layout, Settings settings)
    {
        Prepare(width, height, layout, settings);
        var current = source;
        Context.CSSetSampler(0, linear); Context.CSSetConstantBuffer(0, constants);
        foreach (var stage in stages) {
            Context.UpdateSubresource(EffectiveFilter == "fsr" ? FsrConstants(width, height, stage, settings.Sharpness) : NisConstants(width, height, stage, settings.Sharpness), constants);
            Context.CSSetShader(EffectiveFilter == "fsr" ? (stage.Rcas ? rcas : easu) : nis);
            Context.CSSetShaderResources(0, [current, coefScale, coefUsm]);
            Context.CSSetUnorderedAccessView(0, stage.Output);
            if (EffectiveFilter == "fsr") Context.Dispatch((uint)((stage.Width + 7) / 8), (uint)((stage.Height + 7) / 8), 1);
            else Context.Dispatch((uint)((stage.Width + 31) / 32), (uint)((stage.Height + 23) / 24), 1);
            Context.CSSetUnorderedAccessView(0, null); Context.CSSetShaderResources(0, [null!, null!, null!]);
            current = stage.View; width = stage.Width; height = stage.Height;
        }
        return current;
    }
    public void Present(ID3D11ShaderResourceView source, ID3D11RenderTargetView target, int width, int height, Layout layout, string filter)
    {
        float[] values = new float[64]; values[0] = layout.Left; values[1] = layout.Top; values[2] = layout.Width; values[3] = layout.Height;
        Context.UpdateSubresource(values, constants);
        Context.RSSetState(rasterizer); Context.RSSetViewport(new Viewport(0, 0, width, height));
        Context.OMSetRenderTargets(target); Context.ClearRenderTargetView(target, new Color4(0, 0, 0, 1));
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.VSSetShader(vertex); Context.PSSetShader(pixel); Context.PSSetConstantBuffer(0, constants);
        Context.PSSetShaderResource(0, source); Context.PSSetSampler(0, filter == "nearest" ? nearest : linear);
        Context.Draw(3, 0); Context.PSSetShaderResource(0, null!); Context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }
    public void Dispose()
    {
        Context?.ClearState(); Context?.Flush();
        foreach (var stage in stages) stage.Dispose();
        coefScale?.Dispose(); coefUsm?.Dispose(); rasterizer?.Dispose(); constants?.Dispose(); linear?.Dispose(); nearest?.Dispose();
        vertex?.Dispose(); pixel?.Dispose(); easu?.Dispose(); rcas?.Dispose(); nis?.Dispose(); Context?.Dispose(); Device?.Dispose();
    }
}
