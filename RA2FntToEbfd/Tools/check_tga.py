#!/usr/bin/env python3
"""
扫描 TGA，找出 Emperor: Battle for Dune 引擎**加载不了**的文件。

引擎的判定（Game.exe 的 CTexture::LoadTGA，0x1c410）：

  1) 头部的 imageType（偏移 2）必须是 2（未压缩真彩）或 10（RLE）。
     其它值 -> 直接跳 0x1c658 报 "TGA is an invalid pixel format"。

  2) 头部像素深度（偏移 16）只认 15/16/24/32。
        15、16 -> format = 0x5550
        24     -> format = 0x8880
        32     -> format = 0x8888
     其它值（含 0、8、17..23、25..31、>32）走默认分支：只按"2 字节/像素"
     分配，**完全不设 format 字段**，并且同样报
     "TGA is an invalid pixel format"。

  之后 format 保持 0，任何 GetTextureBPP 调用都会弹
  "GetTextureBPP / Invalid Texture format 0"。

另外：名字形如 <base>_<size>.tga（size ∈ {16,32,64,128,256,512}）的会被当成
LOD 系列，引擎从较小尺寸开始逐倍尝试、取第一个存在的。所以一个坏的 *_32.tga
会**挡住**同族好的 *_64.tga。

用途：改完字体/贴图、重新打包 RFH 之前先跑一遍，避免把引擎读不了的 TGA
打进去（打进去的表现就是进游戏弹 "GetTextureBPP / Invalid Texture format 0"）。
详细背景见 Docs/RFH.md 的「TGA 为什么会让引擎弹框」一节。

用法：
    python3 check_tga.py <目录> [<目录> ...]        # 退出码 0 = 全部可加载
"""
import os
import sys

VALID_TYPE = (2, 10)
VALID_DEPTH = (15, 16, 24, 32)
EXT_LEN = 495
FOOTER_LEN = 26


def check(path):
    """返回 (是否可加载, 说明)。"""
    size = os.path.getsize(path)
    if size < 18:
        return False, f'文件只有 {size} 字节，连 18 字节头都不够'
    with open(path, 'rb') as f:
        h = f.read(18)
    id_len, cmap_type, img_type = h[0], h[1], h[2]
    w = h[12] | (h[13] << 8)
    hh = h[14] | (h[15] << 8)
    depth, desc = h[16], h[17]

    if img_type not in VALID_TYPE:
        return False, (f'imageType={img_type}（只认 2/10）'
                       + ('，整个文件全 0' if not any(h) else ''))
    if depth not in VALID_DEPTH:
        return False, f'像素深度={depth}（只认 15/16/24/32）'
    if w == 0 or hh == 0:
        return False, f'尺寸 {w}x{hh} 不合法'
    if img_type == 2:
        need = 18 + id_len + w * hh * (depth // 8)
        if size + 1 < need:
            return False, f'数据不足：{w}x{hh}x{depth} 需要 ≥{need} 字节，实际 {size}'
    return True, f'{w}x{hh} depth={depth} type={img_type}'


def main(dirs):
    total = bad = 0
    for root_dir in dirs:
        for dp, _, fns in os.walk(root_dir):
            for fn in sorted(fns):
                if not fn.lower().endswith('.tga'):
                    continue
                p = os.path.join(dp, fn)
                total += 1
                ok, why = check(p)
                if not ok:
                    bad += 1
                    print(f'  [无法加载] {os.path.relpath(p, root_dir)}'
                          f'  ({os.path.getsize(p)} 字节)  {why}')
    print(f'\n共 {total} 个 .tga，其中引擎加载不了的 {bad} 个。')
    return 0 if bad == 0 else 1


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1:]))
