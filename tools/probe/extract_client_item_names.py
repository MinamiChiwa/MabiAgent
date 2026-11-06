"""从本机客户端资源提取消耗品类型 ID 与名称；只写指定输出文件。

用法：python tools/probe/extract_client_item_names.py --client <appdata> --out <json>
输出包含完整 Consumable 表、文件指纹和行偏移，可供审核后更新可信目录。
"""
from pathlib import Path
import argparse
import hashlib
import json
import re
import struct
from client_resource_package import Package, decode, string


class Reader:
    def __init__(self, data):
        self.data, self.at = data, 0

    def take(self, size):
        if size < 0 or self.at + size > len(self.data):
            raise ValueError('客户端表截断')
        value = self.data[self.at:self.at + size]
        self.at += size
        return value

    def u32(self):
        return struct.unpack('<I', self.take(4))[0]

    def var(self):
        value = 0
        for shift in range(0, 35, 7):
            byte = self.take(1)[0]
            if shift == 28 and byte > 15:
                raise ValueError('客户端变长整数溢出')
            value |= (byte & 127) << shift
            if byte < 128:
                return value
        raise ValueError('客户端变长整数溢出')

    def text(self):
        return self.take(self.u32()).decode('utf8')

    def end(self):
        if self.at != len(self.data):
            raise ValueError('客户端表存在未解析字节')


def tables(meta, texts):
    reader = Reader(meta)
    result, offset = {}, 0
    count = reader.var()
    if not 0 < count <= 4096:
        raise ValueError('客户端表数量异常')
    for _ in range(count):
        name = string(texts, reader.var())
        field, rows, size, delta = (reader.var() for _ in range(4))
        if name in result or rows > 1_000_000:
            raise ValueError('重复表或行数异常')
        result[name] = dict(field=field, rows=rows, size=size, data_offset=offset)
        offset += delta
    for table in result.values():
        offsets, current = [], table['data_offset']
        for _ in range(table['rows']):
            offsets.append(current)
            current += reader.var()
        table['offsets'] = offsets
    for table in result.values():
        table['meta_offset'] = reader.at
        table['block'] = reader.take(table['size'])
    reader.end()
    return result


def string_tables(data):
    reader, result = Reader(data), {}
    count = reader.u32()
    if count > 10000:
        raise ValueError('文本组数量异常')
    for _ in range(count):
        name = reader.text()
        reader.take(1)
        rows = {}
        for _ in range(reader.u32()):
            key = reader.text()
            rows[key] = reader.text()
        result[name] = rows
        for _ in range(reader.u32()):
            sub, rows = reader.text(), {}
            for _ in range(reader.u32()):
                key = str(reader.u32())
                rows[key] = reader.text()
            result[name + '/' + sub] = rows
    reader.end()
    return result


def display_name(raw, localized):
    def tier(match):
        value = int(match[2])
        if value <= 0:
            return ''
        template = localized['Common']['NoRichText_RuneTierFormat']
        return template.replace('{0}', str(value) if value > 1 else '')
    name = re.sub(r'%RuneTier,\s*(Season\d+),\s*(\d+)%', tier, raw)
    name = re.sub(r'</?color(?:=#[0-9a-fA-F]{6,8})?>', '', name)
    if re.search(r'%[A-Za-z]+[,\s]', name) or '<' in name or not name.strip() or len(name.strip()) > 80:
        raise ValueError('尚未解析的物品显示名：' + raw)
    return name.strip()


def extract(client):
    folder = client / 'ResourcesPackage/Starter'
    package = Package(folder)
    wanted = {
        'Sheetdata_Client/client.sheetmeta.bin': 'meta',
        'Sheetdata_Client/client.sheettext.bin': 'text',
        'StringTables/compiled.strings.bin': 'strings',
    }
    resources, sources = {}, []
    for _, path, key in package.entries():
        if path not in wanted:
            continue
        offset, raw = package.raw(key)
        value = decode(offset, raw)
        resources[wanted[path]] = value
        sources.append(dict(path=path, package_key=key, offset=offset,
                            packed_bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest(),
                            decoded_sha256=hashlib.sha256(value).hexdigest()))
    if len(resources) != len(wanted):
        raise ValueError('客户端缺少所需名称表')
    table = tables(resources['meta'], resources['text'])['Consumable']
    localized = string_tables(resources['strings'])
    reader, items, excluded = Reader(table['block']), {}, []
    for row in range(table['rows']):
        meta_offset = table['meta_offset'] + reader.at
        item_id = str(reader.u32())
        name_offset, flavor_offset = reader.var(), reader.var()
        raw_name = string(resources['text'], name_offset)
        if item_id == '0' or item_id in items:
            raise ValueError('物品类型 ID 重复或为零')
        try:
            name = display_name(raw_name, localized)
        except ValueError as error:
            excluded.append(dict(wire_id=item_id, raw_name=raw_name, reason=str(error)))
            continue
        items[item_id] = dict(name=name, raw_name=raw_name, row=row,
                              meta_offset=meta_offset, name_offset=name_offset,
                              data_offset=table['offsets'][row])
    reader.end()
    return dict(schema_version=1, table='Consumable', rows=table['rows'],
                index_sha256=hashlib.sha256((folder/'index').read_bytes()).hexdigest(),
                sources=sources, items=items, excluded=excluded)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    result = extract(args.client.resolve())
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf8')
    print(json.dumps(dict(rows=result['rows'], named=len(result['items']), excluded=len(result['excluded']))))


if __name__ == '__main__':
    main()
