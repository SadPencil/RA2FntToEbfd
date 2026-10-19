using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.Text;

namespace FntToEbfd
{
    /// <summary>
    /// 把 RA2 的 game.fnt（Westwood "fonT" Unicode 点阵字体）转换成
    /// Emperor: Battle for Dune 的 CFont3D 字库（&lt;前缀&gt;&lt;p&gt;.TGA + .MAP）。
    ///
    /// 生成哪些字符由**字符清单文件**决定；输出文件名前缀可配置，
    /// 所以同一套流程也能生成别的语言的字体页。
    ///
    /// 命令行用 System.CommandLine 解析。
    /// </summary>
    public static class Program
    {
        // 单元格 16x16。实测原版字库 17,270 个字形的墨迹范围：
        // 宽度 15 的占 16,852 个（左留白 0），宽度 14/13 的分别左留白 1，
        // 宽度 16 的有 11 个（真的用到第 15 列）—— 见 BuildCell 的注释。
        private const int Cell = 16;

        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            // System.CommandLine 自带一个异常处理器，会打印 "Unhandled exception:" 加栈
            // 回溯、然后把异常吞掉，外面的 catch 根本拿不到；这里关掉它统一处理。
            var config = new InvocationConfiguration { EnableDefaultExceptionHandler = false };
            try
            {
                return BuildRootCommand().Parse(args).Invoke(config);
            }
            catch (Exception ex) when (ex is IOException            // 含 FileNotFoundException
                                       or InvalidDataException      // 注意：它继承 SystemException，不是 IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException)
            {
                // 可预期错误：文件读不了、game.fnt 的 magic 不是 "fonT"、
                // stride/高度非法……给一行干净提示，不要甩栈回溯。
                Console.Error.WriteLine();
                Console.Error.WriteLine("错误：" + ex.Message);
                return 1;
            }
        }

        // ------------------------------------------------------------ 命令定义

        private static RootCommand BuildRootCommand()
        {
            var root = new RootCommand(
                "把 RA2 的 game.fnt 转成 Emperor: Battle for Dune 的 CFont3D 字库。\n" +
                "生成哪些字符由字符清单文件决定；输出文件名前缀可配置，\n" +
                "所以同一套流程也能生成别的语言的字体页。");

            root.Subcommands.Add(BuildConvertCommand());
            root.Subcommands.Add(BuildInfoCommand());
            root.Subcommands.Add(BuildDumpAllCommand());
            return root;
        }

        private static Option<string> TemplateOption() => new("--template", "-t")
        {
            Description = "原始图集（如 CHINESE0.tga）当模板，复用其扩展区与页脚，"
                        + "保证除像素区外与游戏原文件逐字节一致。强烈建议提供。",
        };

        /// <summary>
        /// 只有 CHINESE(68 页) 和 KOREAN(10 页) 能用：引擎的字体表里另外三项
        /// （Small/Standard/TextIcons）是非 CJK 字体，图集是 128x128 / 64x64、
        /// 或者走不带页号的单页分支，和本工具固定的 256x256、16x16 格对不上。
        /// 详见 Docs/FORMAT.md §3.3。
        /// </summary>
        private static Option<string> PrefixOption() => new("--prefix", "-p")
        {
            Description = "输出文件名前缀，默认 CHINESE（68 页；KOREAN 是 10 页）。"
                        + "只有这两种字体能用，见 Docs/FORMAT.md §3.3。",
            DefaultValueFactory = _ => "CHINESE",
        };

        /// <summary>
        /// 字体页数（= Game.exe 里描述表的 pageCount）：Chinese 68、Korean 10、
        /// Small 2、Standard 1、TextIcons 1。本工具只支持前两种。
        ///
        /// 注意它**不只是上限**：加载函数 0xab1a0 无条件循环
        /// for (i = 0; i &lt; pageCount; i++)，每一页都要打开
        /// &lt;字体&gt;&lt;i&gt;.MAP 和 &lt;字体&gt;&lt;i&gt;.TGA，少一页就踩空指针崩溃
        /// （"0x00000002 内存不能为 read"，CJK 路径已实测）。所以输出页数必须
        /// **恰好**是 pageCount：少了程序会补空页，多了直接报错。
        /// </summary>
        private static Option<int> PageLimitOption() => new("--page-limit")
        {
            Description = "字体页数，默认 68（Game.exe 内 Chinese 的硬编码 pageCount）。"
                        + "页数超过它时多出的页引擎根本不会加载，所以直接报错；"
                        + "不足时自动补空页（MAP 只有 2 字节 BOM，引擎算出的字形数是 0）凑够它 —— "
                        + "因为引擎会无条件打开全部 68 页，少一页就会崩溃。",
            DefaultValueFactory = _ => 68,
        };

        /// <summary>convert：按清单文件转换（主命令）。</summary>
        private static Command BuildConvertCommand()
        {
            var listArg = new Argument<string>("list")
            {
                Description = "UTF-8 字符清单。里面出现的字符就是要生成的字符"
                            + "（自动去重、保持顺序）。支持三种写法混用：\n"
                            + "  们为发车门长电见语说\n"
                            + "  们 为 发   车 门\n"
                            + "  U+4EEC U+4E3A U+53D1",
            };
            var fntArg = new Argument<string>("font")
            {
                Description = "game.fnt（RA2 的 Unicode 变体点阵字体，magic \"fonT\"）",
            };
            var outArg = new Argument<string>("outdir")
            {
                Description = "输出目录",
                DefaultValueFactory = _ => "out",
            };
            var previewOpt = new Option<int>("--preview")
            {
                Description = "在终端用 ASCII 艺术打印前 n 个字形，便于肉眼检查。",
                DefaultValueFactory = _ => 0,
            };

            var templateOpt = TemplateOption();
            var prefixOpt = PrefixOption();
            var pageLimitOpt = PageLimitOption();

            var cmd = new Command("convert", "按字符清单转换（主命令）")
            {
                listArg, fntArg, outArg,
                templateOpt, prefixOpt, pageLimitOpt, previewOpt,
            };
            cmd.SetAction(pr => RunConvert(
                pr.GetValue(listArg)!, pr.GetValue(fntArg)!, pr.GetValue(outArg)!,
                pr.GetValue(templateOpt),
                pr.GetValue(prefixOpt)!, pr.GetValue(pageLimitOpt),
                pr.GetValue(previewOpt)));
            return cmd;
        }

        /// <summary>info：只打印 game.fnt 信息。</summary>
        private static Command BuildInfoCommand()
        {
            var fntArg = new Argument<string>("font") { Description = "game.fnt 路径" };
            var cmd = new Command("info", "只打印 game.fnt 的格式信息后退出") { fntArg };
            cmd.SetAction(pr =>
            {
                Console.WriteLine(WwFont.Load(pr.GetValue(fntArg)!).Describe());
                return 0;
            });
            return cmd;
        }

        /// <summary>dump-all：导出 game.fnt 里所有已映射码位。</summary>
        private static Command BuildDumpAllCommand()
        {
            var fntArg = new Argument<string>("font") { Description = "game.fnt 路径" };
            var outArg = new Argument<string>("outdir")
            {
                Description = "输出目录",
                DefaultValueFactory = _ => "out",
            };
            var templateOpt = TemplateOption();
            var prefixOpt = PrefixOption();
            var pageLimitOpt = PageLimitOption();

            var cmd = new Command("dump-all",
                "导出 game.fnt 里全部已映射码位（不需要字符清单）")
            {
                fntArg, outArg, templateOpt, prefixOpt, pageLimitOpt,
            };
            cmd.SetAction(pr => RunDumpAll(
                pr.GetValue(fntArg)!, pr.GetValue(outArg)!, pr.GetValue(templateOpt),
                pr.GetValue(prefixOpt)!, pr.GetValue(pageLimitOpt)));
            return cmd;
        }

        // ------------------------------------------------------------ 业务逻辑

        private static int RunConvert(string listPath, string fntPath, string outDir,
                                      string? templatePath,
                                      string prefix, int pageLimit, int preview)
        {
            if (!File.Exists(listPath))
            {
                Console.Error.WriteLine("错误：找不到字符清单文件：" + listPath);
                return 1;
            }
            if (!File.Exists(fntPath))
            {
                Console.Error.WriteLine("错误：找不到字体文件：" + fntPath);
                return 1;
            }
            List<int> codepoints = ParseCharList(listPath);
            Console.WriteLine("字符清单：" + listPath + " -> " +
                              codepoints.Count + " 个码位");
            if (codepoints.Count == 0)
            {
                Console.Error.WriteLine("清单里没有解析出任何字符。");
                return 1;
            }
            return ConvertCore(fntPath, outDir, templatePath, prefix,
                               pageLimit, preview, codepoints);
        }

        private static int RunDumpAll(string fntPath, string outDir, string? templatePath,
                                      string prefix, int pageLimit)
        {
            if (!File.Exists(fntPath))
            {
                Console.Error.WriteLine("错误：找不到字体文件：" + fntPath);
                return 1;
            }
            WwFont probe = WwFont.Load(fntPath);
            var all = new List<int>();
            for (int cp = 0; cp < 0x10000; ++cp)
                if (probe.HasGlyph(cp)) all.Add(cp);
            Console.WriteLine("dump-all：导出全部已映射码位 " + all.Count + " 个");
            return ConvertCore(fntPath, outDir, templatePath, prefix,
                               pageLimit, 0, all);
        }

        /// <summary>convert 与 dump-all 共用的主流程。</summary>
        private static int ConvertCore(string fntPath, string outDir, string? templatePath,
                                       string prefix, int pageLimit,
                                       int preview, List<int> codepoints)
        {
            Console.WriteLine("读取字体：" + fntPath);
            WwFont font = WwFont.Load(fntPath);
            Console.WriteLine(font.Describe());

            byte[]? template = null;
            if (templatePath != null)
            {
                if (!File.Exists(templatePath))
                {
                    Console.Error.WriteLine("错误：找不到模板：" + templatePath);
                    return 1;
                }
                template = File.ReadAllBytes(templatePath);
                Console.WriteLine("图集模板：" + templatePath +
                                  " (" + template.Length + " 字节)");
            }
            else
            {
                Console.Error.WriteLine(
                    "警告：未提供 --template。将自行拼接 TGA 页脚；"
                  + "建议用原始图集当模板以确保与游戏一致。");
            }

            // 分离有字形 / 缺字形
            var have = new List<int>();
            var missing = new List<int>();
            foreach (int cp in codepoints)
                (font.HasGlyph(cp) ? have : missing).Add(cp);

            if (missing.Count > 0)
            {
                // 清单里有、但 game.fnt 画不出来 —— 直接跳过。
                // 按 Unicode 区块归类输出，比列一堆 U+XXXX 更看得出缺的是什么。
                Console.Error.WriteLine();
                Console.Error.WriteLine(string.Format(
                    "警告：{0} 个字符在 game.fnt 里没有字形，已跳过"
                    + "（占清单 {0}/{1} = {2:0.00%}）。",
                    missing.Count, codepoints.Count,
                    (double)missing.Count / codepoints.Count));
                Console.Error.WriteLine("      按区块统计：");
                foreach (IGrouping<string, int> group in GroupByBlock(missing))
                {
                    Console.Error.WriteLine(string.Format(
                        "        {0,-30} {1,5} 个   例如 {2}",
                        group.Key, group.Count(),
                        string.Join(" ", group.Take(8)
                            .Select(c => (char)c + "(U+" + c.ToString("X4") + ")")
                            .ToArray())));
                }
                Console.Error.WriteLine("      这些字符不会出现在输出里；如需补齐请另找字形来源。");
            }
            if (have.Count == 0)
            {
                Console.Error.WriteLine("没有任何码位能在 game.fnt 里找到字形。");
                return 1;
            }

            int pages = (have.Count + EbfdFontPage.GlyphsPerPage - 1)
                        / EbfdFontPage.GlyphsPerPage;
            Console.WriteLine();
            Console.WriteLine("将生成 " + pages + " 页数据，前缀 " + prefix +
                              "，目标页数 " + pageLimit);

            // 页数必须**恰好**等于 pageCount（Chinese 68 / Korean 10）。
            // 多出来的页不会被加载，那些字显示为空白，所以这里直接报错；
            // 不足的页稍后自动补空页补齐（少一页引擎会崩）。
            if (pages > pageLimit)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(string.Format(
                    "错误：需要 {0} 页（第 0..{1} 页），超过页数上限 {2}。",
                    pages, pages - 1, pageLimit));
                Console.Error.WriteLine(string.Format(
                    "      引擎（Game.exe 字体描述表）只会加载前 {0} 页，"
                    + "多出的页不会被读取，那些字会显示为空白。",
                    pageLimit));
                Console.Error.WriteLine(string.Format(
                    "      可用字形 {0} 个，上限容量 {1} 页 x {2} = {3} 个。",
                    have.Count, pageLimit, EbfdFontPage.GlyphsPerPage,
                    pageLimit * EbfdFontPage.GlyphsPerPage));
                Console.Error.WriteLine(
                    "      请精简字符清单，或用 --page-limit 显式放宽（不推荐）。");
                return 1;
            }

            // 反过来，页数**不足**也必须补齐：加载函数 0xab1a0 无条件循环
            // 0..pageCount-1 页，每页都要打开 MAP 和 TGA，缺一页就崩溃。
            int emptyPages = pageLimit - pages;
            if (emptyPages > 0)
            {
                Console.WriteLine(string.Format(
                    "将补齐 {0} 个空页（第 {1}..{2} 页）：引擎会无条件打开全部 {3} 页，"
                    + "缺页会崩溃。",
                    emptyPages, pages, pageLimit - 1, pageLimit));
            }

            if (preview > 0)
            {
                Console.WriteLine();
                Console.WriteLine("--- 字形预览（转换后的 16x16 单元格）---");
                foreach (int cp in have.Take(preview))
                {
                    Console.WriteLine("U+" + cp.ToString("X4") + "  " + (char)cp +
                                      "  (源宽 " + font.WidthOf(cp) + ")");
                    foreach (string line in BuildCell(font, cp))
                        Console.WriteLine("    " + line.Replace('1', '#')
                                                        .Replace('0', '.'));
                }
            }

            Directory.CreateDirectory(outDir);
            // 输出目录里若还留着上次跑出来的、本次不会覆盖的页，很容易跟着一起
            // 打进 RFH（文件名对得上就会被打包）——先提醒。
            WarnStalePages(outDir, prefix, pageLimit);
            var manifestHead = new StringBuilder();
            manifestHead.AppendLine("{");
            manifestHead.AppendLine("  \"source\": " + Json(fntPath) + ",");
            manifestHead.AppendLine("  \"prefix\": " + Json(prefix) + ",");
            var manifest = new StringBuilder();
            manifest.AppendLine("  \"pages\": [");

            const int FirstPage = 0;
            int pageNo = FirstPage;
            for (int baseIdx = 0; baseIdx < have.Count;
                 baseIdx += EbfdFontPage.GlyphsPerPage)
            {
                var page = new EbfdFontPage();
                int n = Math.Min(EbfdFontPage.GlyphsPerPage, have.Count - baseIdx);
                for (int i = 0; i < n; ++i)
                {
                    int cp = have[baseIdx + i];
                    page.SetCodepoint(i, cp);
                    page.SetGlyph(i, BuildCell(font, cp));
                }
                string tga = Path.Combine(outDir, prefix + pageNo + ".TGA");
                string map = Path.Combine(outDir, prefix + pageNo + ".MAP");
                page.Save(tga, map, template, n);

                Console.WriteLine(string.Format(
                    "  第 {0,3} 页：{1,3} 字形  U+{2:X4}..U+{3:X4}  -> {4} / {5}",
                    pageNo, n, have[baseIdx], have[baseIdx + n - 1],
                    Path.GetFileName(tga), Path.GetFileName(map)));

                if (pageNo > FirstPage) manifest.AppendLine(",");
                manifest.Append("    { \"page\": " + pageNo +
                                ", \"glyphs\": " + n +
                                ", \"first\": \"U+" + have[baseIdx].ToString("X4") +
                                "\", \"last\": \"U+" + have[baseIdx + n - 1].ToString("X4") +
                                "\", \"tga\": " + Json(Path.GetFileName(tga)) +
                                ", \"map\": " + Json(Path.GetFileName(map)) + " }");
                pageNo++;
            }
            int dataPages = pageNo;

            // 补齐空页，让页数恰好等于 pageLimit。
            // 空页 = 只有 BOM 的 2 字节 MAP + 全空白 TGA；引擎照常打开它们，
            // 但算出的字形数是 0，所以不影响任何字符（详见 EbfdFontPage.SaveEmpty）。
            for (; pageNo < pageLimit; ++pageNo)
            {
                string tga = Path.Combine(outDir, prefix + pageNo + ".TGA");
                string map = Path.Combine(outDir, prefix + pageNo + ".MAP");
                EbfdFontPage.SaveEmpty(tga, map, template);

                if (pageNo > FirstPage) manifest.AppendLine(",");
                manifest.Append("    { \"page\": " + pageNo +
                                ", \"glyphs\": 0, \"empty\": true, \"tga\": " +
                                Json(Path.GetFileName(tga)) + ", \"map\": " +
                                Json(Path.GetFileName(map)) + " }");
            }
            manifest.AppendLine();
            manifest.AppendLine("  ]");
            manifest.AppendLine("}");

            if (emptyPages > 0)
            {
                Console.WriteLine(string.Format(
                    "  第 {0,3}..{1,3} 页：空页（MAP = 2 字节 BOM，TGA = 空白）",
                    dataPages, pageLimit - 1));
            }

            manifestHead.AppendLine("  \"requestedCharacters\": " + codepoints.Count + ",");
            manifestHead.AppendLine("  \"writtenGlyphs\": " + have.Count + ",");
            manifestHead.AppendLine("  \"skippedNoGlyph\": " + missing.Count + ",");
            manifestHead.AppendLine("  \"dataPages\": " + dataPages + ",");
            manifestHead.AppendLine("  \"emptyPages\": " + emptyPages + ",");
            manifestHead.AppendLine("  \"totalPages\": " + pageLimit + ",");
            string manPath = Path.Combine(outDir, "manifest.json");
            File.WriteAllText(manPath, manifestHead.ToString() + manifest.ToString(),
                              new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine(string.Format(
                "完成：共 {0} 页（{1} 页数据 + {2} 页空页）；清单 {3}",
                pageLimit, dataPages, emptyPages, manPath));
            return 0;
        }

        /// <summary>
        /// 检查输出目录里有没有本次不会覆盖的、旧的 &lt;前缀&gt;&lt;数字&gt;.TGA/.MAP。
        ///
        /// 只警告不阻断：`rfh.py replace` 按条目名或唯一文件名匹配替换文件，多出来的
        /// 旧页会被一起打进归档，所以必须在输出里明确提示。
        /// </summary>
        private static void WarnStalePages(string outDir, string prefix, int pageLimit)
        {
            var stale = new List<string>();
            foreach (string path in Directory.EnumerateFiles(outDir, prefix + "*"))
            {
                string ext = Path.GetExtension(path).ToUpperInvariant();
                if (ext != ".TGA" && ext != ".MAP") continue;
                string stem = Path.GetFileNameWithoutExtension(path);
                if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string digits = stem.Substring(prefix.Length);
                if (digits.Length == 0 || !digits.All(char.IsDigit)) continue;
                if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture,
                                 out int page) && page >= pageLimit)
                {
                    stale.Add(Path.GetFileName(path));
                }
            }
            if (stale.Count == 0) return;

            stale.Sort(StringComparer.OrdinalIgnoreCase);
            Console.Error.WriteLine();
            Console.Error.WriteLine(string.Format(
                "警告：输出目录里已有 {0} 个本次不会覆盖的旧文件（第 {1} 页起）：",
                stale.Count, pageLimit));
            foreach (string name in stale.Take(6))
                Console.Error.WriteLine("        " + name);
            if (stale.Count > 6)
                Console.Error.WriteLine("        ... 其余 " + (stale.Count - 6) + " 个");
            Console.Error.WriteLine(
                "      `rfh.py replace` 按条目名或唯一文件名匹配替换文件，这些旧页会被一起打进 RFH；"
              + "建议先清空输出目录，或确认它们确实要保留。");
        }

        // ------------------------------------------------------------ 字形摆放

        /// <summary>把码位按 Unicode 区块归类，用于"缺字形"警告信息。</summary>
        private static IEnumerable<IGrouping<string, int>> GroupByBlock(IEnumerable<int> codePoints)
        {
            return codePoints
                .GroupBy(DescribeBlock)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal);
        }

        private static string DescribeBlock(int cp)
        {
            if (cp < 0x0080) return "Basic Latin";
            if (cp < 0x0100) return "Latin-1 Supplement";
            if (cp < 0x0180) return "Latin Extended-A";
            if (cp < 0x0250) return "Latin Extended-B";
            if (cp < 0x02B0) return "IPA Extensions";
            if (cp < 0x0300) return "Spacing Modifier Letters";
            if (cp < 0x0370) return "Combining Diacritical Marks";
            if (cp < 0x0400) return "Greek and Coptic";
            if (cp < 0x0500) return "Cyrillic";
            if (cp < 0x2000) return "其它字母";
            if (cp < 0x2070) return "General Punctuation";
            if (cp < 0x20D0) return "Superscripts and Subscripts";
            if (cp < 0x2100) return "Currency Symbols";
            if (cp < 0x2150) return "Letterlike Symbols";
            if (cp < 0x2190) return "Number Forms";
            if (cp < 0x2200) return "Arrows";
            if (cp < 0x2300) return "Mathematical Operators";
            if (cp < 0x2400) return "Miscellaneous Technical";
            if (cp < 0x2500) return "Control Pictures";
            if (cp < 0x2580) return "Box Drawing";
            if (cp < 0x25A0) return "Block Elements";
            if (cp < 0x2600) return "Geometric Shapes";
            if (cp < 0x2700) return "Miscellaneous Symbols";
            if (cp < 0x3000) return "Dingbats / 其它符号";
            if (cp < 0x3040) return "CJK Symbols and Punctuation";
            if (cp < 0x30A0) return "Hiragana";
            if (cp < 0x3100) return "Katakana";
            if (cp < 0x3130) return "Bopomofo";
            if (cp < 0x3190) return "Hangul Compatibility Jamo";
            if (cp < 0x3200) return "Kanbun";
            if (cp < 0x3300) return "Enclosed CJK Letters and Months";
            if (cp < 0x3400) return "CJK Compatibility";
            if (cp < 0x4E00) return "CJK Extension A";
            if (cp < 0xA000) return "CJK Unified Ideographs";
            if (cp < 0xF900) return "Yi / 其它";
            if (cp < 0xFB00) return "CJK Compatibility Ideographs";
            if (cp < 0xFF00) return "Alphabetic Presentation Forms";
            if (cp < 0xFFF0) return "Halfwidth and Fullwidth Forms";
            return "其它";
        }

        /// <summary>
        /// 把一个 game.fnt 字形放进 16x16 的单元格。
        ///
        /// **行方向：原样拷贝，不做垂直位移。** 实测 game.fnt 与 Emperor 原字形的
        /// 行号完全一致（「一」在两者里都在第 8 行，179 个字符里 120 个逐像素完全相同），
        /// 加任何行偏移都会让匹配率掉到 0。
        ///
        /// **列方向：按字形实际宽度水平居中，左留白 = (16 - 宽) / 2（整数除法）。**
        /// game.fnt 的字形宽度是逐字不同的（贝 = 11、口 = 14、丨 = 2、一 = 15），
        /// 而引擎给 CJK 字形算的**步进是整格固定值**（0xab820 里 [glyph+0x10] 由格子
        /// 尺寸算出，不看字形宽度）——所以左对齐放进去就会在右边多出一块空白。
        ///
        /// 居中这条规律是从**原版字库**自己的数据里量出来的（68 页、17,270 个字形）：
        ///
        ///   墨迹宽度 15 -> 左留白 0（16,852 个字形，占绝大多数）
        ///   墨迹宽度 14 -> 左留白 1（300 个）
        ///   墨迹宽度 13 -> 左留白 1（78 个）
        ///   墨迹宽度 12 -> 左留白 2（ 9 个）
        ///   墨迹宽度  2 -> 左留白 7（丨）、4 -> 6（丶）、6 -> 5（丿）……
        ///   墨迹宽度 16 -> 左留白 0（11 个，**原版自己也用第 15 列**）
        ///
        /// 全部等于 floor((16 - 宽) / 2)，多出来的那一列落在右边。拿两套字库里都有的
        /// 字逐字对照也一致：丨(w=2)→左7、丶(4)→6、丿(6)→5、冒(12)→2、内(13)→1、
        /// 口(14)→1、一(15)→0。
        ///
        /// 既不缩放也不裁剪：16→15 的最近邻采样会丢掉正中第 8 列（实测 38 个 16px 宽的
        /// 字形合计损失 6.4% 墨点，密的如「す」丢 22.4%），裁剪会断掉右边笔画。
        /// 居中只是整体平移，零损失。
        /// </summary>
        private static string[] BuildCell(WwFont font, int cp)
        {
            string[]? src = font.GetGlyphRows(cp);
            var cell = new string[Cell];
            if (src == null)
            {
                for (int i = 0; i < Cell; ++i) cell[i] = new string('0', Cell);
                return cell;
            }

            int h = Math.Min(src.Length, Cell);
            int w = src.Length > 0 ? src[0].Length : 0;
            int left = w <= Cell ? (Cell - w) / 2 : 0;

            for (int y = 0; y < Cell; ++y)
                cell[y] = CellRow(y < h ? src[y] : "", left);
            return cell;
        }

        /// <summary>把字形的一行摆进 16 列的格子里：从 left 列开始，其余补 0。</summary>
        private static string CellRow(string line, int left)
        {
            var sb = new StringBuilder(Cell);
            for (int x = 0; x < Cell; ++x)
            {
                int sx = x - left;
                sb.Append(sx >= 0 && sx < line.Length ? line[sx] : '0');
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------ 清单解析

        /// <summary>
        /// 支持三种写法混用：
        ///   直接写字符   们为发车
        ///   空格/换行分隔 们 为 发
        ///   U+XXXX       U+4EEC U+4E3A
        /// 空白字符被当作分隔符跳过，结果去重并保持出现顺序。
        /// 非 BMP 字符（&gt;U+FFFF）跳过并警告。
        /// </summary>
        private static List<int> ParseCharList(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            var result = new List<int>();
            var seen = new HashSet<int>();
            int nonBmp = 0;

            var stripped = new StringBuilder();
            for (int i = 0; i < text.Length; ++i)
            {
                if ((text[i] == 'U' || text[i] == 'u') &&
                    i + 1 < text.Length && text[i + 1] == '+')
                {
                    int j = i + 2;
                    StringBuilder hex = new StringBuilder();
                    while (j < text.Length && Uri.IsHexDigit(text[j]))
                    {
                        hex.Append(text[j]);
                        ++j;
                    }
                    if (hex.Length > 0 && hex.Length <= 6)
                    {
                        int cp = int.Parse(hex.ToString(), NumberStyles.HexNumber,
                                           CultureInfo.InvariantCulture);
                        if (cp <= 0xFFFF)
                        {
                            if (seen.Add(cp)) result.Add(cp);
                        }
                        else nonBmp++;
                        i = j - 1;
                        continue;
                    }
                }
                stripped.Append(text[i]);
            }

            foreach (char c in stripped.ToString())
            {
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
                int cp = c;
                if (!char.IsSurrogate(c) && seen.Add(cp)) result.Add(cp);
            }

            if (nonBmp > 0)
                Console.Error.WriteLine("警告：跳过 " + nonBmp +
                                        " 个非 BMP 码位（>U+FFFF）。");
            return result;
        }

        private static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
