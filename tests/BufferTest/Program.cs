using System;
using System.Runtime.InteropServices;
using System.Threading;
class Program {
 static void Main() {
  foreach(bool alpha in new[]{false,true}) {
   int n=alpha?4:3,w=2,h=2,stride=w*n*2+4;
   IntPtr data=Marshal.AllocHGlobal(stride*h);
   try {
    ushort[] samples={0,32768,65535,16384};
    for(int y=0;y<h;y++)for(int x=0;x<w;x++)for(int c=0;c<n;c++)Marshal.WriteInt16(data,y*stride+(x*n+c)*2,unchecked((short)samples[(x+y+c)%4]));
    var bytes=Rgb16Preview.Convert(w,h,stride,data,alpha,CancellationToken.None);
    var values=MemoryMarshal.Cast<byte,float>(bytes.AsSpan());
    for(int y=0;y<h;y++)for(int x=0;x<w;x++)for(int c=0;c<4;c++) {
     float expected=c==3&&!alpha?1:samples[(x+y+c)%4]/65535f;
     if(Math.Abs(values[(y*w+x)*4+c]-expected)>0.000001)throw new Exception("Pixel mismatch");
    }
    try{Rgb16Preview.Convert(w,h,stride,data,alpha,new CancellationToken(true));throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
   } finally{Marshal.FreeHGlobal(data);}
  }
  Console.WriteLine("PASS: RGB16/RGBA16, padded stride, endpoints/midtones, alpha, cancellation.");
 }
}
