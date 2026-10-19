# RFH/RFD 归档与打包流程

Emperor 的资源不在散文件里，而在 `DATA\` 下的一对文件：`<名字>.RFH`（索引）
+ `<名字>.RFD`（数据）。本项目里的 `Tools/rfh.py` 能列目录、解包、逐条校验、
把归档内容和解包出来的目录逐文件比对（`compare`）、把目录打成归档（`pack`），
以及**以某个归档为模板只替换指定文件**（`replace`）。

本文记录格式、引擎行为，以及 **DuneEx 2 的两个问题**：解包会把 `flags=0` 的条目
解成全 0，打包会拒绝空文件（见 §4）。

---

## 1. 格式

所有整数小端。

### 1.1 `.RFH`（索引）

扁平记录，一条接一条，**没有全局头**，一直读到文件尾：

| 偏移 | 大小 | 含义 |
|---|---|---|
| 0 | 4 | `name_len`：文件名长度（含结尾 NUL） |
| 4 | 4 | `file_date`：**Unix 时间戳** |
| 8 | 4 | `flags`：`0` = 原样存储(stored)，`2` = deflate 压缩 |
| 12 | 4 | `stored`：这一块在 `.RFD` 里占的**整块**字节数 |
| 16 | 4 | `raw`：解压后（真实文件）的大小 |
| 20 | 4 | 该块在 `.RFD` 里的起始偏移 |
| 24 | `name_len` | 文件名，NUL 结尾，cp1252，分隔符是 `\` |

`file_date` 实测落在 1999-03 ~ 2001-07（游戏开发期），所以是 Unix 时间戳；
按 DOS 日期时间解会得到 `28:49:08` 这种非法值。

### 1.2 `.RFD`（数据）

各块首尾相接：第一块 `offset = 0`，最后一块正好结束在文件尾。

```
flags = 2（压缩）   偏移 0..3          raw（解压后大小）
                    偏移 4..stored-1   **完整的 zlib 流**：
                        4..5       78 DA（zlib 头，等价 level 9）
                        6..N-5     raw deflate 数据
                        N-4..N-1   **Adler-32 校验和**（4 字节，big-endian）

flags = 0（存储）   偏移 0..stored-1   原始文件字节，**没有那 6 字节块头**
```

**块头那 4 字节，引擎其实不读。** 实测（`Game.exe`）：

```asm
; 解压路径：entry+0xc = stored、entry+0x10 = raw，全部取自索引
37a7  malloc([ebp+0x10])          ; 输出缓冲按 raw 分配
37be  mov  ecx,[ebp+0xc]          ; 输入长度 = stored
37d9  call [edx*4+0x5efc84]       ; 按 flags 选解压器（表：[1]=0x4019f0, [2]=0x5a14a0）

; flags=2 的解压器 0x5a14a0
1a14a4  mov  ecx,[esp+0x14]       ; arg5 = raw
1a14ac  mov  [eax],ecx            ; 把 raw 写到出参（这就是 0x3490 返回的文件大小）
1a14b2  add  edx,0xfffffffc       ; 长度 = stored - 4
1a14b5  add  ecx,0x4              ; ← 只是把前 4 字节跳过去
1a14c0  call 0x1a1590             ; 其余按 zlib 流解压（连 78 DA 一起喂进去）
```

所以：
* 真正决定"读多少、解多大"的是**索引**（`stored` / `raw`），不是块头；
* 块头前 4 字节被 `add ecx,4` 一跳了之，**写成什么值都无所谓**；
* 但偏移 4..5 的 `78 DA` 是**必须**的，它会被当成 zlib 流头解析。
* 注意 `add edx,-4` 之后喂给 zlib 的是 `stored - 4` 字节，也就是
  **`78 DA` + deflate + 尾部 4 字节 Adler-32** 一起交给它 —— 尾部校验和是这条 zlib 流的
  一部分（见下面的 §1.2.1）。
* 推论：`0x3490` 写给调用者的文件大小 = `raw`，所以字库那边 `count = raw / 2`
  成立（22 字节的 `SMALL1.MAP` → 11 words → 10 个码位）。

#### 1.2.1 尾部那 4 字节 Adler-32

压缩块末尾是 4 字节 Adler-32（big-endian），也就是这段 zlib 流的正常结尾。
逐条核对结果：**原版每个压缩块的末尾都是正确的 Adler-32**——
官方繁中包 3,747 / 230 / 7 / 1 / 298 条全部命中，官方英文包同样如此，
严格 `zlib.decompress()` 100% 解得开。

工具的处理方式：

* 打包时写 `struct.pack('<I', len(blob)) + zlib.compress(blob, 9)`，
  后者产出的正好是 `78 DA + raw deflate + adler32`；`replace` 里没被替换的条目
  整块沿用模板字节（见 §5），所以校验和也原样保留；
* `check` 逐条用严格 `zlib.decompress()` 验证；缺校验和会报
  "格式偏差：N 个压缩条目缺尾部 4 字节 Adler-32"；
* 自检脚本把"压缩块尾部必须是正确的 Adler-32"列为失败项；
* `read_data()` 的说明文字区分"zlib 流完整（含 adler32）"与"缺 adler32 尾部"。

判别方法（一行）：

```python
import zlib, struct
blk = rfd[entry.offset:entry.offset + entry.stored]
out = zlib.decompress(blk[4:])                       # 严格 zlib；缺校验和会抛异常
assert blk[-4:] == struct.pack('>I', zlib.adler32(out) & 0xffffffff)
```

### 1.3 三个要点

1. **`stored` 是整块大小**，压缩条目包含那 6 字节块头。验证方法：相邻条目
   首尾相接（归档 `3DDATA0001` 里条目 0 `offset=0 stored=968` → 条目 1 `offset=968`，
   `968+977=1945` = 条目 2 的 `offset`，一路到文件尾）。
2. **stored 条目没有块头**。公开参考实现
   [IceReaper/ebfd-re](https://github.com/IceReaper/ebfd-re) 的
   `LibEmperor/RfhEntry.cs` 无条件 `Position = offset + 6`，对 `flags=0` 的条目
   是错的。照抄这条的工具会把这类文件解坏——见 §4.1。
3. 索引里 `name_len` 含 NUL，名字按 cp1252 解码；重打包时**原样写回原始字节**，
   不要自己重新编码。

---

## 2. 引擎怎么找文件

* **名字比较大小写与分隔符都不敏感**：归档索引读入时，名字逐字符过一遍 ——
  `/`(0x2F) 改写成 `\`(0x5C)，其余字符做大小写折叠（`Game.exe` 0x402e40、0x403060）。
  所以 `SubTitles\SUBTITLE.INI` 与 `Subtitles\SubTitle.ini` 是同一条。
* 名字形如 `<base>_<size>.tga` 且 `size ∈ {16, 32, 64, 128, 256, 512}` 时，
  引擎会把它当成 LOD 系列：截成基础名 `<base>_`，然后从较小尺寸开始逐倍尝试
  `TEXTURES\<base>_16.tga` → `_32` → `_64` …，**取第一个存在的**
  （`Game.exe` 0x14bf6d–0x14bfb7）。
  * 后果：**一个坏的小尺寸 LOD 会挡住同族好的大尺寸 LOD**。例如模型引
    `im_paw_64.tga`，引擎先试 `im_paw_16.tga`（不存在）→ `im_paw_32.tga`（存在，
    但是坏的）→ 就直接用坏的那个。
  * 名字不带 `_<数字>` 的（如 `!siren.tga`）**原样加载**，没有回退。
* 文件缺失是**安全**的：加载器会生成一张 64×64 棋盘格占位图，格式字段也填了
  `0x5550`。文件存在但头不合法才会出事（§3）。

## 3. TGA 为什么会让引擎弹框

`CTexture::LoadTGA`（`Game.exe` 0x1c410）的判定：

| 检查 | 合法值 | 不合法时 |
|---|---|---|
| `imageType`（偏移 2） | `2`（未压缩真彩）、`10`（RLE） | 跳到 0x1c658 |
| 像素深度（偏移 16） | `15/16` → `0x5550`；`24` → `0x8880`；`32` → `0x8888` | 跳 0x1c4f5 |

两条失败路径都做同一件事：写 log `TGA is an invalid pixel format`
（消息串 `0x5f0b64`，日志里 `(LoadTGA)` 那个函数名串在 `0x5f0b5c`），并且**完全不设格式字段**。
之后任何 `GetTextureBPP`（0x1c390）都会弹框：

```
标题 GetTextureBPP
内容 Invalid Texture format 0        <- 0 就是那个从没被赋值的格式字段
```

注意：**8 位索引色（`imageType=1`）也在不合法之列**。原版数据里确实有一个这样的
文件（`Textures\tvt_blood_32.tga`，1836 字节），但它不被任何模型引用，所以没事。

改动贴图/字体之后、打包之前，先跑：

```
python3 Tools/check_tga.py <目录>
```

它按上面两条规则逐个 TGA 检查，退出码 0 表示都能被引擎加载。

---

## 4. DuneEx 2 的两个问题

### 4.1 解包：`flags=0` 的条目会解成全 0 的坏文件

DuneEx 2 的列表里，这些条目的 **Packed 列显示 `No`**（因为它读的就是 `flags=0`），
而解出来的内容是**一整块 0**，长度也不对。实测 4 个案例：

| 归档文件 | 条目 | RFH 里的真实内容 | DuneEx 2 解出来的 |
|---|---|---|---|
| `3DDATA0001.RFH` | `Textures\!siren.tga` | **812** B，16×16 24bpp | 832 B，全 0 |
| `3DDATA0001.RFH` | `Textures\im_paw_32.tga` | **3116** B，32×32 24bpp | 3159 B，全 0 |
| `Ui0001.RFH` | `FONT\SMALL1.MAP` | **22** B，BOM + 10 个码位 | 34 B，全 0 |
| `Ui0001.RFH` | `FONT\KOREAN9.MAP` | **106** B，BOM + 52 个码位 | 119 B，全 0 |

这四个正是 `3DDATA0001.RFH`（3749 条）和 `Ui0001.RFH`（300 条）里**仅有的 4 个
`flags=0` 条目**，其余 4045 条都是 `flags=2`。所以规律很明确：**处理 stored
条目时出错**，很可能就是把"压缩块前面有 6 字节块头"这条规则套到了所有条目上
（见 §1.3 第 2 点）。

* `!siren.tga` 是 Scavenger、MCV、六族建造厂引用的贴图 → 一进战役就
  `TGA is an invalid pixel format` → 弹 `GetTextureBPP / Invalid Texture format 0`；
* `im_paw_32.tga` 会被 LOD 回退搜索选中（§2），挡住好的 `im_paw_64.tga`；
* 两个 `.MAP` 是 Small 字体第 1 页和 Korean 字体第 9 页，坏了会让引擎报
  `CFont3D - Invalid font map file`。

**结论：不要用 DuneEx 2 解出来的目录重新打包。** 改用 `replace`（§5）以原版归档为
模板替换文件：没被替换的条目整块沿用原字节，这些坏文件不会被写进新归档。

### 4.2 打包：DuneEx 2 拒绝"空"文件

DuneEx 2 在加入**只有 BOM 的 2 字节 `.MAP`** 时会拒绝（即使它有 BOM）。
所以**用它打包**时字体空页不能真的空着，得塞一个占位码位：

```
空页 MAP = FF FE 00 00       BOM + 一个 U+0000（仅 DuneEx 路径需要）
```

引擎永远不会去查 U+0000（它量字符串长度用的是 `wcslen` 语义，NUL 是终止符、
不计入长度），而且没有代码直接读字形表的第 0 项，所以塞了也安全。

本项目的字库生成器直接写 `FF FE`（2 字节），`Tools/rfh.py` 打包时也不挑大小，
所以默认输出的是最干净的形式。要改成 DuneEx 那条路只需要改
`EbfdFontPage.SaveEmpty` 一行，细节见 `FORMAT.md` §3.2.1。

### 4.2.1 附带效果：DuneEx 2 写的块头 4 字节是错的，但无害

实测用 DuneEx 2 打出来的归档，压缩块头前 4 字节往往**不等于**索引里的 `raw`
（例如 `C_Output_Full1.txt` 索引 236,084、块头写 196,660）。按 §1.2 的结论，
这 4 字节引擎从不使用，所以**功能上没问题**，只是格式不严谨、和零售版不一致。
真正要提防的仍然是 §4.1 那个**解包**问题。

### 4.3 因此的建议流程

改字体/贴图 → **用 `replace` 以原版归档为模板替换** → `check` + `check_tga.py` 验一遍。
不要 "用 DuneEx 2 解包整个目录 → 改几个文件 → 再用它打包整个目录"，这条路会把
§4.1 的坏字节带进去。

---

## 5. 工具用法

### 运行环境

`Tools/` 下三个脚本**只用 Python 标准库**
（`argparse` `datetime` `hashlib` `os` `shutil` `struct` `sys` `tempfile` `zlib`），所以
**没有 requirements.txt，也不需要 `pip install`**。最低 Python 3.7
（`argparse` 的子命令 `required=True` 需要 3.7；语法本身 3.6 就能解析）。

Windows 上建议用 `py -3 Tools\rfh.py ...`：如果 `python` 指向 Python 2，
会直接报 f-string 语法错误，看不出真正原因。

### 命令

```
python3 Tools/rfh.py list    <x.RFH> [--filter 子串] [--stored-only]
python3 Tools/rfh.py entry   <x.RFH> <名字>
python3 Tools/rfh.py extract <x.RFH> <输出目录> [名字...]
python3 Tools/rfh.py check   <x.RFH>
python3 Tools/rfh.py compare <x.RFH> <解包目录>
python3 Tools/rfh.py pack    <目录> <输出.RFH> [--store 子串]... [--date unix] [--level N]
python3 Tools/rfh.py replace <模板.RFH> <替换文件目录> <输出.RFH>

# 自检：解压 -> 原样打回 -> 再解压 -> 逐字节比对，来回两轮
python3 Tools/selftest.py <DATA目录>          # 目录下所有 .RFH
python3 Tools/selftest.py <x.RFH> [<y.RFH>]   # 指定归档
python3 Tools/selftest.py --quick <DATA目录>  # 只做结构与解压，跳过往返与打包
```

不指定 `--data` 时自动找同名的（大小写不敏感）`.RFD`。

`extract` 按条目名解到各自的相对路径下，不同目录的同名文件互不覆盖。

自检脚本对每个归档依次做：索引独立走查、块排布与 Adler-32 校验、全量解压核对长度、
用 `replace` + 空目录打回（必须与原归档**逐字节相同**）、从打回结果再解压比对、
二次往返，最后把解出来的目录用 `pack` 打成新归档再解出来核对内容（新归档索引是重新生成的，
日期与条目顺序都不同，所以这一项比的是内容与结构是否合法，不是字节相等）。

`pack` 是 `extract` 的逆操作，两者构成一对；`replace` 是进阶操作：

| | `pack`（目录 → 归档） | `replace`（以原归档为模板替换文件） |
|---|---|---|
| 用途 | 把一个目录从零打成归档 | 拿现成归档当模板，替换其中若干文件 |
| 索引（条目表） | 按目录内容生成：名字 = 相对路径，顺序 = 名字排序，日期 = 各文件修改时间（`--date` 可统一指定） | **完全沿用模板**：顺序、名字原始字节、`file_date`、`flags` 都不变 |
| 索引里没有的条目 | 不存在这个概念，目录里有多少就打多少 | 目录里没有的条目**整块沿用模板原字节**（连压缩流一起） |
| 典型结果 | 内容正确、结构合法的全新归档 | 空目录打回 ⇒ 与原归档逐字节相同；只替换了哪些文件，就只有那些块变 |

`pack` 默认全部 deflate（级别 9，块头就是 `78 DA`），`--store 子串` 可让名字含该子串的
文件原样存储。

`replace` 先按条目名匹配替换文件；对不上再按文件名，且要求该文件名在替换目录里唯一
（有多个候选会报错）。输出会标出每个条目是按哪种方式匹配的。

两个命令都会顺带写配套的 `.RFD`，并在结束后提示跑 `check`。

### 例：换掉中文字体

```
# 归档 3DDATA0001.RFH：68 个 CHINESE*.TGA
python3 Tools/rfh.py replace  DATA/3DDATA0001.RFH  sctga/  out/3DDATA0001.RFH
# 归档 Ui0001.RFH：68 个 CHINESE*.MAP
python3 Tools/rfh.py replace  DATA/Ui0001.RFH      scmap/  out/Ui0001.RFH

python3 Tools/rfh.py check   out/3DDATA0001.RFH
python3 Tools/rfh.py check   out/Ui0001.RFH
python3 Tools/rfh.py compare out/3DDATA0001.RFH <解出来的 3DDATA0001 目录>
```

---

## 6. 改完之后的核查清单

0. 想一次性确认工具本身没问题：`python3 Tools/selftest.py <DATA目录>` ——
   它把每个归档解压出来、原样打回、再解压、两次逐字节比对，最后报"全部通过"。
1. `python3 Tools/check_tga.py <改动涉及的目录>` —— 0 个不合格。
   （原版数据里本来就有 1 个加载不了的 `Textures\tvt_blood_32.tga`，8 位索引色、
   无引用，属于历史遗留，不必管它。）
2. `python3 Tools/rfh.py replace <原版.RFH> <改动目录> <新.RFH>` —— 确认输出里
   "替换 N / 沿用模板 M" 的 N 和预期一致，且没有"没被用到"的文件。
3. `python3 Tools/rfh.py check <新.RFH>` —— 索引、解压、块排布、全 0 四项都过。
4. `python3 Tools/rfh.py compare <新.RFH> <对应目录>` —— 差异应**只有**准备替换的
   那些文件。
5. 字体类改动另外验一遍页数与覆盖率（68 页、`MAP` 的码位覆盖到全部用字）。

---

## 7. 本仓库验证过的归档

| 归档文件 | 条目 | 压缩 | stored |
|---|---|---|---|
| `3DDATA0001.RFH` | 3749 | 3747 | 2：`Textures\!siren.tga`(812B)、`Textures\im_paw_32.tga`(3116B) |
| `3DDATA0002.RFH` | 1 | 1 | — |
| `3DDATA0003.RFH` | 2 | 2 | — |
| `Ui0001.RFH` | 300 | 298 | 2：`FONT\SMALL1.MAP`(22B)、`FONT\KOREAN9.MAP`(106B) |

（每个字体各自的 TGA/MAP 分布、页数、以及本工具为什么只支持 Chinese/Korean，
见 [`FORMAT.md`](FORMAT.md) §3。）

四个归档都是块首尾相接、正好铺满 `.RFD`，逐条解压无异常。

字体条目的名字是 `Textures\CHINESE<n>.tga`（`n = 0..67`，在归档 `3DDATA0001` 里）
和 `FONT\CHINESE<n>.MAP`（在归档 `Ui0001` 里）——扩展名在原归档里是小写 `.tga`、
大写 `.MAP`，`replace` 会按模板里的原始字节写回。

---

## 8. 往返验证记录

`Tools/selftest.py` 在四个原版归档上都通过了（解压 → 原样打回 → 再解压 → 逐字节
比对，来回两轮）：

| 归档 | 条目 | 压缩/存储 | 解出字节数 | 往返两轮 |
|---|---|---|---|---|
| `3DDATA0001.RFH` | 3749 | 3747 / 2 | 157,037,720 | 逐字节一致 |
| `3DDATA0002.RFH` | 1 | 1 / 0 | 33,307 | 逐字节一致 |
| `3DDATA0003.RFH` | 2 | 2 / 0 | 68,167 | 逐字节一致 |
| `Ui0001.RFH` | 300 | 298 / 2 | 23,975,116 | 逐字节一致 |

另外：用空的替换目录 `replace` 一遍，产出的 `.RFH`/`.RFD` 与原件**逐字节相同**——
没被替换的条目是整块沿用模板字节（连压缩流一起）的，只有真正被替换的条目才会重新压缩。
自检脚本同时校验每个压缩块尾部的 Adler-32（§1.2.1），以及"解压→打回→再解压"两轮的结果一致。
