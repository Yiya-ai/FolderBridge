using System;
using System.IO;
using System.Windows.Forms;
using System.Reflection;
[assembly: AssemblyTitle("目录搬家助手")]
[assembly: AssemblyDescription("文件复制、完整性核对与迁移链接修复")]
[assembly: AssemblyVersion("1.0.0.0")]
namespace FolderBridge {
    static class Program {
        [STAThread]
        static int Main(string[] args) {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            if(args.Length>=2 && args[0]=="--self-test")return SelfTests.Run(args[1]);
            if(args.Length>=2 && args[0]=="--ui-test") {
                string root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
                try {using(var form=new MainForm(true,Path.Combine(root,"appdata")))form.RunUiSmoke(root);return 0;}
                catch(Exception ex){File.WriteAllText(Path.Combine(root,"ui-test-results.txt"),ex.ToString());return 1;}
            }
            if(args.Length>=2 && args[0]=="--preview") {using(var form=new MainForm(true))form.RenderPreview(args[1],args.Length>2&&args[2]=="links");return 0;}
            Application.ThreadException+=(s,e)=>MessageBox.Show(e.Exception.Message,"操作未完成",MessageBoxButtons.OK,MessageBoxIcon.Error);
            Application.Run(new MainForm());return 0;
        }
    }
}
