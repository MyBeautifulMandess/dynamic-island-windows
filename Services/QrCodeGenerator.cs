using System.Text;

namespace DynamicIsland.Services;

/// <summary>
/// Генератор QR-кодов в байтовом режиме с коррекцией ошибок M.
///
/// Реализован по стандарту ISO/IEC 18004, потому что QRCoder или любую другую
/// библиотеку пришлось бы тащить новой зависимостью в single-file сборку.
/// Поддерживаются версии 1–40, что с запасом покрывает строку сопряжения.
/// </summary>
internal static class QrCodeGenerator
{
    private static readonly int[] EccPerBlock =
    [
        0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26,
        26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28
    ];

    private static readonly int[] BlockCount =
    [
        0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16,
        17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49
    ];

    /// <summary>Возвращает матрицу модулей: true — тёмный модуль. Строки сверху вниз.</summary>
    public static bool[,] Encode(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var version = ChooseVersion(data.Length);
        var bits = new BitBuffer();
        bits.Append(0b0100, 4);
        bits.Append((uint)data.Length, version < 10 ? 8 : 16);
        foreach (var value in data) bits.Append(value, 8);

        var dataCodewords = GetDataCodewords(version);
        var capacity = dataCodewords * 8;
        bits.Append(0, Math.Min(4, capacity - bits.Length));
        while (bits.Length % 8 != 0) bits.Append(0, 1);

        var codewords = bits.ToBytes();
        var pad = 0;
        while (codewords.Count < dataCodewords)
            codewords.Add(pad++ % 2 == 0 ? (byte)0xEC : (byte)0x11);

        var all = Interleave(codewords, version);
        var matrix = new QrMatrix(version);
        matrix.DrawFunctionPatterns();
        matrix.DrawCodewords(all);

        var bestMask = 0;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            matrix.ApplyMask(mask);
            matrix.DrawFormatBits(mask);
            var penalty = matrix.Penalty();
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
            }
            matrix.ApplyMask(mask);
        }

        matrix.ApplyMask(bestMask);
        matrix.DrawFormatBits(bestMask);
        return matrix.Modules;
    }

    private static int ChooseVersion(int byteCount)
    {
        for (var version = 1; version <= 40; version++)
        {
            var headerBits = 4 + (version < 10 ? 8 : 16);
            var neededBits = headerBits + byteCount * 8;
            if (neededBits <= GetDataCodewords(version) * 8) return version;
        }
        throw new InvalidOperationException("Слишком длинная строка для QR-кода");
    }

    private static int GetDataCodewords(int version)
    {
        var rawModules = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var align = version / 7 + 2;
            rawModules -= (25 * align - 10) * align - 55;
            if (version >= 7) rawModules -= 36;
        }
        return rawModules / 8 - EccPerBlock[version] * BlockCount[version];
    }

    private static List<byte> Interleave(List<byte> data, int version)
    {
        var blockCount = BlockCount[version];
        var eccLength = EccPerBlock[version];
        var rawCodewords = (GetDataCodewords(version) + eccLength * blockCount);
        var shortBlockLength = rawCodewords / blockCount;
        var shortCount = blockCount - rawCodewords % blockCount;

        var blocks = new List<byte>[blockCount];
        var offset = 0;
        for (var i = 0; i < blockCount; i++)
        {
            var length = shortBlockLength - eccLength + (i < shortCount ? 0 : 1);
            blocks[i] = data.GetRange(offset, length);
            offset += length;
        }

        var result = new List<byte>(rawCodewords);
        for (var i = 0; i <= shortBlockLength - eccLength; i++)
        for (var b = 0; b < blockCount; b++)
        {
            if (i == shortBlockLength - eccLength && b < shortCount) continue;
            result.Add(blocks[b][i]);
        }

        var eccBlocks = blocks.Select(block => ReedSolomon(block, eccLength)).ToArray();
        for (var i = 0; i < eccLength; i++)
        for (var b = 0; b < blockCount; b++)
            result.Add(eccBlocks[b][i]);

        return result;
    }

    private static byte[] ReedSolomon(List<byte> data, int degree)
    {
        var divisor = new byte[degree];
        divisor[degree - 1] = 1;
        byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                divisor[j] = Multiply(divisor[j], root);
                if (j + 1 < degree) divisor[j] ^= divisor[j + 1];
            }
            root = Multiply(root, 0x02);
        }

        var remainder = new byte[degree];
        foreach (var value in data)
        {
            var factor = (byte)(value ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, degree - 1);
            remainder[degree - 1] = 0;
            for (var i = 0; i < degree; i++) remainder[i] ^= Multiply(divisor[i], factor);
        }
        return remainder;
    }

    private static byte Multiply(byte x, byte y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    private sealed class BitBuffer
    {
        private readonly List<bool> _bits = [];

        public int Length => _bits.Count;

        public void Append(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--) _bits.Add(((value >> i) & 1) != 0);
        }

        public List<byte> ToBytes()
        {
            var result = new List<byte>((_bits.Count + 7) / 8);
            for (var i = 0; i < _bits.Count; i += 8)
            {
                var value = 0;
                for (var j = 0; j < 8; j++)
                {
                    value <<= 1;
                    if (i + j < _bits.Count && _bits[i + j]) value |= 1;
                }
                result.Add((byte)value);
            }
            return result;
        }
    }

    private sealed class QrMatrix
    {
        private readonly int _version;
        private readonly int _size;
        private readonly bool[,] _isFunction;

        public QrMatrix(int version)
        {
            _version = version;
            _size = version * 4 + 17;
            Modules = new bool[_size, _size];
            _isFunction = new bool[_size, _size];
        }

        public bool[,] Modules { get; }

        public void DrawFunctionPatterns()
        {
            for (var i = 0; i < _size; i++)
            {
                SetFunction(6, i, i % 2 == 0);
                SetFunction(i, 6, i % 2 == 0);
            }

            DrawFinder(3, 3);
            DrawFinder(_size - 4, 3);
            DrawFinder(3, _size - 4);

            var positions = AlignmentPositions();
            var count = positions.Length;
            for (var i = 0; i < count; i++)
            for (var j = 0; j < count; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == count - 1) || (i == count - 1 && j == 0)) continue;
                DrawAlignment(positions[i], positions[j]);
            }

            DrawFormatBits(0);
            DrawVersionBits();
        }

        private int[] AlignmentPositions()
        {
            if (_version == 1) return [];
            var count = _version / 7 + 2;
            var step = (_version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
            var result = new int[count];
            result[0] = 6;
            var position = _size - 7;
            for (var i = count - 1; i >= 1; i--)
            {
                result[i] = position;
                position -= step;
            }
            return result;
        }

        private void DrawFinder(int x, int y)
        {
            for (var dy = -4; dy <= 4; dy++)
            for (var dx = -4; dx <= 4; dx++)
            {
                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                var px = x + dx;
                var py = y + dy;
                if (px < 0 || px >= _size || py < 0 || py >= _size) continue;
                SetFunction(px, py, distance != 2 && distance != 4);
            }
        }

        private void DrawAlignment(int x, int y)
        {
            for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        public void DrawFormatBits(int mask)
        {
            // Уровень M (00) и выбранная маска.
            var data = (0b00 << 3) | mask;
            var remainder = data;
            for (var i = 0; i < 10; i++) remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
            var bits = ((data << 10) | remainder) ^ 0x5412;

            for (var i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
            SetFunction(8, 7, Bit(bits, 6));
            SetFunction(8, 8, Bit(bits, 7));
            SetFunction(7, 8, Bit(bits, 8));
            for (var i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));

            for (var i = 0; i < 8; i++) SetFunction(_size - 1 - i, 8, Bit(bits, i));
            for (var i = 8; i < 15; i++) SetFunction(8, _size - 15 + i, Bit(bits, i));
            SetFunction(8, _size - 8, true);
        }

        private void DrawVersionBits()
        {
            if (_version < 7) return;
            var remainder = _version;
            for (var i = 0; i < 12; i++) remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
            var bits = (_version << 12) | remainder;
            for (var i = 0; i < 18; i++)
            {
                var flag = Bit(bits, i);
                var a = _size - 11 + i % 3;
                var b = i / 3;
                SetFunction(a, b, flag);
                SetFunction(b, a, flag);
            }
        }

        public void DrawCodewords(List<byte> codewords)
        {
            var index = 0;
            var total = codewords.Count * 8;
            for (var right = _size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (var vertical = 0; vertical < _size; vertical++)
                {
                    for (var j = 0; j < 2; j++)
                    {
                        var x = right - j;
                        var upward = ((right + 1) & 2) == 0;
                        var y = upward ? _size - 1 - vertical : vertical;
                        if (_isFunction[x, y] || index >= total) continue;
                        Modules[x, y] = ((codewords[index >> 3] >> (7 - (index & 7))) & 1) != 0;
                        index++;
                    }
                }
            }
        }

        public void ApplyMask(int mask)
        {
            for (var y = 0; y < _size; y++)
            for (var x = 0; x < _size; x++)
            {
                if (_isFunction[x, y]) continue;
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0
                };
                if (invert) Modules[x, y] = !Modules[x, y];
            }
        }

        public int Penalty()
        {
            var result = 0;
            for (var pass = 0; pass < 2; pass++)
            for (var a = 0; a < _size; a++)
            {
                var run = 1;
                var dark = false;
                for (var b = 0; b < _size; b++)
                {
                    var value = pass == 0 ? Modules[b, a] : Modules[a, b];
                    if (b > 0 && value == dark) run++;
                    else
                    {
                        if (b > 0 && run >= 5) result += 3 + (run - 5);
                        run = 1;
                        dark = value;
                    }
                }
                if (run >= 5) result += 3 + (run - 5);
            }

            for (var y = 0; y < _size - 1; y++)
            for (var x = 0; x < _size - 1; x++)
            {
                var value = Modules[x, y];
                if (value == Modules[x + 1, y] && value == Modules[x, y + 1] && value == Modules[x + 1, y + 1])
                    result += 3;
            }

            const int n3 = 40;
            for (var pass = 0; pass < 2; pass++)
            for (var a = 0; a < _size; a++)
            for (var b = 0; b <= _size - 7; b++)
            {
                var window = 0;
                for (var i = 0; i < 7; i++) if (Get(pass, a, b + i)) window |= 1 << i;
                if (window != 0b1011101) continue;
                var before = true;
                var after = true;
                for (var i = b - 4; i < b; i++)
                    if (i >= 0 && Get(pass, a, i)) { before = false; break; }
                for (var i = b + 7; i < b + 11; i++)
                    if (i < _size && Get(pass, a, i)) { after = false; break; }
                if (before || after) result += n3;
            }

            var darkCount = 0;
            for (var y = 0; y < _size; y++)
            for (var x = 0; x < _size; x++)
                if (Modules[x, y]) darkCount++;
            var total = _size * _size;
            var deviation = Math.Abs(darkCount * 20 - total * 10) / total;
            result += deviation * 10;

            return result;
        }

        private bool Get(int pass, int a, int b) => pass == 0 ? Modules[b, a] : Modules[a, b];

        private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;

        private void SetFunction(int x, int y, bool dark)
        {
            if (x < 0 || y < 0 || x >= _size || y >= _size) return;
            Modules[x, y] = dark;
            _isFunction[x, y] = true;
        }
    }
}
