# 格式与引擎行为

本文是 `RA2FntToEbfd` 的细节文档：输入/输出格式、页数规则，以及从 `Game.exe` 反汇编得到的加载行为。

---

## 1. 输入：`game.fnt`（RA2 Unicode 变体）

magic 是 **`fonT`**（全小写）。全部为小端序。

| 偏移 | 大小 | 含义 |
|---|---|---|
| 0x00 | 4 | magic `"fonT"` |
| 0x04 | 4 | 表意文字宽度（全角字宽），本文件 20 |
| 0x08 | 4 | **stride**：每行占用的**字节数**，本文件 3（= 最多 24 像素宽） |
| 0x0C | 4 | **fontDataHeight**：字形数据高度（像素），本文件 16 |
| 0x10 | 4 | fontHeight：字体整体高度，本文件 17 |
| 0x14 | 4 | symbolCount：不同字形数据块的数量，本文件 33,201 |
| 0x18 | 4 | symbolDataSize = `stride × fontDataHeight + 1`，本文件 49 |
| 0x1C | 0x20000 | 索引表：65,536 项 `uint16`，**1-based**，0 = 该码位无字形 |
| 0x2001C | … | 字形数据块，共 symbolCount 个，每个 symbolDataSize 字节 |

每个字形数据块：

```
第 0 字节            = 字形宽度（像素，列数）
之后 stride×height   = 1bpp 位图，行主序
```

**位序是「大端」**：行首字节的最高位是该行第一个像素，行内多余位补 0。

验证（用本仓库的 `Files/game.fnt`）：

```
预期大小 = 0x1C + 0x20000 + 33201 × 49 = 1,757,949
实际大小 = 1,757,949   ✓
```

> magic 若是 `"FoNt"`（大写 N），那是 RA2 的**非 Unicode** 变体
> （只有 256 个字形，索引表是 0x30 起的区间形式），本工具不支持，会明确报错。

---

## 2. 输出：Emperor CFont3D 字库

引擎从 `Game.exe` 里读出的加载模板是：

```
%s\%s.TGA      %s\%s.MAP        单页字体
%s\%s%d.TGA    %s\%s%d.MAP     多页字体（%d 是页号）
```

`Chinese` 走多页分支，所以输出的是带页号的 `CHINESE0.TGA` … `CHINESE67.TGA`（共 68 页）。

第一个 `%s` 是目录，取自 `Game.exe` 里一个指向字符串 `"FONT"` 的常量指针（RVA `0x2028d0`）。

生成的两类文件分别进两个不同的归档，条目名如下：
TGA 是 `Textures\CHINESE<p>.tga`（归档 `3DDATA0001`），
MAP 是 `FONT\CHINESE<p>.MAP`（归档 `Ui0001`）。

**`CHINESE<p>.TGA`**

| 项目 | 值 |
|---|---|
| 尺寸 | 256 × 256 |
| 格式 | TGA type 2，**16bpp**，但只用两个值：`0x0000`（底）/ `0x7FFF`（墨）→ 实质 1bit 单色 |
| descriptor | 0 → 行**自下而上** |
| 像素起点 | 文件偏移 18 |
| 字形网格 | 16 列 × 16 行 = 256 格，每格 16×16 像素 |
| 索引 | `slot = 行 × 16 + 列` |
| 文件布局 | `18 头 + 131072 像素 + 495 扩展区 + 26 页脚` = **131,611 字节**（**扩展区是可选的**：原版 `Korean0..8.tga` 就没有，长 131,116 字节；本工具按 `--template` 给的模板来，给了 CHINESE0.tga 就会带扩展区） |

**`CHINESE<p>.MAP`**

```
UTF-16LE + BOM，每个码位 2 字节，**没有结尾 NUL**
满页 256 项 = 514 字节；末页按实际数量（例如 93 项 = 188 字节）
```

**页号怎么算（关键）**

引擎按 **MAP 文件里字符的排列顺序**分页，不是按码位：

```
页号 = 字符在 MAP 里的序位 ÷ 256
```

也就是说第 0–255 个字符在第 0 页，第 256–511 个在第 1 页，依此类推。
原始字库的序位 0 对应 U+4E00，这是巧合（原始字库刚好从 U+4E00 起连续排列），
**不是** `(码位 − 0x4E00) / 256`。序位完全由 MAP 的顺序决定。

---

## 3. 页数：必须正好等于 pageCount（重要）

`Game.exe` 的**字体描述表**在 `.rdata`（VA `0x5d3540`，RVA／文件偏移 `0x1d3540`），每项 16 字节：

| # | 名称 | +4（尺寸） | +5（CJK 标志） | **+8 pageCount** | pageCount 的文件偏移 |
|---|---|---|---|---|---|
| 0 | Small | 16 | 0 | 2 | `0x1d3548` |
| 1 | Standard | 30 | 0 | 1 | `0x1d3558` |
| 2 | **Korean** | 16 | 1 | **10** | `0x1d3568` |
| 3 | **Chinese** | 16 | 1 | **68** | `0x1d3578` |
| 4 | TextIcons | 30 | 0 | 1 | `0x1d3588` |
| 5 | （结束，全零） | | | | |

**页数是「每种字体各自的」，不是全局 68。**

加载函数在 `0xab1a0`，参数是**字体序号**：

```asm
ab1a8  mov  esi,[esp+0x214]
ab1b0  shl  esi,0x4              ; esi = 字体序号 × 16 = 本项的字节偏移
ab1b5  mov  eax,[esi+0x5d3548]   ; eax = 本字体的 pageCount（不是常量 68）
ab1bb  cmp  eax,1
ab1be  jle  0xab282              ; <=1 → 单页分支（"%s\%s.TGA"，不带页号）
ab1c4  xor  edi,edi              ; edi = 0，页号
ab1c6  test eax,eax
ab1c8  jle  0xab321              ; <=0 才整体跳过
ab1ce  ...                       ; 用 edi 拼 "%s\%s%d.TGA" / ".MAP" 并加载
ab26d  inc  edi
ab26e  cmp  edi,eax
ab270  jl   0xab1ce              ; edi < pageCount 就继续
```

上层 `0xab160` 会遍历整张表，对**每一种**字体各调用一次（遇到名字指针为空即停）。

### 3.1 关键：页数必须**恰好**等于 pageCount，少一页就崩

循环体里**没有任何"这一页存在吗"的判断，也没有 break**。68 就是实打实的 68 次
「打开 `Chinese<p>.MAP` + 打开 `Chinese<p>.TGA`」。缺文件时的行为：

```asm
ab835  call 0x3830               ; 资源是否存在
ab83c  jne  0xab84c
ab83e  push esi / push 0x602930  ; "CFont3D - Missing font map file %s"
ab844  call 0x1cc0               ; 只是打印，然后继续
ab85c  call 0x3490               ; 读 MAP —— 找不到时 0x34c3 那条路 xor eax,eax; ret
ab869  test ebx,ebx
ab883  push esi / push 0x60290c  ; "CFont3D - Invalid font map file %s."
ab889  call 0x1cc0               ; 还是只打印，继续
ab8d0  lea  edx,[ebx+0x2]        ; 缓冲区是空指针，游标却被算成 0x00000002
ab8e4  mov  [esp+0x18],edx
ab921  mov  eax,[esp+0x18]
ab927  mov  bx,[eax]             ; ← 读 0x00000002 → 访问违例
```

两条错误信息都只是打印，**打完继续执行**，然后在解析循环里踩空指针。所以实测表现是
**直接崩溃（"0x00000002 内存不能为 read"）**，不是弹个框就算了。

TGA 那边同样没有检查：`ab899 call 0x14c040` 紧跟 `ab8b4 mov edx,[eax+0xc]` /
`ab8bb mov ecx,[eax+0x14]`（取宽度和格式算 UV），中间没有 `test eax,eax`，
所以 TGA 缺了应该同样会崩（**未单独实测**，但 Chinese 那 68 页本来就必须齐）。

反过来，**多出来的文件是无害的**：循环到 pageCount 就停，第 68 页起根本不看。

#### 注意：两种加载路径对「缺 MAP」的容忍度不一样

上面那段崩溃是 **CJK 路径（`0xab820`，Chinese/Korean）** 的行为，已实测。非 CJK 路径
（`0xab330`，Small/Standard/TextIcons）多一层保护：

```asm
ab402  cmp  edx,1
ab408  jbe  0xab7a2              ; count <= 1 -> 一个字形都不解析，直接收尾
```

CJK 路径没有这层 `count <= 1` 保护，`count` 在读取失败时还是那块坏值，
被 `cmp ecx,0x101` 截到 257，于是硬解析 256 个字形、踩空指针。

### 3.2 引擎怎么数一页里有多少字形

`0xab820` 里 `count = 文件长度 / 2`（`ab861 shr ecx,1`），`count > 0x101` 时截断成
`0x101`，然后循环 `i = 0 .. count-2`，写入 `count-1` 个字形 —— 第一个 16 位字是
BOM，被跳过（游标从 `缓冲区+2` 起，`ab8d0 lea edx,[ebx+0x2]`）。所以：

| MAP 文件 | words | 写入字形数 |
|---|---|---|
| 514 字节（满页） | 257 | 256 |
| 238 字节（原始字库那个末页） | 119 | 118 |
| 2 字节（只有 BOM） | 1 | **0 —— 就是补出来的空页** |

也就是说**页不必是 514 字节**，引擎按文件长度解析、不会越界读。

### 3.2.1 空页长什么样

页数不足时本工具要补空页，空页的 MAP **只有 BOM，2 字节**：

```
空页 MAP = FF FE          空页 TGA = 全空白页（沿用模板的头/扩展区/页脚）
```

引擎侧：`count = 2/2 = 1`，`ab877 cmp eax,1 / jb` 只在 `count < 1` 时报错，
count=1 照样往下走；循环条件是 `i+1 < count` 即 `1 < 1`，不成立 ——
于是**一个字形都不写**，既不报错也不越界读。TGA 也必须存在（引擎要读它的宽度
算 UV），所以空页仍配一张合法的空白图。

> 注意：**DuneEx 2 会拒绝空文件**（见 [`RFH.md`](RFH.md) §4.2），用它打包时这个
> 空页 MAP 要写成 `FF FE 00 00`（BOM + 一个 U+0000 占位码位）。U+0000 本身是安全的：
> 引擎量字符串长度用 `wcslen` 语义（`0x1b1a5e`，NUL 是终止符、不计入长度），
> 被渲染的字符串里不会出现它；全 exe 也没有直接读字形表第 0 项的地方
> （`mov ecx,0x7d7650` 的 32 处引用都在 `GetGlyph` 内部，一定带码位参数）。
> 本项目自己的 `Tools/rfh.py` 没有这个限制，所以默认写最干净的 2 字节。

原始归档正好印证了这张表：

| 字体 | pageCount | TGA（归档 `3DDATA0001` 里的条目） | MAP（归档 `Ui0001` 里的条目） |
|---|---|---|---|
| Small | 2 | `small0.tga` 128×128、`small1.tga` 64×64（另有没人读的 `small.tga`） | `SMALL0.MAP`(155 码位)、`SMALL1.MAP`(10)（另有 `SMALL.MAP`） |
| Standard | 1 | `Standard.tga` 256×256 | `STANDARD.MAP`(165) |
| Korean | **10** | `Korean0..9.tga` 256×256 | `KOREAN0..9.MAP` |
| Chinese | 68 | `CHINESE0..67.tga` 256×256 | `CHINESE0..67.MAP`（67 页 514 B + 1 页 238 B） |
| TextIcons | 1 | `TextIcons.tga` 32×32（归档 `3DDATA0003`） | 没有 |

`Standard.tga` / `TextIcons.tga` 不带数字，正是 `cmp eax,1 / jle` 那条单页分支的结果。

另外归档 `3DDATA0001` 里还有两个**不在描述表里**的字体贴图：条目 `Textures\font3d.tga`
（256×256，32bpp）和 `Textures\font3dsmall.tga`（128×128，32bpp），是 3D 场景里用的
字体纹理，本工具不涉及。

### 3.3 所以 `-p/--prefix` 不是随便填的

| 前缀 | 引擎要的页数 | 文件名对得上？ | 本工具能用？ |
|---|---|---|---|
| `CHINESE` | **68** | 是（`CHINESE<p>.TGA` / `.MAP`） | **可以**，默认值 |
| `KOREAN` | **10** | 是（`Korean<p>.TGA` / `KOREAN<p>.MAP`） | **可以**，要显式 `--page-limit 10` |
| `SMALL` | 2 | 是（`small<p>.tga` / `SMALL<p>.MAP`） | **不行**：它是非 CJK 字体，图集是 128×128 / 64×64，而本工具固定按 **256×256、16×16 格**生成，格子尺寸和槽位数都对不上 |
| `STANDARD` | 1 | **否**：pageCount=1 走单页分支，引擎找的是**不带页号**的 `FONT\Standard.TGA`/`.MAP` | **不行**（本工具只会写 `<前缀>0.TGA`） |
| `TEXTICONS` | 1 | 同上（不带页号），而且它**没有 MAP** | **不行** |

所以实际可用的只有 `--prefix CHINESE`（68 页）和 `--prefix KOREAN`（10 页）——
不是"引擎只有这两种字体"，而是**只有这两种的页数与图集布局和本工具的 16×16 格子
对得上**。Small/Standard/TextIcons 属于非 CJK 那条加载路径（`0xab330`），要支持得
另外实现一套图集布局。

`--page-limit` 默认 **68**（= Chinese 的 pageCount），**用 `-p KOREAN` 时请显式加
`--page-limit 10`**。

它同时管两头：

* 页数**超过**它 → 直接报错（多出的页引擎根本不加载，生成出来也是废的）；
* 页数**不足**它 → **自动补空页**（MAP 只有 2 字节 BOM，TGA 是全空白页），
  凑够 pageCount。这一步不能省 —— 少一页游戏启动时就崩。

> 想让 Korean 也能用更多页，得改 `Game.exe` 偏移 `0x1d3568` 的那个 dword（10 → 68）；
> Chinese 的 68 在 `0x1d3578`。改完记得同步调 `--page-limit`。

---

## 4. 源码结构与出处

| 文件 | 作用 |
|---|---|
| `WwFont.cs` | `game.fnt` 解析器（含格式详解注释） |
| `EbfdFontPage.cs` | Emperor 字库写出（TGA + MAP，含格式详解注释） |
| `Program.cs` | 命令行入口、字符清单解析、字形摆放、页数检查 |
| `RA2FntToEbfd.csproj` | .NET 10 工程（Nullable + ImplicitUsings 开启） |

`game.fnt` 的格式知识来自 Nyerguds 的
[WWFontEditor](https://github.com/OpenRA2/WWFontEditor)（WTFPL）：

* `WWFontEditor/Domain/FontTypes/FontFileWsBfUni.cs`
* `WWFontEditor/Domain/Utils/ImageUtils/ImageUtils.cs`

本工程是独立实现，未复用其代码。

Emperor 字库格式（`.TGA`/`.MAP` 布局、16bpp 单色编码、字形在格子里的摆放规律）
与 `Game.exe` 里的 pageCount、加载行为、字形步进方式，均由本仓库对游戏的原始
文件与可执行文件实测得出；摆放规律的验证见 [`GLYPHS.md`](GLYPHS.md)。
