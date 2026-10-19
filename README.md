# RA2FntToEbfd

把《红色警戒 2》的 `game.fnt` 点阵字体转成 *Emperor: Battle for Dune* 《帝王：沙丘之战》 的
CFont3D 字库（`CHINESE<p>.TGA` + `CHINESE<p>.MAP`）。

字形直接从 `game.fnt` 逐位拷贝、按原版字库的规律**水平居中**放进单元格，
不经过任何字体渲染引擎。

## AI 声明

本项目使用 DeepSeek v4.1 Flash 进行代码生成和文档撰写，经过了少量的人工审查、游戏实际测试。

## 构建

需要 .NET 10 SDK：

```
dotnet build
```

## 命令

```
RA2FntToEbfd convert  <字符清单.txt> <game.fnt> [输出目录] [选项]   主命令
RA2FntToEbfd info     <game.fnt>                                    只看字体信息
RA2FntToEbfd dump-all <game.fnt> [输出目录] [选项]                   导出 game.fnt 全部字符
```

| 选项 | 说明 |
|---|---|
| `-t, --template <TGA>` | 原始图集（`CHINESE0.tga`）当模板，复用其扩展区和页脚，保证除像素区外与游戏原文件逐字节一致。建议提供 |
| `--page-limit <n>` | 页数，默认 68。程序会保证生成的页数正好这么多 |
| `-p, --prefix <name>` | 输出文件名前缀，默认 `CHINESE`。可用的只有 `CHINESE`(68 页) 和 `KOREAN`(10 页)：`SMALL`/`STANDARD`/`TEXTICONS` 属于非 CJK 字体，图集布局和页数规则都不同，见 `Docs/FORMAT.md` §3.3 |
| `--preview <n>` | 用 ASCII 艺术打印前 n 个字形，肉眼检查用 |

示例：

```
RA2FntToEbfd convert chars.txt Files/game.fnt out -t Files/CHINESE0.tga
```

## 输入文件

您可在项目的 Files 目录下找到自带的字符清单、`game.fnt` 和 `CHINESE0.tga` 文件。

Files 目录下自带的 `game.fnt` 作者：手柄君，来自：https://github.com/Translate-with-LOVE/Ra2-zh_hans-main

Files 目录下自带的字符清单文件来自：https://github.com/SadPencil/10ThousandsCharsForEmperorBattleForDune

### 字符清单

字符清单必须为 UTF-8 文本文件，程序会按顺序取出里面出现的所有字符并去重。可使用以下任意一种写法：

```
们为发车门长电见语说
们 为 发   车 门
U+4EEC U+4E3A U+53D1
```

`game.fnt` 里没有字形的字符会被跳过，并在 stderr 按 Unicode 区块列出。

## 输出与部署

输出目录里固定 68 页（`CHINESE0..67.TGA` / `.MAP`；用 `-p KOREAN` 则是 10 页）
加一份 `manifest.json`。
不足 68 页的部分是自动补的空页（TGA 全空白，MAP 只有 2 字节 BOM —— 引擎
按文件长度算出的字形数是 0，既不报错也不越界读，见 `Docs/FORMAT.md` §3.2.1）。

**必须整套替换。** 游戏启动时会无条件打开该字体的全部页，少一页就崩
（`0x00000002 内存不能为 read`），多余的页则会被忽略。

字体放在 `DATA\` 下的两个归档里：

| 生成的文件 | 归档 | 归档内部的路径 |
|---|---|---|
| `CHINESE<p>.TGA` | `DATA\3DDATA0001.RFH` + `.RFD` | `Textures\CHINESE<p>.tga` |
| `CHINESE<p>.MAP` | `DATA\Ui0001.RFH` + `.RFD` | `FONT\CHINESE<p>.MAP` |

**不要**用 DuneEx 2 解包再打包，否则会把原归档里本来完好的文件解坏（见
`Docs/RFH.md` §4）。用 `Tools/rfh.py replace` 以原版归档为模板替换：

```bash
python3 Tools/rfh.py replace DATA/3DDATA0001.RFH <TGA目录> out/3DDATA0001.RFH
python3 Tools/rfh.py replace DATA/Ui0001.RFH     <MAP目录> out/Ui0001.RFH
python3 Tools/rfh.py check out/3DDATA0001.RFH
python3 Tools/rfh.py check out/Ui0001.RFH
```

TGA 和 MAP 必须一同替换：码位到页的分配和原字库不同，新旧混用会错字。

## 配套工具

`Tools/` 下是三个 Python 脚本：

| 脚本 | 作用 |
|---|---|
| `Tools/rfh.py` | 读写 `.RFH`/`.RFD`：`list` / `entry` / `extract` / `check` / `compare` / `pack` / `replace` |
| `Tools/check_tga.py` | 扫出导致引擎加载不了进而崩溃的 TGA 图像 |
| `Tools/selftest.py` | 自检：解压 → 原样打包 → 再解压 → 比对 |

用法与格式说明见 `Docs/RFH.md`。

## 文档

* `Docs/RFH.md` —— RFH/RFD 格式、引擎行为、打包流程，以及 DuneEx 2 的两个问题
* `Docs/FORMAT.md` —— `game.fnt` / CFont3D 格式，页数规则与崩溃原因的反汇编依据
* `Docs/GLYPHS.md` —— 字形摆放规则
