import argparse, ast, os, sys, pathlib, shutil, subprocess, html, re
p=argparse.ArgumentParser()
p.add_argument("--root",required=True);p.add_argument("--data",required=True);p.add_argument("--name",required=True)
p.add_argument("--gpu",default="0");p.add_argument("--batch",type=int,default=2);p.add_argument("--epochs",type=int,default=100)
p.add_argument("--workers",type=int,default=8);p.add_argument("--output",required=True);p.add_argument("--deps",required=True)
p.add_argument("--check",action="store_true")
a=p.parse_args()
tool=pathlib.Path(__file__).resolve().parent
root=pathlib.Path(a.root).resolve(); output=pathlib.Path(a.output).resolve()
os.chdir(root);sys.path.insert(0,str(root));sys.argv=[str(root/"webui.py")]
# Load the installed training functions, excluding the browser UI and shared TEMP cleanup.
tree=ast.parse((root/"webui.py").read_text(encoding="utf-8"))
nodes=[]
for node in tree.body:
    if isinstance(node,ast.With): break
    if isinstance(node,ast.For) and "os.listdir(tmp)" in ast.unparse(node): continue
    nodes.append(node)
namespace={"__name__":"xvoice_training","__file__":str(root/"webui.py")}
exec(compile(ast.Module(body=nodes,type_ignores=[]),str(root/"webui.py"),"exec"),namespace)
from i18n.i18n import I18nAuto
namespace["i18n"] = I18nAuto("zh_CN")
if a.check:
    assert callable(namespace["train1key"])
    print("训练函数加载检查通过",flush=True)
    sys.exit(0)
format_workflow = namespace["format_workflow_status"]
def report_workflow(step, *args, **kwargs):
    stage = {"数据切分": 1, "F0与HuBERT特征提取": 2, "模型训练": 3, "索引训练": 4}.get(step)
    if stage:
        print("@XVOICE_STAGE:" + str(stage), flush=True)
    return format_workflow(step, *args, **kwargs)
namespace["format_workflow_status"] = report_workflow
previous=""
for update in namespace["train1key"](a.name,"40k",True,a.data,0,a.workers,"rmvpe",10,a.epochs,a.batch,namespace["i18n"]("是"),"assets/pretrained/f0G40k.pth","assets/pretrained/f0D40k.pth",a.gpu,namespace["i18n"]("否"),namespace["i18n"]("是"),"v1",a.gpu):
    text=html.unescape(re.sub("<[^>]+>"," ",str(update[0])))
    text=text.replace("•","-").replace("—","-").replace("–","-")
    if text!=previous: print(text,flush=True);previous=text
    if "失败" in text or "Traceback" in text: raise RuntimeError(text)
weight=root/"assets/weights"/(a.name+".pth")
if not weight.is_file():
    candidates=list((root/"assets/weights").glob(a.name+"*.pth"))
    if not candidates: raise RuntimeError("未找到训练输出模型，请查看日志")
    shutil.copy2(max(candidates,key=lambda f:f.stat().st_mtime),weight)
indices=list((root/"logs"/a.name).glob("added_*.index"))
if not indices: raise RuntimeError("未找到训练输出索引")
shutil.copy2(max(indices,key=lambda f:f.stat().st_mtime),root/"assets/indices"/(a.name+".index"))
output.mkdir(parents=True,exist_ok=True)
shutil.copy2(weight,output/weight.name)
shutil.copy2(root/"assets/indices"/(a.name+".index"),output/(a.name+".index"))
# Preserve original RVC files and export the C# inference assets into a separate folder.
print("@XVOICE_STAGE:5",flush=True)
converted=output/"CSharp";converted.mkdir(exist_ok=True)
subprocess.run([sys.executable,str(tool/"export_models.py"),str(root),str(converted),a.deps,a.name],check=True)
subprocess.run([sys.executable,str(tool/"export_quality_assets.py"),str(root),str(converted)],check=True)
shutil.copy2(converted/(a.name+".index"),converted/(a.name+".ivf"))
print("完成。输出目录："+str(output),flush=True)



