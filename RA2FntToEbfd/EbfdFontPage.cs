using System.Text;

namespace FntToEbfd
{
    /// <summary>
    /// 把字形写进 Emperor: Battle for Dune 的 CFont3D 字库格式。
    ///
    /// 输出一页 = 一对文件：
    ///   CHINESE&lt;p&gt;.TGA   256x256，TGA type 2，16bpp
    ///                       但只用两个值：0x0000 = 底，0x7FFF = 墨
    ///                       （所以实质是 1bit 单色）
    ///                       descriptor = 0 → 行**自下而上**
    ///                       像素起点 = 偏移 18
    ///                       16 列 × 16 行 = 256 格，每格 16×16 像素
    ///                       索引 slot = 行*16 + 列
    ///                       文件长 131,611 = 18 头 + 131,072 像素 + 495 扩展区
    ///                       + 26 页脚；**扩展区可选**（原版 Korean0..8.tga 就
    ///                       没有，长 131,116），给了 --template 就按模板来
    ///   CHINESE&lt;p&gt;.MAP   UTF-16LE + BOM，每码位 2 字节，**没有结尾 NUL**
    ///                       满页 256 项 = 514 字节；末页按实际数量
    ///
    /// 这些数字都是从游戏原文件（归档 3DDATA0001.RFH 里的
    /// Textures\CHINESE0.tga 等）逐字节实测出来的，不是猜的。
    ///
    /// 引擎解析 MAP 的方式（Game.exe 0xab820）：count = 文件长度 / 2，
    /// count &gt; 0x101 时截断为 0x101，然后循环 i = 0 .. count-2 写入
    /// count-1 个字形（第一个 16 位字是 BOM，被跳过）。所以页大小**不必**
    /// 是 514 字节，引擎不会越界读。
    ///
    /// 本工具只生成 CJK 字体（Chinese 68 页 / Korean 10 页）：另外三项
    /// Small/Standard/TextIcons 走非 CJK 路径（0xab330），图集是 128x128 /
    /// 64x64，或者文件名不带页号，和这里的 256x256、16x16 格对不上。
    /// </summary>
    public sealed class EbfdFontPage
    {
        public const int AtlasW = 256;
        public const int AtlasH = 256;
        public const int Grid = 16;          // 16 x 16 个字形
        public const int Cell = 16;          // 每格 16x16 像素
        public const int GlyphsPerPage = Grid * Grid;   // 256
        public const ushort Ink = 0x7FFF;
        public const int HeaderLen = 18;

        /// <summary>每页 256 个槽位，存码位；0 表示该槽留空。</summary>
        public readonly int[] Slots = new int[GlyphsPerPage];

        private readonly int[] m_PendingCodepoint = new int[GlyphsPerPage];
        private readonly string[][] m_Cells = new string[GlyphsPerPage][];

        /// <summary>设置某槽要放的码位（随后再 SetGlyph 填点阵）。</summary>
        public void SetCodepoint(int slot, int codepoint)
        {
            m_PendingCodepoint[slot] = codepoint;
            Slots[slot] = codepoint;
        }

        public void SetGlyph(int slot, string[] cellRows16)
        {
            if (slot < 0 || slot >= GlyphsPerPage)
                throw new ArgumentOutOfRangeException("slot");
            if (cellRows16 == null || cellRows16.Length != Cell)
                throw new ArgumentException("单元格必须是 " + Cell + " 行");
            Slots[slot] = m_PendingCodepoint[slot];
            m_Cells[slot] = cellRows16;
        }

        public int UsedSlots
        {
            get
            {
                int n = 0;
                for (int i = 0; i < GlyphsPerPage; ++i)
                    if (m_Cells[i] != null) n++;
                return n;
            }
        }

        /// <summary>最后一页可能不满，这里返回实际用到的槽数（从 0 连续到 n-1）。</summary>
        public int ContiguousSlotCount
        {
            get
            {
                int n = 0;
                while (n < GlyphsPerPage && m_Cells[n] != null) n++;
                return n;
            }
        }

        /// <summary>
        /// 写出这一页的 .TGA 与 .MAP。
        /// </summary>
        /// <param name="tgaPath">CHINESE&lt;p&gt;.TGA 路径</param>
        /// <param name="mapPath">CHINESE&lt;p&gt;.MAP 路径</param>
        /// <param name="template">
        /// 可选的原始 TGA 文件内容（131611 字节）。给了就用它的扩展区和页脚，
        /// 保证除像素区外与游戏原文件逐字节一致。不给则自己拼一个最小可用的。
        /// </param>
        /// <param name="slotCount">本页实际写出的槽位数（末页用）</param>
        public void Save(string tgaPath, string mapPath, byte[]? template, int slotCount)
        {
            EnsureDir(tgaPath);
            EnsureDir(mapPath);
            File.WriteAllBytes(tgaPath, BuildTga(template));
            WriteMap(mapPath, slotCount);
        }

        /// <summary>
        /// 写一个**空页**：MAP 只有 BOM（2 字节），TGA 是空白页。
        ///
        /// 为什么需要它：Game.exe 加载字体时（0xab1a0）**无条件**循环
        /// 0..pageCount-1 页，每一页都要打开对应的 MAP 和 TGA，缺一页就
        /// 踩空指针崩溃（读地址 0x00000002）。所以页数必须**恰好**等于
        /// pageCount，不足的页只能拿空页补齐，不能省掉文件。
        ///
        /// 空页为什么是安全的：引擎按**文件长度**算字形数（count = 长度/2，
        /// 第一个 16 位字是 BOM 被跳过）。2 字节的 MAP -&gt; count = 1 -&gt;
        /// `cmp eax,1 / jb` 那条只在 count &lt; 1 时报错，count=1 照样往下走，
        /// 而循环条件是 `i+1 &lt; count` 即 `1 &lt; 1` 不成立 -&gt; **一个字形都不写**，
        /// 既不报错也不越界读。TGA 仍然会被打开（引擎要读它的宽度算 UV），
        /// 所以空页的 TGA 也必须存在，且是一张合法的图。
        ///
        /// 历史备注：早先这里塞过一个 U+0000 占位码位（MAP = FF FE 00 00），
        /// 唯一原因是 **DuneEx 2 拒绝"空"文件**。改用本项目的 Tools/rfh.py
        /// pack 打包后就没这个限制了，于是回到最干净的 2 字节 BOM。
        /// 若将来又要用 DuneEx 2 打包，把下面那行换成
        /// `new byte[] { 0xFF, 0xFE, 0x00, 0x00 }` 即可 —— 引擎不会去查 U+0000
        /// （字符串长度按 wcslen 量，NUL 是终止符不计入），所以也安全。
        /// </summary>
        public static void SaveEmpty(string tgaPath, string mapPath, byte[]? template)
        {
            EnsureDir(tgaPath);
            EnsureDir(mapPath);
            // 没填任何槽位 → 像素区全 0 → 全空白页
            File.WriteAllBytes(tgaPath, new EbfdFontPage().BuildTga(template));
            // 只有 BOM：count = 1 → 0 个字形
            File.WriteAllBytes(mapPath, new byte[] { 0xFF, 0xFE });
        }

        private byte[] BuildTga(byte[]? template)
        {
            int pixelsLen = AtlasW * AtlasH * 2;
            const int extLen = 495;
            const int footerLen = 26;

            byte[] buf;
            if (template != null)
            {
                if (template.Length != HeaderLen + pixelsLen + extLen + footerLen)
                    throw new InvalidDataException(
                        "模板 TGA 大小不是预期的 131611 字节。");
                buf = (byte[])template.Clone();
                // 清空像素区
                Array.Clear(buf, HeaderLen, pixelsLen);
            }
            else
            {
                buf = new byte[HeaderLen + pixelsLen];
                // TGA 头：idLen=0, cmapType=0, imgType=2(真彩), cmap=0/0/0,
                //         x=0, y=0, w=256, h=256, bpp=16, descriptor=0(自下而上)
                buf[2] = 2;
                buf[12] = (byte)(AtlasW & 0xFF);
                buf[13] = (byte)(AtlasW >> 8);
                buf[14] = (byte)(AtlasH & 0xFF);
                buf[15] = (byte)(AtlasH >> 8);
                buf[16] = 16;
                buf[17] = 0;
            }

            for (int slot = 0; slot < GlyphsPerPage; ++slot)
            {
                string[]? cell = m_Cells[slot];
                if (cell == null) continue;
                int cx = slot % Grid, cy = slot / Grid;
                int x0 = cx * Cell, y0 = cy * Cell;
                for (int dy = 0; dy < Cell; ++dy)
                {
                    string line = cell[dy];
                    for (int dx = 0; dx < Cell; ++dx)
                    {
                        ushort v = (dx < line.Length && line[dx] == '1') ? Ink : (ushort)0;
                        // 行自下而上
                        int fileY = AtlasH - 1 - (y0 + dy);
                        int off = HeaderLen + (fileY * AtlasW + (x0 + dx)) * 2;
                        buf[off] = (byte)(v & 0xFF);
                        buf[off + 1] = (byte)(v >> 8);
                    }
                }
            }

            return buf;
        }

        private void WriteMap(string path, int slotCount)
        {
            // BOM + 每码位 2 字节 UTF-16LE，无结尾 NUL
            byte[] outBuf = new byte[2 + slotCount * 2];
            outBuf[0] = 0xFF;
            outBuf[1] = 0xFE;
            for (int i = 0; i < slotCount; ++i)
            {
                int cp = Slots[i];
                outBuf[2 + i * 2] = (byte)(cp & 0xFF);
                outBuf[3 + i * 2] = (byte)((cp >> 8) & 0xFF);
            }
            File.WriteAllBytes(path, outBuf);
        }

        private static void EnsureDir(string path)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
    }
}
