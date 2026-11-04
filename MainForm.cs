using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FolderBridge {
    public class MainForm : Form {
        readonly Color ink=Color.FromArgb(27,43,58),accent=Color.FromArgb(24,91,151),muted=Color.FromArgb(88,104,118);
        readonly string appData;
        TextBox source,destination,repairSource,repairDestination,oldRoot,newRoot;
        RadioButton folderMode,fileMode;NumericUpDown threads;CheckBox verify,hash,autoLinks,keepExisting,restartable;
        Button copyButton,verifyButton,scanButton,repairButton,cancelButton,adminButton;
        Label status,detail,summary,fileSelection;
        RichTextBox journal;DataGridView grid;ProgressBar progress;
        TabControl tabs;Panel inputPanel;
        string[] selectedFiles;List<LinkPlan> plans=new List<LinkPlan>();
        string currentJob,latestProgress="";CancellationTokenSource cancellation;
        bool busy,pendingClose,planDirty=true,preview,smokeTest;
        readonly System.Windows.Forms.Timer pulse=new System.Windows.Forms.Timer();

        public MainForm(bool preview=false,string dataRoot=null) {
            this.preview=preview;
            appData=dataRoot??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FolderBridge");
            Text="目录搬家助手";Size=new Size(1080,850);MinimumSize=new Size(940,760);
            StartPosition=FormStartPosition.CenterScreen;BackColor=Color.FromArgb(246,248,250);
            Font=new Font("Microsoft YaHei UI",9F);ForeColor=ink;AutoScaleMode=AutoScaleMode.Dpi;
            Build();if(!preview)LoadSettings();
            pulse.Interval=250;pulse.Tick+=(s,e)=>{if(busy)detail.Text=latestProgress;};pulse.Start();
            FormClosing+=OnClosing;
        }
        Button Button(string title,EventHandler action,bool primary=false) {
            var b=new Button {Text=title,AutoSize=true,MinimumSize=new Size(96,34),Height=34,FlatStyle=FlatStyle.Flat,Margin=new Padding(0,0,10,0),Padding=new Padding(10,2,10,2),Cursor=Cursors.Hand};
            b.FlatAppearance.BorderColor=primary?accent:Color.FromArgb(194,205,215);
            b.BackColor=primary?accent:Color.White;b.ForeColor=primary?Color.White:ink;b.Click+=action;return b;
        }
        Label Label(string text,int size=9,bool bold=false) {return new Label {Text=text,AutoSize=true,Font=new Font("Microsoft YaHei UI",size,bold?FontStyle.Bold:FontStyle.Regular),ForeColor=ink,Margin=new Padding(0,5,0,4)};}
        TableLayoutPanel Stack(int rows) {
            var p=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=rows,Padding=new Padding(22,14,22,12),BackColor=Color.White};
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));return p;
        }
        TextBox PathRow(TableLayoutPanel table,int row,string title,bool optional=false) {
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=new Padding(0,3,0,5)};
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,105));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,82));
            var label=Label(title);label.AutoSize=false;label.Dock=DockStyle.Fill;label.TextAlign=ContentAlignment.MiddleLeft;label.Margin=new Padding(0);
            var box=new TextBox {Dock=DockStyle.Fill,Margin=new Padding(0,7,10,0),AccessibleName=title};
            var browse=Button("浏览…",(s,e)=>Browse(box));browse.AutoSize=false;browse.MinimumSize=new Size(0,0);browse.Dock=DockStyle.Fill;browse.Margin=new Padding(0);
            layout.Controls.Add(label,0,0);layout.Controls.Add(box,1,0);layout.Controls.Add(browse,2,0);table.Controls.Add(layout,0,row);
            box.AllowDrop=true;box.DragEnter+=(s,e)=>{if(e.Data.GetDataPresent(DataFormats.FileDrop))e.Effect=DragDropEffects.Copy;};
            box.DragDrop+=(s,e)=>{var paths=(string[])e.Data.GetData(DataFormats.FileDrop);if(paths.Length>0&&Directory.Exists(paths[0]))box.Text=paths[0];};
            return box;
        }
        void Build() {
            var root=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(18)};
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,80));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,82));root.RowStyles.Add(new RowStyle(SizeType.Absolute,135));Controls.Add(root);
            var header=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2};header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute,45));header.RowStyles.Add(new RowStyle(SizeType.Absolute,28));
            header.Controls.Add(Label("目录搬家助手",21,true),0,0);
            var subtitle=Label("复制文件，核对完整性，修复搬盘后的目录链接。");subtitle.ForeColor=muted;header.Controls.Add(subtitle,0,1);
            adminButton=Button(TransferEngine.IsAdmin?"管理员模式":"以管理员身份重开",RestartAdmin);adminButton.Enabled=!TransferEngine.IsAdmin;header.Controls.Add(adminButton,1,0);
            root.Controls.Add(header,0,0);
            inputPanel=new Panel {Dock=DockStyle.Fill};tabs=new TabControl {Dock=DockStyle.Fill,Padding=new Point(16,7)};inputPanel.Controls.Add(tabs);root.Controls.Add(inputPanel,0,1);
            var cp=new TabPage("复制与核对") {BackColor=Color.White};var rp=new TabPage("链接修复") {BackColor=Color.White};tabs.TabPages.Add(cp);tabs.TabPages.Add(rp);

            var copyScroll=new Panel {Dock=DockStyle.Fill,AutoScroll=true};cp.Controls.Add(copyScroll);
            var copy=Stack(10);copy.Dock=DockStyle.Top;copy.Height=445;copyScroll.Controls.Add(copy);
            foreach(float h in new[]{42f,44f,44f,38f,44f,36f,36f,36f,48f})copy.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
            copy.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var select=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};select.Controls.Add(Label("复制内容"));
            folderMode=new RadioButton {Text="整个文件夹",Checked=true,AutoSize=true,Margin=new Padding(16,6,14,0)};fileMode=new RadioButton {Text="指定文件",AutoSize=true,Margin=new Padding(0,6,14,0)};
            select.Controls.Add(folderMode);select.Controls.Add(fileMode);var selectFiles=Button("选择文件…",SelectFiles);select.Controls.Add(selectFiles);fileSelection=Label("支持多个文件");fileSelection.ForeColor=muted;select.Controls.Add(fileSelection);copy.Controls.Add(select,0,0);
            source=PathRow(copy,1,"源目录");destination=PathRow(copy,2,"目标目录");
            var tip=Label("复制源目录内的内容；如需保留文件夹名，请将该名称加到目标路径末尾。");tip.ForeColor=muted;tip.Dock=DockStyle.Fill;copy.Controls.Add(tip,0,3);
            var settings=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};settings.Controls.Add(Label("并行线程"));
            threads=new NumericUpDown {Minimum=1,Maximum=32,Value=8,Width=64,Margin=new Padding(12,3,18,0)};settings.Controls.Add(threads);
            keepExisting=new CheckBox {Text="保留目标已有文件",AutoSize=true,Margin=new Padding(0,7,18,0)};settings.Controls.Add(keepExisting);
            restartable=new CheckBox {Text="大文件断点续传",AutoSize=true,Margin=new Padding(0,7,0,0)};settings.Controls.Add(restartable);copy.Controls.Add(settings,0,4);
            verify=new CheckBox {Text="复制后自动核对大小、时间和失败项",Checked=true,AutoSize=true,Margin=new Padding(0,5,0,0)};copy.Controls.Add(verify,0,5);
            hash=new CheckBox {Text="逐文件 SHA-256 内容校验（更严格，大量文件耗时较长）",AutoSize=true,Margin=new Padding(0,5,0,0)};copy.Controls.Add(hash,0,6);
            autoLinks=new CheckBox {Text="自动恢复项目内部链接；外部依赖和失效链接单独列出",Checked=true,AutoSize=true,Margin=new Padding(0,5,0,0)};copy.Controls.Add(autoLinks,0,7);
            var actions=new FlowLayoutPanel {Dock=DockStyle.Fill};copyButton=Button("开始复制",(s,e)=>StartCopy(false),true);verifyButton=Button("只核对，不复制",(s,e)=>StartCopy(true));
            actions.Controls.Add(copyButton);actions.Controls.Add(verifyButton);copy.Controls.Add(actions,0,8);
            var notice=Label("默认更新有差异的同名文件。源文件和目标多余文件都会保留。\n核对通过也不会自动删除源目录；项目运行配置需另行确认。");notice.ForeColor=muted;notice.Dock=DockStyle.Fill;copy.Controls.Add(notice,0,9);
            fileMode.CheckedChanged+=(s,e)=>{fileSelection.Text=!fileMode.Checked?"支持多个文件":(selectedFiles==null?"尚未选择文件":"已选 "+selectedFiles.Length+" 个文件");autoLinks.Enabled=!fileMode.Checked;};

            var repair=Stack(8);rp.Controls.Add(repair);
            foreach(float h in new[]{34f,42f,42f,42f,42f,42f})repair.RowStyles.Add(new RowStyle(SizeType.Absolute,h));repair.RowStyles.Add(new RowStyle(SizeType.Percent,100));repair.RowStyles.Add(new RowStyle(SizeType.Absolute,31));
            var explanation=Label("从源目录找回链接，或仅扫描目标目录。修复前请检查下方的新目标。");explanation.ForeColor=muted;repair.Controls.Add(explanation,0,0);
            repairSource=PathRow(repair,1,"原目录（可空）",true);repairDestination=PathRow(repair,2,"待修复目录");oldRoot=PathRow(repair,3,"旧项目根目录");newRoot=PathRow(repair,4,"新项目根目录");
            var linkActions=new FlowLayoutPanel {Dock=DockStyle.Fill};linkActions.Controls.Add(Button("从复制页带入",(s,e)=>BringPaths()));
            scanButton=Button("扫描链接",ScanLinks);repairButton=Button("修复勾选的链接",RepairLinks,true);repairButton.Enabled=false;linkActions.Controls.Add(scanButton);linkActions.Controls.Add(repairButton);repair.Controls.Add(linkActions,0,5);
            grid=new DataGridView {Dock=DockStyle.Fill,BackgroundColor=Color.White,BorderStyle=BorderStyle.FixedSingle,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=true,ClipboardCopyMode=DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText};
            grid.ColumnHeadersDefaultCellStyle.BackColor=Color.FromArgb(235,241,247);grid.EnableHeadersVisualStyles=false;grid.RowTemplate.Height=30;
            grid.Columns.Add(new DataGridViewCheckBoxColumn {Name="Selected",HeaderText="选中",FillWeight=35});
            foreach(var c in new[]{new[]{"State","状态","65"},new[]{"Path","链接位置","160"},new[]{"Old","原目标","140"},new[]{"New","新目标","140"},new[]{"Reason","说明","150"}})grid.Columns.Add(new DataGridViewTextBoxColumn {Name=c[0],HeaderText=c[1],FillWeight=Int32.Parse(c[2]),ReadOnly=true});
            grid.CellBeginEdit+=(s,e)=>{if(e.ColumnIndex==0 && !plans[e.RowIndex].Ready)e.Cancel=true;};
            repair.Controls.Add(grid,0,6);summary=Label("只替换链接或空目录；不会替换真实文件、非空目录，也不会展开循环链接。");summary.ForeColor=muted;repair.Controls.Add(summary,0,7);
            foreach(var t in new[]{repairSource,repairDestination,oldRoot,newRoot})t.TextChanged+=(s,e)=>{planDirty=true;repairButton.Enabled=false;};

            var state=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=3,Padding=new Padding(0,9,0,0)};state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));state.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,118));
            status=Label("准备就绪",11,true);detail=new Label {Text="选择源目录和目标目录开始。",Dock=DockStyle.Fill,AutoEllipsis=true,ForeColor=muted};progress=new ProgressBar {Dock=DockStyle.Fill,Height=6,Margin=new Padding(0,5,14,0)};
            state.Controls.Add(status,0,0);state.Controls.Add(detail,0,1);state.Controls.Add(progress,0,2);cancelButton=Button("停止任务",(s,e)=>{if(cancellation!=null){cancellation.Cancel();status.Text="正在停止…";cancelButton.Enabled=false;}});cancelButton.Enabled=false;state.Controls.Add(cancelButton,1,0);root.Controls.Add(state,0,2);
            var logArea=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2};logArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));logArea.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,118));logArea.RowStyles.Add(new RowStyle(SizeType.Absolute,35));logArea.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            logArea.Controls.Add(Label("任务记录",10,true),0,0);logArea.Controls.Add(Button("打开日志",OpenLogs),1,0);
            journal=new RichTextBox {Dock=DockStyle.Fill,ReadOnly=true,BorderStyle=BorderStyle.FixedSingle,BackColor=Color.White,Font=new Font("Microsoft YaHei UI",9),DetectUrls=false};logArea.Controls.Add(journal,0,1);logArea.SetColumnSpan(journal,2);root.Controls.Add(logArea,0,3);
            var tools=new ToolTip {AutoPopDelay=12000};tools.SetToolTip(oldRoot,"例如 C:\\Projects\\ExampleProject。复制 output 等子目录时，填写完整项目根目录。");tools.SetToolTip(newRoot,"例如 F:\\Projects\\ExampleProject。仅映射目录边界匹配的路径，不会替换相似名称。");
        }
        void Browse(TextBox box) {
            using(var dialog=new FolderBrowserDialog {Description="选择目录",ShowNewFolderButton=true}) {if(Directory.Exists(box.Text))dialog.SelectedPath=box.Text;if(dialog.ShowDialog(this)==DialogResult.OK)box.Text=dialog.SelectedPath;}
        }
        void SelectFiles(object s,EventArgs e) {
            using(var dialog=new OpenFileDialog {Multiselect=true,Title="选择要复制的文件"}) {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                source.Text=Path.GetDirectoryName(dialog.FileNames[0]);selectedFiles=dialog.FileNames.Select(Path.GetFileName).ToArray();fileMode.Checked=true;fileSelection.Text="已选 "+selectedFiles.Length+" 个文件";
            }
        }
        void BringPaths() {repairSource.Text=source.Text;repairDestination.Text=destination.Text;oldRoot.Text=source.Text;newRoot.Text=destination.Text;}
        void Ui(Action action) {if(IsDisposed)return;if(InvokeRequired){try{BeginInvoke(action);}catch(InvalidOperationException){}}else action();}
        void Note(string text) {
            if(currentJob!=null)File.AppendAllText(Path.Combine(currentJob,"任务记录.txt"),DateTime.Now.ToString("HH:mm:ss ")+text+Environment.NewLine,Encoding.UTF8);
            Ui(()=>{journal.AppendText(DateTime.Now.ToString("HH:mm:ss  ")+text+Environment.NewLine);journal.ScrollToCaret();});
        }
        void Phase(string text) {latestProgress=text;Ui(()=>status.Text=text);Note(text);}
        void StartWork(string title,Action<CancellationToken> action) {
            if(busy)return;
            try {currentJob=Path.Combine(appData,"Logs",DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));Directory.CreateDirectory(currentJob);SaveSettings();}
            catch(Exception ex){MessageBox.Show(this,ex.Message,"无法创建日志",MessageBoxButtons.OK,MessageBoxIcon.Error);return;}
            cancellation=new CancellationTokenSource();busy=true;inputPanel.Enabled=false;adminButton.Enabled=false;cancelButton.Enabled=true;progress.Style=ProgressBarStyle.Marquee;status.Text=title;latestProgress=title;journal.Clear();
            var token=cancellation.Token;
            Task.Factory.StartNew(()=>{
                try {action(token);}
                catch(OperationCanceledException){Note("任务已停止。已完成的复制保留，可再次运行补复制；本次未完成核对。");Ui(()=>status.Text="已停止，尚未完成核对");}
                catch(Exception ex){Note("未完成："+ex.Message);Ui(()=>status.Text="任务未完成，请查看记录");}
                finally {Ui(()=>{busy=false;inputPanel.Enabled=true;cancelButton.Enabled=false;adminButton.Enabled=!TransferEngine.IsAdmin;progress.Style=ProgressBarStyle.Blocks;progress.Value=0;detail.Text="日志已保存："+currentJob;repairButton.Enabled=!planDirty&&plans.Any(p=>p.Ready);cancellation.Dispose();cancellation=null;if(pendingClose)Close();});}
            });
        }
        void StartCopy(bool onlyVerify) {
            if(fileMode.Checked && (selectedFiles==null || selectedFiles.Length==0)){MessageBox.Show(this,"请先选择要复制的文件。");return;}
            var opts=new CopyOptions {Source=source.Text,Destination=destination.Text,Files=fileMode.Checked?selectedFiles:null,Threads=(int)threads.Value,KeepExisting=keepExisting.Checked,Verify=verify.Checked,Hash=hash.Checked,AutoLinks=autoLinks.Checked&&!fileMode.Checked,Restartable=restartable.Checked};
            string linkOld=opts.Source,linkNew=opts.Destination;
            if(String.Equals(repairSource.Text.Trim(),opts.Source.Trim(),StringComparison.OrdinalIgnoreCase) && String.Equals(repairDestination.Text.Trim(),opts.Destination.Trim(),StringComparison.OrdinalIgnoreCase) && !String.IsNullOrWhiteSpace(oldRoot.Text) && !String.IsNullOrWhiteSpace(newRoot.Text)) {linkOld=oldRoot.Text;linkNew=newRoot.Text;}
            StartWork(onlyVerify?"正在准备核对…":"正在准备复制…",token=>{
                TransferEngine.Preflight(opts,Path.Combine(appData,"Logs"),token);
                Phase("检查源目录与目标目录");
                var inventory=opts.Files==null?Inventory.Scan(opts.Source,token):new Inventory();
                if(inventory.Errors.Count>0) {File.WriteAllLines(Path.Combine(currentJob,"扫描错误.txt"),inventory.Errors,Encoding.UTF8);throw new IOException("源目录扫描不完整（"+inventory.Errors.Count+" 项）。请检查权限/云端占位文件，必要时以管理员身份重开。");}
                if(onlyVerify&&!Directory.Exists(opts.Destination))throw new IOException("目标目录不存在，尚未复制。");
                if(!onlyVerify)Directory.CreateDirectory(opts.Destination);
                var engine=new TransferEngine(currentJob);bool copyFailed=false;
                if(!onlyVerify) {
                    Phase("正在复制；详细进度写入日志");
                    CopyResult result=engine.Run(opts,false,token,line=>latestProgress=line.Trim());
                    Note("复制结束，退出码 "+result.ExitCode+"；日志："+Path.GetFileName(result.Log));
                    copyFailed=result.ExitCode>=8 || !result.Parsed || result.Files.Failed>0 || result.Directories.Failed>0;
                    if(copyFailed)Note("复制阶段存在失败或统计无法解析，不能认定迁移完成。");
                }
                int unresolved=0;
                if(opts.Files==null) {
                    Phase("检查目录链接");
                    LinkScan scan=LinkService.Scan(opts.Source,opts.Destination,linkOld,linkNew,token);
                    if(!onlyVerify && opts.AutoLinks && !copyFailed) foreach(var plan in scan.Plans.Where(p=>p.Ready)) {
                        token.ThrowIfCancellationRequested();try{LinkService.Apply(plan,opts.Destination);Note("恢复链接："+plan.Path);}catch(Exception ex){plan.Ready=false;plan.Status="修复失败";plan.Reason=ex.Message;}
                    }
                    unresolved=scan.Errors.Count+scan.Plans.Count(p=>p.Status!="正常"&&p.Status!="已修复");
                    ExportPlans(scan.Plans,currentJob);
                    if(scan.Errors.Count>0)File.WriteAllLines(Path.Combine(currentJob,"链接扫描错误.txt"),scan.Errors,Encoding.UTF8);
                    Ui(()=>{BringPaths();oldRoot.Text=linkOld;newRoot.Text=linkNew;plans=scan.Plans;planDirty=false;PopulateGrid();});
                    Note("链接 "+scan.Plans.Count+" 个；仍需处理 "+unresolved+" 项。详情见链接修复页。");
                }
                CopyResult check=null;
                if(onlyVerify || opts.Verify || opts.Hash) {
                    Phase("核对文件大小、时间和失败项");check=engine.Run(opts,true,token,line=>latestProgress=line.Trim());
                    if(check.Parsed)Note(String.Format("核对：文件 {0:N0}；待补复制 {1:N0}；不匹配 {2:N0}；文件失败 {3:N0}；目录失败 {4:N0}；目标额外文件 {5:N0}。",check.Files.Total,check.Files.Needed,check.Files.Mismatch,check.Files.Failed,check.Directories.Failed,check.Files.Extra));
                    else Note("当前系统语言的统计未能解析，不能判定核对通过，请打开原始日志。");
                }
                if(opts.Hash && check!=null && check.Clean) {Phase("逐文件 SHA-256 内容校验");long n=TransferEngine.HashVerify(opts,token,line=>latestProgress=line);Note("SHA-256 校验一致："+n.ToString("N0")+" 个普通文件。");}
                bool passed=!copyFailed&&check!=null&&check.Clean&&unresolved==0;
                string finish=passed?"核对通过，未发现待补文件或未处理链接":check==null?"复制结束，尚未完成核对":"尚有差异、失败或未处理链接，请查看日志";
                Note(finish);if(!opts.Hash)Note("本次按大小和时间核对，未逐字节比较；链接目标内容不重复计入普通文件核对。");
                Ui(()=>status.Text=finish);
            });
        }
        void ScanLinks(object sender,EventArgs e) {
            string src=repairSource.Text,dst=repairDestination.Text,old=oldRoot.Text,next=newRoot.Text;
            StartWork("正在扫描链接…",token=>{
                LinkScan scan=LinkService.Scan(src,dst,old,next,token);ExportPlans(scan.Plans,currentJob);
                if(scan.Errors.Count>0)File.WriteAllLines(Path.Combine(currentJob,"链接扫描错误.txt"),scan.Errors,Encoding.UTF8);
                Note("发现 "+scan.Plans.Count+" 个链接；可修复 "+scan.Plans.Count(p=>p.Ready)+"；扫描错误 "+scan.Errors.Count+"。修复前请核对新目标。");
                Ui(()=>{plans=scan.Plans;planDirty=false;PopulateGrid();status.Text=scan.Errors.Count==0?"扫描完成，请检查修复清单":"扫描不完整，请先检查错误记录";});
            });
        }
        void PopulateGrid() {
            grid.Rows.Clear();foreach(var plan in plans) {
                int n=grid.Rows.Add(plan.Ready,plan.Status,plan.Path,plan.OldTarget,plan.NewTarget,plan.Reason);
                grid.Rows[n].Cells[2].ToolTipText=plan.Path;grid.Rows[n].Cells[3].ToolTipText=plan.OldTarget;grid.Rows[n].Cells[4].ToolTipText=plan.NewTarget;grid.Rows[n].Cells[5].ToolTipText=plan.Reason;
                grid.Rows[n].Cells[1].Style.ForeColor=plan.Status=="正常"||plan.Status=="已修复"?Color.FromArgb(33,113,80):plan.Ready?accent:Color.FromArgb(155,88,14);
            }
            summary.Text="共 "+plans.Count+" 个链接，可修复 "+plans.Count(p=>p.Ready)+" 个。选中行后可 Ctrl+C 复制详情。";
            repairButton.Enabled=!busy&&!planDirty&&plans.Any(p=>p.Ready);
        }
        void RepairLinks(object sender,EventArgs e) {
            if(planDirty)return;grid.EndEdit();var selected=new List<LinkPlan>();
            for(int i=0;i<plans.Count;i++)if(plans[i].Ready&&Convert.ToBoolean(grid.Rows[i].Cells[0].Value))selected.Add(plans[i]);
            if(selected.Count==0){MessageBox.Show(this,"请勾选可修复的链接。");return;}
            if(!smokeTest && MessageBox.Show(this,"将按清单修复 "+selected.Count+" 个链接。\n只替换链接或空目录，不删除链接目标中的文件。\n\n确认新目标正确后继续。","执行链接修复",MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK)return;
            string dst=NativeFiles.Full(repairDestination.Text);
            StartWork("正在修复链接…",token=>{
                int failed=0;foreach(var plan in selected) {
                    token.ThrowIfCancellationRequested();try{LinkService.Apply(plan,dst);Note("已修复并验证："+plan.Path+" → "+plan.NewTarget);}
                    catch(Exception ex){failed++;plan.Ready=false;plan.Status="修复失败";plan.Reason=ex.Message;Note("未修复："+plan.Path+"；"+ex.Message);}
                    ExportPlans(plans,currentJob);
                }
                Ui(()=>{PopulateGrid();status.Text="链接修复结束：成功 "+(selected.Count-failed)+"，失败 "+failed;});
            });
        }
        static string Csv(string s){return "\""+(s??"").Replace("\"","\"\"")+"\"";}
        static void ExportPlans(List<LinkPlan> records,string folder) {
            var rows=new List<string>{"链接位置,类型,原目标,新目标,状态,说明"};foreach(var p in records)rows.Add(String.Join(",",new[]{p.Path,p.Kind,p.OldTarget,p.NewTarget,p.Status,p.Reason}.Select(Csv)));
            File.WriteAllLines(Path.Combine(folder,"链接清单.csv"),rows,new UTF8Encoding(true));
        }
        void OpenLogs(object s,EventArgs e) {
            string folder=currentJob??Path.Combine(appData,"Logs");try{Directory.CreateDirectory(folder);Process.Start(new ProcessStartInfo(folder){UseShellExecute=true});}catch(Exception ex){MessageBox.Show(this,ex.Message);}
        }
        void RestartAdmin(object s,EventArgs e) {
            if(busy)return;try{SaveSettings();Process.Start(new ProcessStartInfo(Application.ExecutablePath){UseShellExecute=true,Verb="runas"});Close();}catch(Exception ex){MessageBox.Show(this,"未启动管理员模式："+ex.Message);}
        }
        void SaveSettings() {
            if(preview)return;Directory.CreateDirectory(appData);
            var data=new Dictionary<string,string>{{"source",source.Text},{"destination",destination.Text},{"repairSource",repairSource.Text},{"repairDestination",repairDestination.Text},{"oldRoot",oldRoot.Text},{"newRoot",newRoot.Text}};
            data["threads"]=threads.Value.ToString();data["verify"]=verify.Checked.ToString();data["hash"]=hash.Checked.ToString();data["autoLinks"]=autoLinks.Checked.ToString();data["keepExisting"]=keepExisting.Checked.ToString();data["restartable"]=restartable.Checked.ToString();data["fileMode"]=fileMode.Checked.ToString();data["selectedFiles"]=new JavaScriptSerializer().Serialize(selectedFiles);
            File.WriteAllText(Path.Combine(appData,"settings.json"),new JavaScriptSerializer().Serialize(data),Encoding.UTF8);
        }
        void LoadSettings() {
            try {
                string file=Path.Combine(appData,"settings.json");if(!File.Exists(file))return;var d=new JavaScriptSerializer().Deserialize<Dictionary<string,string>>(File.ReadAllText(file));
                foreach(var p in new[]{new KeyValuePair<string,TextBox>("source",source),new KeyValuePair<string,TextBox>("destination",destination),new KeyValuePair<string,TextBox>("repairSource",repairSource),new KeyValuePair<string,TextBox>("repairDestination",repairDestination),new KeyValuePair<string,TextBox>("oldRoot",oldRoot),new KeyValuePair<string,TextBox>("newRoot",newRoot)})if(d.ContainsKey(p.Key))p.Value.Text=d[p.Key];
                foreach(var p in new[]{new KeyValuePair<string,CheckBox>("verify",verify),new KeyValuePair<string,CheckBox>("hash",hash),new KeyValuePair<string,CheckBox>("autoLinks",autoLinks),new KeyValuePair<string,CheckBox>("keepExisting",keepExisting),new KeyValuePair<string,CheckBox>("restartable",restartable)}) {bool value;if(d.ContainsKey(p.Key)&&Boolean.TryParse(d[p.Key],out value))p.Value.Checked=value;}
                int n;if(d.ContainsKey("threads")&&Int32.TryParse(d["threads"],out n))threads.Value=Math.Min(32,Math.Max(1,n));
                if(d.ContainsKey("selectedFiles"))selectedFiles=new JavaScriptSerializer().Deserialize<string[]>(d["selectedFiles"]);
                if(d.ContainsKey("fileMode")&&d["fileMode"]=="True"&&selectedFiles!=null&&selectedFiles.Length>0)fileMode.Checked=true;
            } catch{}
        }
        void OnClosing(object sender,FormClosingEventArgs e) {
            if(busy){e.Cancel=true;if(!pendingClose&&MessageBox.Show(this,"任务仍在进行。停止当前任务后退出？","退出",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes){pendingClose=true;cancellation.Cancel();}return;}
            try{SaveSettings();}catch{}pulse.Stop();pulse.Dispose();
        }
        public void RenderPreview(string path,bool linkTab) {
            source.Text=@"C:\Projects\ExampleProject";destination.Text=@"F:\Projects\ExampleProject";
            if(linkTab) {
                BringPaths();plans=new List<LinkPlan>{new LinkPlan{Path=@"F:\Projects\ExampleProject\output\candidate\node_modules",OldTarget=@"C:\Projects\ExampleProject\frontend\node_modules",NewTarget=@"F:\Projects\ExampleProject\frontend\node_modules",Status="可修复",Reason="新目标存在；只替换空目录。",Ready=true,Kind="Junction",Directory=true}};PopulateGrid();tabs.SelectedIndex=1;
            }
            Show();Application.DoEvents();using(var bitmap=new Bitmap(Width,Height)){DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height));bitmap.Save(path);}Close();
        }
        void WaitForJob() {
            var clock=Stopwatch.StartNew();
            while(busy) {Application.DoEvents();Thread.Sleep(20);if(clock.Elapsed.TotalSeconds>90){cancellation.Cancel();throw new Exception("UI job timed out.");}}
            Application.DoEvents();
        }
        public void RunUiSmoke(string root) {
            smokeTest=true;Directory.CreateDirectory(root);
            string src=Path.Combine(root,"source"),dst=Path.Combine(root,"destination"),old=Path.Combine(root,"old-project"),next=Path.Combine(root,"new-project");
            Directory.CreateDirectory(src);Directory.CreateDirectory(Path.Combine(next,"deps"));File.WriteAllText(Path.Combine(src,"hello.txt"),"UI test data");File.WriteAllText(Path.Combine(next,"deps","module.txt"),"dependency");
            NativeFiles.CreateJunction(Path.Combine(src,"node_modules"),Path.Combine(old,"deps"));
            Show();Application.DoEvents();source.Text=src;destination.Text=dst;
            copyButton.PerformClick();WaitForJob();
            if(!File.Exists(Path.Combine(dst,"hello.txt")) || !status.Text.Contains("未处理"))throw new Exception("Copy UI did not report the unresolved external link: "+status.Text);
            tabs.SelectedIndex=1;oldRoot.Text=old;newRoot.Text=next;scanButton.PerformClick();WaitForJob();
            if(plans.Count!=1 || !plans[0].Ready || !repairButton.Enabled)throw new Exception("Repair UI did not enable the reviewed plan.");
            repairButton.PerformClick();WaitForJob();
            if(!File.Exists(Path.Combine(dst,"node_modules","module.txt")) || !status.Text.Contains("成功 1"))throw new Exception("Repair UI did not finish successfully.");
            tabs.SelectedIndex=0;hash.Checked=true;verifyButton.PerformClick();WaitForJob();
            if(!status.Text.Contains("核对通过"))throw new Exception("Verify UI lost the custom mapping or did not finish: "+status.Text);
            preview=false;selectedFiles=new[]{"hello.txt"};fileMode.Checked=true;keepExisting.Checked=true;threads.Value=17;SaveSettings();
            using(var restored=new MainForm(false,appData)) {
                if(!restored.fileMode.Checked || !restored.keepExisting.Checked || restored.threads.Value!=17 || restored.selectedFiles.Single()!="hello.txt")throw new Exception("Settings restart lost overwrite protection or file selection.");
            }
            preview=true;folderMode.Checked=true;
            File.WriteAllText(Path.Combine(root,"ui-test-results.txt"),"PASS: 真实界面按钮完成复制 → 扫描 → 修复 → 使用原映射核对 → SHA-256 校验。\r\nPASS: 重启保留指定文件、覆盖保护和线程设置。\r\n"+journal.Text,Encoding.UTF8);
            using(var bitmap=new Bitmap(Width,Height)){DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height));bitmap.Save(Path.Combine(root,"ui-complete.png"));}
            Close();
        }
    }
}
