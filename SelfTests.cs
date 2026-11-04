using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
namespace FolderBridge {
    public static class SelfTests {
        static List<string> results;
        static void Check(bool value,string title){if(!value)throw new Exception("FAIL: "+title);results.Add("PASS: "+title);}
        static void Throws(Action action,string title){bool threw=false;try{action();}catch{threw=true;}Check(threw,title);}
        static void Put(string path,string text){Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,text,Encoding.UTF8);}
        public static int Run(string root) {
            root=NativeFiles.Full(root);Directory.CreateDirectory(root);results=new List<string>();
            string report=Path.Combine(root,"test-results.txt");
            try {
                string test=Path.Combine(root,"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(test);
                string src=Path.Combine(test,"源目录 & space"),dst=Path.Combine(test,"目标目录"),logs=Path.Combine(test,"logs");
                Directory.CreateDirectory(src);Directory.CreateDirectory(dst);
                Put(Path.Combine(src,"中文 文件.txt"),"完整复制：中文内容 123");Put(Path.Combine(src,"dependencies","module.js"),"module.exports = 42;");
                Directory.CreateDirectory(Path.Combine(src,"empty"));Put(Path.Combine(src,".hidden"),"hidden");File.SetAttributes(Path.Combine(src,".hidden"),FileAttributes.Hidden);
                string longDir=Path.Combine(src,new string('a',90),new string('b',90),new string('c',80));Put(Path.Combine(longDir,"long.txt"),"long path");
                NativeFiles.CreateJunction(Path.Combine(src,"modules"),Path.Combine(src,"dependencies"));
                var link=NativeFiles.ReadLink(Path.Combine(src,"modules"));Check(link.Kind=="Junction"&&NativeFiles.Same(link.Target,Path.Combine(src,"dependencies")),"读取目录联接目标");
                Throws(()=>NativeFiles.ValidatePair(src,src),"拒绝相同源与目标");Throws(()=>NativeFiles.ValidatePair(src,Path.Combine(src,"nested")),"拒绝目标在源内");Throws(()=>NativeFiles.ValidatePair(src,test),"拒绝目标包含源");
                string alias=Path.Combine(test,"alias");NativeFiles.CreateJunction(alias,src);Throws(()=>NativeFiles.ValidatePair(src,Path.Combine(alias,"child")),"拒绝目录别名造成的递归复制");
                Check(LinkService.Map(@"C:\project-two\x",@"C:\project",@"F:\project")==null,"映射按目录边界而非字符串前缀");
                var scan=Inventory.Scan(src,CancellationToken.None);Check(scan.Errors.Count==0&&scan.Links.Count==1&&scan.Files==4,"不展开目录联接，中文和长路径扫描");
                var opts=new CopyOptions{Source=src,Destination=dst,Threads=4};TransferEngine.Preflight(opts,logs,CancellationToken.None);
                var engine=new TransferEngine(logs);var copied=engine.Run(opts,false,CancellationToken.None,s=>{});
                Check(copied.Parsed&&copied.ExitCode<8&&copied.Files.Failed==0,"Robocopy 真实复制和中文统计解析");
                Check(File.ReadAllText(Path.Combine(dst,"中文 文件.txt"))==File.ReadAllText(Path.Combine(src,"中文 文件.txt")),"中文路径与内容正确");
                Check(File.Exists(Path.Combine(dst,longDir.Substring(src.Length+1),"long.txt")),"超过 260 字符路径复制");
                Check((File.GetAttributes(Path.Combine(dst,".hidden"))&FileAttributes.Hidden)!=0,"保留隐藏属性");Check(Directory.Exists(Path.Combine(dst,"empty")),"复制空目录");
                var plan=LinkService.Scan(src,dst,src,dst,CancellationToken.None);Check(plan.Errors.Count==0&&plan.Plans.Single().Ready,"迁移内部链接生成可审阅计划");
                LinkService.Apply(plan.Plans[0],dst);Check(NativeFiles.Same(NativeFiles.ReadLink(Path.Combine(dst,"modules")).Target,Path.Combine(dst,"dependencies")),"内部链接重定向至目标盘");
                Check(File.Exists(Path.Combine(dst,"modules","module.js")),"修复后通过链接读取内容");
                var verified=engine.Run(opts,true,CancellationToken.None,s=>{});Check(verified.Clean,"复制后只读核对通过");
                Check(TransferEngine.HashVerify(opts,CancellationToken.None,s=>{})==4,"SHA-256 全文件内容校验");
                File.Delete(Path.Combine(dst,"中文 文件.txt"));Check(!engine.Run(opts,true,CancellationToken.None,s=>{}).Clean,"识别遗漏文件");engine.Run(opts,false,CancellationToken.None,s=>{});
                var repeated=engine.Run(opts,false,CancellationToken.None,s=>{});Check(repeated.Files.Needed==0,"重复复制跳过相同文件");
                string module=Path.Combine(dst,"dependencies","module.js");DateTime stamp=File.GetLastWriteTimeUtc(module);byte[] content=File.ReadAllBytes(module);content[content.Length-1]^=1;File.WriteAllBytes(module,content);File.SetLastWriteTimeUtc(module,stamp);
                Check(engine.Run(opts,true,CancellationToken.None,s=>{}).Clean,"大小时间核对有明确局限");Throws(()=>TransferEngine.HashVerify(opts,CancellationToken.None,s=>{}),"SHA-256 识别同大小同时间的内容损坏");
                File.SetLastWriteTimeUtc(module,DateTime.UtcNow.AddHours(-3));engine.Run(opts,false,CancellationToken.None,s=>{});Check(TransferEngine.HashVerify(opts,CancellationToken.None,s=>{})==4,"补复制恢复正确内容");
                Put(Path.Combine(dst,"only-target.txt"),"retain me");Check(engine.Run(opts,true,CancellationToken.None,s=>{}).Clean,"目标额外文件不误判为源文件遗漏");engine.Run(opts,false,CancellationToken.None,s=>{});Check(File.Exists(Path.Combine(dst,"only-target.txt")),"复制不清理目标额外文件");
                Put(module,"user edit");opts.KeepExisting=true;engine.Run(opts,false,CancellationToken.None,s=>{});Check(File.ReadAllText(module)=="user edit","保留目标已有文件模式");Check(!engine.Run(opts,true,CancellationToken.None,s=>{}).Clean,"保留已有文件仍能报告内容差异");opts.KeepExisting=false;engine.Run(opts,false,CancellationToken.None,s=>{});
                var canceled=new CancellationTokenSource();canceled.Cancel();Throws(()=>engine.Run(opts,false,canceled.Token,s=>{}),"启动前取消不会开始复制");
                string oldProject=Path.Combine(test,"old-project"),newProject=Path.Combine(test,"new-project");Directory.CreateDirectory(oldProject);Put(Path.Combine(newProject,"deps","ok.txt"),"ok");
                string snapshots=Path.Combine(test,"snapshots"),copies=Path.Combine(test,"copies");Directory.CreateDirectory(snapshots);Directory.CreateDirectory(copies);
                NativeFiles.CreateJunction(Path.Combine(snapshots,"broken"),Path.Combine(oldProject,"deps"));Directory.CreateDirectory(Path.Combine(copies,"broken"));
                var broken=LinkService.Scan(snapshots,copies,oldProject,newProject,CancellationToken.None).Plans.Single();Check(broken.Ready,"失效源链接通过旧根到新根映射恢复");LinkService.Apply(broken,copies);Check(File.Exists(Path.Combine(copies,"broken","ok.txt")),"空目录安全替换为链接");
                var again=LinkService.Scan(snapshots,copies,oldProject,newProject,CancellationToken.None).Plans.Single();Check(again.Status=="正常"&&!again.Ready,"再次扫描修复结果幂等");
                var destOnly=LinkService.Scan("",copies,oldProject,newProject,CancellationToken.None);Check(destOnly.Plans.Single().Status=="正常","无源目录时支持检查目标已有链接");
                NativeFiles.CreateJunction(Path.Combine(snapshots,"conflict"),Path.Combine(oldProject,"deps"));Put(Path.Combine(copies,"conflict","precious.txt"),"do not delete");
                var conflict=LinkService.Scan(snapshots,copies,oldProject,newProject,CancellationToken.None).Plans.First(p=>p.Path.EndsWith("conflict"));Check(!conflict.Ready&&conflict.Status=="冲突","保护非空真实目录");Throws(()=>LinkService.Apply(conflict,copies),"执行前再次检查冲突");Check(File.ReadAllText(Path.Combine(copies,"conflict","precious.txt"))=="do not delete","冲突不损坏现有文件");
                NativeFiles.CreateJunction(Path.Combine(snapshots,"real-file"),Path.Combine(oldProject,"deps"));Put(Path.Combine(copies,"real-file"),"real file");Check(!LinkService.Scan(snapshots,copies,oldProject,newProject,CancellationToken.None).Plans.First(p=>p.Path.EndsWith("real-file")).Ready,"保护同名真实文件");
                NativeFiles.CreateJunction(Path.Combine(snapshots,"race"),Path.Combine(oldProject,"deps"));Directory.CreateDirectory(Path.Combine(copies,"race"));var race=LinkService.Scan(snapshots,copies,oldProject,newProject,CancellationToken.None).Plans.First(p=>p.Path.EndsWith("race"));Put(Path.Combine(copies,"race","new.txt"),"arrived after scan");Throws(()=>LinkService.Apply(race,copies),"扫描后出现新文件时拒绝替换");
                var unmapped=LinkService.Scan(snapshots,copies,snapshots,copies,CancellationToken.None);Check(unmapped.Plans.Any(p=>p.Status=="失效链接"&&!p.Ready),"无法映射的失效链接不猜测修复");
                string targetWithLink=Path.Combine(test,"unsafe-target");Directory.CreateDirectory(targetWithLink);NativeFiles.CreateJunction(Path.Combine(targetWithLink,"dependencies"),Path.Combine(newProject,"deps"));Throws(()=>TransferEngine.Preflight(new CopyOptions{Source=src,Destination=targetWithLink},logs,CancellationToken.None),"防止复制通过目标链接写出目标树");
                var cycle=new LinkPlan{Path=Path.Combine(copies,"cycle"),NewTarget=copies,OldTarget=copies,Kind="Junction",Directory=true};Throws(()=>LinkService.Apply(cycle,copies),"拒绝指向父目录的循环链接");
                string parser=Path.Combine(logs,"parser.log");File.WriteAllText(parser,"目录: 10 0 10 0 1 0\r\n文件: 30 0 30 0 0 0\r\n",Encoding.Unicode);Check(!TransferEngine.Parse(parser,0).Clean,"目录失败即使退出码为零也不能通过");
                File.WriteAllText(parser,"unrecognized localization",Encoding.Unicode);Check(!TransferEngine.Parse(parser,0).Clean,"未知统计格式不能误报成功");
                var filesDest=Path.Combine(test,"selected-files");Directory.CreateDirectory(filesDest);var single=new CopyOptions{Source=src,Destination=filesDest,Files=new[]{"中文 文件.txt"}};engine.Run(single,false,CancellationToken.None,s=>{});Check(File.Exists(Path.Combine(filesDest,"中文 文件.txt"))&&!Directory.Exists(Path.Combine(filesDest,"dependencies")),"选择文件模式不复制其他内容");
                Check(File.Exists(Path.Combine(src,"中文 文件.txt")),"所有操作均保留源文件");
                try {
                    NativeFiles.CreateLink(Path.Combine(src,"file-link.txt"),Path.Combine(src,"中文 文件.txt"),"SymbolicLink",false);
                    engine.Run(opts,false,CancellationToken.None,s=>{});
                    Check(!NativeFiles.Exists(Path.Combine(dst,"file-link.txt")),"普通复制排除文件符号链接");
                    var fp=LinkService.Scan(src,dst,src,dst,CancellationToken.None).Plans.First(p=>p.Path.EndsWith("file-link.txt"));LinkService.Apply(fp,dst);Check(NativeFiles.IsLink(fp.Path)&&File.ReadAllText(fp.Path)==File.ReadAllText(Path.Combine(src,"中文 文件.txt")),"文件符号链接单独重定向");
                } catch(IOException ex) {
                    if(ex.Message.Contains("开发者模式"))results.Add("SKIP: 当前进程无创建文件符号链接权限，界面会提示管理员模式。");else throw;
                }
                results.Add("全部通过；测试数据保留于："+test);File.WriteAllLines(report,results,new UTF8Encoding(true));return 0;
            } catch(Exception ex) {results.Add(ex.ToString());File.WriteAllLines(report,results,new UTF8Encoding(true));return 1;}
        }
    }
}
