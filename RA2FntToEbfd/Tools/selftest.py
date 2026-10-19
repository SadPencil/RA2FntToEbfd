#!/usr/bin/env python3
"""
RFH/RFD 工具自检：解压 → 原样打回 → 再解压 → 逐字节比对，外加"从零打包"往返。

这个脚本回答一个问题：**Tools/rfh.py 解出来的文件是不是真的对，打回去以后
还能不能原样解出来。** 它不依赖任何外部数据，只需要游戏 DATA 目录下的归档。

对每个归档做这些检查：

  T1  索引自洽：按 24 字节记录 + 名字长度独立走查一遍，必须**正好**停在文件尾
      （结构一旦理解错，走查就会错位、落不到 EOF）
  T2  元数据与块排布：flags 合法、块首尾相接、压缩块头是 `raw + 78 DA`、
      尾部有 4 字节 Adler-32（严格 zlib 解得开）、最后一块正好结束在 .RFD 尾
  T3  全量解压：每个条目解出的长度必须等于索引里的 raw
  T4  原样打回：用空的替换目录 replace 一遍，产出的 .RFH/.RFD 必须与原件**逐字节相同**
  T5  再解压：从打回后的归档解到另一个目录，与 T3 的解压结果**逐字节**比对
  T6  二次往返：把打回后的归档再打回一遍、再解压，仍然逐字节一致
  T7  从零打包：把 T3 解出来的目录用 pack 打成新归档（索引全新生成），再解压出来，
      内容必须与 T3 逐字节一致，且新归档自身结构合法（块连续、末尾吻合、带 Adler-32）
  T3b 解出来的 TGA 里有没有引擎加载不了的（只警告：原版数据里本来就有
      `textures\tvt_blood_32.tga` 这种 8 位索引色、无人引用的文件）

用法：
    python3 Tools/selftest.py <DATA目录> [<DATA目录> ...] [--keep]
    python3 Tools/selftest.py .                    # 当前目录下所有 *.RFH
    --keep 保留中间产物（默认用临时目录，跑完删掉）
"""
import argparse
import hashlib
import os
import shutil
import sys
import tempfile
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rfh                                      # noqa: E402
import check_tga                                # noqa: E402


def sha(blob):
    return hashlib.sha256(blob).digest()


def find_archives(paths):
    out = []
    for p in paths:
        if os.path.isdir(p):
            for dirpath, _, fns in os.walk(p):
                for fn in sorted(fns):
                    if fn.lower().endswith('.rfh'):
                        out.append(os.path.join(dirpath, fn))
        else:
            out.append(p)
    return sorted(set(out))


def extract_all(rfh_path, dest, verbose=False):
    """解包全部条目；返回 {相对路径(小写): (磁盘路径, 字节数)}。"""
    entries, _ = rfh.read_index(rfh_path)
    rfd = rfh.load_data(rfh.find_rfd(rfh_path))
    result = {}
    for e in entries:
        blob, note = rfh.read_data(e, rfd)
        if blob is None:
            raise RuntimeError(f'{rfh_path}: {e.name}: {note}')
        parts = rfh.safe_rel_name(e.name)
        if parts is None:
            raise RuntimeError(f'{rfh_path}: {e.name}: 条目名不是安全的相对路径')
        path = os.path.join(dest, *parts)
        os.makedirs(os.path.dirname(path) or '.', exist_ok=True)
        with open(path, 'wb') as f:
            f.write(blob)
        result[e.name.lower()] = (path, len(blob))
    return result, rfd


def compare_trees(a, b):
    """按条目名比对两棵解出来的目录，返回问题列表。"""
    problems = []
    if set(a) != set(b):
        problems.append(f'条目集合不同：只在一侧 {sorted(set(a) ^ set(b))[:3]}')
    for key in sorted(set(a) & set(b)):
        pa, na = a[key]
        pb, nb = b[key]
        if na != nb:
            problems.append(f'{key}: 大小 {na} != {nb}')
            continue
        if sha(open(pa, 'rb').read()) != sha(open(pb, 'rb').read()):
            problems.append(f'{key}: 内容不同')
    return problems


def check_archive(rfh_path, keep=False, quick=False):
    name = os.path.basename(rfh_path)
    fails, warns = [], []
    tmp = tempfile.mkdtemp(prefix='rfhselftest_')
    try:
        rfd_path = rfh.find_rfd(rfh_path)
        rfd = rfh.load_data(rfd_path)
        entries, idx_len = rfh.read_index(rfh_path)

        # T1 索引自洽（独立走查）
        blob = open(rfh_path, 'rb').read()
        pos, cnt = 0, 0
        while pos + rfh.REC <= len(blob):
            nl = int.from_bytes(blob[pos:pos + 4], 'little')
            if nl == 0 or nl > 1024 or pos + rfh.REC + nl > len(blob):
                break
            pos += rfh.REC + nl
            cnt += 1
        if pos != len(blob) or cnt != len(entries):
            fails.append(f'T1 索引走查错位：停在 {pos}/{len(blob)}，{cnt} 条 vs {len(entries)} 条')

        # T2 元数据与块排布
        expect = 0
        for e in entries:
            if e.flags not in (0, rfh.FLAG_COMPRESSED):
                fails.append(f'T2 {e.name}: flags={e.flags}')
            if e.offset != expect:
                fails.append(f'T2 {e.name}: 块不连续（{e.offset} != {expect}）')
            expect = e.offset + e.stored
            if e.compressed:
                size = int.from_bytes(rfd[e.offset:e.offset + 4], 'little')
                magic = int.from_bytes(rfd[e.offset + 4:e.offset + 6], 'little')
                if magic != rfh.ZLIB_MAGIC:
                    fails.append(f'T2 {e.name}: 块头 magic={magic:#06x}')
                if size != e.raw:
                    fails.append(f'T2 {e.name}: 块头 raw={size} != 索引 {e.raw}')
                # 尾部必须是 4 字节 Adler-32（原版每个压缩块都有）：严格 zlib 解得开才算对。
                zstream = rfd[e.offset + rfh.SIZE_FIELD:e.offset + e.stored]
                try:
                    zlib.decompress(zstream)
                except zlib.error:
                    fails.append(f'T2 {e.name}: 缺尾部 Adler-32（严格 zlib 解不开）')
        if expect != len(rfd):
            fails.append(f'T2 末尾不吻合：{expect} != {len(rfd)}')

        # T3 全量解压
        ex1 = os.path.join(tmp, 'ex1')
        tree1, _ = extract_all(rfh_path, ex1)
        total = sum(n for _, n in tree1.values())
        if len(tree1) != len(entries):
            fails.append(f'T3 解出 {len(tree1)} 个 != {len(entries)} 条')

        # T3b 解出来的 TGA 里，引擎加载不了的有哪些 —— 只警告，不算失败。
        # 原因：原版数据里本来就有一个这种文件（textures\tvt_blood_32.tga，
        # 8 位索引色，没有任何模型引用它），把它当失败会误报。
        # 真正要警惕的是「原件里本来没有、打回之后才冒出来」的那种。
        bad_tga = []
        for key, (path, _) in tree1.items():
            if key.endswith('.tga'):
                ok, why = check_tga.check(path)
                if not ok:
                    bad_tga.append(f'{key}: {why}')

        if bad_tga:
            warns.append(f'{len(bad_tga)} 个 TGA 引擎加载不了（原版数据里也有这种，'
                         f'如未改动可忽略）：' + '；'.join(bad_tga[:3]))

        if quick:
            stored = sum(1 for e in entries if not e.compressed)
            print(f'  {"OK  " if not fails else "FAIL"} {name:22s} {len(entries):5d} 条 '
                  f'({len(entries) - stored} 压缩 / {stored} 存储), '
                  f'解出 {total:,} 字节（--quick：跳过往返）')
            for w in warns:
                print(f'         警告 {w}')
            for f in fails[:6]:
                print(f'         {f}')
            return not fails

        # T4 原样打回（空替换目录）：归档必须**逐字节**相同
        empty = os.path.join(tmp, 'empty')
        os.makedirs(empty)
        rt1 = os.path.join(tmp, 'rt1', name)
        os.makedirs(os.path.dirname(rt1))
        rfh.cmd_replace(argparse.Namespace(rfh=rfh_path, data=rfd_path, dir=empty,
                                        out=rt1, show=0))
        e1, _ = rfh.read_index(rt1)
        d1 = rfh.load_data(rfh.find_rfd(rt1))
        if sha(d1) != sha(rfd) or open(rt1, 'rb').read() != open(rfh_path, 'rb').read():
            fails.append('T4 空替换目录打回后，归档字节与原件不同')
        if len(e1) != len(entries):
            fails.append(f'T4 打回后条目数变了：{len(e1)} != {len(entries)}')
        else:
            for a, b in zip(entries, e1):
                if (a.name, a.file_date, a.flags, a.raw) != (b.name, b.file_date, b.flags, b.raw):
                    fails.append(f'T4 {a.name}: 元数据变了')
                    break
                if sha(rfh.read_data(a, rfd)[0]) != sha(rfh.read_data(b, d1)[0]):
                    fails.append(f'T4 {a.name}: 内容变了')
                    break

        # T5 从打回后的归档再解压，逐字节比对
        ex2 = os.path.join(tmp, 'ex2')
        tree2, _ = extract_all(rt1, ex2)
        problems = compare_trees(tree1, tree2)
        if problems:
            fails.append(f'T5 二次解压不一致：{problems[0]}')

        # T6 二次往返
        rt2 = os.path.join(tmp, 'rt2', name)
        os.makedirs(os.path.dirname(rt2))
        rfh.cmd_replace(argparse.Namespace(rfh=rt1, data=rfh.find_rfd(rt1), dir=empty,
                                        out=rt2, show=0))
        ex3 = os.path.join(tmp, 'ex3')
        tree3, _ = extract_all(rt2, ex3)
        problems = compare_trees(tree1, tree3)
        if problems:
            fails.append(f'T6 二次往返不一致：{problems[0]}')

        # T7 从零打包（pack）：目录 -> 新归档，再解出来逐字节比对
        pk = os.path.join(tmp, 'packed', name)
        os.makedirs(os.path.dirname(pk))
        rfh.cmd_pack(argparse.Namespace(paths=[ex1, pk], dir=ex1, out=pk,
                                        store=[], level=9, date=None))
        e7, _ = rfh.read_index(pk)
        d7 = rfh.load_data(rfh.find_rfd(pk))
        expect7 = 0
        for e in e7:
            if e.offset != expect7:
                fails.append(f'T7 {e.name}: 块不连续（{e.offset} != {expect7}）')
                break
            expect7 = e.offset + e.stored
            if e.compressed:
                stream = d7[e.offset + rfh.SIZE_FIELD:e.offset + e.stored]
                try:
                    zlib.decompress(stream)
                except zlib.error:
                    fails.append(f'T7 {e.name}: 缺尾部 Adler-32')
                    break
        if expect7 != len(d7):
            fails.append(f'T7 末尾不吻合：{expect7} != {len(d7)}')
        ex7 = os.path.join(tmp, 'ex7')
        tree7, _ = extract_all(pk, ex7)
        problems = compare_trees(tree1, tree7)
        if problems:
            fails.append(f'T7 pack 往返不一致：{problems[0]}')

        stored = sum(1 for e in entries if not e.compressed)
        status = 'OK  ' if not fails else 'FAIL'
        print(f'  {status} {name:22s} {len(entries):5d} 条 '
              f'({len(entries) - stored} 压缩 / {stored} 存储), '
              f'解出 {total:,} 字节, replace 往返逐字节一致, pack 往返内容一致'
              + ('' if not fails else f'  问题 {len(fails)} 处'))
        for w in warns:
            print(f'         警告 {w}')
        for f in fails[:6]:
            print(f'         {f}')
        if keep:
            print(f'         中间产物保留在 {tmp}')
        return not fails
    finally:
        if not keep:
            shutil.rmtree(tmp, ignore_errors=True)


def main():
    ap = argparse.ArgumentParser(description='RFH/RFD 工具自检（解压-打回-再解压）')
    ap.add_argument('paths', nargs='+', help='DATA 目录或 .RFH 文件')
    ap.add_argument('--keep', action='store_true', help='保留中间产物')
    ap.add_argument('--quick', action='store_true',
                    help='只做 T1-T3（解压与结构校验），跳过往返与打包测试')
    args = ap.parse_args()

    archives = find_archives(args.paths)
    if not archives:
        print('没找到 .RFH', file=sys.stderr)
        return 2
    print(f'自检 {len(archives)} 个归档：')
    ok = True
    for a in archives:
        try:
            ok &= check_archive(a, keep=args.keep, quick=args.quick)
        except Exception as exc:                      # noqa: BLE001
            ok = False
            print(f'  FAIL {os.path.basename(a):22s} 异常：{exc!r}')
    print('\n结论：' + ('全部通过' if ok else '有失败项'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
