using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

/*
 Main HSL color ranges (approximate hues in degrees):

 - Red:      345� - 15�  (wraps around 360 -> 0)
 - Orange:    15� - 45�
 - Yellow:    45� - 75�
 - Green:     75� - 165�  (includes lime -> pure green)
 - Cyan:     165� - 195�
 - Blue:     195� - 255�
 - Purple:   255� - 285�
 - Magenta:  285� - 330�
 - Pink/Rose: 330� - 345�

 Notes on saturation/lightness for non-hue colors:
 - Black:    very low lightness (L <= ~0.05)
 - White:    very high lightness (L >= ~0.95)
 - Gray/Silver: low saturation (S <= ~0.10) with intermediate lightness

 These ranges are approximate and can be tuned. When classifying a pixel by "main color":
 1) If L <= 0.05 treat as Black.
 2) Else if L >= 0.95 treat as White.
 3) Else if S <= 0.10 treat as Gray/Silver (depending on L).
 4) Otherwise use the H (hue) degree to pick the color bucket above.

 Use these ranges when mapping or shifting hues (for example replacing "blue" hues with "orange").
*/

// Small tool to convert a single PNG or ICO into a multi-size .ico file with common icon sizes.
// It also converts blue shades to green and writes the resulting multi-size icon into the same directory as the input.
// Usage: ConvertPngToIco <input.png|input.ico> <output.ico>

string inputPng = "PhoneAssistant.WPF/Resources/releasePhone.png";
string outputIcon = "orange.ico";

if (!File.Exists(inputPng))
{
    Console.Error.WriteLine($"Input file not found: {inputPng}");
    return;
}

string? outputDir = Path.GetDirectoryName(Path.GetFullPath(inputPng));
ArgumentNullException.ThrowIfNull(outputDir);
outputIcon = Path.Combine(outputDir, outputIcon);

int[] sizes = [256, 128, 64, 48, 32, 24, 16];

using var ms = new MemoryStream();
using (var writer = new BinaryWriter(ms))
{
    // Icon header
    writer.Write((short)0); // reserved
    writer.Write((short)1); // image type: icon
    writer.Write((short)sizes.Length); // number of images

    var imageDatas = sizes.Select(size =>
    {
        using var srcImage = Image.Load<Rgba32>(inputPng);

        // Resize to desired size
        using var image = srcImage.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(size, size),
            Mode = ResizeMode.Max
        }));

        // Convert blue shades 
        ConvertBlueToShade(image);

        using var imgMs = new MemoryStream();
        // Save as PNG inside ICO for modern Windows
        image.Save(imgMs, new PngEncoder());
        var data = imgMs.ToArray();
        return (size, data);
    }).ToList();

    var offset = 6 + 16 * sizes.Length;
    foreach (var (size, data) in imageDatas)
    {
        writer.Write((byte)size); // width
        writer.Write((byte)size); // height
        writer.Write((byte)0); // colors
        writer.Write((byte)0); // reserved
        writer.Write((short)1); // color planes
        writer.Write((short)32); // bits per pixel
        writer.Write(data.Length); // size of data
        writer.Write(offset); // offset
        offset += data.Length;
    }

    foreach (var (_, data) in imageDatas)
    {
        writer.Write(data);
    }
}

File.WriteAllBytes(outputIcon, ms.ToArray());
Console.WriteLine($"Wrote {outputIcon}");


// Helper: convert shades of blue (by hue) to choosen shade while preserving saturation and lightness
static void ConvertBlueToShade(Image<Rgba32> image)
{
    image.ProcessPixelRows(accessor =>
    {
        for (int y = 0; y < accessor.Height; y++)
        {
            var row = accessor.GetRowSpan(y);
            for (int x = 0; x < row.Length; x++)
            {
                ref var p = ref row[x];
                // skip fully transparent
                if (p.A == 0) continue;

                // convert to 0..1 range
                double r = p.R / 255.0;
                double g = p.G / 255.0;
                double b = p.B / 255.0;

                // RGB -> HSL
                RgbToHsl(r, g, b, out double h, out double s, out double l);

                // If hue is in blue range (approx 180..260 degrees), shift to orange (30 deg)
                if (h >= 180 && h <= 260)
                {
                    h = 30; // orange
                    HslToRgb(h, s, l, out double nr, out double ng, out double nb);
                    p.R = (byte)ClampToByte(nr * 255.0);
                    p.G = (byte)ClampToByte(ng * 255.0);
                    p.B = (byte)ClampToByte(nb * 255.0);
                }
            }
        }
    });
}

static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
{
    double max = Math.Max(r, Math.Max(g, b));
    double min = Math.Min(r, Math.Min(g, b));
    l = (max + min) / 2.0;

    if (Math.Abs(max - min) < 1e-10)
    {
        // achromatic
        h = 0.0;
        s = 0.0;
    }
    else
    {
        double d = max - min;
        s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        if (Math.Abs(max - r) < 1e-10)
        {
            h = (g - b) / d + (g < b ? 6.0 : 0.0);
        }
        else if (Math.Abs(max - g) < 1e-10)
        {
            h = (b - r) / d + 2.0;
        }
        else
        {
            h = (r - g) / d + 4.0;
        }

        h *= 60.0; // convert to degrees
    }
}

static void HslToRgb(double hDeg, double s, double l, out double r, out double g, out double b)
{
    double h = hDeg / 360.0;
    if (s == 0.0)
    {
        r = g = b = l; // achromatic
    }
    else
    {
        double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
        double p = 2.0 * l - q;
        r = HueToRgb(p, q, h + 1.0 / 3.0);
        g = HueToRgb(p, q, h);
        b = HueToRgb(p, q, h - 1.0 / 3.0);
    }
}

static double HueToRgb(double p, double q, double t)
{
    if (t < 0) t += 1.0;
    if (t > 1) t -= 1.0;
    if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
    if (t < 1.0 / 2.0) return q;
    return t < 2.0 / 3.0 ? p + (q - p) * (2.0 / 3.0 - t) * 6.0 : p;
}

static double ClampToByte(double v) => v < 0 ? 0 : v > 255 ? 255 : v;

/* 
 static void ConvertBlueToGreen(Image<Rgba32> image)
{
    image.ProcessPixelRows(accessor =>
    {
        for (int y = 0; y < accessor.Height; y++)
        {
            var row = accessor.GetRowSpan(y);
            for (int x = 0; x < row.Length; x++)
            {
                ref var p = ref row[x];
                // skip fully transparent
                if (p.A == 0) continue;

                // convert to 0..1 range
                double r = p.R / 255.0;
                double g = p.G / 255.0;
                double b = p.B / 255.0;

                // RGB -> HSL
                RgbToHsl(r, g, b, out double h, out double s, out double l);

                // If hue is in blue range (approx 180..260 degrees), shift to green (120 deg)
                if (h >= 180 && h <= 260)
                {
                    h = 120; // green
                             // convert back
                    HslToRgb(h, s, l, out double nr, out double ng, out double nb);
                    p.R = (byte)ClampToByte(nr * 255.0);
                    p.G = (byte)ClampToByte(ng * 255.0);
                    p.B = (byte)ClampToByte(nb * 255.0);
                }
            }
        }
    });
}
*/
