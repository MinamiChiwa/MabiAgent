"""只读解析客户端资源索引与压缩封装。"""
from pathlib import Path
import struct
import functools
import zlib


def decode(offset,data):
    mask=(1<<64)-1
    def rol(x,n):
        x&=mask
        return ((x<<n)|(x>>(64-n)))&mask
    size=len(data)
    v=((offset ^ ((~size)&mask))+0x171974e5e5bdda11)&mask
    x=v*0xd3833e804f4c574b&mask
    y=rol(size+0x849ee64c9720bb37-offset-0x708b6ba5fb11f033,12)
    z=rol(v-size-0x849ee64c9720bb37,44)
    out=bytearray(data)
    for at in range(0,size,8):
        key=x.to_bytes(8,'little')
        x,y,z=z*0xd3833e804f4c574b&mask,rol(y-x,12),rol(z-y,44)
        for i in range(min(8,size-at)):out[at+i]^=key[i]
    expected=struct.unpack_from('<I',out)[0]
    assert expected<100_000_000, (expected,out[:20].hex())
    result=zlib.decompress(out[4:],-15)
    assert len(result)==expected,(len(result),expected)
    return result

def string(data, at):
    result=bytearray()
    for _ in range(4096):
        token=data[at]
        if token==0:return result.decode('utf8')
        if token&128:
            code=((token&127)<<8)|data[at+1]
            length=(code&31)+1
            source=at-1-(code>>5)
            assert source>=0
            result.extend(data[source:source+length])
            at+=2
        else:
            result.extend(data[at+1:at+1+token])
            at+=1+token
    raise ValueError('string budget')

class Package:
    def __init__(self,folder):
        self.folder=Path(folder)
        b=(self.folder/'index').read_bytes()
        magic,n=struct.unpack_from('<II',b)
        assert magic==0xe6a9c3ba
        self.contents=struct.unpack_from('<'+str(n)+'I',b,8)
        at=8+4*n
        deps=struct.unpack_from('<I',b,at)[0];at+=4
        for _ in range(deps):
            count=struct.unpack_from('<I',b,at+4)[0];at+=8+4*count
        hashes=struct.unpack_from('<I',b,at)[0];at+=4+4*hashes
        paths=struct.unpack_from('<I',b,at)[0];at+=4
        self.paths=struct.unpack_from('<'+str(paths)+'Q',b,at);at+=8*paths
        buckets=struct.unpack_from('<I',b,at)[0];at+=4+4*buckets
        size=struct.unpack_from('<I',b,at)[0];at+=4
        self.dirs=b[at:at+size];at+=size
        size=struct.unpack_from('<I',b,at)[0];at+=4
        self.names=b[at:at+size];at+=size
        size=struct.unpack_from('<I',b,at)[0];at+=4
        self.postfix=b[at:at+size];at+=size
        assert at==len(b),(at,len(b))
    @functools.lru_cache(None)
    def directory(self,at):
        if at==0:return ''
        end=at+1
        while self.dirs[end]>4:end+=1
        length=self.dirs[end]
        parent=at-int.from_bytes(self.dirs[end+1:end+1+length],'little') if length else 0
        segment=self.dirs[at:end].decode()
        return (self.directory(parent)+'/' if parent else '')+segment
    def entries(self):
        for i,v in enumerate(self.paths):
            folder=self.directory(v&0xffffff)
            name=string(self.names,(v>>24)&0xfffffff)
            ext=string(self.postfix,v>>52)
            yield i,(folder+'/' if folder else '')+name+ext,self.contents[i]
    def raw(self,key):
        number=key&0x7fff;index=key>>16
        folder=self.folder/'Starter' if key&0x8000 and self.folder.name!='Starter' else self.folder
        with (folder/f'{number:04d}.blob').open('rb') as stream:
            magic,start=struct.unpack('<II',stream.read(8))
            assert magic==0xe5a9c3ba
            if index==0:offset=start
            else:stream.seek(4+index*4);offset=struct.unpack('<I',stream.read(4))[0]
            stream.seek(8+index*4);end=struct.unpack('<I',stream.read(4))[0]
            stream.seek(offset);data=stream.read(end-offset)
            assert len(data)==end-offset
            return offset,data


