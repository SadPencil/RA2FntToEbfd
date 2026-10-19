#!/usr/bin/env python3
"""
Emperor: Battle for Dune 的 .RFH / .RFD 归档读写工具。

游戏把资源放在 DATA\\ 下的一对文件里：<名字>.RFH 是索引，<名字>.RFD 是数据。
本工具能列目录、解包、逐条校验、把归档内容和解包出来的目录逐文件比对（compare）、
把目录打成归档（pack），以及**以某个归档为模板只替换指定文件**（replace）。

------------------------------------------------------------------ 格式
所有整数都是小端。

.RFH（索引）—— 扁平记录，一条接一条，**没有全局头**，读到文件尾为止：

    偏移  大小  含义
     0     4    文件名长度 name_len（含结尾 NUL）
     4     4    file_date，**Unix 时间戳**（实测 1999-03 ~ 2001-07，正是开发期）
     8     4    flags：0 = 原样存储(stored)，2 = deflate 压缩；其它值是错的
    12     4    stored：这一块在 .RFD 里占的**整块**字节数
    16     4    raw：解压后（也就是真实文件）的大小
    20     4    该块在 .RFD 里的起始偏移
    24   name_len  文件名，NUL 结尾，cp1252，目录分隔符是 '\\'

.RFD（数据）—— 各块首尾相接，第一块 offset = 0，最后一块正好到文件尾：

    flags = 2（压缩）：  偏移 0..3 = raw（解压后大小）
                        偏移 4..stored-1 = **完整的 zlib 流**：
                            4..5   = 78 DA（zlib 头，即 level 9）
                            6..N-5 = raw deflate 数据
                            N-4..N-1 = Adler-32 校验和（big-endian，4 字节）
                        → 负载长度 = stored - 4（含 2 字节头 + 4 字节校验和）
     flags = 0（存储）：  偏移 0..stored-1 就是原始文件字节，**没有那 6 字节块头**

    末尾 4 字节是 Adler-32。引擎按索引里的 raw 长度取输出，不依赖尾部；
    但**原版每个压缩块都带它**（官方繁中/英文包逐条核对：3,747/230/7/1/298 全部命中，
    严格 zlib 解码 100% 通过），所以缺了就不是原版格式（严格 zlib 工具会报
    "incomplete or truncated stream"）。`pack` 写 `zlib.compress(blob, 9)`，
    产出的就是 `78 DA + raw deflate + adler32`；`check` / `selftest.py` 会校验它。

两个容易踩的点（都经过实测验证）：

  * `stored` 是**整块**大小：压缩条目包含那 6 字节块头。用「相邻条目首尾相接」
    验证过：条目 0 offset=0 stored=968 → 条目 1 offset=968，一路到底刚好铺满 RFD。
  * **stored 条目没有块头**。IceReaper/ebfd-re 的 LibEmperor/RfhEntry.cs 无条件
    seek 到 offset+6，对 flags=0 的条目是错的；照抄它的工具会把这类文件解坏
    （见 Docs/RFH.md 里 DuneEx 2 的记录）。

------------------------------------------------------------------ 命令
    rfh.py list    <x.RFH> [--filter 子串] [--stored-only] [--data x.RFD]
    rfh.py entry   <x.RFH> <名字> [--data x.RFD]
    rfh.py extract <x.RFH> <输出目录> [名字...] [--data x.RFD]
    rfh.py check   <x.RFH> [--data x.RFD]
    rfh.py compare <x.RFH> <解包目录> [--data x.RFD]
    rfh.py pack    <目录> <输出.RFH> [--store 子串]... [--date unix] [--level N]
    rfh.py replace <模板.RFH> <替换文件目录> <输出.RFH> [--show N] [--data x.RFD]

`pack` 是 `extract` 的逆操作：把一个目录从零打成归档，索引完全由目录内容生成
（条目名 = 相对路径，日期 = 文件时间戳，默认 deflate 压缩），不需要任何模板。
`replace` 以现成归档为模板：目录里有的文件替换，目录里没有的条目**整块沿用模板原字节**
（连压缩流一起），所以空目录打回可以与原归档逐字节相同。

不指定 --data 时，自动找同名（大小写不敏感）的 .RFD。
"""
import argparse
import datetime
import hashlib
import os
import struct
import sys
import zlib

REC = 24               # 索引记录固定部分
SIZE_FIELD = 4         # 压缩块偏移 0..3 = raw；zlib 流（78 DA…）从这里开始
BLOCK_HEADER = 6       # 跳过 4 字节 raw + 2 字节 78 DA 之后就是 raw deflate 数据
ZLIB_MAGIC = 0xDA78
FLAG_COMPRESSED = 2


# ------------------------------------------------------------------ 索引

class Entry:
    """一条索引记录。"""

    __slots__ = ('index', 'index_pos', 'name_len', 'file_date', 'flags',
                 'stored', 'raw', 'offset', 'name', 'raw_name')

    def __repr__(self):
        return (f'<{self.name} flags={self.flags} stored={self.stored} '
                f'raw={self.raw} off={self.offset:#x}>')

    @property
    def compressed(self):
        return bool(self.flags & FLAG_COMPRESSED)

    @property
    def packed_text(self):
        return 'yes' if self.flags == FLAG_COMPRESSED else (
            'no' if self.flags == 0 else f'?({self.flags})')

    @property
    def date(self):
        try:
            return datetime.datetime.fromtimestamp(
                self.file_date, datetime.timezone.utc).strftime('%Y-%m-%d %H:%M:%S')
        except (OverflowError, OSError, ValueError):
            return f'(非法时间戳 {self.file_date:#x})'


def read_index(rfh_path, verbose=False):
    """读索引；返回 (entries, 索引字节数)。"""
    with open(rfh_path, 'rb') as f:
        blob = f.read()
    entries, pos = [], 0
    while pos + REC <= len(blob):
        name_len, file_date, flags, stored, raw, offset = struct.unpack_from('<6I', blob, pos)
        start = pos
        pos += REC
        if name_len == 0 or name_len > 1024 or pos + name_len > len(blob):
            if verbose:
                print(f'  索引在 {start:#x} 处中断：name_len={name_len}', file=sys.stderr)
            break
        raw_name = blob[pos:pos + name_len]
        pos += name_len

        e = Entry()
        e.index, e.index_pos = len(entries), start
        e.name_len, e.file_date, e.flags = name_len, file_date, flags
        e.stored, e.raw, e.offset = stored, raw, offset
        e.name = raw_name.split(b'\0', 1)[0].decode('cp1252', 'replace')
        e.raw_name = raw_name          # 打包时原样写回
        entries.append(e)
    return entries, len(blob)


def find_rfd(rfh_path):
    """大小写不敏感地找配套的 .RFD。"""
    base = os.path.splitext(rfh_path)[0]
    for cand in (base + '.RFD', base + '.rfd'):
        if os.path.exists(cand):
            return cand
    folder = os.path.dirname(base) or '.'
    stem = os.path.basename(base).lower()
    for fn in sorted(os.listdir(folder)):
        if fn.lower() == stem + '.rfd':
            return os.path.join(folder, fn)
    raise SystemExit(f'找不到配套的 .RFD：{base}.RFD')


def load_data(path):
    with open(path, 'rb') as f:
        return f.read()


# ------------------------------------------------------------------ 读条目

def read_data(entry, rfd):
    """取出一个条目的数据；返回 (bytes 或 None, 说明)。"""
    if entry.stored == 0 and entry.raw == 0:
        return b'', '零长度条目'

    if not entry.compressed:
        if entry.flags != 0:
            return None, f'未知 flags={entry.flags}（只认 0 和 2）'
        if entry.offset + entry.stored > len(rfd):
            return None, (f'越界：offset {entry.offset:#x} + stored {entry.stored} '
                          f'> RFD {len(rfd)}')
        blob = rfd[entry.offset:entry.offset + entry.stored]
        note = f'原样存储 {entry.stored} 字节（存储条目没有块头）'
        if entry.stored != entry.raw:
            note += f'；注意 stored({entry.stored}) != raw({entry.raw})'
        return blob, note

    if entry.offset + entry.stored > len(rfd):
        return None, (f'越界：offset {entry.offset:#x} + stored {entry.stored} '
                      f'> RFD {len(rfd)}')
    size, magic = struct.unpack_from('<IH', rfd, entry.offset)
    hdr = f'块头 raw={size} magic={magic:#06x}'
    zstream = rfd[entry.offset + SIZE_FIELD:entry.offset + entry.stored]
    raw_only = rfd[entry.offset + BLOCK_HEADER:entry.offset + entry.stored]
    # 原版块尾是 4 字节 Adler-32（big-endian）：块 = [raw:u32le] + 完整 zlib 流。
    # 引擎只按索引里的 raw 长度取输出、不读尾部，所以缺了也能玩；但原版都有，
    # 缺了就不是原版格式（严格 zlib 工具会报 truncated）。这里两条路都试并如实说明。
    out = None
    how = ''
    try:
        out = zlib.decompress(zstream)
        how = 'zlib 流完整（含 adler32）'
    except zlib.error:
        try:
            out = zlib.decompressobj(-15).decompress(raw_only)
            how = '缺 adler32 尾部（用 raw deflate 宽容解出；引擎能忍，但不是原版格式）'
        except zlib.error:
            return None, f'deflate 解压失败；{hdr}'
    warn = ''
    if len(out) != entry.raw:
        warn = f'，但索引标称 {entry.raw}' + ('（偏短）' if len(out) < entry.raw else '（偏长）')
    return out, f'{how} -> {len(out)} 字节{warn}；{hdr}'


def safe_rel_name(name):
    """把归档条目名转成安全的相对路径段列表；不安全（绝对路径、上级目录、盘符、空）
    返回 None。归档内部用 '\\' 分层，这里统一按 '/' 处理，与平台无关。"""
    if not name:
        return None
    parts = name.replace('\\', '/').split('/')
    if name.startswith(('\\', '/')) or '..' in parts or ':' in parts[0]:
        return None
    parts = [p for p in parts if p not in ('', '.')]
    return parts or None


# ------------------------------------------------------------------ 命令

def cmd_list(args):
    entries, idx_len = read_index(args.rfh, verbose=True)
    print(f'{args.rfh}：索引 {idx_len} 字节，{len(entries)} 条')
    sel = entries
    if args.filter:
        sel = [e for e in sel if args.filter.lower() in e.name.lower()]
    if args.stored_only:
        sel = [e for e in sel if not e.compressed]
    print(f'{"#":>5} {"flags":>5} {"packed":>6} {"stored":>10} {"raw":>10} '
          f'{"offset":>10}  {"日期":>19}  name')
    for e in sel:
        print(f'{e.index:>5} {e.flags:>5} {e.packed_text:>6} {e.stored:>10} '
              f'{e.raw:>10} {e.offset:>10}  {e.date:>19}  {e.name}')
    top = {}
    for e in entries:
        key = e.name.replace('\\', '/').split('/')[0].upper()
        top[key] = top.get(key, 0) + 1
    print('\n顶层目录：' + ', '.join(f'{k}={v}' for k, v in sorted(top.items())))
    n_yes = sum(1 for e in entries if e.flags == FLAG_COMPRESSED)
    n_no = sum(1 for e in entries if e.flags == 0)
    print(f'packed=yes {n_yes} 条，packed=no {n_no} 条，'
          f'非法 flags {len(entries) - n_yes - n_no} 条')
    return 0


def cmd_entry(args):
    entries, _ = read_index(args.rfh)
    rfd = load_data(args.data or find_rfd(args.rfh))
    hit = [e for e in entries if e.name.lower() == args.name.lower()]
    if not hit:
        hit = [e for e in entries if args.name.lower() in e.name.lower()]
    if not hit:
        print('没找到:', args.name)
        return 1
    for e in hit:
        print(f'#{e.index}  {e.name}')
        print(f'   索引位置    {e.index_pos:#x}')
        print(f'   name_len    {e.name_len}（名字 {len(e.name)} 字符 + NUL）')
        print(f'   file_date   {e.file_date:#010x}  {e.date} UTC')
        print(f'   flags       {e.flags}  -> packed={e.packed_text}')
        print(f'   stored      {e.stored}')
        print(f'   raw         {e.raw}')
        print(f'   offset      {e.offset:#x} ({e.offset})')
        print(f'   RFD 该处    {rfd[e.offset:e.offset + 16].hex(" ")}')
        data, note = read_data(e, rfd)
        if data is None:
            print(f'   读取        [失败] {note}')
        else:
            print(f'   读取        {len(data)} 字节，前 16 字节 {data[:16].hex(" ")}')
            print(f'   说明        {note}')
            if data and not any(data):
                print('   *** 内容全 0 ***')
    return 0


def cmd_extract(args):
    entries, _ = read_index(args.rfh)
    rfd = load_data(args.data or find_rfd(args.rfh))
    want = {n.lower() for n in args.names}
    ok = bad = 0
    written = {}          # 条目名（小写）-> 实际写出的相对路径，用来发现会互相覆盖的名字
    clashes = []
    for e in entries:
        # 归档内部用 '\\' 分层，但在 POSIX 上 os.path.basename 不认它，先归一成 '/'
        short = os.path.basename(e.name.replace('\\', '/')).lower()
        if want and e.name.lower() not in want and short not in want:
            continue
        parts = safe_rel_name(e.name)
        if parts is None:
            print(f'  [拒绝] {e.name}：条目名不是安全的相对路径')
            bad += 1
            continue
        data, note = read_data(e, rfd)
        if data is None:
            print(f'  [失败] {e.name}：{note}')
            bad += 1
            continue
        key = e.name.lower()
        if key in written:
            clashes.append((written[key], e.name))
        written.setdefault(key, e.name)
        out = os.path.join(args.outdir, *parts)
        os.makedirs(os.path.dirname(out) or '.', exist_ok=True)
        with open(out, 'wb') as f:
            f.write(data)
        ok += 1
        if args.verbose:
            print(f'  {e.name}  {len(data)} 字节  ({note})')
    print(f'解出 {ok} 个，失败 {bad} 个 -> {args.outdir}')
    if clashes:
        print(f'  注意：有 {len(clashes)} 组条目只在大小写上有区别，在大小写不敏感的文件系统'
              f'（如 Windows）上会写到同一个文件：')
        for a, b in clashes[:5]:
            print(f'    {a}  /  {b}')
    return 0 if bad == 0 else 1


def cmd_check(args):
    entries, _ = read_index(args.rfh)
    rfd = load_data(args.data or find_rfd(args.rfh))
    print(f'{args.rfh}：{len(entries)} 条，RFD {len(rfd)} 字节')
    problems = []
    no_adler = []
    n_comp = 0
    expect = 0
    for e in entries:
        if e.offset != expect:
            problems.append((e, f'块不连续：offset={e.offset}，应为 {expect}', None))
        expect = e.offset + e.stored
        if e.compressed:
            n_comp += 1
            if e.offset + BLOCK_HEADER <= len(rfd):
                hraw, = struct.unpack_from('<I', rfd, e.offset)
                if hraw != e.raw:
                    problems.append((e, f'块头里的 raw={hraw} != 索引 raw={e.raw}', None))
            zstream = rfd[e.offset + SIZE_FIELD:e.offset + e.stored]
            try:
                zlib.decompress(zstream)          # 严格 zlib：校验 78 DA 与 adler32
            except zlib.error:
                no_adler.append(e)
        data, note = read_data(e, rfd)
        if data is None or len(data) != e.raw:
            problems.append((e, note, None if data is None else len(data)))
        elif data and not any(data):
            problems.append((e, '内容全 0', len(data)))
    if expect != len(rfd):
        problems.append((None, f'最后一块结束于 {expect}，RFD 长度是 {len(rfd)}', None))
    if no_adler:
        print(f'  格式偏差：{len(no_adler)}/{n_comp} 个压缩条目缺尾部 4 字节 Adler-32'
              f'（原版每个压缩块都有；引擎能忍，但严格 zlib 工具会报 truncated）。'
              f' 用 pack 重新生成即可补上。')
        for e in no_adler[:5]:
            print(f'      #{e.index} {e.name}')
        if len(no_adler) > 5:
            print(f'      ... 另 {len(no_adler) - 5} 个')
    if not problems:
        extra = '' if not no_adler else '（但有上面那处格式偏差）'
        print(f'  索引、解压、块排布全部正常，且没有全 0 的文件。{extra}')
        return 0
    print(f'  异常 {len(problems)} 处：')
    for e, note, n in problems:
        head = f'#{e.index} {e.name}' if e else '(全局)'
        print(f'    {head}\n        -> {note}')
    return 1


def cmd_compare(args):
    """把归档里的每个条目和目录里的同名文件逐字节比对。"""
    entries, _ = read_index(args.rfh)
    rfd = load_data(args.data or find_rfd(args.rfh))
    tree = {}
    for dirpath, _, fns in os.walk(args.dir):
        for fn in fns:
            p = os.path.join(dirpath, fn)
            key = os.path.relpath(p, args.dir).replace(os.sep, '\\').lower()
            tree[key] = p
    same = diff = 0
    lines, seen = [], set()
    for e in entries:
        key = e.name.lower()
        seen.add(key)
        path = tree.get(key)
        if path is None:
            diff += 1
            lines.append(f'  [只在归档里] {e.name}')
            continue
        data, note = read_data(e, rfd)
        if data is None:
            diff += 1
            lines.append(f'  [读取失败] {e.name}  {note}')
            continue
        with open(path, 'rb') as f:
            disk = f.read()
        if hashlib.sha256(data).digest() == hashlib.sha256(disk).digest():
            same += 1
        else:
            diff += 1
            lines.append(f'  [内容不同] {e.name}  归档 {len(data)} 字节 vs 磁盘 {len(disk)} 字节')
    extra = sorted(k for k in tree if k not in seen)
    print(f'{args.rfh} vs {args.dir}')
    print(f'  相同 {same}，不同 {diff}，只在磁盘上 {len(extra)}')
    for line in lines:
        print(line)
    for k in extra:
        print(f'  [只在磁盘上] {tree[k].replace(os.sep, "/")}')
    return 0 if not lines and not extra else 1


def cmd_pack(args):
    """从零打包：目录 → 新归档（索引由目录内容生成，不需要模板）。"""
    if len(args.paths) == 3:
        # 旧写法（pack <模板> <目录> <输出>）已被 replace 取代，给一条明确的迁移提示
        raise SystemExit('pack 现在只做"目录 -> 归档"。以原归档为模板替换文件请用 replace：\n'
                         f'  {os.path.basename(sys.argv[0])} replace '
                         f'{args.paths[0]} {args.paths[1]} {args.paths[2]}')
    if len(args.paths) != 2:
        raise SystemExit(f'用法：{os.path.basename(sys.argv[0])} pack <目录> <输出.RFH>')
    args.dir, args.out = args.paths
    root = args.dir
    if not os.path.isdir(root):
        raise SystemExit(f'不是目录：{root}')

    items = []
    for dirpath, dirnames, fns in os.walk(root):
        dirnames.sort()
        for fn in sorted(fns):
            p = os.path.join(dirpath, fn)
            rel = os.path.relpath(p, root).replace(os.sep, '\\')
            items.append((rel, p))
    items.sort(key=lambda t: t[0].lower())
    if not items:
        raise SystemExit(f'目录里没有文件：{root}')

    index, data = bytearray(), bytearray()
    n_stored = 0
    empties, warnings = [], []
    for rel, p in items:
        try:
            name_bytes = rel.encode('cp1252')
        except UnicodeEncodeError:
            raise SystemExit(f'文件名含非 cp1252 字符，归档格式放不下：{rel}')
        blob = open(p, 'rb').read()
        if not blob:
            empties.append(rel)
        if any(s.lower() in rel.lower() for s in args.store):
            block, flags = blob, 0          # 原样存储：整块就是文件字节，没有块头
            n_stored += 1
        else:
            block = struct.pack('<I', len(blob)) + zlib.compress(blob, args.level)
            flags = FLAG_COMPRESSED

        date = args.date if args.date is not None else int(os.path.getmtime(p))
        raw_name = name_bytes + b'\0'
        offset = len(data)
        data += block
        index += struct.pack('<6I', len(raw_name), date, flags,
                             len(block), len(blob), offset)
        index += raw_name

    out_rfd = os.path.splitext(args.out)[0] + '.RFD'
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, 'wb') as f:
        f.write(index)
    with open(out_rfd, 'wb') as f:
        f.write(data)

    print(f'从目录打包 -> {args.out}')
    print(f'  条目 {len(items)}（压缩 {len(items) - n_stored} / 存储 {n_stored}），'
          f'索引 {len(index)} 字节，数据 {len(data)} 字节 -> {out_rfd}')
    if empties:
        warnings.append(f'{len(empties)} 个文件是 0 字节：{empties[:3]}'
                        '（引擎对 0 字节条目的处理未验证）')
    for w in warnings:
        print('  警告：' + w)
    print(f'  建议接着跑：{os.path.basename(sys.argv[0])} check {args.out}')
    return 0


def cmd_replace(args):
    """以模板归档的条目表为准，用替换目录里的文件换掉部分条目后重新打包。

    条目顺序、名字（原始字节）、日期、flags 全部沿用模板，所以不会多出文件、
    也不会漏条目；替换目录里没被用到的文件会被报出来。
    """
    entries, _ = read_index(args.rfh)
    rfd = load_data(args.data or find_rfd(args.rfh))

    by_path, by_base = {}, {}
    for dirpath, _, fns in os.walk(args.dir):
        for fn in fns:
            p = os.path.join(dirpath, fn)
            rel = os.path.relpath(p, args.dir).replace(os.sep, '\\').lower()
            by_path[rel] = p
            by_base.setdefault(fn.lower(), []).append(p)

    def lookup(name):
        """按条目名匹配；找不到时，只有该文件名在替换目录里唯一才退回按文件名。"""
        key = name.lower()
        if key in by_path:
            return by_path[key], '条目名'
        short = os.path.basename(key.replace('\\', '/'))
        cands = by_base.get(short, [])
        if len(cands) == 1:
            return cands[0], '文件名'
        if len(cands) > 1:
            rels = ', '.join(sorted(os.path.relpath(c, args.dir) for c in cands))
            raise SystemExit(
                f'替换目录里有 {len(cands)} 个同名文件，无法确定用哪个：{short}\n'
                f'    {rels}\n'
                f'  按归档里的条目名放置它们，或每个名字只保留一份。')
        return None, None

    index, data = bytearray(), bytearray()
    replaced, kept, used, warnings = [], 0, set(), []

    for e in entries:
        src, how = lookup(e.name)
        if src is None:
            blob, note = read_data(e, rfd)
            if blob is None:
                raise SystemExit(f'模板条目读不出来：{e.name}：{note}')
            kept += 1
            # 没被替换的条目：整块字节（含压缩流）原样沿用模板。
            # 这样「空替换目录打回」能逐字节还原原归档，替换时也只动被替换的块。
            block = rfd[e.offset:e.offset + e.stored]
            flags, stored, raw = e.flags, e.stored, e.raw
        else:
            with open(src, 'rb') as f:
                blob = f.read()
            used.add(os.path.abspath(src))
            replaced.append((e.name, len(blob), src, how))
            if not blob:
                warnings.append(f'替换文件是空的：{src}（引擎可能不喜欢 0 字节文件）')

            if e.compressed:
                # 原版压缩块 = [raw:u32le] + 完整 zlib 流：78 DA + raw deflate + adler32
                #（zlib.compress(data, 9) 产出的正好就是这三段）。
                payload = zlib.compress(blob, 9)
                if payload[:2] != b'\x78\xda':
                    raise SystemExit(f'内部错误：zlib 头不是 78 DA 而是 {payload[:2].hex()}')
                block = struct.pack('<I', len(blob)) + payload
                flags = FLAG_COMPRESSED
            else:
                block = blob
                flags = 0
            stored, raw = len(block), len(blob)

        offset = len(data)
        data += block
        index += struct.pack('<6I', e.name_len, e.file_date, flags,
                             stored, raw, offset)
        index += e.raw_name

    unused = [p for p in sorted(set(by_path.values()))
              if os.path.abspath(p) not in used]

    out_data = os.path.splitext(args.out)[0] + os.path.splitext(
        args.data or find_rfd(args.rfh))[1]
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, 'wb') as f:
        f.write(index)
    with open(out_data, 'wb') as f:
        f.write(data)

    print(f'以 {args.rfh} 为模板重建 -> {args.out}')
    n_basename = sum(1 for r in replaced if r[3] == '文件名')
    print(f'  条目 {len(entries)}：替换 {len(replaced)}，沿用模板 {kept}'
          + (f'（其中 {n_basename} 个是按文件名匹配的，其余按条目名）' if n_basename else ''))
    print(f'  索引 {len(index)} 字节，数据 {len(data)} 字节 -> {out_data}')
    for name, n, src, how in replaced[:args.show]:
        print(f'    [{how}] {name}  <-  {src}  ({n} 字节)')
    if len(replaced) > args.show:
        print(f'    ... 其余 {len(replaced) - args.show} 个略（--show 可调）')
    if unused:
        print(f'  注意：替换目录里有 {len(unused)} 个文件没被用到：')
        for p in unused[:10]:
            print(f'    {p}')
        if len(unused) > 10:
            print(f'    ... 还有 {len(unused) - 10} 个')
    for w in warnings:
        print('  警告：' + w)
    print(f'  建议接着跑：{os.path.basename(sys.argv[0])} check {args.out}')
    return 0


# ------------------------------------------------------------------ 入口

def build_parser():
    ap = argparse.ArgumentParser(
        description='Emperor: Battle for Dune 的 .RFH/.RFD 归档工具',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog='格式说明与使用流程见 Docs/RFH.md。')
    sub = ap.add_subparsers(dest='cmd', required=True)

    def add(name, help_text, func):
        p = sub.add_parser(name, help=help_text)
        p.add_argument('rfh', help='.RFH 路径')
        p.add_argument('--data', help='.RFD 路径（默认自动找同名文件）')
        p.set_defaults(func=func)
        return p

    p = add('list', '列出索引', cmd_list)
    p.add_argument('--filter', help='只显示名字含该子串的条目')
    p.add_argument('--stored-only', action='store_true', help='只显示未压缩条目')

    p = add('entry', '打印某一条的原始字段', cmd_entry)
    p.add_argument('name', help='条目名（可只给文件名或子串）')

    p = add('extract', '解包', cmd_extract)
    p.add_argument('outdir', help='输出目录')
    p.add_argument('names', nargs='*', help='只解这些条目（默认全解）')
    p.add_argument('-v', '--verbose', action='store_true')

    add('check', '逐条校验（解压、大小、块排布、全 0）', cmd_check)
    p = add('compare', '把归档内容和解包目录里的文件逐个比对', cmd_compare)
    p.add_argument('dir', help='解包出来的目录（按相对路径对应归档条目名）')

    # pack 是 extract 的逆操作：目录 -> 归档（没有模板归档这一项）
    p = sub.add_parser('pack', help='打包：目录 -> 新归档（extract 的逆操作）')
    p.set_defaults(func=cmd_pack)
    p.add_argument('paths', nargs='+', metavar='<目录> <输出.RFH>',
                   help='要打包的目录（子目录会成为条目路径的一部分）与输出 RFH')
    p.add_argument('--store', action='append', default=[],
                   help='名字含该子串的文件不压缩（可重复）；默认全部 deflate')
    p.add_argument('--level', type=int, default=9,
                   help='deflate 压缩级别 1..9，默认 9（原版用的就是 9，头是 78 DA）')
    p.add_argument('--date', type=int, default=None,
                   help='索引里的 file_date（Unix 时间戳）；默认取各文件的修改时间')

    # replace 是进阶操作：拿现成归档当模板替换文件
    p = add('replace', '以原归档为模板，用目录里的文件替换若干条目', cmd_replace)
    p.add_argument('dir', help='替换文件目录（按完整相对路径或文件名匹配）')
    p.add_argument('out', help='输出的新 RFH（配套 RFD 扩展名跟随模板）')
    p.add_argument('--show', type=int, default=10, help='打印前几条被替换的条目')
    return ap


def main(argv=None):
    args = build_parser().parse_args(argv)
    return args.func(args)


if __name__ == '__main__':
    sys.exit(main())
