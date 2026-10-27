// Lightweight, high-performance pure C# QR matrix detector and payload reader.

using System.Text;
using ImageMagick;

namespace Filee.Engines.Text;

/// <summary>Pure C# QR code detector and decoder.</summary>
public static class QrDecoder
{
    /// <summary>Decodes a QR code from an image file, or returns null if no valid QR code is found.</summary>
    public static string? DecodeFromFile(string imagePath)
    {
        try
        {
            using var image = new MagickImage(imagePath);
            return Decode(image);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decodes a QR code from a MagickImage.</summary>
    public static string? Decode(IMagickImage<byte> image)
    {
        try
        {
            // Downscale very large images to keep processing under 10ms
            using var clone = image.Clone();
            if (clone.Width > 800 || clone.Height > 800)
            {
                clone.Resize(new MagickGeometry(800, 800) { Greater = true });
            }

            clone.Grayscale();
            var width = (int)clone.Width;
            var height = (int)clone.Height;
            if (width < 21 || height < 21)
                return null;

            var pixels = clone.GetPixels();
            var gray = new byte[width, height];
            long sum = 0;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var p = pixels.GetPixel(x, y);
                    var val = p.GetChannel(0);
                    gray[x, y] = val;
                    sum += val;
                }
            }

            var threshold = (byte)(sum / (width * height));
            var bin = new bool[width, height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    bin[x, y] = gray[x, y] < threshold;

            // Find finder patterns (1:1:3:1:1 black:white:black:white:black)
            var centers = FindCenters(bin, width, height);
            if (centers.Count < 3)
                return null;

            // Try decoding using the best 3 centers
            return DecodeFromCenters(bin, width, height, centers[0], centers[1], centers[2]);
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct Point(int X, int Y);

    private static List<Point> FindCenters(bool[,] bin, int width, int height)
    {
        var list = new List<Point>();
        var state = new int[5];

        for (var y = 2; y < height - 2; y += 2)
        {
            Array.Clear(state, 0, 5);
            var currentState = 0;

            for (var x = 0; x < width; x++)
            {
                var isDark = bin[x, y];
                if (isDark)
                {
                    if ((currentState & 1) == 1)
                        currentState++;
                    state[currentState]++;
                }
                else
                {
                    if ((currentState & 1) == 0)
                    {
                        if (currentState == 4)
                        {
                            if (CheckRatio(state))
                            {
                                var cx = x - state[4] - state[3] - state[2] / 2;
                                if (CheckVertical(bin, cx, y, state[2], height))
                                    list.Add(new Point(cx, y));
                            }
                            state[0] = state[2];
                            state[1] = state[3];
                            state[2] = state[4];
                            state[3] = 1;
                            state[4] = 0;
                            currentState = 3;
                            continue;
                        }
                        currentState++;
                    }
                    state[currentState]++;
                }
            }
        }

        // Cluster and deduplicate centers
        var clustered = new List<Point>();
        foreach (var p in list)
        {
            var found = false;
            for (var i = 0; i < clustered.Count; i++)
            {
                var c = clustered[i];
                var dx = c.X - p.X;
                var dy = c.Y - p.Y;
                if (dx * dx + dy * dy < 400)
                {
                    clustered[i] = new Point((c.X + p.X) / 2, (c.Y + p.Y) / 2);
                    found = true;
                    break;
                }
            }
            if (!found)
                clustered.Add(p);
        }

        return clustered;
    }

    private static bool CheckRatio(int[] state)
    {
        var total = 0;
        for (var i = 0; i < 5; i++)
        {
            var count = state[i];
            if (count == 0) return false;
            total += count;
        }
        if (total < 7) return false;
        var moduleSize = total / 7.0;
        var maxVariance = moduleSize / 2.0;

        return Math.Abs(moduleSize - state[0]) < maxVariance &&
               Math.Abs(moduleSize - state[1]) < maxVariance &&
               Math.Abs(3.0 * moduleSize - state[2]) < 3.0 * maxVariance &&
               Math.Abs(moduleSize - state[3]) < maxVariance &&
               Math.Abs(moduleSize - state[4]) < maxVariance;
    }

    private static bool CheckVertical(bool[,] bin, int startX, int startY, int centerCount, int height)
    {
        var state = new int[5];
        var y = startY;

        while (y >= 0 && bin[startX, y]) { state[2]++; y--; }
        if (y < 0) return false;
        while (y >= 0 && !bin[startX, y]) { state[1]++; y--; }
        if (y < 0) return false;
        while (y >= 0 && bin[startX, y]) { state[0]++; y--; }
        if (y < 0) return false;

        y = startY + 1;
        while (y < height && bin[startX, y]) { state[2]++; y++; }
        if (y >= height) return false;
        while (y < height && !bin[startX, y]) { state[3]++; y++; }
        if (y >= height) return false;
        while (y < height && bin[startX, y]) { state[4]++; y++; }
        if (y >= height) return false;

        return CheckRatio(state);
    }

    private static string? DecodeFromCenters(bool[,] bin, int width, int height, Point a, Point b, Point c)
    {
        // Sort centers to determine Top-Left, Top-Right, Bottom-Left
        // The right-angle vertex is Top-Left.
        var dAB = DistSq(a, b);
        var dBC = DistSq(b, c);
        var dCA = DistSq(c, a);

        Point tl, tr, bl;
        if (dAB > dBC && dAB > dCA) { tl = c; tr = a; bl = b; }
        else if (dBC > dAB && dBC > dCA) { tl = a; tr = b; bl = c; }
        else { tl = b; tr = a; bl = c; }

        // Cross product to ensure correct orientation (tr is clockwise from bl)
        var cross = (tr.X - tl.X) * (bl.Y - tl.Y) - (tr.Y - tl.Y) * (bl.X - tl.X);
        if (cross < 0)
            (tr, bl) = (bl, tr);

        // Estimate module count
        var distModules = Math.Sqrt(DistSq(tl, tr));
        var estModuleSize = 7.0; // approximate
        var modulesCount = (int)Math.Round(distModules / estModuleSize) + 7;
        modulesCount = Math.Clamp(modulesCount, 21, 177);
        // Align to valid QR size: 21 + 4*k
        var k = (modulesCount - 21 + 2) / 4;
        var size = 21 + 4 * k;

        // Sample the grid
        var grid = new bool[size, size];
        for (var gy = 0; gy < size; gy++)
        {
            var v = (double)gy / (size - 1);
            for (var gx = 0; gx < size; gx++)
            {
                var u = (double)gx / (size - 1);
                var px = (int)Math.Round(tl.X + u * (tr.X - tl.X) + v * (bl.X - tl.X));
                var py = (int)Math.Round(tl.Y + u * (tr.Y - tl.Y) + v * (bl.Y - tl.Y));

                if (px >= 0 && px < width && py >= 0 && py < height)
                    grid[gx, gy] = bin[px, py];
            }
        }

        // Read format information from row 8 (around TL)
        var formatBits = 0;
        for (var i = 0; i <= 5; i++)
            if (grid[i, 8]) formatBits |= 1 << (14 - i);
        if (grid[7, 8]) formatBits |= 1 << 8;
        if (grid[8, 8]) formatBits |= 1 << 7;
        if (grid[8, 7]) formatBits |= 1 << 6;
        for (var i = 0; i <= 5; i++)
            if (grid[8, 5 - i]) formatBits |= 1 << i;

        formatBits ^= 0x5412; // Unmask format bits
        var mask = (formatBits >> 10) & 0x07;

        // Unmask data modules and extract bits
        var dataBits = new List<bool>();
        var readRight = true;
        for (var col = size - 1; col > 0; col -= 2)
        {
            if (col == 6) col--; // Skip vertical timing pattern
            var rows = readRight ? Enumerable.Range(0, size).Reverse().ToList() : Enumerable.Range(0, size).ToList();
            readRight = !readRight;

            foreach (var row in rows)
            {
                for (var dc = 0; dc < 2; dc++)
                {
                    var cx = col - dc;
                    var cy = row;
                    if (IsFunctionPattern(cx, cy, size))
                        continue;

                    var bit = grid[cx, cy];
                    if (ApplyMask(mask, cx, cy))
                        bit = !bit;
                    dataBits.Add(bit);
                }
            }
        }

        return ParsePayload(dataBits);
    }

    private static double DistSq(Point p1, Point p2) =>
        (p1.X - p2.X) * (p1.X - p2.X) + (p1.Y - p2.Y) * (p1.Y - p2.Y);

    private static bool IsFunctionPattern(int x, int y, int size)
    {
        // Top-Left finder + separator
        if (x <= 8 && y <= 8) return true;
        // Top-Right finder + separator
        if (x >= size - 8 && y <= 8) return true;
        // Bottom-Left finder + separator
        if (x <= 8 && y >= size - 8) return true;
        // Timing patterns
        if (x == 6 || y == 6) return true;
        return false;
    }

    private static bool ApplyMask(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (y / 2 + x / 3) % 2 == 0,
        5 => (x * y) % 2 + (x * y) % 3 == 0,
        6 => ((x * y) % 2 + (x * y) % 3) % 2 == 0,
        7 => ((x + y) % 2 + (x * y) % 3) % 2 == 0,
        _ => false,
    };

    private static string? ParsePayload(List<bool> bits)
    {
        var offset = 0;
        int Read(int count)
        {
            if (offset + count > bits.Count) return -1;
            var val = 0;
            for (var i = 0; i < count; i++)
                if (bits[offset++]) val |= 1 << (count - 1 - i);
            return val;
        }

        var sb = new StringBuilder();
        while (offset + 4 <= bits.Count)
        {
            var mode = Read(4);
            if (mode is 0 or -1) break; // End of message

            if (mode == 4) // 8-bit Byte mode (UTF-8 / ASCII)
            {
                var len = Read(8);
                if (len <= 0) break;
                var bytes = new byte[len];
                for (var i = 0; i < len; i++)
                {
                    var b = Read(8);
                    if (b < 0) break;
                    bytes[i] = (byte)b;
                }
                sb.Append(Encoding.UTF8.GetString(bytes));
            }
            else if (mode == 2) // Alphanumeric
            {
                var len = Read(9);
                if (len <= 0) break;
                const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
                while (len > 0)
                {
                    if (len >= 2)
                    {
                        var pair = Read(11);
                        if (pair < 0) break;
                        sb.Append(chars[pair / 45]);
                        sb.Append(chars[pair % 45]);
                        len -= 2;
                    }
                    else
                    {
                        var single = Read(6);
                        if (single < 0) break;
                        sb.Append(chars[single]);
                        len--;
                    }
                }
            }
            else if (mode == 1) // Numeric
            {
                var len = Read(10);
                if (len <= 0) break;
                while (len > 0)
                {
                    if (len >= 3)
                    {
                        var triple = Read(10);
                        if (triple < 0) break;
                        sb.Append(triple.ToString("D3"));
                        len -= 3;
                    }
                    else if (len == 2)
                    {
                        var pair = Read(7);
                        if (pair < 0) break;
                        sb.Append(pair.ToString("D2"));
                        len -= 2;
                    }
                    else
                    {
                        var single = Read(4);
                        if (single < 0) break;
                        sb.Append(single.ToString());
                        len--;
                    }
                }
            }
            else
            {
                // Other mode or padding
                break;
            }
        }

        var result = sb.ToString().Trim();
        return result.Length > 0 ? result : null;
    }
}
