using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FolderBridge {
    public class Inventory {
        public List<LinkInfo> Links=new List<LinkInfo>();
        public List<string> Errors=new List<string>();
        public long Files,Bytes;
        public static Inventory Scan(string root,CancellationToken token) {
            var result=new Inventory();var pending=new Stack<string>();pending.Push(NativeFiles.Full(root));
            while(pending.Count>0) {
                token.ThrowIfCancellationRequested();string dir=pending.Pop();
                try {
                    foreach(string path in System.IO.Directory.EnumerateFileSystemEntries(dir)) {
                        token.ThrowIfCancellationRequested();
                        try {
                            uint attr=NativeFiles.Attributes(path);
                            if(attr==0xffffffff) {result.Errors.Add("扫描期间路径消失："+path);continue;}
                            if((attr&0x400)!=0) result.Links.Add(NativeFiles.ReadLink(path));
                            else if((attr&16)!=0) pending.Push(path);
                            else {result.Files++;result.Bytes+=new FileInfo(path).Length;}
                        } catch(OperationCanceledException){throw;} catch(Exception ex){result.Errors.Add(path+"："+ex.Message);}
                    }
                } catch(OperationCanceledException){throw;} catch(Exception ex){result.Errors.Add(dir+"："+ex.Message);}
            }
            return result;
        }
    }
    public class LinkPlan {
        public string Path,OldTarget,NewTarget,Kind,Status,Reason;
        public bool Directory,Ready;
    }
    public class LinkScan {
        public List<LinkPlan> Plans=new List<LinkPlan>();
        public List<string> Errors=new List<string>();
    }
    public static class LinkService {
        public static string Map(string path,string from,string to) {
            if(String.IsNullOrWhiteSpace(from)||String.IsNullOrWhiteSpace(to)||!NativeFiles.Inside(path,from)) return null;
            return NativeFiles.Full(Path.Combine(to,NativeFiles.Full(path).Substring(NativeFiles.Full(from).Length).TrimStart('\\')));
        }
        public static LinkScan Scan(string source,string destination,string oldRoot,string newRoot,CancellationToken token) {
            destination=NativeFiles.Full(destination);
            if(!Directory.Exists(destination)) throw new IOException("目标目录尚不存在，请先复制或创建目标目录。");
            if(String.IsNullOrWhiteSpace(oldRoot)!=String.IsNullOrWhiteSpace(newRoot)) throw new IOException("旧根目录和新根目录需要同时填写。");
            if(!String.IsNullOrWhiteSpace(oldRoot)) {oldRoot=NativeFiles.Full(oldRoot);newRoot=NativeFiles.Full(newRoot);}
            var result=new LinkScan();var candidates=new Dictionary<string,LinkInfo>(StringComparer.OrdinalIgnoreCase);
            var dest=Inventory.Scan(destination,token);result.Errors.AddRange(dest.Errors);
            foreach(var link in dest.Links) candidates[link.Path]=link;
            if(!String.IsNullOrWhiteSpace(source)) {
                source=NativeFiles.Full(source);NativeFiles.ValidatePair(source,destination);
                var src=Inventory.Scan(source,token);result.Errors.AddRange(src.Errors);
                foreach(var link in src.Links) candidates[Map(link.Path,source,destination)]=link;
            }
            foreach(var pair in candidates) {
                token.ThrowIfCancellationRequested();LinkInfo item=pair.Value;
                var plan=new LinkPlan {Path=pair.Key,OldTarget=item.Target,Kind=item.Kind,Directory=item.Directory,Status="未处理"};
                plan.NewTarget=Map(item.Target,oldRoot,newRoot);
                if(plan.NewTarget==null && !String.IsNullOrWhiteSpace(source)) plan.NewTarget=Map(item.Target,source,destination);
                if(plan.NewTarget==null) {
                    // A healthy target outside the migration root is deliberately not copied or rewritten.
                    plan.NewTarget=item.Target;
                    plan.Status="外部依赖";plan.Reason="目标不在映射范围内；请设置旧项目根目录和新项目根目录。";
                    if(!String.IsNullOrWhiteSpace(newRoot) && NativeFiles.Inside(item.Target,newRoot)) Assess(plan,destination);
                    else if(!NativeFiles.Exists(item.Target)) {plan.Status="失效链接";plan.Reason="原目标不存在，且没有匹配的迁移映射。";}
                } else Assess(plan,destination);
                result.Plans.Add(plan);
            }
            return result;
        }
        static void Assess(LinkPlan plan,string destination) {
            plan.Ready=false;
            try {
                NativeFiles.RequirePlainParents(plan.Path,destination);
                if(!NativeFiles.Exists(plan.NewTarget)) {plan.Status="目标缺失";plan.Reason="新目标不存在，请先复制依赖目录。";return;}
                bool targetDir=Directory.Exists(plan.NewTarget);
                if(targetDir!=plan.Directory) throw new IOException("新目标的文件/目录类型不匹配。");
                if(NativeFiles.Inside(NativeFiles.Physical(plan.NewTarget),NativeFiles.Full(plan.Path))) throw new IOException("该目标会造成自引用或循环链接。");
                // Also reject an ancestor target: traversing it would lead back into the link.
                if(plan.Directory && NativeFiles.Inside(plan.Path,NativeFiles.Physical(plan.NewTarget))) throw new IOException("链接目标包含链接自身，会形成循环。");
                uint attr=NativeFiles.Attributes(plan.Path);
                if(attr!=0xffffffff && (attr&0x400)!=0) {
                    var existing=NativeFiles.ReadLink(plan.Path);
                    if(NativeFiles.Same(existing.Target,plan.NewTarget) && existing.Directory==plan.Directory && existing.Kind==plan.Kind) {
                        plan.Status="正常";plan.Reason="已指向正确位置，目标可访问。";return;
                    }
                } else if(attr!=0xffffffff) {
                    if((attr&16)==0 || !plan.Directory) throw new IOException("此位置已有真实文件，不会替换。");
                    if(Directory.EnumerateFileSystemEntries(plan.Path).Any()) throw new IOException("此位置是非空目录，不会替换。");
                }
                if(plan.Kind=="Junction" && plan.NewTarget.StartsWith(@"\\")) throw new IOException("目录联接不支持网络目标。");
                plan.Ready=true;plan.Status="可修复";plan.Reason="将创建链接或替换空目录/旧链接；不会修改目标内容。";
            } catch(Exception ex) {plan.Status="冲突";plan.Reason=ex.Message;}
        }
        public static void Apply(LinkPlan plan,string destination) {
            string expected=plan.NewTarget;
            Assess(plan,destination);
            if(plan.Status=="正常") return;
            if(!plan.Ready) throw new IOException(plan.Reason);
            NativeFiles.RequirePlainParents(plan.Path,destination);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.Path));
            LinkInfo oldLink=null;bool empty=false;
            uint attr=NativeFiles.Attributes(plan.Path);
            if(attr!=0xffffffff) {
                if((attr&0x400)!=0) {oldLink=NativeFiles.ReadLink(plan.Path);NativeFiles.DeleteLinkOnly(plan.Path);}
                else {Directory.Delete(plan.Path,false);empty=true;}
            }
            try {
                NativeFiles.CreateLink(plan.Path,expected,plan.Kind,plan.Directory);
                var check=NativeFiles.ReadLink(plan.Path);
                if(!NativeFiles.Same(check.Target,expected) || !NativeFiles.Exists(check.Target)) throw new IOException("修复后链接校验失败。");
                if(plan.Directory) { using(var enumerator=Directory.EnumerateFileSystemEntries(plan.Path).GetEnumerator()) {enumerator.MoveNext();} }
                else {using(var stream=File.Open(plan.Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)){stream.ReadByte();}}
                plan.Ready=false;plan.Status="已修复";plan.Reason="新目标已验证可访问。";
            } catch {
                if(NativeFiles.IsLink(plan.Path)) NativeFiles.DeleteLinkOnly(plan.Path);
                if(!NativeFiles.Exists(plan.Path)) {
                    if(oldLink!=null) NativeFiles.CreateLink(plan.Path,oldLink.RawTarget,oldLink.Kind,oldLink.Directory);
                    else if(empty) Directory.CreateDirectory(plan.Path);
                }
                throw;
            }
        }
    }
    public class CopyOptions {
        public string Source,Destination;
        public string[] Files;
        public int Threads=8;
        public bool KeepExisting,Verify=true,Hash,AutoLinks=true,Restartable;
    }
    public class CopyStats {
        public long Total,Needed,Skipped,Mismatch,Failed,Extra;
    }
    public class CopyResult {
        public CopyStats Files,Directories;
        public int ExitCode;
        public bool Parsed;
        public string Log;
        public bool Clean {get{return Parsed && ExitCode<8 && Files.Needed==0 && Files.Mismatch==0 && Files.Failed==0 && Directories.Failed==0 && Directories.Mismatch==0;}}
    }
    public class TransferEngine {
        public static bool IsAdmin {get{return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);}}
        readonly string jobRoot;
        public TransferEngine(string jobRoot) {this.jobRoot=jobRoot;Directory.CreateDirectory(jobRoot);}
        public static string Quote(string arg) {
            // Windows CommandLineToArgvW rules, including a quoted drive-root trailing backslash.
            var b=new StringBuilder("\"");int slashes=0;
            foreach(char c in arg) {
                if(c=='\\') {slashes++;continue;}
                if(c=='"') {b.Append('\\',slashes*2+1);b.Append(c);slashes=0;continue;}
                b.Append('\\',slashes);slashes=0;b.Append(c);
            }
            b.Append('\\',slashes*2);b.Append('"');return b.ToString();
        }
        public static void Preflight(CopyOptions opts,string logs,CancellationToken token) {
            opts.Source=NativeFiles.Full(opts.Source);opts.Destination=NativeFiles.Full(opts.Destination);
            NativeFiles.ValidatePair(opts.Source,opts.Destination);
            if(NativeFiles.Inside(logs,opts.Source)||NativeFiles.Inside(logs,opts.Destination)) throw new IOException("源或目标目录包含工具日志目录，请选择更具体的文件夹。");
            if(opts.Files!=null) foreach(string f in opts.Files) {
                if(Path.GetFileName(f)!=f || f.IndexOfAny(new[]{'*','?'})>=0) throw new IOException("文件选择无效。");
                if(!File.Exists(Path.Combine(opts.Source,f))) throw new IOException("所选文件不存在："+f);
                if(NativeFiles.IsLink(Path.Combine(opts.Source,f))) throw new IOException("所选文件是链接；请使用文件夹模式并在链接修复页处理。");
            }
            if(Directory.Exists(opts.Destination)) {
                if(opts.Files!=null) {
                    foreach(string name in opts.Files) {
                        string target=Path.Combine(opts.Destination,name);
                        NativeFiles.RequirePlainParents(target,opts.Destination);
                        if(NativeFiles.IsLink(target)) throw new IOException("目标同名文件是链接，请先处理："+target);
                    }
                    return;
                }
                var dest=Inventory.Scan(opts.Destination,token);
                if(dest.Errors.Count>0) throw new IOException("目标目录无法完整检查，请以管理员身份运行或处理访问问题。\n"+dest.Errors[0]);
                foreach(var link in dest.Links) {
                    string relative=link.Path.Substring(opts.Destination.Length).TrimStart('\\');
                    if(opts.Files!=null && !opts.Files.Contains(relative,StringComparer.OrdinalIgnoreCase)) continue;
                    string origin=Path.Combine(opts.Source,relative);
                    if(NativeFiles.Exists(origin) && !NativeFiles.IsLink(origin)) throw new IOException("目标中存在会把复制写到其他位置的链接，请先处理："+link.Path);
                }
            }
        }
        public CopyResult Run(CopyOptions opts,bool dryRun,CancellationToken token,Action<string> progress) {
            token.ThrowIfCancellationRequested();
            string log=Path.Combine(jobRoot,(dryRun?"verify-":"copy-")+DateTime.Now.ToString("HHmmss-fff")+".log");
            var args=new List<string>{opts.Source,opts.Destination};
            if(opts.Files==null) args.Add("/E");else args.AddRange(opts.Files);
            args.AddRange(new[]{"/XJ","/COPY:DAT","/DCOPY:DAT","/R:1","/W:1","/FP","/BYTES","/NP","/NDL","/TEE","/UNILOG:"+log});
            if(dryRun) {args.Add("/L");args.Add("/R:0");args.Add("/W:0");}
            else {args.Add("/MT:"+opts.Threads);if(opts.KeepExisting)args.AddRange(new[]{"/XC","/XN","/XO"});}
            if(IsAdmin) args.Add(opts.Restartable&&!dryRun?"/ZB":"/B");else if(opts.Restartable&&!dryRun)args.Add("/Z");
            var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"robocopy.exe"),String.Join(" ",args.Select(Quote)));
            start.UseShellExecute=false;start.CreateNoWindow=true;start.RedirectStandardOutput=true;start.RedirectStandardError=true;
            start.StandardOutputEncoding=Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            using(var process=new Process {StartInfo=start}) {
                process.OutputDataReceived+=(s,e)=>{if(!String.IsNullOrEmpty(e.Data))progress(e.Data);};
                process.ErrorDataReceived+=(s,e)=>{if(!String.IsNullOrEmpty(e.Data))progress(e.Data);};
                process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();
                while(!process.WaitForExit(150)) {
                    if(token.IsCancellationRequested) {
                        try {process.Kill();}catch(InvalidOperationException){}
                        process.WaitForExit();token.ThrowIfCancellationRequested();
                    }
                }
                process.WaitForExit();token.ThrowIfCancellationRequested();
                var result=Parse(log,process.ExitCode);
                return result;
            }
        }
        public static CopyResult Parse(string log,int exitCode) {
            var result=new CopyResult {Log=log,ExitCode=exitCode};
            var row=new Regex(@"^\s*(Dirs|Files|目录|文件)\s*:\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$",RegexOptions.IgnoreCase);
            foreach(string line in File.ReadLines(log,Encoding.Unicode)) {
                var m=row.Match(line);if(!m.Success)continue;
                var st=new CopyStats {Total=Int64.Parse(m.Groups[2].Value),Needed=Int64.Parse(m.Groups[3].Value),Skipped=Int64.Parse(m.Groups[4].Value),Mismatch=Int64.Parse(m.Groups[5].Value),Failed=Int64.Parse(m.Groups[6].Value),Extra=Int64.Parse(m.Groups[7].Value)};
                if(m.Groups[1].Value=="目录" || m.Groups[1].Value.Equals("Dirs",StringComparison.OrdinalIgnoreCase)) result.Directories=st;else result.Files=st;
            }
            result.Parsed=result.Files!=null && result.Directories!=null;
            return result;
        }
        public static long HashVerify(CopyOptions opts,CancellationToken token,Action<string> progress) {
            long count=0;var dirs=new Stack<string>();dirs.Push(opts.Source);
            while(dirs.Count>0) {
                token.ThrowIfCancellationRequested();string dir=dirs.Pop();
                foreach(string path in Directory.EnumerateFileSystemEntries(dir)) {
                    token.ThrowIfCancellationRequested();uint attr=NativeFiles.Attributes(path);
                    if((attr&0x400)!=0)continue;
                    if((attr&16)!=0) {if(opts.Files==null)dirs.Push(path);continue;}
                    string relative=path.Substring(opts.Source.Length).TrimStart('\\');
                    if(opts.Files!=null&&!opts.Files.Contains(relative,StringComparer.OrdinalIgnoreCase))continue;
                    string target=Path.Combine(opts.Destination,relative);
                    progress("内容校验 "+(++count).ToString("N0")+"："+relative);
                    if(!NativeFiles.Exists(target) || NativeFiles.IsLink(target)) throw new IOException("目标文件缺失或变为链接："+target);
                    // FileShare.Read blocks concurrent writes/deletes while each pair is checked.
                    using(var a=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                    using(var b=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                        if(a.Length!=b.Length)throw new IOException("文件大小不同："+relative);
                        byte[] ha=HashStream(a,token),hb=HashStream(b,token);
                        if(!ha.SequenceEqual(hb))throw new IOException("文件内容不同："+relative);
                    }
                }
            }
            return count;
        }
        static byte[] HashStream(Stream stream,CancellationToken token) {
            using(var hash=SHA256.Create()) {
                byte[] buffer=new byte[1024*1024];int n;
                while((n=stream.Read(buffer,0,buffer.Length))>0) {token.ThrowIfCancellationRequested();hash.TransformBlock(buffer,0,n,buffer,0);}
                hash.TransformFinalBlock(new byte[0],0,0);return hash.Hash;
            }
        }
    }
}
