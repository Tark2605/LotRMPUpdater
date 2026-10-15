import sys, hashlib, warnings
warnings.filterwarnings("ignore")
from pyfastcdc.py.constants import GEAR, MASKS
from pyfastcdc import FastCDC
M=(1<<64)-1
def splitmix(seed, n):
    out=bytearray(n); x=seed&M; i=0
    while i<n:
        x=(x+0x9E3779B97F4A7C15)&M
        z=x
        z=((z^(z>>30))*0xBF58476D1CE4E5B9)&M
        z=((z^(z>>27))*0x94D049BB133111EB)&M
        z=z^(z>>31)
        b=z.to_bytes(8,'little')
        k=min(8,n-i); out[i:i+k]=b[:k]; i+=k
    return bytes(out)
cases=[("rand4m",splitmix(1,4*1024*1024),262144,65536,1048576),
       ("rand3m_odd",splitmix(2,3000001),262144,65536,1048576),
       ("zero3m",bytes(3*1024*1024+5),262144,65536,1048576),
       ("small_params",splitmix(3,200000),1024,256,4096),
       ("tiny",splitmix(4,1000),262144,65536,1048576)]
for name,data,avg,mn,mx in cases:
    ch=list(FastCDC(avg,min_size=mn,max_size=mx).cut_buf(data))
    lens=[c.length for c in ch]
    allh=hashlib.sha256(b''.join(hashlib.sha256(bytes(c.data)).digest() for c in ch)).hexdigest()
    print(name, len(data), avg, mn, mx, ",".join(map(str,lens)), allh)
print("SPLITMIX_SHA_rand4m", hashlib.sha256(cases[0][1]).hexdigest())
