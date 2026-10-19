// ---------------------------------------------------------------------------
// 来源声明 / Attribution
//
// 本文件是 Westwood/RA2 点阵字体（game.fnt）格式的**独立实现**，
// 格式知识来自 Nyerguds 的 Westwood Font Editor：
//
//     WWFontEditor - https://github.com/OpenRA2/WWFontEditor
//     参考文件：WWFontEditor/Domain/FontTypes/FontFileWsBfUni.cs
//               WWFontEditor/Domain/Utils/ImageUtils/ImageUtils.cs
//     作者：Nyerguds    许可：WTFPL
// ---------------------------------------------------------------------------

using System.Text;

namespace FntToEbfd
{
    /// <summary>
    /// Westwood / RA2 点阵字体（game.fnt）读取器。
    ///
    /// 格式（RA2 Unicode 变体，magic = "fonT"，全部小写）：
    ///
    ///   偏移   大小   含义
    ///   0x00   4      magic "fonT"
    ///   0x04   4      表意文字宽度（全角字宽，常见 0x14 = 20）
    ///   0x08   4      stride：每行占用的**字节数**（1bpp，所以 3 字节 = 最多 24 像素宽）
    ///   0x0C   4      fontDataHeight：字形实际数据高度（像素），本例 16
    ///   0x10   4      fontHeight：字体整体高度，本例 17
    ///   0x14   4      symbolCount：不同字形数据块的数量
    ///   0x18   4      symbolDataSize = stride * fontDataHeight + 1（多出的 1 字节是宽度）
    ///   0x1C   0x20000  索引表：65536 项 uint16 小端，**1-based**，0 表示该码位无字形
    ///   0x2001C ...     字形数据块，共 symbolCount 个，每个 symbolDataSize 字节：
    ///                     第 0 字节           = 字形宽度（像素，列数）
    ///                     之后 stride*height  = 1bpp 位图，行主序
    ///
    /// 位序：**大端**（每行第 1 个像素是行首字节的最高位），行内多余位补 0。
    /// 这正是 Nyerguds 的 ImageUtils.ConvertTo8Bit(..., bigEndian: true) 所描述的形式。
    ///
    /// 重复字形被去重：多个码位可以指向同一个 symbol（索引值相同），
    /// 所以 symbolCount &lt;= 已映射码位数。
    /// </summary>
    public sealed class WwFont
    {
        public uint IdeographicWidth;
        public int Stride;              // 字节/行
        public int DataHeight;          // 字形数据高度（像素）
        public int FontHeight;          // 字体整体高度
        public int SymbolCount;
        public int SymbolDataSize;      // = Stride * DataHeight + 1

        /// <summary>索引表，长度 65536。值为 1-based symbol 号；0 = 无字形。</summary>
        public ushort[] Index = null!;   // 由 Parse() 赋值

        /// <summary>去重后的字形数据块，下标 0 对应 symbol 号 1。</summary>
        public byte[][] Symbols = null!;  // 由 Parse() 赋值

        public int MaxWidthPixels { get { return Stride * 8; } }

        private byte[] m_Data = null!;    // 由 Load() 赋值

        public static WwFont Load(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            WwFont f = new WwFont();
            f.m_Data = data;
            f.Parse();
            return f;
        }

        private uint ReadU32(int offset)
        {
            return (uint)(m_Data[offset] | (m_Data[offset + 1] << 8)
                        | (m_Data[offset + 2] << 16) | (m_Data[offset + 3] << 24));
        }

        private void Parse()
        {
            if (m_Data.Length < 0x1C)
                throw new InvalidDataException("文件太短，装不下头部。");
            string magic = Encoding.ASCII.GetString(m_Data, 0, 4);
            if (magic == "fonT")
            {
                // RA2 Unicode 变体
            }
            else if (magic == "FoNt")
            {
                throw new InvalidDataException(
                    "这是 RA2 的**非 Unicode** 变体（magic \"FoNt\"，" +
                    "只有 256 个字形、索引表是 0x30 起的区间形式）。\n" +
                    "本工具按 Unicode 变体（\"fonT\"）实现。");
            }
            else
            {
                throw new InvalidDataException(
                    "无法识别的 magic：" + magic + "（期望 \"fonT\"）");
            }

            IdeographicWidth = ReadU32(0x04);
            Stride = (int)ReadU32(0x08);
            DataHeight = (int)ReadU32(0x0C);
            FontHeight = (int)ReadU32(0x10);
            SymbolCount = (int)ReadU32(0x14);
            SymbolDataSize = (int)ReadU32(0x18);

            if (Stride <= 0 || DataHeight <= 0)
                throw new InvalidDataException("stride / fontDataHeight 非法。");
            if (SymbolDataSize != Stride * DataHeight + 1)
                throw new InvalidDataException(string.Format(
                    "symbolDataSize({0}) != stride*height+1({1})，格式不符。",
                    SymbolDataSize, Stride * DataHeight + 1));

            int idxOff = 0x1C;
            int dataOff = idxOff + 0x20000;
            long need = (long)dataOff + (long)SymbolCount * SymbolDataSize;
            if (need != m_Data.Length)
                throw new InvalidDataException(string.Format(
                    "文件大小不符：期望 {0}，实际 {1}。", need, m_Data.Length));

            Index = new ushort[0x10000];
            for (int i = 0; i < 0x10000; ++i)
                Index[i] = (ushort)(m_Data[idxOff + i * 2]
                                  | (m_Data[idxOff + i * 2 + 1] << 8));

            Symbols = new byte[SymbolCount][];
            for (int i = 0; i < SymbolCount; ++i)
            {
                byte[] block = new byte[SymbolDataSize];
                Buffer.BlockCopy(m_Data, dataOff + i * SymbolDataSize, block, 0, SymbolDataSize);
                Symbols[i] = block;
            }
        }

        /// <summary>该码位是否有字形。</summary>
        public bool HasGlyph(int codepoint)
        {
            return codepoint >= 0 && codepoint < 0x10000 && Index[codepoint] != 0;
        }

        public int WidthOf(int codepoint)
        {
            if (!HasGlyph(codepoint)) return 0;
            return Symbols[Index[codepoint] - 1][0];
        }

        /// <summary>
        /// 取一个码位的点阵，返回 DataHeight 行字符串，每行 WidthOf(cp) 个 '0'/'1'。
        /// 位序为大端：行首字节的最高位是该行第一个像素。
        /// </summary>
        public string[]? GetGlyphRows(int codepoint)
        {
            if (!HasGlyph(codepoint)) return null;
            byte[] block = Symbols[Index[codepoint] - 1];
            int w = block[0];
            string[] rows = new string[DataHeight];
            for (int y = 0; y < DataHeight; ++y)
            {
                char[] line = new char[w];
                int rowBase = 1 + y * Stride;
                for (int x = 0; x < w; ++x)
                {
                    byte b = block[rowBase + (x >> 3)];
                    int bit = (b >> (7 - (x & 7))) & 1;
                    line[x] = bit != 0 ? '1' : '0';
                }
                rows[y] = new string(line);
            }
            return rows;
        }

        /// <summary>统计信息，便于确认解析正确。</summary>
        public string Describe()
        {
            int mapped = 0;
            HashSet<int> used = new HashSet<int>();
            for (int i = 0; i < Index.Length; ++i)
            {
                if (Index[i] != 0) { mapped++; used.Add(Index[i]); }
            }
            return string.Format(
                "magic=fonT  表意宽度={0}  stride={1}字节/行(最大{2}px)  " +
                "字形高={3}px  字体高={4}\n" +
                "symbolCount={5}  symbolDataSize={6}  已映射码位={7}  实际用到字形={8}",
                IdeographicWidth, Stride, MaxWidthPixels, DataHeight, FontHeight,
                SymbolCount, SymbolDataSize, mapped, used.Count);
        }
    }
}
