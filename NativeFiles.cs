using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FolderBridge {
    public class LinkInfo {
        public string Path, Target, RawTarget, Kind;
        public bool Directory;
    }
    public static class NativeFiles {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sec, uint mode, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError=true)]
        static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inSize, byte[] output, int outSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern uint GetFileAttributes(string path);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.U1)]
        static extern bool CreateSymbolicLink(string link, string target, uint flags);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern bool RemoveDirectory(string path);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern bool DeleteFile(string path);

        public static string Full(string path) {
            if (String.IsNullOrWhiteSpace(path)) throw new IOException("请选择目录。");
            string full=System.IO.Path.GetFullPath(path.Trim().Trim('"'));
            string root=System.IO.Path.GetPathRoot(full);
            return full.Length>root.Length ? full.TrimEnd('\\') : full;
        }
        public static string Long(string path) {
            if(path.StartsWith(@"\\?\")) return path;
            if(path.StartsWith(@"\\")) return @"\\?\UNC\"+path.Substring(2);
            return @"\\?\"+path;
        }
        static string Plain(string path) {
            if(path.StartsWith(@"\??\UNC\")) return @"\\"+path.Substring(8);
            if(path.StartsWith(@"\\?\UNC\")) return @"\\"+path.Substring(8);
            if(path.StartsWith(@"\??\") || path.StartsWith(@"\\?\")) return path.Substring(4);
            return path;
        }
        public static bool Same(string a,string b) { return String.Equals(Full(a),Full(b),StringComparison.OrdinalIgnoreCase); }
        public static bool Inside(string path,string root) {
            return Same(path,root) || Full(path).StartsWith(Full(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);
        }
        public static uint Attributes(string path) {
            uint attr=GetFileAttributes(Long(Full(path)));
            if(attr==0xffffffff) {
                int code=Marshal.GetLastWin32Error();
                if(code==2 || code==3) return attr;
                throw new IOException("无法读取路径："+path+"；"+new Win32Exception(code).Message);
            }
            return attr;
        }
        public static bool Exists(string path) { return Attributes(path)!=0xffffffff; }
        public static bool IsLink(string path) { uint a=Attributes(path); return a!=0xffffffff && (a&0x400)!=0; }
        public static string Physical(string path) {
            path=Full(path); string current=path,tail="";
            while(!Exists(current)) {
                string parent=System.IO.Path.GetDirectoryName(current);
                if(String.IsNullOrEmpty(parent)) throw new IOException("找不到路径所在磁盘："+path);
                tail=System.IO.Path.Combine(System.IO.Path.GetFileName(current),tail); current=parent;
            }
            using(var h=CreateFile(Long(current),0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero)) {
                if(h.IsInvalid) throw new IOException("无法解析实际路径："+current+"；"+new Win32Exception(Marshal.GetLastWin32Error()).Message);
                var buffer=new StringBuilder(32768);
                if(GetFinalPathNameByHandle(h,buffer,(uint)buffer.Capacity,0)==0) throw new Win32Exception(Marshal.GetLastWin32Error());
                return Full(System.IO.Path.Combine(Plain(buffer.ToString()),tail));
            }
        }
        public static void ValidatePair(string source,string destination) {
            source=Full(source); destination=Full(destination);
            if(!System.IO.Directory.Exists(source)) throw new IOException("源目录不存在或无法访问。");
            if(Same(source,System.IO.Path.GetPathRoot(source)) || Same(destination,System.IO.Path.GetPathRoot(destination))) throw new IOException("请选择磁盘中的文件夹，不要直接选择整个磁盘根目录。");
            string a=Physical(source),b=Physical(destination);
            if(Inside(a,b)||Inside(b,a)) throw new IOException("源目录与目标目录不能相同或互相包含（包含目录链接指向的真实路径）。");
        }
        public static void RequirePlainParents(string path,string root) {
            path=Full(path); root=Full(root);
            if(!Inside(path,root) || Same(path,root)) throw new IOException("操作位置超出目标目录。");
            string p=System.IO.Path.GetDirectoryName(path);
            while(!String.IsNullOrEmpty(p)) {
                if(Exists(p) && IsLink(p)) throw new IOException("父目录是链接，为避免修改其他位置已停止："+p);
                if(Same(p,System.IO.Path.GetPathRoot(p))) break;
                p=System.IO.Path.GetDirectoryName(p);
            }
        }
        public static LinkInfo ReadLink(string path) {
            uint attr=Attributes(path);
            if(attr==0xffffffff || (attr&0x400)==0) throw new IOException("此位置不是链接："+path);
            var result=new LinkInfo {Path=Full(path),Directory=(attr&16)!=0};
            using(var h=CreateFile(Long(path),0,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero)) {
                if(h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                byte[] data=new byte[16384];int n;
                if(!DeviceIoControl(h,0x000900a8,null,0,data,data.Length,out n,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                uint tag=BitConverter.ToUInt32(data,0);
                if(tag!=0xa0000003 && tag!=0xa000000c) throw new IOException("不支持的重解析点（例如云端占位文件），请先在本地下载："+path);
                int start=tag==0xa0000003?16:20;
                int offset=BitConverter.ToUInt16(data,8),length=BitConverter.ToUInt16(data,10);
                if(start+offset+length>n) throw new IOException("链接数据无效。");
                result.RawTarget=Plain(Encoding.Unicode.GetString(data,start+offset,length));
                result.Kind=tag==0xa0000003?"Junction":"SymbolicLink";
                result.Target=Full(System.IO.Path.IsPathRooted(result.RawTarget)?result.RawTarget:System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path),result.RawTarget));
                return result;
            }
        }
        static void U16(byte[] data,int offset,int value) {Array.Copy(BitConverter.GetBytes((ushort)value),0,data,offset,2);}
        public static void CreateJunction(string path,string target) {
            if(target.StartsWith(@"\\")) throw new IOException("目录联接不支持网络目标，请使用本地磁盘目录。");
            byte[] sub=Encoding.Unicode.GetBytes(@"\??\"+Full(target));
            byte[] print=Encoding.Unicode.GetBytes(Full(target));
            byte[] data=new byte[16+sub.Length+2+print.Length+2];
            if(data.Length>16384) throw new IOException("链接目标路径过长。");
            Array.Copy(BitConverter.GetBytes(0xa0000003u),data,4);
            U16(data,4,data.Length-8);U16(data,8,0);U16(data,10,sub.Length);U16(data,12,sub.Length+2);U16(data,14,print.Length);
            Array.Copy(sub,0,data,16,sub.Length);Array.Copy(print,0,data,18+sub.Length,print.Length);
            System.IO.Directory.CreateDirectory(path);
            try {
                using(var h=CreateFile(Long(path),0x40000000,0,IntPtr.Zero,3,0x02200000,IntPtr.Zero)) {
                    if(h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    int n;if(!DeviceIoControl(h,0x000900a4,data,data.Length,null,0,out n,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            } catch { System.IO.Directory.Delete(path,false);throw; }
        }
        public static void CreateLink(string path,string target,string kind,bool directory) {
            if(kind=="Junction") { CreateJunction(path,target);return; }
            uint flags=directory?1u:0u;
            if(!CreateSymbolicLink(Long(path),target,flags|2) && !CreateSymbolicLink(Long(path),target,flags))
                throw new IOException("无法创建符号链接；请启用 Windows 开发者模式或以管理员身份运行。"+new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        public static void DeleteLinkOnly(string path) {
            uint attr=Attributes(path);
            if(attr==0xffffffff || (attr&0x400)==0) throw new IOException("此位置已不是链接，停止替换："+path);
            bool ok=(attr&16)!=0?RemoveDirectory(Long(path)):DeleteFile(Long(path));
            if(!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
