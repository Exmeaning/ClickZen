using System.Text;

namespace ClickZen.Core.Wireless;

/// <summary>
/// Self-contained ISO/IEC 18004 QR Code encoder (Byte mode, Error Correction Level M, Versions 1–6, up to 106 UTF-8 bytes)
/// used to render Android 11+ Wireless Debugging QR codes without external dependencies.
/// </summary>
public sealed class QrMatrix
{
    private readonly bool[,] _modules;

    internal QrMatrix(int version, int mask, bool[,] modules)
    {
        Version = version;
        Mask = mask;
        _modules = modules;
        Size = modules.GetLength(0);
    }

    public int Version { get; }

    public int Mask { get; }

    /// <summary>Module count per side (<c>17 + 4 * Version</c>).</summary>
    public int Size { get; }

    /// <summary>True when module <c>(row, col)</c> is dark (black).</summary>
    public bool this[int row, int col] => _modules[row, col];

    /// <summary>Renders the QR matrix into a top-down 32-bit BGRA pixel buffer with a white quiet zone.</summary>
    public (int Width, int Height, byte[] Bgra) ToBgraPixels(int modulePixels = 8, int quietZoneModules = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(modulePixels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(quietZoneModules, 0);

        var totalModules = Size + 2 * quietZoneModules;
        var dim = totalModules * modulePixels;
        var bgra = new byte[dim * dim * 4];
        // Initialize full background to opaque white (255, 255, 255, 255).
        Array.Fill(bgra, (byte)255);

        for (var r = 0; r < Size; r++)
        {
            for (var c = 0; c < Size; c++)
            {
                if (!_modules[r, c])
                {
                    continue;
                }

                var y0 = (r + quietZoneModules) * modulePixels;
                var x0 = (c + quietZoneModules) * modulePixels;
                for (var py = 0; py < modulePixels; py++)
                {
                    var rowOffset = ((y0 + py) * dim + x0) * 4;
                    for (var px = 0; px < modulePixels; px++)
                    {
                        var idx = rowOffset + px * 4;
                        bgra[idx] = 0;     // B
                        bgra[idx + 1] = 0; // G
                        bgra[idx + 2] = 0; // R
                        bgra[idx + 3] = 255;
                    }
                }
            }
        }

        return (dim, dim, bgra);
    }
}

public static class QrCodeEncoder
{
    // ISO/IEC 18004 Table 9 — Error Correction Level M parameters for Versions 1..6:
    // (Version, DataCodewordsPerBlock, EcCodewordsPerBlock, BlockCount, AlignmentCoord)
    private sealed record VersionSpec(int Version, int DataPerBlock, int EcPerBlock, int Blocks, int AlignmentCenter)
    {
        public int TotalDataCodewords => DataPerBlock * Blocks;
    }

    private static readonly VersionSpec[] Specs =
    [
        new(1, 16, 10, 1, 0),
        new(2, 28, 16, 1, 18),
        new(3, 44, 26, 1, 22),
        new(4, 32, 18, 2, 26),
        new(5, 43, 24, 2, 30),
        new(6, 27, 16, 4, 34),
    ];

    /// <summary>Encodes <paramref name="text"/> as an Error Correction Level M QR Code.</summary>
    public static QrMatrix Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encode(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Encodes raw bytes in QR Byte Mode (Error Correction Level M, Versions 1–6).</summary>
    public static QrMatrix Encode(ReadOnlySpan<byte> payload)
    {
        // Byte mode header: 4-bit mode (0100) + 8-bit byte count = 12 bits -> max payload = TotalDataCodewords - 2.
        var payloadLength = payload.Length;
        var spec = Specs.FirstOrDefault(s => payloadLength + 2 <= s.TotalDataCodewords)
            ?? throw new ArgumentOutOfRangeException(nameof(payload), $"Payload length {payloadLength} exceeds QR Version 6-M capacity (106 bytes).");

        var dataCodewords = BuildDataCodewords(payload, spec.TotalDataCodewords);
        var allCodewords = BuildInterleavedCodewords(dataCodewords, spec);

        var size = 17 + 4 * spec.Version;
        var baseModules = new bool[size, size];
        var isFunction = new bool[size, size];
        DrawFunctionPatterns(spec, baseModules, isFunction);
        DrawCodewords(allCodewords, baseModules, isFunction);

        var bestMask = 0;
        var bestPenalty = int.MaxValue;
        bool[,]? bestGrid = null;

        for (var mask = 0; mask < 8; mask++)
        {
            var candidate = (bool[,])baseModules.Clone();
            ApplyMask(mask, candidate, isFunction);
            DrawFormatBits(mask, candidate, isFunction);
            var penalty = ComputePenalty(candidate);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
                bestGrid = candidate;
            }
        }

        return new QrMatrix(spec.Version, bestMask, bestGrid!);
    }

    private static byte[] BuildDataCodewords(ReadOnlySpan<byte> payload, int capacity)
    {
        var bits = new List<bool>(capacity * 8);
        AppendBits(bits, 0b0100, 4); // Byte mode indicator
        AppendBits(bits, payload.Length, 8);
        foreach (var b in payload)
        {
            AppendBits(bits, b, 8);
        }

        var maxBits = capacity * 8;
        var terminator = Math.Min(4, maxBits - bits.Count);
        AppendBits(bits, 0, terminator);
        while (bits.Count % 8 != 0)
        {
            bits.Add(false);
        }

        var result = new byte[capacity];
        var byteCount = bits.Count / 8;
        for (var i = 0; i < byteCount; i++)
        {
            byte v = 0;
            for (var b = 0; b < 8; b++)
            {
                v = (byte)((v << 1) | (bits[i * 8 + b] ? 1 : 0));
            }

            result[i] = v;
        }

        byte pad = 0xEC;
        for (var i = byteCount; i < capacity; i++)
        {
            result[i] = pad;
            pad = pad == 0xEC ? (byte)0x11 : (byte)0xEC;
        }

        return result;
    }

    private static byte[] BuildInterleavedCodewords(byte[] data, VersionSpec spec)
    {
        var generator = BuildReedSolomonGenerator(spec.EcPerBlock);
        var dataBlocks = new byte[spec.Blocks][];
        var ecBlocks = new byte[spec.Blocks][];

        for (var b = 0; b < spec.Blocks; b++)
        {
            dataBlocks[b] = data.AsSpan(b * spec.DataPerBlock, spec.DataPerBlock).ToArray();
            ecBlocks[b] = ComputeReedSolomonRemainder(dataBlocks[b], generator);
        }

        var output = new byte[(spec.DataPerBlock + spec.EcPerBlock) * spec.Blocks];
        var pos = 0;
        for (var i = 0; i < spec.DataPerBlock; i++)
        {
            for (var b = 0; b < spec.Blocks; b++)
            {
                output[pos++] = dataBlocks[b][i];
            }
        }

        for (var i = 0; i < spec.EcPerBlock; i++)
        {
            for (var b = 0; b < spec.Blocks; b++)
            {
                output[pos++] = ecBlocks[b][i];
            }
        }

        return output;
    }

    private static void DrawFunctionPatterns(VersionSpec spec, bool[,] modules, bool[,] isFunction)
    {
        var size = modules.GetLength(0);

        // Timing patterns (row 6 and col 6).
        for (var i = 0; i < size; i++)
        {
            SetFunction(6, i, i % 2 == 0, modules, isFunction);
            SetFunction(i, 6, i % 2 == 0, modules, isFunction);
        }

        // Three 7x7 finder patterns + white separators.
        DrawFinderPattern(3, 3, modules, isFunction);
        DrawFinderPattern(3, size - 4, modules, isFunction);
        DrawFinderPattern(size - 4, 3, modules, isFunction);

        // Alignment pattern (Versions 2..6 have one 5x5 alignment pattern at bottom-right).
        if (spec.AlignmentCenter > 0)
        {
            DrawAlignmentPattern(spec.AlignmentCenter, spec.AlignmentCenter, modules, isFunction);
        }

        // Reserve format info slots + dark module (filled again for each mask).
        DrawFormatBits(0, modules, isFunction);
    }

    private static void DrawFinderPattern(int centerRow, int centerCol, bool[,] modules, bool[,] isFunction)
    {
        var size = modules.GetLength(0);
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var r = centerRow + dy;
                var c = centerCol + dx;
                if (r >= 0 && r < size && c >= 0 && c < size)
                {
                    var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunction(r, c, dist != 2 && dist != 4, modules, isFunction);
                }
            }
        }
    }

    private static void DrawAlignmentPattern(int centerRow, int centerCol, bool[,] modules, bool[,] isFunction)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunction(centerRow + dy, centerCol + dx, dist != 1, modules, isFunction);
            }
        }
    }

    private static void DrawFormatBits(int mask, bool[,] modules, bool[,] isFunction)
    {
        var size = modules.GetLength(0);
        // EC Level M indicator = 0b00.
        var data = (0b00 << 3) | mask;
        var rem = data;
        for (var i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        }

        var bits = ((data << 10) | rem) ^ 0x5412;

        // First copy around top-left finder.
        for (var i = 0; i <= 5; i++)
        {
            SetFunction(i, 8, GetBit(bits, i), modules, isFunction);
        }

        SetFunction(7, 8, GetBit(bits, 6), modules, isFunction);
        SetFunction(8, 8, GetBit(bits, 7), modules, isFunction);
        SetFunction(8, 7, GetBit(bits, 8), modules, isFunction);
        for (var i = 9; i < 15; i++)
        {
            SetFunction(8, 14 - i, GetBit(bits, i), modules, isFunction);
        }

        // Second copy split across top-right and bottom-left finders.
        for (var i = 0; i < 8; i++)
        {
            SetFunction(8, size - 1 - i, GetBit(bits, i), modules, isFunction);
        }

        for (var i = 8; i < 15; i++)
        {
            SetFunction(size - 15 + i, 8, GetBit(bits, i), modules, isFunction);
        }

        // Always-dark module.
        SetFunction(size - 8, 8, true, modules, isFunction);
    }

    private static void DrawCodewords(byte[] codewords, bool[,] modules, bool[,] isFunction)
    {
        var size = modules.GetLength(0);
        var bitIndex = 0;
        var totalBits = codewords.Length * 8;
        var upward = true;

        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5; // Skip vertical timing column.
            }

            for (var vert = 0; vert < size; vert++)
            {
                var r = upward ? size - 1 - vert : vert;
                for (var j = 0; j < 2; j++)
                {
                    var c = right - j;
                    if (!isFunction[r, c])
                    {
                        var dark = bitIndex < totalBits && GetBit(codewords[bitIndex >> 3], 7 - (bitIndex & 7));
                        modules[r, c] = dark;
                        bitIndex++;
                    }
                }
            }

            upward = !upward;
        }
    }

    private static void ApplyMask(int mask, bool[,] modules, bool[,] isFunction)
    {
        var size = modules.GetLength(0);
        for (var r = 0; r < size; r++)
        {
            for (var c = 0; c < size; c++)
            {
                if (!isFunction[r, c] && MaskMatches(mask, r, c))
                {
                    modules[r, c] = !modules[r, c];
                }
            }
        }
    }

    private static bool MaskMatches(int mask, int r, int c) => mask switch
    {
        0 => (r + c) % 2 == 0,
        1 => r % 2 == 0,
        2 => c % 3 == 0,
        3 => (r + c) % 3 == 0,
        4 => (r / 2 + c / 3) % 2 == 0,
        5 => r * c % 2 + r * c % 3 == 0,
        6 => (r * c % 2 + r * c % 3) % 2 == 0,
        7 => ((r + c) % 2 + r * c % 3) % 2 == 0,
        _ => false,
    };

    private static int ComputePenalty(bool[,] modules)
    {
        var size = modules.GetLength(0);
        var penalty = 0;

        // N1: Adjacent identical modules in row/column (5+ run -> 3 + (len - 5)).
        for (var i = 0; i < size; i++)
        {
            var rowRun = 1;
            var colRun = 1;
            for (var j = 1; j < size; j++)
            {
                if (modules[i, j] == modules[i, j - 1])
                {
                    rowRun++;
                    if (rowRun == 5) penalty += 3;
                    else if (rowRun > 5) penalty++;
                }
                else
                {
                    rowRun = 1;
                }

                if (modules[j, i] == modules[j - 1, i])
                {
                    colRun++;
                    if (colRun == 5) penalty += 3;
                    else if (colRun > 5) penalty++;
                }
                else
                {
                    colRun = 1;
                }
            }
        }

        // N2: 2x2 blocks of identical color (+3 each).
        for (var r = 0; r < size - 1; r++)
        {
            for (var c = 0; c < size - 1; c++)
            {
                var v = modules[r, c];
                if (v == modules[r, c + 1] && v == modules[r + 1, c] && v == modules[r + 1, c + 1])
                {
                    penalty += 3;
                }
            }
        }

        // N3: 1:1:3:1:1 finder-like patterns preceded/followed by 4 light modules (+40).
        for (var i = 0; i < size; i++)
        {
            for (var j = 0; j <= size - 11; j++)
            {
                if (MatchesFinderLikeRow(modules, i, j)) penalty += 40;
                if (MatchesFinderLikeCol(modules, j, i)) penalty += 40;
            }
        }

        // N4: Dark module balance from 50%.
        var darkCount = 0;
        foreach (var m in modules)
        {
            if (m) darkCount++;
        }

        var total = size * size;
        var k = Math.Abs(darkCount * 20 - total * 10) / total;
        penalty += k * 10;
        return penalty;
    }

    private static bool MatchesFinderLikeRow(bool[,] m, int r, int c)
    {
        // 10111010000 or 00001011101
        bool p1 = m[r, c] && !m[r, c + 1] && m[r, c + 2] && m[r, c + 3] && m[r, c + 4] && !m[r, c + 5] && m[r, c + 6]
                  && !m[r, c + 7] && !m[r, c + 8] && !m[r, c + 9] && !m[r, c + 10];
        bool p2 = !m[r, c] && !m[r, c + 1] && !m[r, c + 2] && !m[r, c + 3]
                  && m[r, c + 4] && !m[r, c + 5] && m[r, c + 6] && m[r, c + 7] && m[r, c + 8] && !m[r, c + 9] && m[r, c + 10];
        return p1 || p2;
    }

    private static bool MatchesFinderLikeCol(bool[,] m, int r, int c)
    {
        bool p1 = m[r, c] && !m[r + 1, c] && m[r + 2, c] && m[r + 3, c] && m[r + 4, c] && !m[r + 5, c] && m[r + 6, c]
                  && !m[r + 7, c] && !m[r + 8, c] && !m[r + 9, c] && !m[r + 10, c];
        bool p2 = !m[r, c] && !m[r + 1, c] && !m[r + 2, c] && !m[r + 3, c]
                  && m[r + 4, c] && !m[r + 5, c] && m[r + 6, c] && m[r + 7, c] && m[r + 8, c] && !m[r + 9, c] && m[r + 10, c];
        return p1 || p2;
    }

    private static void SetFunction(int r, int c, bool dark, bool[,] modules, bool[,] isFunction)
    {
        modules[r, c] = dark;
        isFunction[r, c] = true;
    }

    private static void AppendBits(List<bool> list, int val, int len)
    {
        for (var i = len - 1; i >= 0; i--)
        {
            list.Add(((val >> i) & 1) != 0);
        }
    }

    private static bool GetBit(int val, int bit) => ((val >> bit) & 1) != 0;

    private static byte[] BuildReedSolomonGenerator(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = GfMultiply(result[j], root);
                if (j + 1 < degree)
                {
                    result[j] ^= result[j + 1];
                }
            }

            root = GfMultiply(root, 0x02);
        }

        return result;
    }

    private static byte[] ComputeReedSolomonRemainder(ReadOnlySpan<byte> data, ReadOnlySpan<byte> generator)
    {
        var result = new byte[generator.Length];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var j = 0; j < generator.Length; j++)
            {
                result[j] ^= GfMultiply(generator[j], factor);
            }
        }

        return result;
    }

    private static byte GfMultiply(byte x, byte y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }
}
