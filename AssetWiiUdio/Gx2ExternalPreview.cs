using System;
using System.IO;
using System.Buffers.Binary;

namespace AssetWiiUdio;

// Native GX2 inspection and BC1 decoder. No external process, Python or third-party code.
// IMPORTANT: Macro-tiled Wii U surfaces require an address-library implementation;
// do not decode tiled data as linear, which would display corrupt textures.
internal static class Gx2ExternalPreview
{
    internal sealed record Result(int Width, int Height, byte[] Bgra, byte[] Dds);
    static uint BE(byte[] b,int p) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p,4));
    public static Result ConvertDds(byte[] b)
    {
        if(b.Length<128||System.Text.Encoding.ASCII.GetString(b,0,4)!="DDS ")throw new InvalidDataException("Invalid DDS header.");
        int h=BitConverter.ToInt32(b,12),w=BitConverter.ToInt32(b,16);string code=System.Text.Encoding.ASCII.GetString(b,84,4);
        if(w<1||h<1||w>8192||h>8192||(code!="DXT1"&&code!="DXT5"))throw new NotSupportedException("External DDS preview requires BC1/DXT1 or BC3/DXT5.");
        return DecodeLinearBc(b.AsSpan(128).ToArray(),w,h,code=="DXT5");
    }
    public static Result Convert(byte[] input)
    {
        if(input.Length<0x100 || input[0]!='G'||input[1]!='f'||input[2]!='x'||input[3]!='2')
            throw new InvalidDataException("Not a GX2 Gfx2 container.");
        // GX2 blocks use big-endian metadata. Locate the surface descriptor (block type 0x0B).
        int at=0x20;
        while(at+0x20<=input.Length)
        {
            if(input[at]!='B'||input[at+1]!='L'||input[at+2]!='K'||input[at+3]!='{')
                throw new InvalidDataException($"Invalid GX2 block at 0x{at:X}.");
            int header=checked((int)BE(input,at+4));
            int blockType=checked((int)BE(input,at+16));
            int size=checked((int)BE(input,at+20));
            if(header<0x20||size<0||at+(long)header+size>input.Length)
                throw new InvalidDataException("Invalid GX2 block length.");
            if(blockType==0x0B)
            {
                int d=at+header;
                if(size<0x40)throw new InvalidDataException("Truncated GX2 surface descriptor.");
                int width=checked((int)BE(input,d+4)),height=checked((int)BE(input,d+8));
                int pitch=checked((int)BE(input,d+0x3C));
                uint format=BE(input,d+0x14),tileMode=BE(input,d+0x30),swizzle=BE(input,d+0x34);
                if(width<1||height<1||width>8192||height>8192)
                    throw new InvalidDataException("Invalid GX2 dimensions.");
                bool bc3 = format==0x33 || format==0x433;
                if(format!=0x31 && format!=0x431 && !bc3)
                    throw new NotSupportedException($"GX2 format 0x{format:X} is not yet supported (BC1/BC3 only).");
                if(tileMode!=0 && tileMode!=1 && tileMode!=2 && tileMode!=4)
                    throw new NotSupportedException($"GX2 tile mode {tileMode} is not supported yet.");
                // Linear surfaces: image payload is in a separate GX2 image-data block (0x0C).
                int scan=at+header+size;
                while(scan+0x20<=input.Length)
                {
                    if(input[scan]!='B'||input[scan+1]!='L'||input[scan+2]!='K'||input[scan+3]!='{')break;
                    int bh=checked((int)BE(input,scan+4));int bt=checked((int)BE(input,scan+16));int bs=checked((int)BE(input,scan+20));
                    if(bh<0x20||bs<0||scan+(long)bh+bs>input.Length)break;
                    if(bt==0x0C)
                    {
                        int blocksX=(width+3)/4, blocksY=(height+3)/4;
                        byte[] linear=Deswizzle(input.AsSpan(scan+bh,bs),blocksX,blocksY,pitch,bc3?128:64,(int)tileMode,swizzle);
                        return DecodeLinearBc(linear,width,height,bc3);
                    }
                    scan+=bh+bs;
                }
                throw new InvalidDataException("GX2 image-data block not found.");
            }
            at+=header+size;
        }
        throw new InvalidDataException("GX2 surface descriptor not found.");
    }
    // Native GX2 address translation for single-sample BC1/BC3 surfaces.
    // Pixel coordinates here are *compressed block* coordinates, not texels.
    // Wii U GX2 uses 8x8 microtiles and bank/pipe-interleaved macrotiles.
    static int MicroIndex(int x,int y,int bpp)
    {
        if(bpp==64)
            return (x&1) | ((y&1)<<1) | (((x>>1)&1)<<2) | (((x>>2)&1)<<3) | (((y>>1)&1)<<4) | (((y>>2)&1)<<5);
        // 128-bit blocks follow the default GX2 element bit ordering.
        return (x&1) | (((x>>1)&1)<<1) | ((y&1)<<2) | (((x>>2)&1)<<3) | (((y>>1)&1)<<4) | (((y>>2)&1)<<5);
    }
    static int SurfaceOffset(int x,int y,int pitch,int height,int bpp,int mode,uint swizzle)
    {
        int elementBytes=bpp/8;
        if(mode==0||mode==1)return (y*pitch+x)*elementBytes;
        int elem=MicroIndex(x,y,bpp)*elementBytes;
        if(mode==2)
            return ((y/8)*(pitch/8)+(x/8))*64*elementBytes+elem;
        if(mode!=4)throw new NotSupportedException($"GX2 tile mode {mode} not supported.");
        int pipe=((y>>3)^(x>>3))&1;
        int bank=(((y>>5)^(x>>3))&1) | ((((y>>4)^(x>>4))&1)<<1);
        int bankPipe=(pipe+2*bank) ^ (int)((swizzle>>8)&7);
        pipe=bankPipe&1;bank=(bankPipe>>1)&3;
        int macroBytes=32*16*elementBytes;
        int macroOffset=((x/32)+(pitch/32)*(y/16))*macroBytes;
        int total=elem+(macroOffset>>3);
        return (bank<<9)|(pipe<<8)|(total&255)|((total&~255)<<3);
    }
    static byte[] Deswizzle(ReadOnlySpan<byte> tiled,int blocksX,int blocksY,int pitch,int bpp,int mode,uint swizzle)
    {
        int elementBytes=bpp/8;
        if(pitch<blocksX || pitch>32768)throw new InvalidDataException($"Invalid GX2 pitch {pitch} for {blocksX} blocks.");
        byte[] linear=new byte[checked(blocksX*blocksY*elementBytes)];
        for(int y=0;y<blocksY;y++)for(int x=0;x<blocksX;x++)
        {
            int src=SurfaceOffset(x,y,pitch,blocksY,bpp,mode,swizzle);
            int dst=(y*blocksX+x)*elementBytes;
            if(src<0||src>tiled.Length-elementBytes)
                throw new InvalidDataException($"GX2 tiled block ({x},{y}) at 0x{src:X} exceeds image data ({tiled.Length} bytes).");
            tiled.Slice(src,elementBytes).CopyTo(linear.AsSpan(dst,elementBytes));
        }
        return linear;
    }
    static Result DecodeLinearBc(ReadOnlySpan<byte> data,int width,int height,bool bc3)
    {
        int bx=(width+3)/4,by=(height+3)/4,blockBytes=bc3?16:8,n=checked(bx*by*blockBytes);
        if(data.Length<n)throw new InvalidDataException("BC image data truncated.");
        byte[] pixels=new byte[checked(width*height*4)];
        Span<byte> alpha=stackalloc byte[8]; Span<byte> colors=stackalloc byte[16];
        for(int yb=0;yb<by;yb++)for(int xb=0;xb<bx;xb++)
        {
            var block=data.Slice((yb*bx+xb)*blockBytes,blockBytes);
            var colorBlock=block.Slice(bc3?8:0,8);
            ushort c0=BinaryPrimitives.ReadUInt16LittleEndian(colorBlock),c1=BinaryPrimitives.ReadUInt16LittleEndian(colorBlock.Slice(2));
            uint bits=BinaryPrimitives.ReadUInt32LittleEndian(colorBlock.Slice(4));
            ulong alphaBits=0;
            if(bc3)
            {
                alpha[0]=block[0];alpha[1]=block[1];
                if(alpha[0]>alpha[1])
                    for(int i=2;i<8;i++)alpha[i]=(byte)(((8-i)*alpha[0]+(i-1)*alpha[1])/7);
                else
                {
                    for(int i=2;i<6;i++)alpha[i]=(byte)(((6-i)*alpha[0]+(i-1)*alpha[1])/5);
                    alpha[6]=0;alpha[7]=255;
                }
                for(int i=0;i<6;i++)alphaBits|=(ulong)block[2+i]<<(8*i);
            }

            Color565(c0,colors);Color565(c1,colors.Slice(4));
            for(int c=0;c<3;c++)
            {
                colors[8+c]=(byte)((bc3||c0>c1)?(2*colors[c]+colors[4+c])/3:(colors[c]+colors[4+c])/2);
                colors[12+c]=(byte)((bc3||c0>c1)?(colors[c]+2*colors[4+c])/3:0);
            }
            colors[3]=colors[7]=colors[11]=255;colors[15]=(byte)((bc3||c0>c1)?255:0);
            for(int py=0;py<4;py++)for(int px=0;px<4;px++)
            {
                int x=xb*4+px,y=yb*4+py;if(x>=width||y>=height)continue;
                int src=(int)((bits>>(2*(py*4+px)))&3)*4;
                int target=(y*width+x)*4;
                colors.Slice(src,4).CopyTo(pixels.AsSpan(target,4));
                if(bc3)pixels[target+3]=alpha[(int)((alphaBits>>(3*(py*4+px)))&7)];
            }
        }
        byte[] dds=new byte[128+n];
        dds[0]=(byte)'D';dds[1]=(byte)'D';dds[2]=(byte)'S';dds[3]=(byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(4),124);
        BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(8),0x81007);
        BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(12),height);
        BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(16),width);
        BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(20),n);
        BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(76),32);
        BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(80),4);
        dds[84]=(byte)'D';dds[85]=(byte)'X';dds[86]=(byte)'T';dds[87]=(byte)(bc3?'5':'1');
        BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(108),0x1000);
        data.Slice(0,n).CopyTo(dds.AsSpan(128));
        return new Result(width,height,pixels,dds);
    }
    static void Color565(ushort c,Span<byte> p)
    {
        p[0]=(byte)(((c&31)*255+15)/31);
        p[1]=(byte)((((c>>5)&63)*255+31)/63);
        p[2]=(byte)((((c>>11)&31)*255+15)/31);
    }
}

