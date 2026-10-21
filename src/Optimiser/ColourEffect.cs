using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Optimiser;

// Brightness, contrast, saturation and warmth for the clip player, applied live so the video file is never changed.
// WPF needs compiled shader bytecode; d3dcompiler_47.dll ships with Windows 10 and 11, so it is compiled once at
// startup instead of keeping a binary .ps file in the repo. Shader is null if that fails, and the page hides colours.
public class ColourEffect : ShaderEffect
{
    const string Hlsl = """
        sampler2D input : register(s0);
        float brightness : register(c0);
        float contrast : register(c1);
        float saturation : register(c2);
        float warmth : register(c3);
        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float4 c = tex2D(input, uv);
            float3 rgb = c.rgb / max(c.a, 0.0001);
            rgb = (rgb - 0.5) * contrast + 0.5 + brightness;
            rgb = lerp(dot(rgb, float3(0.299, 0.587, 0.114)).xxx, rgb, saturation);
            rgb = saturate(rgb + float3(warmth, warmth * 0.3, -warmth));
            return float4(rgb * c.a, c.a);
        }
        """;

    public static readonly PixelShader? Shader = Compile();

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(ColourEffect), 0);
    public static readonly DependencyProperty BrightnessProperty = Constant(nameof(Brightness), 0, 0.0);
    public static readonly DependencyProperty ContrastProperty = Constant(nameof(Contrast), 1, 1.0);
    public static readonly DependencyProperty SaturationProperty = Constant(nameof(Saturation), 2, 1.0);
    public static readonly DependencyProperty WarmthProperty = Constant(nameof(Warmth), 3, 0.0);

    public ColourEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(BrightnessProperty);
        UpdateShaderValue(ContrastProperty);
        UpdateShaderValue(SaturationProperty);
        UpdateShaderValue(WarmthProperty);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public double Brightness { get => (double)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }
    public double Contrast { get => (double)GetValue(ContrastProperty); set => SetValue(ContrastProperty, value); }
    public double Saturation { get => (double)GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }
    public double Warmth { get => (double)GetValue(WarmthProperty); set => SetValue(WarmthProperty, value); }

    public void Set(ClipEdit e) => (Brightness, Contrast, Saturation, Warmth) = (e.Brightness, e.Contrast, e.Saturation, e.Warmth);

    static DependencyProperty Constant(string name, int register, double fallback) =>
        DependencyProperty.Register(name, typeof(double), typeof(ColourEffect),
            new UIPropertyMetadata(fallback, PixelShaderConstantCallback(register)));

    static PixelShader? Compile()
    {
        try
        {
            var source = Encoding.ASCII.GetBytes(Hlsl);
            var hr = D3DCompile(source, source.Length, null, 0, 0, "main", "ps_2_0", 0, 0, out var code, out var errors);
            if (errors != null) Marshal.ReleaseComObject(errors);
            if (hr < 0 || code == null) return null;
            var bytes = new byte[(int)code.GetBufferSize()];
            Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
            Marshal.ReleaseComObject(code);
            var shader = new PixelShader();
            shader.SetStreamSource(new MemoryStream(bytes));
            shader.Freeze();
            return shader;
        }
        catch { return null; }
    }

    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ID3DBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] nuint GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll")]
    static extern int D3DCompile(byte[] src, nint size, string? name, nint defines, nint include,
        [MarshalAs(UnmanagedType.LPStr)] string entry, [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1, uint flags2, out ID3DBlob? code, out ID3DBlob? errors);
}
